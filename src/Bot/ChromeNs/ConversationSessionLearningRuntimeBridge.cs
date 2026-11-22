using Bot.ChatRecord;
using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using DbEntity.Response;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class ConversationSessionLearningRuntimeBridge
    {
        private const string StyleMessageKeyPrefix = "session-style:";
        // QN instances can be replaced without restarting Bot. A monotonically
        // growing hash-code dictionary can mistake a new instance for an old one,
        // skipping its message subscription. Weak reference-identity keys avoid
        // both hash reuse and retaining retired QN wrappers.
        private static readonly ConditionalWeakTable<QN, object> Attached =
            new ConditionalWeakTable<QN, object>();
        private static readonly object AttachSync = new object();
        private static readonly ConcurrentDictionary<string, DateTime> Seen =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly DateTime StartedAt = DateTime.Now;
        private static Timer _attachTimer;
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            AttachExisting();
            _attachTimer = new Timer(_ => AttachExisting(), null, 200, 500);
        }

        public static List<ConversationContextTurn> GetTurnsBetween(
            string seller,
            string buyer,
            DateTime from,
            DateTime to,
            int maxTurns,
            bool includeWithdrawn)
        {
            try
            {
                var state = GetTimelineState(seller, buyer);
                if (state == null) return new List<ConversationContextTurn>();
                object sync;
                List<ConversationContextTurn> turns;
                if (!TryGetStateParts(state, out sync, out turns)) return new List<ConversationContextTurn>();

                lock (sync)
                {
                    // maxTurns is retained for source compatibility only. A learning
                    // round must never silently analyze just the first 120/200 turns.
                    return turns
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Text))
                        .Where(x => x.Role == "user" || x.Role == "assistant")
                        .Where(x => includeWithdrawn || !x.Withdrawn)
                        // Unknown timestamps cannot be safely assigned to this session.
                        // The complete-history gate reports them as incomplete instead.
                        .Where(x => x.Timestamp != DateTime.MinValue)
                        .Where(x => (from == DateTime.MinValue || x.Timestamp >= from)
                            && (to == DateTime.MinValue || x.Timestamp <= to))
                        .OrderBy(x => x.Timestamp)
                        .Select(CloneTurn)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取接待学习时间线失败：" + ex.Message, 10);
                return new List<ConversationContextTurn>();
            }
        }

        public static Task<ConversationHistoryRefreshResult> RefreshRemoteHistoryCompleteAsync(
            string shopKey,
            string seller,
            string buyer,
            string ccode,
            DateTime from,
            DateTime to,
            CancellationToken cancellationToken)
        {
            return ConversationContextStore.RefreshLearningHistoryCompleteAsync(
                shopKey, seller, buyer, ccode, from, to, cancellationToken);
        }

        public static bool RefreshRemoteHistory(string seller, string buyer, string ccode)
        {
            var endAt = DateTime.Now;
            try
            {
                // Compatibility wrapper resolves the same explicit scope as the
                // calling operation. Ambiguous seller nicknames fail closed.
                var shop = ShopSettingsScope.Current
                    ?? ShopContextLocator.ResolveRuntimeBySellerNick(seller);
                var result = RefreshRemoteHistoryCompleteAsync(
                    shop.ShopKey, seller, buyer, ccode,
                    endAt.AddDays(-7), endAt, CancellationToken.None)
                    .GetAwaiter().GetResult();
                return result != null && result.IsComplete;
            }
            catch (Exception ex)
            {
                Log.Info("接待学习刷新完整聊天历史失败: error=" + ex.Message);
                return false;
            }
        }

        private static void AttachExisting()
        {
            try
            {
                QN[] qns;
                try
                {
                    qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray();
                }
                catch
                {
                    return;
                }

                foreach (var qn in qns)
                {
                    if (qn == null) continue;
                    lock (AttachSync)
                    {
                        object marker;
                        if (Attached.TryGetValue(qn, out marker)) continue;
                        var captured = qn;
                        captured.EvRecieveNewMessage += (s, e) => OnRawMessages(captured, e);
                        Attached.Add(qn, new object());
                    }
                }
                CleanupSeen();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("安装接待自动学习消息监听失败：" + ex.Message, 10);
            }
        }

        private static void OnRawMessages(QN qn, RecieveNewMessageEventArgs e)
        {
            if (qn == null || e == null || string.IsNullOrWhiteSpace(e.Message)) return;
            try
            {
                var response = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                if (response == null || response.result == null) return;

                ShopContext shop;
                try
                {
                    // The event source is the only unambiguous shop identity here.
                    // Do not let duplicate seller nicknames collapse into a legacy key.
                    shop = ShopContextLocator.ResolveRuntimeBySeller(qn);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("接待自动学习拒绝未绑定店铺的消息事件: " + ex.Message, 10);
                    return;
                }

                using (ShopSettingsScope.Enter(shop))
                {
                    foreach (var message in response.result
                        .Where(x => x != null)
                        .OrderBy(IncomingMessageSafety.GetSortValue))
                    {
                        ObserveMessage(qn, message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("解析接待自动学习消息失败：" + ex.Message, 10);
            }
        }

        private static void ObserveMessage(QN qn, QNChatMessage message)
        {
            if (message == null) return;
            var messageTime = GetMessageTime(message);
            if (messageTime != DateTime.MinValue && messageTime < StartedAt.AddSeconds(-8)) return;

            var text = ExtractMessageText(message);
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, text);
            var shop = ShopSettingsScope.Current;
            if (shop == null)
            {
                try { shop = ShopContextLocator.ResolveRuntimeBySeller(qn); }
                catch { return; }
            }
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return;

            var seller = (shop.SellerNick ?? string.Empty).Trim();
            var qnSeller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            if (seller.Length == 0
                || (qnSeller.Length > 0 && !DirectOrderIdentityResolver.IdentityEquals(qnSeller, seller)))
                return;
            var loginNick = message.loginid == null ? string.Empty : (message.loginid.nick ?? string.Empty).Trim();
            if (loginNick.Length > 0 && !DirectOrderIdentityResolver.IdentityEquals(loginNick, seller))
                return;

            var from = message.fromid == null ? string.Empty : (message.fromid.nick ?? string.Empty).Trim();
            var to = message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
            // The transport may prefix seller nicknames with "cntaobao". Resolve
            // the seller endpoint canonically instead of dropping otherwise valid
            // messages through exact string comparison.
            var fromIsSeller = DirectOrderIdentityResolver.IdentityEquals(from, seller);
            var toIsSeller = DirectOrderIdentityResolver.IdentityEquals(to, seller);
            if (fromIsSeller == toIsSeller) return;
            var buyer = fromIsSeller ? to : from;
            if (string.IsNullOrWhiteSpace(buyer)
                || DirectOrderIdentityResolver.IdentityEquals(buyer, seller)) return;
            if (shop.HasStableSellerId)
            {
                var expectedTargetId = (shop.SellerId ?? string.Empty).Trim();
                var sellerEndpointTargetId = fromIsSeller
                    ? (message.fromid == null ? string.Empty : (message.fromid.targetId ?? string.Empty).Trim())
                    : (message.toid == null ? string.Empty : (message.toid.targetId ?? string.Empty).Trim());
                var loginTargetId = message.loginid == null ? string.Empty
                    : (message.loginid.targetId ?? string.Empty).Trim();
                if ((sellerEndpointTargetId.Length > 0
                        && !string.Equals(sellerEndpointTargetId, expectedTargetId, StringComparison.Ordinal))
                    || (loginTargetId.Length > 0
                        && !string.Equals(loginTargetId, expectedTargetId, StringComparison.Ordinal)))
                    return;
            }

            BuyerIdentityAliasService.ObserveMessage(seller, message);
            var canonicalBuyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            if (!string.IsNullOrWhiteSpace(canonicalBuyer)) buyer = canonicalBuyer;

            // Do not consume the message fingerprint until its exact shop, seller and buyer
            // identities are known. A standby CDP page may deliver the same payload before
            // the authoritative QN binding is ready; that early copy must not suppress the
            // later identity-complete event from the owning session.
            var scopedMessageKey = shop.ShopKey + "|" + messageKey;
            if (!string.IsNullOrWhiteSpace(messageKey)
                && !Seen.TryAdd(scopedMessageKey, DateTime.Now)) return;

            var fromIsBuyer = BuyerIdentityAliasService.AreEquivalent(seller, from, buyer);
            var isBuyer = fromIsBuyer && toIsSeller;
            if (isBuyer && !ConversationContextStore.IsPlatformSystemTip(message, text))
            {
                // Archive all incoming buyer message text independently from reply success
                // and buyer learning, without persisting buyer nickname or conversation ID.
                BuyerMessageArchiveService.Observe(seller, text, messageKey, messageTime);
                // This event is raised before QN processes the same buyer message. Recording now is safe
                // because ConversationContextStore deduplicates by message key, and it gives us a state in
                // which to inject the store-specific style as a system turn before AI generation starts.
                ConversationContextStore.RefreshAndRecord(message, text);
                InjectLearnedStyle(seller, buyer);
            }

            ConversationSessionLearningService.ObserveLiveMessage(message, text, seller, buyer);
        }

        private static void InjectLearnedStyle(string seller, string buyer)
        {
            var addon = ConversationSessionLearningService.BuildReplyStylePromptAddon(seller);
            var state = GetTimelineState(seller, buyer);
            if (state == null) return;
            object sync;
            List<ConversationContextTurn> turns;
            if (!TryGetStateParts(state, out sync, out turns)) return;

            lock (sync)
            {
                turns.RemoveAll(x => x != null
                    && !string.IsNullOrWhiteSpace(x.MessageKey)
                    && x.MessageKey.StartsWith(StyleMessageKeyPrefix, StringComparison.Ordinal));
                if (string.IsNullOrWhiteSpace(addon)) return;
                turns.Add(new ConversationContextTurn
                {
                    Role = "system",
                    Text = addon,
                    Timestamp = DateTime.Now.AddMilliseconds(-1),
                    MessageKey = StyleMessageKeyPrefix + StableHash(addon),
                    Withdrawn = false
                });
            }
        }

        private static object GetTimelineState(string seller, string buyer)
        {
            // Use the canonical shop-scoped key owned by ConversationContextStore.
            // The old reflection lookup used Seller#Buyer and never matched
            // the actual ShopKey#Seller#Buyer dictionary keys.
            return ConversationContextStore.GetExistingLearningState(seller, buyer);
        }

        private static bool TryGetStateParts(
            object state,
            out object sync,
            out List<ConversationContextTurn> turns)
        {
            sync = null;
            turns = null;
            if (state == null) return false;
            var stateType = state.GetType();
            var syncField = stateType.GetField("Sync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var turnsField = stateType.GetField("Turns", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            sync = syncField == null ? null : syncField.GetValue(state);
            turns = turnsField == null ? null : turnsField.GetValue(state) as List<ConversationContextTurn>;
            return sync != null && turns != null;
        }

        private static ConversationContextTurn CloneTurn(ConversationContextTurn source)
        {
            return new ConversationContextTurn
            {
                Role = source.Role,
                Text = source.Text,
                Timestamp = source.Timestamp,
                MessageKey = source.MessageKey,
                Withdrawn = source.Withdrawn,
                // Learning must retain every native transport identity discovered when
                // live events and remote history are reconciled. Otherwise evidence IDs
                // and provenance are silently reduced to the first canonical key.
                NativeAliases = source.NativeAliases == null
                    ? new string[0]
                    : source.NativeAliases.ToArray(),
                PreciseTimeTicks = source.PreciseTimeTicks,
                SeenLive = source.SeenLive,
                SeenHistory = source.SeenHistory
            };
        }

        private static string ExtractMessageText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            try
            {
                if (message.originalData != null)
                {
                    var text = message.originalData.text ?? string.Empty;
                    if (message.originalData.header != null)
                    {
                        text += message.originalData.header.summary ?? string.Empty;
                    }
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
            catch
            {
            }
            return (message.summary ?? string.Empty).Trim();
        }

        private static DateTime GetMessageTime(QNChatMessage message)
        {
            if (message == null) return DateTime.MinValue;
            DateTime result;
            if (TryParseTime(message.sendTime, out result)) return result;
            if (TryParseTime(message.sortTimeMicrosecond, out result)) return result;
            return DateTime.MinValue;
        }

        private static bool TryParseTime(string value, out DateTime result)
        {
            result = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            long raw;
            if (long.TryParse(value.Trim(), out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) result = DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    else if (raw > 100000000000L) result = DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    else if (raw > 1000000000L) result = DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                    if (result != DateTime.MinValue) return true;
                }
                catch
                {
                }
            }
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto)
                || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                result = dto.LocalDateTime;
                return true;
            }
            return false;
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                var hash = 17;
                foreach (var ch in value ?? string.Empty) hash = hash * 31 + ch;
                return hash == int.MinValue ? 0 : Math.Abs(hash);
            }
        }

        private static void CleanupSeen()
        {
            if (Seen.Count < 5000) return;
            var cutoff = DateTime.Now.AddHours(-2);
            foreach (var pair in Seen.Where(x => x.Value < cutoff).Take(2500).ToList())
            {
                // Timer callbacks can overlap. Remove only the exact stale timestamp
                // observed by this sweep, never a newer value re-added under the same key.
                ((ICollection<KeyValuePair<string, DateTime>>)Seen)
                    .Remove(new KeyValuePair<string, DateTime>(pair.Key, pair.Value));
            }
        }
    }
}
