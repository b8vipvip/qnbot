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
using System.Threading;
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
        public bool HasImage { get; set; }
    }

    internal sealed class ConversationMirrorSnapshot
    {
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public DateTime LastActivityUtc { get; set; }
        public List<ConversationContextTurn> Turns { get; set; }
    }

    internal sealed class ConversationHistoryRefreshResult
    {
        public bool IsComplete { get; set; }
        public int PagesFetched { get; set; }
        public int MessagesFetched { get; set; }
        public int MessagesInWindow { get; set; }
        public int UnknownTimestampCount { get; set; }
        public string StopReason { get; set; }
        public string ErrorCode { get; set; }
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
            public bool RemoteHistoryInFlight;
            public bool RemoteHistoryLoaded;
            // Activity time, not message time: remote history may contain old timestamps.
            public DateTime LastTouchedUtc = DateTime.UtcNow;
            public string ShopKey = string.Empty;
            public string Seller = string.Empty;
            public string Buyer = string.Empty;
            public PendingPresetReply PendingPreset;
        }

        private static readonly ConcurrentDictionary<string, TimelineState> States =
            new ConcurrentDictionary<string, TimelineState>(StringComparer.Ordinal);
        private const int LearningHistoryPageSize = 100;
        private const int LearningHistoryMaxPages = 200;
        private const int LearningHistoryPageRetries = 3;
        // Keep full 72-hour AI context for returning buyers. Evict only inactive
        // timelines; the on-disk chat/Bot history and Knowledge V2 are untouched.
        private const int SoftTimelineStateLimit = 256;
        private static readonly TimeSpan TimelineContextWindow = TimeSpan.FromHours(72);
        private static readonly TimeSpan TimelineMaxIdle = TimeSpan.FromDays(8);
        private static readonly TimeSpan TimelineSweepInterval = TimeSpan.FromMinutes(10);
        private static long _lastTimelineSweepUtcTicks;

        internal static int CachedTimelineCount { get { return States.Count; } }

        /// <summary>
        /// Exact match only. Preset content is read from the shop-scoped editable
        /// catalog so disabled/deleted entries stop being used immediately.
        /// </summary>
        internal static bool IsProductLinkPresetReply(string seller, string answer)
        {
            return BotReplyPresetService.IsPresetReply(seller, answer);
        }

        public static void RefreshAndRecord(QNChatMessage message, string messageText, string shopKey = null)
        {
            if (message == null) return;
            var seller = GetSellerNick(message, shopKey);
            var buyer = GetBuyerNick(message, seller);
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;

            var currentScope = ShopSettingsScope.Current;
            ShopContext shop;
            try
            {
                shop = ResolveShop(seller, shopKey);
            }
            catch (AmbiguousShopIdentityException ex)
            {
                Log.ErrorWithMaxCount("会话时间线因卖家身份歧义而跳过写入: " + ex.Message, 10);
                return;
            }
            if (currentScope != null
                && (shop == null
                    || !string.Equals(currentScope.ShopKey, shop.ShopKey, StringComparison.Ordinal)))
            {
                // A stale frame must never be recorded under an unrelated ambient
                // ShopSettingsScope. In particular, do not fall through to the
                // synthetic unresolved:<guid> timeline key.
                Log.ErrorWithMaxCount("会话时间线拒绝与当前ShopKey作用域不匹配的消息。", 10);
                return;
            }

            IDisposable scope = null;
            try
            {
                if (shop != null && currentScope == null)
                    scope = ShopSettingsScope.Enter(shop);
                // Live messages can carry the buyer's internal nick while the UI
                // and the current conversation expose its display nick (or vice versa).
                // Learn the alias inside the exact ShopKey scope before choosing the
                // canonical timeline key, otherwise the same buyer gets split timelines.
                BuyerIdentityAliasService.ObserveMessage(seller, message);
                var canonicalBuyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
                if (!string.IsNullOrWhiteSpace(canonicalBuyer)) buyer = canonicalBuyer;
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
            string messageText,
            string shopKey = null)
        {
            if (message == null) return false;
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0
                || string.Equals(seller, buyer, StringComparison.Ordinal)) return false;
            var shop = ResolveShop(seller, shopKey);
            if (shop == null) return false;
            IDisposable scope = null;
            try
            {
                scope = ShopSettingsScope.Enter(shop);
                // Seller echoes can expose a different buyer nick than the active
                // conversation snapshot. Resolve only aliases observed in this exact
                // shop scope; do not accept an unverified third-party participant.
                BuyerIdentityAliasService.ObserveMessage(seller, message);
                var from = message.fromid == null ? string.Empty
                    : (message.fromid.nick ?? string.Empty).Trim();
                var to = message.toid == null ? string.Empty
                    : (message.toid.nick ?? string.Empty).Trim();
                if (!DirectOrderIdentityResolver.IdentityEquals(from, seller)
                    || (!string.Equals(to, buyer, StringComparison.Ordinal)
                        && !BuyerIdentityAliasService.AreEquivalent(seller, to, buyer)))
                    return false;

                var loginNick = message.loginid == null ? string.Empty
                    : (message.loginid.nick ?? string.Empty).Trim();
                if (loginNick.Length > 0
                    && !DirectOrderIdentityResolver.IdentityEquals(loginNick, seller))
                    return false;
                if (shop.HasStableSellerId)
                {
                    var expectedTargetId = (shop.SellerId ?? string.Empty).Trim();
                    var sellerTargetId = message.fromid == null ? string.Empty
                        : (message.fromid.targetId ?? string.Empty).Trim();
                    var loginTargetId = message.loginid == null ? string.Empty
                        : (message.loginid.targetId ?? string.Empty).Trim();
                    if (sellerTargetId.Length > 0
                        && !string.Equals(sellerTargetId, expectedTargetId, StringComparison.Ordinal))
                        return false;
                    if (loginTargetId.Length > 0
                        && !string.Equals(loginTargetId, expectedTargetId, StringComparison.Ordinal))
                        return false;
                }
                var canonicalBuyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
                if (!string.IsNullOrWhiteSpace(canonicalBuyer)) buyer = canonicalBuyer;
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

        public static void RegisterProductLinkReply(QNChatMessage message, string messageText, string shopKey = null)
        {
            if (message == null) return;
            var seller = GetSellerNick(message, shopKey);
            var buyer = GetBuyerNick(message, seller);
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;
            var shop = ResolveShop(seller, shopKey);
            if (shop == null) return;

            using (ShopSettingsScope.Enter(shop))
            {
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
        }

        public static bool TryTakeProductLinkReply(
            string seller,
            string buyer,
            string question,
            out string reply,
            string shopKey = null)
        {
            reply = string.Empty;
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            var shop = ResolveShop(seller, shopKey);
            if (shop == null || buyer.Length == 0) return false;
            using (ShopSettingsScope.Enter(shop))
            {
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
        }

        public static List<ConversationContextTurn> GetRecentTurns(
            string seller,
            string buyer,
            string currentQuestion,
            int maxTurns,
            string shopKey = null)
        {
            TimelineState state;
            if (!States.TryGetValue(Key(seller, buyer, shopKey), out state))
                return new List<ConversationContextTurn>();
            var normalizedCurrent = NormalizeText(currentQuestion);
            var now = DateTime.Now;
            lock (state.Sync)
            {
                state.LastTouchedUtc = DateTime.UtcNow;
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

        public static List<ConversationMirrorSnapshot> GetMirrorConversationSnapshots(
            string shopKey,
            int maxConversations,
            int maxTurns)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0) return new List<ConversationMirrorSnapshot>();

            // Zero/non-positive limits mean the complete retained mirror. The mirror
            // is already bounded by the timeline's explicit 7-day retention and
            // inactive-state sweep; silently cutting it to 100 turns hid real messages.
            var conversationLimit = maxConversations <= 0 ? int.MaxValue : maxConversations;
            var turnLimit = Math.Max(10, Math.Min(100, maxTurns));
            var now = DateTime.Now;
            var result = new List<ConversationMirrorSnapshot>();

            foreach (var state in States.Values)
            {
                if (state == null) continue;
                lock (state.Sync)
                {
                    if (!string.Equals(state.ShopKey, shopKey, StringComparison.Ordinal))
                        continue;

                    Cleanup(state, now);
                    var eligible = state.Turns
                        .Where(t => t != null
                            && (t.Role == "user" || t.Role == "assistant")
                            && (!string.IsNullOrWhiteSpace(t.Text) || t.Withdrawn))
                        .OrderBy(t => t.Timestamp)
                        .ToList();
                    if (eligible.Count == 0
                        || string.IsNullOrWhiteSpace(state.Seller)
                        || string.IsNullOrWhiteSpace(state.Buyer))
                        continue;

                    var turns = eligible
                        .Skip(Math.Max(0, eligible.Count - turnLimit))
                        .Select(CloneTurn)
                        .ToList();
                    var latest = turns[turns.Count - 1];
                    var activityUtc = latest.Timestamp == DateTime.MinValue
                        ? state.LastTouchedUtc
                        : latest.Timestamp.ToUniversalTime();
                    result.Add(new ConversationMirrorSnapshot
                    {
                        Seller = state.Seller,
                        Buyer = state.Buyer,
                        LastActivityUtc = activityUtc,
                        Turns = turns
                    });
                }
            }

            return result
                .OrderByDescending(x => x.LastActivityUtc)
                .ThenBy(x => x.Seller, StringComparer.Ordinal)
                .ThenBy(x => x.Buyer, StringComparer.Ordinal)
                .Take(conversationLimit)
                .ToList();
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
                state.LastTouchedUtc = DateTime.UtcNow;
                Cleanup(state, now);
                // Bot Web must mirror the complete retained desktop timeline, not
                // silently truncate a buyer conversation to the latest 100 messages.
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
                if (state.RemoteHistoryInFlight) return;
                // A failed first fetch retries after five seconds. Once the API has
                // returned a valid message array, live events maintain the timeline and
                // a slower 30-second reconciliation is sufficient.
                var minimumInterval = state.RemoteHistoryLoaded
                    ? TimeSpan.FromSeconds(30)
                    : TimeSpan.FromSeconds(5);
                if (DateTime.Now - state.LastRemoteRefresh < minimumInterval) return;
                state.LastRemoteRefresh = DateTime.Now;
                state.RemoteHistoryInFlight = true;
            }

            try
            {
                var qn = FindQn(shop, seller);
                if (qn == null || qn.CDP == null) return;
                var response = qn.CDP.InvokeForHistory<JObject>("im.singlemsg.GetRemoteHisMsg", new
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
                if (messages == null)
                {
                    Log.Info("千牛镜像历史读取未返回有效消息列表: shop="
                        + (shop == null ? "unknown" : shop.ShopKey));
                    return;
                }

                var merged = 0;
                foreach (var item in messages.Where(m => m != null).OrderBy(GetSortValue))
                {
                    var recordedKey = RecordMessage(state, seller, buyer, item, ExtractMessageText(item), true);
                    if (!string.IsNullOrWhiteSpace(recordedKey)) merged++;
                }
                lock (state.Sync) state.RemoteHistoryLoaded = true;
                Log.Info("千牛镜像历史同步完成: shop="
                    + (shop == null ? "unknown" : shop.ShopKey)
                    + ", fetched=" + messages.Count
                    + ", merged=" + merged
                    + ", filtered=" + Math.Max(0, messages.Count - merged));
            }
            catch (Exception ex)
            {
                Log.Info("读取本店会话上下文失败: shop=" + (shop == null ? "unknown" : shop.ShopKey)
                    + ", seller=" + seller + ", buyer=" + buyer + ", error=" + SafeLog(ex.Message));
            }
            finally
            {
                lock (state.Sync) state.RemoteHistoryInFlight = false;
            }
        }

        private static QN FindQn(ShopContext shop, string seller)
        {
            if (shop == null) return null;
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return null;

            // QNSet can temporarily contain the previous wrapper while Qianniu
            // replaces an injected WebSocket. Select through the shared ShopKey
            // router so an invalidated/stale CDP wrapper cannot win by list order.
            var qn = ShopContextLocator.FindConnectedQnByShopKey(shop.ShopKey);
            if (qn == null || qn.Seller == null || qn.CDP == null) return null;
            if (!string.Equals((qn.Seller.Nick ?? string.Empty).Trim(),
                seller, StringComparison.Ordinal)) return null;

            try
            {
                var resolved = ShopIdentityResolver.Resolve(qn.Seller);
                return string.Equals(resolved.ShopKey, shop.ShopKey, StringComparison.Ordinal)
                    ? qn
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static string RecordMessage(
            TimelineState state,
            string seller,
            string buyer,
            QNChatMessage message,
            string text,
            bool fromHistory)
        {
            if (message == null) return string.Empty;
            var timestamp = GetMessageTime(message);
            if (IsWithdrawalNotice(message, text))
            {
                if (IsSellerWithdrawal(message, text, seller)) MarkLastSellerTurnWithdrawn(state, timestamp);
                return string.Empty;
            }
            if (IsPlatformSystemTip(message, text)) return string.Empty;

            var from = message.fromid == null ? string.Empty : (message.fromid.nick ?? string.Empty).Trim();
            var to = message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
            // GetRemoteHisMsg and live events do not always serialize the same nick
            // form. Record aliases in the current ShopKey scope, then classify only
            // exact seller↔buyer pairs or their already-observed buyer aliases.
            BuyerIdentityAliasService.ObserveMessage(seller, message);
            var fromIsBuyer = string.Equals(from, buyer, StringComparison.Ordinal)
                || BuyerIdentityAliasService.AreEquivalent(seller, from, buyer);
            var toIsBuyer = string.Equals(to, buyer, StringComparison.Ordinal)
                || BuyerIdentityAliasService.AreEquivalent(seller, to, buyer);
            string role;
            if (string.Equals(from, seller, StringComparison.Ordinal) && toIsBuyer)
                role = "assistant";
            else if (fromIsBuyer && string.Equals(to, seller, StringComparison.Ordinal))
                role = "user";
            else return string.Empty;

            var cleanText = IncomingMessageSafety.GetDisplayText(message, text);
            if (string.IsNullOrWhiteSpace(cleanText)
                || string.Equals(cleanText, "[空白或未知消息]", StringComparison.Ordinal))
            {
                return string.Empty;
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

                    MergeObservedIdentity(exact, aliases, preciseTime, timestamp, fromHistory);
                    exact.HasImage = exact.HasImage || IsImageMessage(message);
                    return exact.MessageKey ?? string.Empty;
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

                    MergeObservedIdentity(precisePeers[0], aliases, preciseTime, timestamp, fromHistory);
                    precisePeers[0].HasImage = precisePeers[0].HasImage || IsImageMessage(message);
                    Log.Info("千牛镜像原始时间戳归并回显: origin="
                        + (fromHistory ? "history" : "live") + ", role=" + role
                        + ", idHash=" + StableHash(messageKey).ToString(CultureInfo.InvariantCulture));
                    return precisePeers[0].MessageKey ?? string.Empty;
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

                        MergeObservedIdentity(opposite[0], aliases, preciseTime, timestamp, fromHistory);
                        opposite[0].HasImage = opposite[0].HasImage || IsImageMessage(message);
                        Log.Info("千牛镜像单一跨源回显归并: origin="
                            + (fromHistory ? "history" : "live") + ", role=" + role
                            + ", idHash=" + StableHash(messageKey).ToString(CultureInfo.InvariantCulture));
                        return opposite[0].MessageKey ?? string.Empty;
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
                    HasImage = IsImageMessage(message),
                    Withdrawn = false
                });
                Cleanup(state, DateTime.Now);
                return messageKey;
            }
        }

        private static bool IsImageMessage(QNChatMessage message)
        {
            if (message == null) return false;
            try
            {
                if (message.HasImage) return true;
                return !string.IsNullOrWhiteSpace(VisionImageResolver.ExtractUrl(message));
            }
            catch { return false; }
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
            ConversationContextTurn turn,
            string[] aliases,
            long preciseTime,
            DateTime timestamp,
            bool fromHistory)
        {
            // Keep the first canonical MessageKey stable: Bot source rows,
            // context and mirror menus use it as the identity of that turn.
            turn.NativeAliases = (turn.NativeAliases ?? new string[0])
                .Concat(aliases ?? new string[0]).Distinct(StringComparer.Ordinal).ToArray();
            if (turn.PreciseTimeTicks == 0) turn.PreciseTimeTicks = preciseTime;
            // Live transports occasionally omit their timestamp. A later history
            // observation of the same native message must enrich it so the session
            // window can include it without admitting unscoped MinValue turns.
            if (turn.Timestamp == DateTime.MinValue && timestamp != DateTime.MinValue)
                turn.Timestamp = timestamp;
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
            // Do not silently truncate a live session to 100 turns. The 7-day
            // age window and bounded inactive-state sweep provide the retention policy.
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
            // Session finalization has already entered the exact persisted ShopKey
            // scope. Never resolve by seller nickname again: multiple shops may
            // legitimately share one nickname and must remain distinct.
            var shop = ShopSettingsScope.Current
                ?? ShopContextLocator.ResolveRuntimeBySellerNick(seller);
            if (shop == null) return null;
            using (ShopSettingsScope.Enter(shop))
            {
                TimelineState state;
                return States.TryGetValue(Key(seller, buyer), out state) ? (object)state : null;
            }
        }

        // Rebuild the complete requested session window from the remote history API.
        // A single 100-message page is not evidence of a complete conversation.
        // On restart GetState recreates the in-memory timeline before paging, so the
        // persisted pending-session report can safely reconstruct its transcript.
        internal static async Task<ConversationHistoryRefreshResult> RefreshLearningHistoryCompleteAsync(
            string shopKey,
            string seller,
            string buyer,
            string ccode,
            DateTime from,
            DateTime to,
            CancellationToken cancellationToken)
        {
            var result = new ConversationHistoryRefreshResult
            {
                IsComplete = false,
                StopReason = string.Empty,
                ErrorCode = string.Empty
            };
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            ccode = (ccode ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0 || buyer.Length == 0 || ccode.Length == 0)
            {
                result.ErrorCode = "missing_conversation_scope";
                return result;
            }
            if (from == DateTime.MinValue || to == DateTime.MinValue || to < from)
            {
                result.ErrorCode = "invalid_session_window";
                return result;
            }

            ShopContext shop;
            try
            {
                shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
            }
            catch
            {
                result.ErrorCode = "shop_scope_unavailable";
                return result;
            }

            if (shop == null
                || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal))
            {
                result.ErrorCode = "shop_scope_mismatch";
                return result;
            }

            try
            {
                using (ShopSettingsScope.Enter(shop))
                {
                    // Recreate the canonical ShopKey#Seller#Buyer state after a process
                    // restart instead of requiring an in-memory state to already exist.
                    var state = GetState(seller, buyer);
                    var qn = FindQn(shop, seller);
                    if (qn == null || qn.CDP == null)
                    {
                        result.ErrorCode = "shop_cdp_unavailable";
                        return result;
                    }

                    var cursorId = "-1";
                    var cursorTime = "-1";
                    var previousCursor = string.Empty;
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    var countedTimelineKeys = new HashSet<string>(StringComparer.Ordinal);
                    var reachedSessionStart = false;

                    for (var page = 0; page < LearningHistoryMaxPages; page++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        JObject response = null;
                        for (var retry = 0; retry < LearningHistoryPageRetries; retry++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                response = await qn.CDP.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg", new
                                {
                                    cid = new { ccode = ccode, type = 1 },
                                    count = LearningHistoryPageSize,
                                    gohistory = 1,
                                    msgid = cursorId,
                                    msgtime = cursorTime
                                }).ConfigureAwait(false);
                                if (response != null) break;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch
                            {
                                if (retry == LearningHistoryPageRetries - 1)
                                {
                                    result.ErrorCode = "history_page_request_failed";
                                    return result;
                                }
                            }
                            await Task.Delay(300 * (retry + 1), cancellationToken).ConfigureAwait(false);
                        }

                        if (response == null)
                        {
                            result.ErrorCode = "history_page_unavailable";
                            return result;
                        }

                        result.PagesFetched++;
                        var resultToken = response["result"] ?? response;
                        var resultObject = resultToken as JObject;
                        var messagesToken = resultObject == null
                            ? resultToken as JArray
                            : (resultObject["msgs"] ?? resultObject["messages"] ?? resultObject["list"]);
                        if (messagesToken == null || messagesToken.Type == JTokenType.Null)
                        {
                            result.ErrorCode = "history_page_missing_messages";
                            result.StopReason = "missing_messages";
                            break;
                        }
                        var messages = messagesToken.ToObject<List<QNChatMessage>>();
                        if (messages == null)
                        {
                            result.ErrorCode = "history_page_invalid_messages";
                            result.StopReason = "invalid_messages";
                            break;
                        }
                        result.MessagesFetched += messages.Count;
                        int hasMore = 0;
                        var hasMoreKnown = resultObject != null
                            && TryReadInt(resultObject["hasMore"], out hasMore);
                        if (messages.Count == 0)
                        {
                            if (!hasMoreKnown)
                            {
                                // An empty page without an explicit server end is not proof
                                // that the cursor reached the end; transient empty pages must
                                // never make a partial transcript pass the learning gate.
                                result.ErrorCode = "history_empty_page_missing_has_more";
                                result.StopReason = "empty_page_missing_has_more";
                                break;
                            }
                            if (hasMore > 0)
                            {
                                result.ErrorCode = "history_empty_page_with_more";
                                result.StopReason = "empty_page_with_more";
                                break;
                            }
                            result.IsComplete = true;
                            result.StopReason = "server_end";
                            break;
                        }
                        if (messages.Any(x => x == null))
                        {
                            result.ErrorCode = "history_page_contains_null_messages";
                            result.StopReason = "invalid_messages";
                            break;
                        }

                        foreach (var item in messages.OrderBy(GetSortValue))
                        {
                            var text = ExtractMessageText(item);
                            var messageKey = IncomingMessageSafety.BuildMessageKey(item, text);
                            if (!string.IsNullOrWhiteSpace(messageKey) && !seen.Add(messageKey)) continue;

                            var messageTime = GetMessageTime(item);
                            if (messageTime == DateTime.MinValue)
                            {
                                result.UnknownTimestampCount++;
                                continue;
                            }
                            if (messageTime < from || messageTime > to) continue;

                            var canonicalTurnKey = RecordMessage(state, seller, buyer, item, text, true);
                            if (!string.IsNullOrWhiteSpace(canonicalTurnKey)
                                && countedTimelineKeys.Add(canonicalTurnKey))
                                result.MessagesInWindow++;
                        }

                        var oldest = messages.Where(x => x != null).OrderBy(GetSortValue).FirstOrDefault();
                        if (oldest == null)
                        {
                            // A non-empty but unparseable page is not proof of server end.
                            result.ErrorCode = "history_page_no_valid_messages";
                            result.StopReason = "no_valid_messages";
                            break;
                        }

                        var oldestTime = GetMessageTime(oldest);
                        if (oldestTime != DateTime.MinValue && oldestTime < from)
                        {
                            reachedSessionStart = true;
                            result.IsComplete = true;
                            result.StopReason = "session_start_reached";
                            break;
                        }

                        // An explicit server end is authoritative even if the last
                        // page repeats its cursor; cursor-stall is only a failure while
                        // the server may still have more pages.
                        if (hasMoreKnown && hasMore == 0)
                        {
                            result.IsComplete = true;
                            result.StopReason = "server_end";
                            break;
                        }

                        cursorId = oldest.mcode == null || string.IsNullOrWhiteSpace(oldest.mcode.messageId)
                            ? (oldest.ext == null ? "-1" : oldest.ext.ww_msgid.ToString(CultureInfo.InvariantCulture))
                            : oldest.mcode.messageId;
                        cursorTime = !string.IsNullOrWhiteSpace(oldest.sortTimeMicrosecond)
                            ? oldest.sortTimeMicrosecond
                            : (!string.IsNullOrWhiteSpace(oldest.sendTime) ? oldest.sendTime : "-1");
                        var cursor = cursorId + "|" + cursorTime;
                        if (cursor == previousCursor)
                        {
                            result.ErrorCode = "history_cursor_stalled";
                            break;
                        }
                        previousCursor = cursor;
                    }

                    if (!result.IsComplete && string.IsNullOrWhiteSpace(result.ErrorCode))
                        result.ErrorCode = "history_page_limit_reached";
                    if (result.IsComplete && result.MessagesFetched == 0)
                    {
                        result.IsComplete = false;
                        result.ErrorCode = "history_empty";
                    }
                    if (result.UnknownTimestampCount > 0)
                    {
                        result.IsComplete = false;
                        result.ErrorCode = "history_contains_unknown_timestamps";
                    }
                    if (result.IsComplete && !reachedSessionStart && result.StopReason.Length == 0)
                        result.StopReason = "server_end";
                }
            }
            catch (OperationCanceledException)
            {
                result.IsComplete = false;
                result.ErrorCode = "history_refresh_cancelled";
            }
            catch (Exception ex)
            {
                result.IsComplete = false;
                result.ErrorCode = "history_refresh_failed";
                Log.ErrorWithMaxCount("接待学习完整历史分页失败: " + SafeLog(ex.Message), 10);
            }
            return result;
        }

        private static bool TryReadInt(JToken token, out int value)
        {
            value = 0;
            // Missing or malformed pagination metadata is unknown, not hasMore=false.
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type == JTokenType.Boolean)
            {
                value = token.Value<bool>() ? 1 : 0;
                return true;
            }

            var text = token.ToString().Trim();
            bool boolean;
            if (bool.TryParse(text, out boolean))
            {
                value = boolean ? 1 : 0;
                return true;
            }

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                // Negative pagination flags are malformed/unknown, not an explicit
                // server-end signal. Treating -1 as zero can truncate a non-empty page.
                if (value < 0)
                {
                    value = 0;
                    return false;
                }
                return true;
            }

            double numeric;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out numeric))
            {
                if (double.IsNaN(numeric) || double.IsInfinity(numeric) || numeric < 0)
                {
                    value = 0;
                    return false;
                }
                value = numeric > 0 ? 1 : 0;
                return true;
            }
            return false;
        }

        private static TimelineState GetState(string seller, string buyer)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            var shopKey = ScopeKey(seller);
            var key = shopKey + "#" + seller + "#" + buyer;
            var state = States.GetOrAdd(key, _ => new TimelineState
            {
                ShopKey = shopKey,
                Seller = seller,
                Buyer = buyer
            });
            lock (state.Sync)
            {
                if (string.IsNullOrWhiteSpace(state.ShopKey)) state.ShopKey = shopKey;
                if (string.IsNullOrWhiteSpace(state.Seller)) state.Seller = seller;
                if (string.IsNullOrWhiteSpace(state.Buyer)) state.Buyer = buyer;
                state.LastTouchedUtc = DateTime.UtcNow;
            }
            SweepInactiveTimelinesIfDue();
            return state;
        }

        private static void SweepInactiveTimelinesIfDue()
        {
            var now = DateTime.UtcNow;
            var ticks = Interlocked.Read(ref _lastTimelineSweepUtcTicks);
            if (ticks != 0 && now.Ticks - ticks < TimelineSweepInterval.Ticks) return;
            if (Interlocked.CompareExchange(ref _lastTimelineSweepUtcTicks, now.Ticks, ticks) != ticks)
                return;

            var removed = 0;
            var candidates = States.ToArray()
                .OrderBy(pair => pair.Value == null ? DateTime.MinValue : pair.Value.LastTouchedUtc);
            foreach (var pair in candidates)
            {
                var state = pair.Value;
                if (state == null) continue;
                lock (state.Sync)
                {
                    var idle = now - state.LastTouchedUtc;
                    // The soft limit never evicts a buyer needed by the 72-hour
                    // message-context window. A live preset cannot be dropped.
                    if (idle < TimelineMaxIdle
                        && (States.Count <= SoftTimelineStateLimit || idle < TimelineContextWindow))
                        continue;
                    if (state.PendingPreset != null && state.PendingPreset.ExpiresAt >= DateTime.Now)
                        continue;
                    TimelineState current;
                    if (!States.TryGetValue(pair.Key, out current) || !ReferenceEquals(current, state))
                        continue;
                    TimelineState ignored;
                    if (States.TryRemove(pair.Key, out ignored)) removed++;
                }
            }
            if (removed > 0)
                Log.Info("会话上下文缓存清理: removed=" + removed + ", retained=" + States.Count);
        }

        private static string Key(string seller, string buyer, string shopKey = null)
        {
            return ScopeKey(seller, shopKey) + "#" + (seller ?? string.Empty).Trim()
                + "#" + (buyer ?? string.Empty).Trim();
        }

        private static string ScopeKey(string seller, string shopKey = null)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length > 0)
            {
                var exact = ResolveShop(seller, shopKey);
                return exact == null
                    ? "unresolved:" + Guid.NewGuid().ToString("N")
                    : exact.ShopKey;
            }
            var current = ShopSettingsScope.Current;
            if (current != null)
                return string.Equals((current.SellerNick ?? string.Empty).Trim(),
                    (seller ?? string.Empty).Trim(), StringComparison.Ordinal)
                    ? current.ShopKey
                    : "unresolved:" + Guid.NewGuid().ToString("N");
            var resolved = ResolveShop(seller);
            // A missing or ambiguous seller nickname must never fall back to a shared
            // seller-only key: two shops with the same nickname could then read each
            // other's buyer timeline. Without a proven ShopKey, isolate this access.
            return resolved == null
                ? "unresolved:" + Guid.NewGuid().ToString("N")
                : resolved.ShopKey;
        }

        private static ShopContext ResolveShop(string seller, string shopKey = null)
        {
            seller = (seller ?? string.Empty).Trim();
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length > 0)
            {
                try
                {
                    var exact = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
                    return string.Equals((exact.SellerNick ?? string.Empty).Trim(),
                        seller, StringComparison.Ordinal) ? exact : null;
                }
                catch { return null; }
            }
            var current = ShopSettingsScope.Current;
            if (current != null)
            {
                return string.Equals((current.SellerNick ?? string.Empty).Trim(),
                    seller, StringComparison.Ordinal) ? current : null;
            }
            if (seller.Length == 0) return null;
            try { return ShopContextLocator.ResolveRuntimeBySellerNick(seller); }
            catch (AmbiguousShopIdentityException) { throw; }
            catch { return null; }
        }

        private static string GetSellerNick(QNChatMessage message, string shopKey = null)
        {
            if (message == null) return string.Empty;
            var loginNick = message.loginid == null ? string.Empty : (message.loginid.nick ?? string.Empty).Trim();
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length > 0)
            {
                try
                {
                    var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
                    if (shop == null || !DirectOrderIdentityResolver.IsMessageFromShop(message, shop))
                        return string.Empty;
                    return shop.SellerNick.Trim();
                }
                catch { return string.Empty; }
            }
            var current = ShopSettingsScope.Current;
            if (current != null && !string.IsNullOrWhiteSpace(current.SellerNick))
            {
                if (!DirectOrderIdentityResolver.IsMessageFromShop(message, current))
                    return string.Empty;
                return current.SellerNick.Trim();
            }
            if (loginNick.Length > 0)
            {
                var fromIsSeller = DirectOrderIdentityResolver.IdentityEquals(
                    message.fromid == null ? string.Empty : message.fromid.nick, loginNick);
                var toIsSeller = DirectOrderIdentityResolver.IdentityEquals(
                    message.toid == null ? string.Empty : message.toid.nick, loginNick);
                return fromIsSeller == toIsSeller ? string.Empty : loginNick;
            }
            return message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
        }

        private static string GetBuyerNick(QNChatMessage message, string seller)
        {
            if (message == null) return string.Empty;
            var from = message.fromid == null ? string.Empty : (message.fromid.nick ?? string.Empty).Trim();
            var to = message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
            var fromIsSeller = DirectOrderIdentityResolver.IdentityEquals(from, seller);
            var toIsSeller = DirectOrderIdentityResolver.IdentityEquals(to, seller);
            if (fromIsSeller == toIsSeller) return string.Empty;
            return fromIsSeller ? to : from;
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
                SeenHistory = source.SeenHistory,
                HasImage = source.HasImage
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