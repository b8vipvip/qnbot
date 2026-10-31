using Bot.ChatRecord;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class ConversationContextTurn
    {
        public string Role { get; set; }
        public string Text { get; set; }
        public DateTime Timestamp { get; set; }
        public string MessageKey { get; set; }
        public bool Withdrawn { get; set; }
        // Multiple transports may represent one physical message using different IDs.
        public string[] NativeAliases { get; set; }
        public long PreciseTimeTicks { get; set; }
        public bool SeenLive { get; set; }
        public bool SeenHistory { get; set; }
    }

    internal static class ConversationContextStore
    {
        private sealed class PendingPresetReply
        {
            public string QuestionKey;
            public string Reply;
            public string PresetId;
            public string ProductItemId;
            public DateTime ExpiresAt;
        }

        private sealed class TimelineState
        {
            public readonly object Sync = new object();
            public readonly List<ConversationContextTurn> Turns = new List<ConversationContextTurn>();
            public readonly Dictionary<string, DateTime> WithdrawnAnswers =
                new Dictionary<string, DateTime>(StringComparer.Ordinal);
            public DateTime LastRemoteRefresh = DateTime.MinValue;
            public PendingPresetReply PendingPreset;
        }

        private static readonly ConcurrentDictionary<string, TimelineState> States =
            new ConcurrentDictionary<string, TimelineState>(StringComparer.Ordinal);

        /// <summary>
        /// Exact match only. Preset content is read from the shop-scoped editable
        /// catalog so disabled/deleted entries stop being used immediately.
        /// </summary>
        internal static bool IsProductLinkPresetReply(string seller, string answer)
        {
            return BotReplyPresetService.IsPresetReply(seller, answer);
        }

        public static void RefreshAndRecord(QNChatMessage message, string messageText)
        {
            if (message == null) return;
            var seller = GetSellerNick(message);
            var buyer = GetBuyerNick(message, seller);
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;

            var shop = ResolveShop(seller);
            IDisposable scope = null;
            try
            {
                if (shop != null && ShopSettingsScope.Current == null)
                    scope = ShopSettingsScope.Enter(shop);
                var state = GetState(seller, buyer);
                RecordMessage(state, seller, buyer, message, messageText, false);
                var ccode = message.cid == null ? string.Empty : (message.cid.ccode ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(ccode))
                {
                    Task.Run(() =>
                    {
                        IDisposable asyncScope = null;
                        try
                        {
                            if (shop != null) asyncScope = ShopSettingsScope.Enter(shop);
                            RefreshRemoteHistory(state, shop, seller, buyer, ccode);
                        }
                        finally
                        {
                            if (asyncScope != null) asyncScope.Dispose();
                        }
                    });
                }
            }
            finally
            {
                if (scope != null) scope.Dispose();
            }
        }

        /// <summary>
        /// Seller messages arrive through the raw-message observer, not the buyer-only
        /// IncomingMessageSafety.Evaluate path. Feed the same canonical, shop-scoped
        /// timeline used by the Bot mirror immediately on seller echo; do not wait
        /// for GetRemoteHisMsg, which can return only a partial history.
        /// The original message id is preserved so later history sync deduplicates
        /// the same echo instead of rendering it twice.
        /// </summary>
        internal static bool RecordObservedSellerMessage(
            string seller,
            string buyer,
            QNChatMessage message,
            string messageText)
        {
            if (message == null) return false;
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0
                || string.Equals(seller, buyer, StringComparison.Ordinal)) return false;
            var from = message.fromid == null ? string.Empty
                : (message.fromid.nick ?? string.Empty).Trim();
            var to = message.toid == null ? string.Empty
                : (message.toid.nick ?? string.Empty).Trim();
            // Never trust a guessed seller/buyer relation or create a cross-shop
            // conversation from an unverified third-party message.
            if (!string.Equals(from, seller, StringComparison.Ordinal)
                || !string.Equals(to, buyer, StringComparison.Ordinal)) return false;
            var shop = ResolveShop(seller);
            if (shop == null) return false;
            IDisposable scope = null;
            try
            {
                scope = ShopSettingsScope.Enter(shop);
                var state = GetState(seller, buyer);
                RecordMessage(state, seller, buyer, message, messageText, false);
                return true;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("实时卖家回显写入Bot镜像时间线失败: "
                    + SafeLog(ex.Message), 10);
                return false;
            }
            finally
            {
                if (scope != null) scope.Dispose();
            }
        }

        public static bool IsPlatformSystemTip(QNChatMessage message, string messageText)
        {
            var text = CollectVisibleText(message, messageText);
            var compact = Compact(text);
            if (string.IsNullOrWhiteSpace(compact)) return false;

            return compact == "当前用户来自商品详情页"
                || compact == "当前用户来自店铺首页"
                || compact.StartsWith("当前用户来自", StringComparison.Ordinal)
                || compact.StartsWith("该用户来自", StringComparison.Ordinal)
                || compact.StartsWith("买家正在浏览", StringComparison.Ordinal)
                || compact.StartsWith("买家从商品详情页进入", StringComparison.Ordinal)
                || compact.StartsWith("平台提示", StringComparison.Ordinal)
                || compact.StartsWith("系统提示", StringComparison.Ordinal);
        }

        public static bool IsWithdrawalNotice(QNChatMessage message, string messageText)
        {
            var compact = Compact(CollectVisibleText(message, messageText));
            if (string.IsNullOrWhiteSpace(compact)) return false;
            return compact.IndexOf("撤回了一条消息", StringComparison.Ordinal) >= 0
                || compact.IndexOf("撤回了1条消息", StringComparison.Ordinal) >= 0
                || compact.IndexOf("撤回消息", StringComparison.Ordinal) >= 0
                || compact.IndexOf("消息已撤回", StringComparison.Ordinal) >= 0;
        }

        public static bool IsProductLink(QNChatMessage message, string messageText)
        {
            if (message == null || IsPlatformSystemTip(message, messageText)) return false;
            var original = message.originalData;
            if (original != null)
            {
                if (!string.IsNullOrWhiteSpace(original.actionUrl)) return true;
                if (!string.IsNullOrWhiteSpace(original.itemId)) return true;
            }

            var text = CollectVisibleText(message, messageText);
            if (string.IsNullOrWhiteSpace(text)) return false;
            return Regex.IsMatch(text, @"(?i)https?://\S+")
                || Regex.IsMatch(text, @"(?i)(item\.taobao\.com|detail\.tmall\.com|h5\.m\.taobao\.com|m\.tb\.cn)/");
        }

        // Only a trusted itemId field or a recognized Taobao/Tmall item URL can
        // authorize an exact product preset. Arbitrary digits in chat never can.
        internal static string ResolveProductItemId(QNChatMessage message, string text)
        {
            var original = message == null ? null : message.originalData;
            var field = original == null ? string.Empty : (original.itemId ?? string.Empty).Trim();
            if (Regex.IsMatch(field, @"^\d{6,20}$")) return field;
            var urls = string.Join(" ", new[]
            {
                original == null ? string.Empty : (original.actionUrl ?? string.Empty),
                original == null ? string.Empty : (original.url ?? string.Empty),
                text ?? string.Empty,
                message == null ? string.Empty : (message.summary ?? string.Empty)
            });
            var match = Regex.Match(urls,
                @"(?i)(?:https?://)?(?:item\.taobao\.com|detail\.tmall\.com|h5\.m\.taobao\.com)/[^\s""'<>]*?[?&](?:id|item_id)=(\d{6,20})(?!\d)",
                RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        public static void RegisterProductLinkReply(QNChatMessage message, string messageText)
        {
            if (message == null) return;
            var seller = GetSellerNick(message);
            var buyer = GetBuyerNick(message, seller);
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;

            var itemId = ResolveProductItemId(message, messageText);
            var presets = BotReplyPresetService.GetPresetsForProductLink(seller, itemId);
            if (presets.Count == 0)
            {
                var emptyState = GetState(seller, buyer);
                lock (emptyState.Sync) emptyState.PendingPreset = null;
                Log.Info("商品链接预设回复已全部停用，本条交由后续接待流程处理: seller=" + seller);
                return;
            }

            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);
            var seed = StableHash(ScopeKey(seller) + "|" + messageKey + "|" + messageText);
            var index = (seed & int.MaxValue) % presets.Count;
            var selectedPreset = presets[index];
            var reply = selectedPreset.Text;
            string exactRuleItem;
            var exactMatch = BotReplyPresetService.TryParseExactItemTrigger(
                selectedPreset.Trigger, out exactRuleItem);
            Log.Info("商品链接预设匹配: presetId=" + selectedPreset.Id
                + ", rule=" + (exactMatch ? "exact_item" : "generic")
                + ", itemIdKnown=" + (itemId.Length > 0)
                + ", candidates=" + presets.Count);
            var state = GetState(seller, buyer);
            lock (state.Sync)
            {
                state.PendingPreset = new PendingPresetReply
                {
                    QuestionKey = NormalizeText(messageText),
                    Reply = reply,
                    PresetId = selectedPreset.Id,
                    ProductItemId = itemId,
                    ExpiresAt = DateTime.Now.AddMinutes(2)
                };
            }
        }

        public static bool TryTakeProductLinkReply(string seller, string buyer, string question, out string reply)
        {
            reply = string.Empty;
            TimelineState state;
            if (!States.TryGetValue(Key(seller, buyer), out state)) return false;
            lock (state.Sync)
            {
                var pending = state.PendingPreset;
                if (pending == null || pending.ExpiresAt < DateTime.Now)
                {
                    state.PendingPreset = null;
                    return false;
                }
                var normalized = NormalizeText(question);
                if (!string.IsNullOrWhiteSpace(pending.QuestionKey)
                    && !string.IsNullOrWhiteSpace(normalized)
                    && pending.QuestionKey != normalized)
                {
                    return false;
                }
                // An operator may have disabled/deleted the preset while it
                // was waiting inside the two-minute receive buffer.
                var selectedReply = pending.Reply ?? string.Empty;
                state.PendingPreset = null;
                // Fail closed if the selected preset is disabled, edited or no longer
                // matches this exact item while the event waits in the queue.
                if (!BotReplyPresetService.GetEnabledPresets(seller).Any(x =>
                    string.Equals(x.Id, pending.PresetId, StringComparison.Ordinal)
                    && string.Equals(x.Text, selectedReply, StringComparison.Ordinal)
                    && BotReplyPresetService.GetPresetsForProductLink(seller, pending.ProductItemId)
                        .Any(y => string.Equals(x.Id, y.Id, StringComparison.Ordinal))))
                {
                    reply = string.Empty;
                    return false;
                }
                reply = selectedReply;
                return !string.IsNullOrWhiteSpace(reply);
            }
        }

        public static List<ConversationContextTurn> GetRecentTurns(
            string seller,
            string buyer,
            string currentQuestion,
            int maxTurns)
        {
            TimelineState state;
            if (!States.TryGetValue(Key(seller, buyer), out state)) return new List<ConversationContextTurn>();
            var normalizedCurrent = NormalizeText(currentQuestion);
            var now = DateTime.Now;
            lock (state.Sync)
            {
                Cleanup(state, now);
                var eligible = state.Turns
                    .Where(t => t != null && !t.Withdrawn && !string.IsNullOrWhiteSpace(t.Text))
                    .Where(t => t.Timestamp == DateTime.MinValue || t.Timestamp >= now.AddHours(-72))
                    .OrderBy(t => t.Timestamp)
                    .ToList();

                if (!string.IsNullOrWhiteSpace(normalizedCurrent))
                {
                    var currentParts = new HashSet<string>(
                        (currentQuestion ?? string.Empty)
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(NormalizeText)
                            .Where(x => x.Length > 0),
                        StringComparer.Ordinal);
                    for (var i = eligible.Count - 1; i >= 0; i--)
                    {
                        var item = eligible[i];
                        if (item.Role == "assistant") break;
                        var itemText = NormalizeText(item.Text);
                        if (item.Role == "user"
                            && (itemText == normalizedCurrent
                                || currentParts.Contains(itemText)
                                || (itemText.Length >= 2 && normalizedCurrent.Contains(itemText))))
                        {
                            eligible.RemoveAt(i);
                        }
                    }
                }

                var take = Math.Max(2, Math.Min(24, maxTurns));
                return eligible.Skip(Math.Max(0, eligible.Count - take))
                    .Select(CloneTurn)
                    .ToList();
            }
        }

        public static List<ConversationContextTurn> GetMirrorTurns(
            string seller,
            string buyer,
            int maxTurns)
        {
            TimelineState state;
            if (!States.TryGetValue(Key(seller, buyer), out state)) return new List<ConversationContextTurn>();
            var now = DateTime.Now;
            lock (state.Sync)
            {
                Cleanup(state, now);
                var take = Math.Max(10, Math.Min(100, maxTurns));
                // OrderBy is stable: when Qianniu gives several messages the same timestamp,
                // keep their original arrival/history order instead of reordering by a hash/key.
                var eligible = state.Turns
                    // The AI context timeline also contains internal style/system
                    // prompts. Only real buyer/seller messages belong in the GUI.
                    .Where(t => t != null
                        && (t.Role == "user" || t.Role == "assistant")
                        && !string.IsNullOrWhiteSpace(t.Text))
                    .OrderBy(t => t.Timestamp)
                    .ToList();
                return eligible.Skip(Math.Max(0, eligible.Count - take))
                    .Select(CloneTurn)
                    .ToList();
            }
        }

        public static void RequestMirrorRemoteRefresh(
            string seller,
            string buyer,
            string ccode)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            ccode = (ccode ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0 || ccode.Length == 0) return;

            var shop = ResolveShop(seller);
            var state = GetState(seller, buyer);
            Task.Run(() =>
            {
                IDisposable scope = null;
                try
                {
                    if (shop != null) scope = ShopSettingsScope.Enter(shop);
                    RefreshRemoteHistory(state, shop, seller, buyer, ccode, 100);
                }
                catch (Exception ex)
                {
                    Log.Info("镜像聊天记录同步失败: seller=" + seller
                        + ", buyer=" + buyer + ", error=" + SafeLog(ex.Message));
                }
                finally
                {
                    if (scope != null) scope.Dispose();
                }
            });
        }

        public static bool TryGetLatestBuyerQuestion(
            string seller,
            string buyer,
            out string question,
            out DateTime timestamp)
        {
            question = string.Empty;
            timestamp = DateTime.MinValue;
            TimelineState state;
            if (!States.TryGetValue(Key(seller, buyer), out state)) return false;
            lock (state.Sync)
            {
                Cleanup(state, DateTime.Now);
                var latest = state.Turns
                    .Where(t => t != null && t.Role == "user" && !t.Withdrawn && !string.IsNullOrWhiteSpace(t.Text))
                    .OrderByDescending(t => t.Timestamp)
                    .FirstOrDefault();
                if (latest == null) return false;
                question = latest.Text;
                timestamp = latest.Timestamp == DateTime.MinValue ? DateTime.Now.AddMinutes(-1) : latest.Timestamp;
                return true;
            }
        }

        public static string BuildTimelineText(string seller, string buyer, string currentQuestion, int maxTurns)
        {
            var turns = GetRecentTurns(seller, buyer, currentQuestion, maxTurns);
            if (turns.Count < 1) return string.Empty;
            var sb = new StringBuilder();
            foreach (var turn in turns)
            {
                var speaker = turn.Role == "assistant" ? "客服" : "买家";
                var time = turn.Timestamp == DateTime.MinValue
                    ? "时间未知"
                    : turn.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                var text = (turn.Text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length > 500) text = text.Substring(0, 500) + "...";
                sb.Append('[').Append(time).Append(' ').Append(speaker).Append("] ").AppendLine(text);
            }
            return sb.ToString().Trim();
        }

        public static bool IsWithdrawnAnswer(string seller, string buyer, string answer)
        {
            var normalized = NormalizeText(answer);
            if (string.IsNullOrWhiteSpace(normalized)) return false;
            TimelineState state;
            if (!States.TryGetValue(Key(seller, buyer), out state)) return false;
            lock (state.Sync)
            {
                Cleanup(state, DateTime.Now);
                return state.WithdrawnAnswers.ContainsKey(normalized);
            }
        }

        private static void RefreshRemoteHistory(
            TimelineState state,
            ShopContext shop,
            string seller,
            string buyer,
            string ccode,
            int requestedCount = 40)
        {
            lock (state.Sync)
            {
                if (DateTime.Now - state.LastRemoteRefresh < TimeSpan.FromMilliseconds(750)) return;
                state.LastRemoteRefresh = DateTime.Now;
            }

            try
            {
                var qn = FindQn(shop, seller);
                if (qn == null || qn.CDP == null) return;
                var response = qn.CDP.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
                {
                    cid = new { ccode = ccode, type = 1 },
                    count = Math.Max(20, Math.Min(100, requestedCount)),
                    gohistory = 1,
                    msgid = "-1",
                    msgtime = "-1"
                }).GetAwaiter().GetResult();
                var messages = response == null
                    ? null
                    : response["result"]?["msgs"]?.ToObject<List<QNChatMessage>>();
                if (messages == null) return;
                foreach (var item in messages.Where(m => m != null).OrderBy(GetSortValue))
                {
                    RecordMessage(state, seller, buyer, item, ExtractMessageText(item), true);
                }
            }
            catch (Exception ex)
            {
                Log.Info("读取本店会话上下文失败: shop=" + (shop == null ? "unknown" : shop.ShopKey)
                    + ", seller=" + seller + ", buyer=" + buyer + ", error=" + SafeLog(ex.Message));
            }
        }

        private static QN FindQn(ShopContext shop, string seller)
        {
            if (shop == null) return null;
            try
            {
                var qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray();
                foreach (var qn in qns)
                {
                    if (qn == null || qn.Seller == null) continue;
                    if (!string.Equals((qn.Seller.Nick ?? string.Empty).Trim(),
                        (seller ?? string.Empty).Trim(), StringComparison.Ordinal)) continue;
                    try
                    {
                        if (string.Equals(ShopIdentityResolver.Resolve(qn.Seller).ShopKey,
                            shop.ShopKey, StringComparison.Ordinal)) return qn;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static void RecordMessage(
            TimelineState state,
            string seller,
            string buyer,
            QNChatMessage message,
            string text,
            bool fromHistory)
        {
            if (message == null) return;
            var timestamp = GetMessageTime(message);
            if (IsWithdrawalNotice(message, text))
            {
                if (IsSellerWithdrawal(message, text, seller)) MarkLastSellerTurnWithdrawn(state, timestamp);
                return;
            }
            if (IsPlatformSystemTip(message, text)) return;

            var from = message.fromid == null ? string.Empty : (message.fromid.nick ?? string.Empty).Trim();
            var to = message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
            string role;
            if (from == seller && to == buyer) role = "assistant";
            else if (from == buyer && to == seller) role = "user";
            else return;

            var cleanText = IncomingMessageSafety.GetDisplayText(message, text);
            if (string.IsNullOrWhiteSpace(cleanText)
                || string.Equals(cleanText, "[空白或未知消息]", StringComparison.Ordinal))
            {
                return;
            }
            var messageKey = ScopeKey(seller) + "|" + IncomingMessageSafety.BuildMessageKey(message, cleanText);
            var aliases = GetNativeMessageAliases(message);
            var preciseTime = GetPreciseNativeTimeTicks(message);
            var normalizedText = NormalizeText(cleanText);
            lock (state.Sync)
            {
                // Canonical identity wins. In a mixed live/history message one
                // transport may report mcode while the other reports ww_msgid.
                var exact = state.Turns.FirstOrDefault(t => t != null
                    && t.Role == role
                    && (string.Equals(t.MessageKey, messageKey, StringComparison.Ordinal)
                        || HasSharedNativeAlias(t.NativeAliases, aliases)));
                if (exact != null)
                {
                    MergeObservedIdentity(exact, aliases, preciseTime, fromHistory);
                    return;
                }

                // Distinct native keys can still describe one message when both
                // observations have the identical high-resolution timestamp.
                var precisePeers = preciseTime == 0
                    ? new List<ConversationContextTurn>()
                    : state.Turns.Where(t => t != null && t.Role == role
                        && t.PreciseTimeTicks == preciseTime
                        && (fromHistory ? t.SeenLive && !t.SeenHistory
                                        : t.SeenHistory && !t.SeenLive)
                        && string.Equals(NormalizeText(t.Text), normalizedText, StringComparison.Ordinal))
                        .ToList();
                if (precisePeers.Count == 1)
                {
                    MergeObservedIdentity(precisePeers[0], aliases, preciseTime, fromHistory);
                    Log.Info("千牛镜像原始时间戳归并回显: origin="
                        + (fromHistory ? "history" : "live") + ", role=" + role
                        + ", idHash=" + StableHash(messageKey).ToString(CultureInfo.InvariantCulture));
                    return;
                }

                // If an endpoint only supplies second-resolution timestamps,
                // match a single unmatched *opposite-feed* event. Never merge
                // two live sends with equal text, or two history-only entries.
                if (timestamp != DateTime.MinValue)
                {
                    var peers = state.Turns.Where(t => t != null && t.Role == role
                        && t.Timestamp != DateTime.MinValue
                        && t.Timestamp.Ticks / TimeSpan.TicksPerSecond
                            == timestamp.Ticks / TimeSpan.TicksPerSecond
                        && string.Equals(NormalizeText(t.Text), normalizedText, StringComparison.Ordinal))
                        .ToList();
                    var opposite = peers.Where(t => fromHistory
                        ? t.SeenLive && !t.SeenHistory
                        : t.SeenHistory && !t.SeenLive).ToList();
                    if (peers.Count == 1 && opposite.Count == 1
                        && (preciseTime == 0 || opposite[0].PreciseTimeTicks == 0))
                    {
                        MergeObservedIdentity(opposite[0], aliases, preciseTime, fromHistory);
                        Log.Info("千牛镜像单一跨源回显归并: origin="
                            + (fromHistory ? "history" : "live") + ", role=" + role
                            + ", idHash=" + StableHash(messageKey).ToString(CultureInfo.InvariantCulture));
                        return;
                    }
                }

                state.Turns.Add(new ConversationContextTurn
                {
                    Role = role,
                    Text = cleanText,
                    Timestamp = timestamp,
                    MessageKey = messageKey,
                    NativeAliases = aliases,
                    PreciseTimeTicks = preciseTime,
                    SeenLive = !fromHistory,
                    SeenHistory = fromHistory,
                    Withdrawn = false
                });
                Cleanup(state, DateTime.Now);
            }
        }

        private static string[] GetNativeMessageAliases(QNChatMessage message)
        {
            var aliases = new List<string>();
            if (message == null) return aliases.ToArray();
            var clientId = message.mcode == null ? string.Empty
                : (message.mcode.clientId ?? string.Empty).Trim();
            var messageId = message.mcode == null ? string.Empty
                : (message.mcode.messageId ?? string.Empty).Trim();
            // clientId alone may identify a transport client rather than a
            // unique message. Never merge different sends on clientId alone.
            if (messageId.Length > 0)
                aliases.Add("mcode:" + clientId + ":" + messageId);
            if (messageId.Length > 0) aliases.Add("mcode-id:" + messageId);
            if (message.ext != null && message.ext.ww_msgid != 0)
                aliases.Add("ww:" + message.ext.ww_msgid.ToString(CultureInfo.InvariantCulture));
            return aliases.Distinct(StringComparer.Ordinal).ToArray();
        }

        private static long GetPreciseNativeTimeTicks(QNChatMessage message)
        {
            if (message == null) return 0;
            foreach (var raw in new[] { message.sortTimeMicrosecond, message.sendTime })
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var value = raw.Trim();
                long numeric;
                if (!((long.TryParse(value, out numeric) && numeric >= 1000000000000L)
                    || (value.IndexOf('.') >= 0 && value.IndexOf(':') >= 0))) continue;
                DateTime time;
                if (TryParseTime(value, out time) && time != DateTime.MinValue)
                    return time.Ticks;
            }
            return 0;
        }

        private static bool HasSharedNativeAlias(string[] previous, string[] incoming)
        {
            return previous != null && incoming != null && previous.Length > 0
                && incoming.Length > 0
                && previous.Intersect(incoming, StringComparer.Ordinal).Any();
        }

        private static void MergeObservedIdentity(
            ConversationContextTurn turn, string[] aliases, long preciseTime, bool fromHistory)
        {
            // Keep the first canonical MessageKey stable: Bot source rows,
            // context and mirror menus use it as the identity of that turn.
            turn.NativeAliases = (turn.NativeAliases ?? new string[0])
                .Concat(aliases ?? new string[0]).Distinct(StringComparer.Ordinal).ToArray();
            if (turn.PreciseTimeTicks == 0) turn.PreciseTimeTicks = preciseTime;
            if (fromHistory) turn.SeenHistory = true;
            else turn.SeenLive = true;
        }

        private static void MarkLastSellerTurnWithdrawn(TimelineState state, DateTime withdrawalTime)
        {
            lock (state.Sync)
            {
                var candidate = state.Turns
                    .Where(t => t != null && t.Role == "assistant" && !t.Withdrawn)
                    .Where(t => withdrawalTime == DateTime.MinValue
                        || t.Timestamp == DateTime.MinValue
                        || t.Timestamp <= withdrawalTime.AddSeconds(3))
                    .OrderByDescending(t => t.Timestamp)
                    .FirstOrDefault();
                if (candidate == null) return;
                candidate.Withdrawn = true;
                var normalized = NormalizeText(candidate.Text);
                if (!string.IsNullOrWhiteSpace(normalized)) state.WithdrawnAnswers[normalized] = DateTime.Now;
                Log.Info("已记录本店客服撤回回复: answerHash="
                    + StableHash(normalized).ToString(CultureInfo.InvariantCulture));
            }
        }

        private static bool IsSellerWithdrawal(QNChatMessage message, string text, string seller)
        {
            var from = message == null || message.fromid == null
                ? string.Empty
                : (message.fromid.nick ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(seller) && from == seller) return true;
            var compact = Compact(CollectVisibleText(message, text));
            return compact.StartsWith("你撤回", StringComparison.Ordinal)
                || compact.StartsWith("您撤回", StringComparison.Ordinal)
                || compact.StartsWith("客服撤回", StringComparison.Ordinal)
                || compact.StartsWith("卖家撤回", StringComparison.Ordinal);
        }

        private static void Cleanup(TimelineState state, DateTime now)
        {
            state.Turns.RemoveAll(t => t == null
                || (t.Timestamp != DateTime.MinValue && t.Timestamp < now.AddDays(-7)));
            if (state.Turns.Count > 100) state.Turns.RemoveRange(0, state.Turns.Count - 100);
            var expired = state.WithdrawnAnswers
                .Where(x => x.Value < now.AddDays(-7))
                .Select(x => x.Key)
                .ToList();
            foreach (var key in expired) state.WithdrawnAnswers.Remove(key);
            if (state.PendingPreset != null && state.PendingPreset.ExpiresAt < now) state.PendingPreset = null;
        }

        // Typed access point for session learning. Never reconstruct a timeline
        // dictionary key in a consumer: keys are ShopKey#Seller#Buyer.
        internal static object GetExistingLearningState(string seller, string buyer)
        {
            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
            if (shop == null) return null;
            using (ShopSettingsScope.Enter(shop))
            {
                TimelineState state;
                return States.TryGetValue(Key(seller, buyer), out state) ? (object)state : null;
            }
        }

        // The underlying history refresh takes a shop and an optional count.
        // Calling it through a 4-argument reflection invocation silently failed
        // on every completed learning round; keep the invocation typed.
        internal static bool RefreshLearningHistory(string seller, string buyer, string ccode)
        {
            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
            if (shop == null || string.IsNullOrWhiteSpace(ccode)) return false;
            using (ShopSettingsScope.Enter(shop))
            {
                TimelineState state;
                if (!States.TryGetValue(Key(seller, buyer), out state)) return false;
                RefreshRemoteHistory(state, shop, seller, buyer, ccode, 100);
                return true;
            }
        }

        private static TimelineState GetState(string seller, string buyer)
        {
            return States.GetOrAdd(Key(seller, buyer), _ => new TimelineState());
        }

        private static string Key(string seller, string buyer)
        {
            return ScopeKey(seller) + "#" + (seller ?? string.Empty).Trim()
                + "#" + (buyer ?? string.Empty).Trim();
        }

        private static string ScopeKey(string seller)
        {
            var current = ShopSettingsScope.Current;
            if (current != null) return current.ShopKey;
            var resolved = ResolveShop(seller);
            return resolved == null
                ? "legacy-" + StableHash((seller ?? string.Empty).Trim()).ToString(CultureInfo.InvariantCulture)
                : resolved.ShopKey;
        }

        private static ShopContext ResolveShop(string seller)
        {
            var current = ShopSettingsScope.Current;
            if (current != null) return current;
            if (string.IsNullOrWhiteSpace(seller)) return null;
            try { return ShopContextLocator.ResolveRuntimeBySellerNick(seller); }
            catch { return null; }
        }

        private static string GetSellerNick(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            if (message.loginid != null && !string.IsNullOrWhiteSpace(message.loginid.nick))
                return message.loginid.nick.Trim();
            var current = ShopSettingsScope.Current;
            if (current != null && !string.IsNullOrWhiteSpace(current.DisplayName))
                return current.DisplayName.Trim();
            return message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
        }

        private static string GetBuyerNick(QNChatMessage message, string seller)
        {
            if (message == null) return string.Empty;
            var from = message.fromid == null ? string.Empty : (message.fromid.nick ?? string.Empty).Trim();
            var to = message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(from) && from != seller) return from;
            if (!string.IsNullOrWhiteSpace(to) && to != seller) return to;
            return string.Empty;
        }

        private static string ExtractMessageText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            var sb = new StringBuilder();
            if (message.originalData != null)
            {
                if (!string.IsNullOrWhiteSpace(message.originalData.text))
                    sb.Append(message.originalData.text.Trim());
                if (message.originalData.header != null
                    && !string.IsNullOrWhiteSpace(message.originalData.header.summary))
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(message.originalData.header.summary.Trim());
                }
            }
            if (sb.Length < 1 && !string.IsNullOrWhiteSpace(message.summary)) sb.Append(message.summary.Trim());
            return sb.ToString();
        }

        private static string CollectVisibleText(QNChatMessage message, string messageText)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(messageText)) parts.Add(messageText);
            if (message != null)
            {
                if (!string.IsNullOrWhiteSpace(message.summary)) parts.Add(message.summary);
                if (message.originalData != null)
                {
                    if (!string.IsNullOrWhiteSpace(message.originalData.text)) parts.Add(message.originalData.text);
                    if (message.originalData.header != null)
                    {
                        if (!string.IsNullOrWhiteSpace(message.originalData.header.summary))
                            parts.Add(message.originalData.header.summary);
                        if (!string.IsNullOrWhiteSpace(message.originalData.header.title))
                            parts.Add(message.originalData.header.title);
                    }
                }
            }
            return string.Join(" ", parts.Distinct());
        }

        private static long GetSortValue(QNChatMessage message)
        {
            var time = GetMessageTime(message);
            if (time != DateTime.MinValue) return time.Ticks;
            long raw;
            return message != null && long.TryParse(message.sortTimeMicrosecond, out raw) ? raw : 0;
        }

        private static DateTime GetMessageTime(QNChatMessage message)
        {
            if (message == null) return DateTime.MinValue;
            DateTime value;
            if (TryParseTime(message.sendTime, out value)) return value;
            if (TryParseTime(message.sortTimeMicrosecond, out value)) return value;
            return DateTime.MinValue;
        }

        private static bool TryParseTime(string value, out DateTime localTime)
        {
            localTime = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            long raw;
            if (long.TryParse(value.Trim(), out raw))
            {
                try
                {
                    if (raw > 1000000000000000L)
                        localTime = DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    else if (raw > 100000000000L)
                        localTime = DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    else if (raw > 1000000000L)
                        localTime = DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                    if (localTime != DateTime.MinValue) return true;
                }
                catch { }
            }
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto)
                || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                localTime = dto.LocalDateTime;
                return true;
            }
            return false;
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
                NativeAliases = source.NativeAliases == null
                    ? new string[0] : (string[])source.NativeAliases.Clone(),
                PreciseTimeTicks = source.PreciseTimeTicks,
                SeenLive = source.SeenLive,
                SeenHistory = source.SeenHistory
            };
        }

        private static string NormalizeText(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }

        private static string Compact(string value)
        {
            return Regex.Replace((value ?? string.Empty), @"[\s:：,，。.!！?？]", string.Empty);
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                var hash = 17;
                foreach (var ch in value ?? string.Empty) hash = hash * 31 + ch;
                return hash == int.MinValue ? 0 : hash;
            }
        }

        private static string SafeLog(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length > 160 ? value.Substring(0, 160) + "..." : value;
        }
    }
}