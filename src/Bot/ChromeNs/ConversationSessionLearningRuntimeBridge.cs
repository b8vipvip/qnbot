using Bot.ChatRecord;
using Bot.Knowledge;
using BotLib;
using DbEntity.Response;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Bot.ChromeNs
{
    internal static class ConversationSessionLearningRuntimeBridge
    {
        private const string StyleMessageKeyPrefix = "session-style:";
        private static readonly ConcurrentDictionary<int, bool> Attached =
            new ConcurrentDictionary<int, bool>();
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
                    var take = Math.Max(1, Math.Min(200, maxTurns <= 0 ? 120 : maxTurns));
                    return turns
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Text))
                        .Where(x => x.Role == "user" || x.Role == "assistant")
                        .Where(x => includeWithdrawn || !x.Withdrawn)
                        .Where(x => x.Timestamp == DateTime.MinValue
                            || ((from == DateTime.MinValue || x.Timestamp >= from)
                                && (to == DateTime.MinValue || x.Timestamp <= to)))
                        .OrderBy(x => x.Timestamp)
                        .Take(take)
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

        public static bool RefreshRemoteHistory(string seller, string buyer, string ccode)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            ccode = (ccode ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0 || ccode.Length == 0) return false;

            try
            {
                // Learning must never use QN.CurQN as a cross-shop fallback.
                var qn = QN.FindExistingBySellerNick(seller);
                if (qn == null || qn.CDP == null) return false;
                return ConversationContextStore.RefreshLearningHistory(seller, buyer, ccode);
            }
            catch (Exception ex)
            {
                Log.Info("接待学习刷新聊天历史失败: seller=" + seller
                    + ", buyer=" + buyer + ", error=" + ex.Message);
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
                    var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(qn);
                    if (!Attached.TryAdd(key, true)) continue;
                    var captured = qn;
                    captured.EvRecieveNewMessage += (s, e) => OnRawMessages(captured, e);
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
                foreach (var message in response.result
                    .Where(x => x != null)
                    .OrderBy(IncomingMessageSafety.GetSortValue))
                {
                    ObserveMessage(qn, message);
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
            if (!string.IsNullOrWhiteSpace(messageKey) && !Seen.TryAdd(messageKey, DateTime.Now)) return;

            var seller = qn.Seller == null ? string.Empty : (qn.Seller.Nick ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(seller)
                && message.loginid != null
                && !string.IsNullOrWhiteSpace(message.loginid.nick))
            {
                seller = message.loginid.nick.Trim();
            }
            if (string.IsNullOrWhiteSpace(seller)) return;

            var from = message.fromid == null ? string.Empty : (message.fromid.nick ?? string.Empty).Trim();
            var to = message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
            var buyer = from == seller ? to : (to == seller ? from : string.Empty);
            if (string.IsNullOrWhiteSpace(buyer)) return;

            var isBuyer = from == buyer && to == seller;
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
                Withdrawn = source.Withdrawn
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
            foreach (var key in Seen.Where(x => x.Value < cutoff).Select(x => x.Key).Take(2500).ToList())
            {
                DateTime ignored;
                Seen.TryRemove(key, out ignored);
            }
        }
    }
}
