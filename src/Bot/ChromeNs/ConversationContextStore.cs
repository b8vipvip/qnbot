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
    }

    internal static class ConversationContextStore
    {
        private sealed class PendingPresetReply
        {
            public string QuestionKey;
            public string Reply;
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

        private static readonly string[] ProductLinkReplies =
        {
            "我看到您发来的商品链接了，想咨询哪方面呢？",
            "商品链接已收到，您想了解规格、使用还是售后呢？",
            "我看到了这款商品，请问您主要想确认哪项信息？",
            "这款商品链接我收到了，您想重点了解什么呢？"
        };

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
                RecordMessage(state, seller, buyer, message, messageText);
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

        public static void RegisterProductLinkReply(QNChatMessage message, string messageText)
        {
            if (message == null) return;
            var seller = GetSellerNick(message);
            var buyer = GetBuyerNick(message, seller);
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;

            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);
            var seed = StableHash(ScopeKey(seller) + "|" + messageKey + "|" + messageText);
            var reply = ProductLinkReplies[Math.Abs(seed % ProductLinkReplies.Length)];
            var state = GetState(seller, buyer);
            lock (state.Sync)
            {
                state.PendingPreset = new PendingPresetReply
                {
                    QuestionKey = NormalizeText(messageText),
                    Reply = reply,
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
                reply = pending.Reply ?? string.Empty;
                state.PendingPreset = null;
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
                    .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Text))
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
                    RecordMessage(state, seller, buyer, item, ExtractMessageText(item));
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
            string text)
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
            lock (state.Sync)
            {
                if (!string.IsNullOrWhiteSpace(messageKey)
                    && state.Turns.Any(t => t.MessageKey == messageKey)) return;
                state.Turns.Add(new ConversationContextTurn
                {
                    Role = role,
                    Text = cleanText,
                    Timestamp = timestamp,
                    MessageKey = messageKey,
                    Withdrawn = false
                });
                Cleanup(state, DateTime.Now);
            }
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
                Withdrawn = source.Withdrawn
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