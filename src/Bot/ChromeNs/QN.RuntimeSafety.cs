using Bot.ChatRecord;
using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Bot.ChromeNs
{
    internal sealed class FirstInquiryFixedReplySettings
    {
        public bool Enabled { get; set; }
        public string Answer { get; set; }
        public int DelaySeconds { get; set; }
    }

    internal static class FirstInquiryFixedReplyService
    {
        internal const int SessionResetMinutes = 30;
        internal const string DefaultAnswer = "在的，亲！";
        private const int PendingReplySeconds = 45;
        private const int SameBurstHistoryGraceSeconds = 8;
        private const string SettingsScope = "feature";
        private const string EnabledKey = "FirstInquiryFixedReplyEnabled";
        private const string AnswerKey = "FirstInquiryFixedReplyAnswer";
        private const string DelayKey = "FirstInquiryFixedReplyDelaySeconds";

        private static readonly HashSet<string> GreetingOnlyTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "你好", "您好", "哈喽", "哈罗", "嗨", "hi", "hello", "hey",
            "在吗", "在不在", "有人吗", "客服在吗", "客服", "亲", "亲亲",
            "早", "早上好", "上午好", "中午好", "下午好", "晚上好",
            "你好在吗", "您好在吗", "亲在吗", "亲亲在吗", "哈喽在吗"
        };

        private static readonly HashSet<string> MeaninglessOnlyTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "嗯", "恩", "哦", "噢", "喔", "啊", "额", "呃", "哈", "呵", "哈哈", "呵呵", "嘿嘿",
            "好", "好的", "好吧", "行", "行吧", "可以", "收到", "知道了", "明白了",
            "谢谢", "谢谢你", "谢谢亲", "谢了", "辛苦了", "ok", "okay", "okey",
            "1", "11", "111", "666", "测试", "test", "再见", "拜拜", "晚安"
        };

        private sealed class PendingReply
        {
            public string Answer { get; set; }
            public DateTime ExpiresAt { get; set; }
            public bool InFlight { get; set; }
        }

        private static readonly ConcurrentDictionary<string, PendingReply> PendingReplies =
            new ConcurrentDictionary<string, PendingReply>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> TriggeredAt =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> KnownNonProblemMessageKeys =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        public static FirstInquiryFixedReplySettings Load(string seller, string shopKey = null)
        {
            return RunInShopScope(seller, LoadCurrentScope, shopKey);
        }

        public static int ClampDelaySeconds(int seconds)
        {
            return Math.Max(0, Math.Min(300, seconds));
        }

        public static int GetDelaySeconds(string seller, string shopKey = null)
        {
            var settings = Load(seller, shopKey);
            return ClampDelaySeconds(settings == null ? 0 : settings.DelaySeconds);
        }

        public static void Save(string seller, bool enabled, string answer)
        {
            Save(seller, enabled, answer, 0);
        }

        public static void Save(string seller, bool enabled, string answer, int delaySeconds, string shopKey = null)
        {
            var expectedEnabled = enabled ? "true" : "false";
            var expectedAnswer = (answer ?? string.Empty).Trim();
            var expectedDelay = ClampDelaySeconds(delaySeconds).ToString();
            var saved = RunInShopScope(seller, delegate
            {
                BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(EnabledKey, SettingsScope, expectedEnabled);
                BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(AnswerKey, SettingsScope, expectedAnswer);
                BotLib.Db.Sqlite.PersistentParams.TrySaveParam2Key(DelayKey, SettingsScope, expectedDelay);
                // PersistentParams write helpers are best-effort/void. Read back all three
                // values so cloud sync cannot acknowledge a delay that failed to persist.
                return string.Equals(BotLib.Db.Sqlite.PersistentParams.GetParam2Key(EnabledKey, SettingsScope, "true"), expectedEnabled, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(BotLib.Db.Sqlite.PersistentParams.GetParam2Key(AnswerKey, SettingsScope, DefaultAnswer), expectedAnswer, StringComparison.Ordinal)
                    && string.Equals(BotLib.Db.Sqlite.PersistentParams.GetParam2Key(DelayKey, SettingsScope, "0"), expectedDelay, StringComparison.Ordinal);
            }, shopKey);
            if (!saved)
                throw new InvalidOperationException("无法确认首条咨询固定回复配置所属店铺，已阻止保存到全局设置。");
        }

        public static bool TryPrepare(string seller, string buyer, string currentQuestion, IncomingMessageDecision decision, out string answer)
        {
            return TryPrepare(seller, buyer, null, currentQuestion, decision, out answer);
        }

        public static bool TryPrepare(
            string seller,
            string buyer,
            QNChatMessage message,
            string currentQuestion,
            IncomingMessageDecision decision,
            out string answer,
            string shopKey = null)
        {
            answer = string.Empty;
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return false;
            if (!IsActualProblemMessage(message, currentQuestion, decision))
            {
                RememberKnownNonProblemMessage(seller, buyer, message, currentQuestion, shopKey);
                return false;
            }
            if (ShouldSuppressForOffHours(seller, buyer, shopKey)) return false;

            var resolved = RunInShopScope(seller, delegate
            {
                var now = DateTime.Now;
                var key = RuntimeKey(seller, buyer);
                if (key.Length == 0) return string.Empty;
                CleanupRuntimeState(key, now);
                DateTime triggered;
                if (TriggeredAt.TryGetValue(key, out triggered) && triggered >= now.AddMinutes(-SessionResetMinutes)) return string.Empty;

                PendingReply existing;
                if (PendingReplies.TryGetValue(key, out existing) && existing != null
                    && existing.ExpiresAt >= now && !string.IsNullOrWhiteSpace(existing.Answer)) return existing.Answer;

                var candidate = ResolveFreshCurrentScope(seller, buyer, currentQuestion, now);
                if (string.IsNullOrWhiteSpace(candidate)) return string.Empty;
                var pending = new PendingReply { Answer = candidate, ExpiresAt = now.AddSeconds(PendingReplySeconds), InFlight = false };
                PendingReplies.AddOrUpdate(key, pending, (ignored, old) => old != null && old.ExpiresAt >= now ? old : pending);
                PendingReply stored;
                return PendingReplies.TryGetValue(key, out stored) && stored != null ? (stored.Answer ?? string.Empty) : candidate;
            }, shopKey);
            answer = (resolved ?? string.Empty).Trim();
            var prepared = !string.IsNullOrWhiteSpace(answer);
            if (prepared)
            {
                Log.Info("首条咨询固定回复已预留: seller=" + seller + ", buyer=" + buyer
                    + ", trigger=" + (currentQuestion ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim()
                    + ", actualProblem=true");
            }
            return prepared;
        }

        public static bool TryResolve(string seller, string buyer, string currentQuestion, out string answer, string shopKey = null)
        {
            answer = string.Empty;
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer) || string.IsNullOrWhiteSpace(currentQuestion)) return false;
            if (ShouldSuppressForOffHours(seller, buyer, shopKey)) return false;

            var resolved = RunInShopScope(seller, delegate
            {
                var now = DateTime.Now;
                var key = RuntimeKey(seller, buyer);
                if (key.Length == 0) return string.Empty;
                CleanupRuntimeState(key, now);
                DateTime triggered;
                if (TriggeredAt.TryGetValue(key, out triggered) && triggered >= now.AddMinutes(-SessionResetMinutes)) return string.Empty;

                // A first-inquiry reservation must be created while the full incoming message is
                // available to TryPrepare. Do not recreate one here from display text alone: doing
                // so loses the system/product-card metadata and can turn a platform card into a
                // fake buyer consultation.
                PendingReply pending;
                if (!PendingReplies.TryGetValue(key, out pending) || pending == null
                    || pending.ExpiresAt < now || string.IsNullOrWhiteSpace(pending.Answer))
                {
                    return string.Empty;
                }
                if (pending.InFlight) return string.Empty;
                pending.InFlight = true;
                pending.ExpiresAt = now.AddSeconds(PendingReplySeconds);
                return pending.Answer;
            }, shopKey);
            answer = (resolved ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(answer);
        }

        public static void MarkDelivered(string seller, string buyer, string shopKey = null)
        {
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;
            RunInShopScope(seller, delegate
            {
                var key = RuntimeKey(seller, buyer);
                if (key.Length == 0) return false;
                PendingReply ignored;
                PendingReplies.TryRemove(key, out ignored);
                TriggeredAt[key] = DateTime.Now;
                Log.Info("首条咨询固定回复已确认送达，开始30分钟会话去重: seller=" + seller + ", buyer=" + buyer);
                return true;
            }, shopKey);
        }

        public static void ReleaseReservation(string seller, string buyer, string reason, string shopKey = null)
        {
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return;
            RunInShopScope(seller, delegate
            {
                var key = RuntimeKey(seller, buyer);
                if (key.Length == 0) return false;
                PendingReply ignored;
                if (PendingReplies.TryRemove(key, out ignored))
                    Log.Info("首条咨询固定回复发送未完成，已释放首条资格: seller=" + seller + ", buyer=" + buyer + ", reason=" + (reason ?? string.Empty));
                return true;
            }, shopKey);
        }

        public static bool HasPending(string seller, string buyer, string shopKey = null)
        {
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer)) return false;
            return RunInShopScope(seller, delegate
            {
                var key = RuntimeKey(seller, buyer);
                if (key.Length == 0) return false;
                PendingReply pending;
                if (!PendingReplies.TryGetValue(key, out pending) || pending == null) return false;
                if (pending.ExpiresAt >= DateTime.Now && !string.IsNullOrWhiteSpace(pending.Answer)) return true;
                PendingReply ignored;
                PendingReplies.TryRemove(key, out ignored);
                return false;
            }, shopKey);
        }

        internal static bool IsActualProblemMessage(
            QNChatMessage message,
            string currentQuestion,
            IncomingMessageDecision decision)
        {
            if (decision != null)
            {
                if (!IsEligibleTrigger(decision)) return false;
                var isImage = string.Equals(decision.MessageLabel, "[图片]", StringComparison.Ordinal);
                if (!decision.ShouldCallAi && !isImage) return false;
            }

            var rawText = currentQuestion ?? string.Empty;
            if (message != null)
            {
                var messageText = rawText;
                if (ConversationContextStore.IsPlatformSystemTip(message, messageText)) return false;
                if (ConversationContextStore.IsProductLink(message, messageText)) return false;
                if (ConversationContextStore.IsWithdrawalNotice(message, messageText)) return false;
            }

            return IsActualProblemText(rawText);
        }

        internal static bool IsActualProblemText(string value)
        {
            var compact = Compact(value);
            if (string.IsNullOrWhiteSpace(compact)) return false;

            var lowered = compact.ToLowerInvariant();
            if (string.Equals(lowered, "[图片]", StringComparison.Ordinal)) return true;
            if ((lowered.StartsWith("[", StringComparison.Ordinal) && lowered.EndsWith("]", StringComparison.Ordinal))
                || string.Equals(lowered, "[商品链接]", StringComparison.Ordinal)
                || string.Equals(lowered, "[淘宝系统提示]", StringComparison.Ordinal)
                || string.Equals(lowered, "[撤回提示]", StringComparison.Ordinal)
                || string.Equals(lowered, "[空白或未知消息]", StringComparison.Ordinal))
            {
                return false;
            }

            if (LooksLikeSystemOrProductMetadata(lowered)) return false;

            var semantic = new string(lowered.Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrWhiteSpace(semantic)) return false;
            if (GreetingOnlyTexts.Contains(semantic)) return false;
            if (MeaninglessOnlyTexts.Contains(semantic)) return false;
            if (semantic.Length <= 8 && semantic.All(IsFillerCharacter)) return false;
            return true;
        }

        private static bool IsFillerCharacter(char value)
        {
            return "嗯恩哦噢喔啊额呃哈哈呵嘿".IndexOf(value) >= 0;
        }

        private static bool LooksLikeSystemOrProductMetadata(string compactLower)
        {
            if (string.IsNullOrWhiteSpace(compactLower)) return true;
            if (compactLower.StartsWith("当前用户来自", StringComparison.Ordinal)
                || compactLower.StartsWith("该用户来自", StringComparison.Ordinal)
                || compactLower.StartsWith("买家正在浏览", StringComparison.Ordinal)
                || compactLower.StartsWith("买家从商品详情页进入", StringComparison.Ordinal)
                || compactLower.StartsWith("平台提示", StringComparison.Ordinal)
                || compactLower.StartsWith("系统提示", StringComparison.Ordinal))
            {
                return true;
            }

            return compactLower.IndexOf("http://", StringComparison.Ordinal) >= 0
                || compactLower.IndexOf("https://", StringComparison.Ordinal) >= 0
                || compactLower.IndexOf("item.taobao.com", StringComparison.Ordinal) >= 0
                || compactLower.IndexOf("detail.tmall.com", StringComparison.Ordinal) >= 0
                || compactLower.IndexOf("h5.m.taobao.com", StringComparison.Ordinal) >= 0
                || compactLower.IndexOf("m.tb.cn/", StringComparison.Ordinal) >= 0;
        }

        private static void RememberKnownNonProblemMessage(
            string seller,
            string buyer,
            QNChatMessage message,
            string displayText,
            string shopKey = null)
        {
            if (message == null) return;
            var rawKey = IncomingMessageSafety.BuildMessageKey(message, displayText);
            if (string.IsNullOrWhiteSpace(rawKey)) return;
            RunInShopScope(seller, delegate
            {
                var scopedKey = KnownNonProblemKey(seller, buyer, rawKey);
                if (scopedKey.Length == 0) return false;
                var now = DateTime.Now;
                KnownNonProblemMessageKeys[scopedKey] = now.AddMinutes(SessionResetMinutes);

                if (KnownNonProblemMessageKeys.Count > 1024)
                {
                    foreach (var expired in KnownNonProblemMessageKeys.Where(x => x.Value < now).Take(128).ToList())
                    {
                        DateTime ignored;
                        KnownNonProblemMessageKeys.TryRemove(expired.Key, out ignored);
                    }
                }
                return true;
            }, shopKey);
        }

        private static bool IsKnownNonProblemHistoryTurn(
            string seller,
            string buyer,
            string storedMessageKey,
            DateTime now)
        {
            if (string.IsNullOrWhiteSpace(storedMessageKey)) return false;
            var separator = storedMessageKey.IndexOf('|');
            var rawKey = separator >= 0 && separator + 1 < storedMessageKey.Length
                ? storedMessageKey.Substring(separator + 1)
                : storedMessageKey;
            var key = KnownNonProblemKey(seller, buyer, rawKey);
            if (key.Length == 0) return false;
            DateTime expiresAt;
            if (!KnownNonProblemMessageKeys.TryGetValue(key, out expiresAt)) return false;
            if (expiresAt >= now) return true;
            DateTime ignored;
            KnownNonProblemMessageKeys.TryRemove(key, out ignored);
            return false;
        }

        private static string KnownNonProblemKey(string seller, string buyer, string rawMessageKey)
        {
            var runtimeKey = RuntimeKey(seller, buyer);
            return runtimeKey.Length == 0
                ? string.Empty
                : runtimeKey + "|" + (rawMessageKey ?? string.Empty).Trim();
        }

        private static bool ShouldSuppressForOffHours(string seller, string buyer, string shopKey = null)
        {
            return RunInShopScope(seller, delegate
            {
                var cfg = BotFeatureStore.GetAutoReplyRules();
                if (cfg == null || !cfg.EnableWorkHours) return false;

                TimeSpan start;
                TimeSpan end;
                if (!TryParseClock(cfg.WorkStartTime, out start)
                    || !TryParseClock(cfg.WorkEndTime, out end))
                {
                    Log.ErrorWithMaxCount(
                        "人工客服工作时间配置无效，首条咨询不按下班状态抑制，避免伪造09:00-18:00。 workStart="
                        + (cfg.WorkStartTime ?? string.Empty)
                        + ", workEnd=" + (cfg.WorkEndTime ?? string.Empty),
                        20);
                    return false;
                }
                var offHours = !IsInsideWorkHours(DateTime.Now.TimeOfDay, start, end);
                if (!offHours) return false;

                var key = RuntimeKey(seller, buyer);
                if (key.Length > 0)
                {
                    PendingReply ignored;
                    if (PendingReplies.TryRemove(key, out ignored))
                    {
                        Log.Info("首条咨询固定回复已取消：当前处于下班自动回复时段，由下班回复独占本轮。seller="
                            + seller + ", buyer=" + buyer);
                    }
                }
                return true;
            });
        }

        private static bool TryParseClock(string value, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            value = (value ?? string.Empty).Trim();
            DateTime parsed;
            if (!DateTime.TryParseExact(
                value,
                new[] { "H:mm", "HH:mm" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out parsed))
            {
                return false;
            }
            time = parsed.TimeOfDay;
            return true;
        }

        private static bool IsInsideWorkHours(TimeSpan now, TimeSpan start, TimeSpan end)
        {
            if (start == end) return true;
            if (start < end) return now >= start && now < end;
            return now >= start || now < end;
        }

        private static bool IsEligibleTrigger(IncomingMessageDecision decision)
        {
            if (decision == null) return false;
            if (string.Equals(decision.MessageLabel, "历史消息", StringComparison.Ordinal)) return false;
            if (string.Equals(decision.MessageLabel, "[充值进度查询]", StringComparison.Ordinal)) return false;
            if (string.Equals(decision.MessageLabel, "[淘宝系统提示]", StringComparison.Ordinal)) return false;
            if (string.Equals(decision.MessageLabel, "[撤回提示]", StringComparison.Ordinal)) return false;
            if (string.Equals(decision.MessageLabel, "[空白或未知消息]", StringComparison.Ordinal)) return false;
            return true;
        }

        private static string ResolveFreshCurrentScope(string seller, string buyer, string currentQuestion, DateTime now)
        {
            var settings = LoadCurrentScope();
            if (settings == null || !settings.Enabled || string.IsNullOrWhiteSpace(settings.Answer)) return string.Empty;
            var priorTurns = ConversationContextStore.GetRecentTurns(seller, buyer, currentQuestion, 24);
            var latestPrior = priorTurns
                .Where(x => x != null
                    && string.Equals(x.Role, "user", StringComparison.Ordinal)
                    && !x.Withdrawn
                    && !string.IsNullOrWhiteSpace(x.Text))
                .Where(x => !IsIgnorableFirstInquiryHistoryTurn(seller, buyer, x, now))
                .OrderByDescending(x => x.Timestamp).FirstOrDefault();
            if (latestPrior != null)
            {
                if (latestPrior.Timestamp == DateTime.MinValue) return string.Empty;
                if (latestPrior.Timestamp >= now.AddMinutes(-SessionResetMinutes)) return string.Empty;
            }
            return BotFeatureStore.ApplyOutputPolicy(settings.Answer.Trim()) ?? string.Empty;
        }

        private static bool IsIgnorableFirstInquiryHistoryTurn(
            string seller,
            string buyer,
            ConversationContextTurn turn,
            DateTime now)
        {
            if (turn == null) return true;
            if (!string.Equals(turn.Role, "user", StringComparison.Ordinal)) return true;
            var text = Compact(turn.Text);
            if (string.IsNullOrWhiteSpace(text)) return true;

            // Product cards can be stored in conversation history using their visible title even
            // though the live message metadata correctly identified them as non-problem context.
            // Remember their authoritative message key so a title-only history turn cannot later
            // consume the first substantive buyer-question slot.
            if (IsKnownNonProblemHistoryTurn(seller, buyer, turn.MessageKey, now)) return true;

            // The current incoming message can appear in local/remote history before the first
            // reservation check completes. It must not make itself look like a prior consultation.
            if (turn.Timestamp != DateTime.MinValue
                && turn.Timestamp >= now.AddSeconds(-SameBurstHistoryGraceSeconds))
            {
                return true;
            }

            // Only an earlier substantive buyer problem consumes the first-inquiry slot. Greetings,
            // acknowledgements/noise, platform entry tips and product links/cards do not. This keeps
            // “你好” or a system-injected item card from blocking a later real first question such as
            // “不行”, while a genuine earlier problem inside the 30-minute session still blocks it.
            return !IsActualProblemText(turn.Text);
        }

        private static string Compact(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Replace(" ", string.Empty)
                .Replace("\t", string.Empty)
                .Trim();
        }

        private static FirstInquiryFixedReplySettings LoadCurrentScope()
        {
            var enabledText = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(EnabledKey, SettingsScope, "true");
            var answer = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(AnswerKey, SettingsScope, DefaultAnswer);
            return new FirstInquiryFixedReplySettings
            {
                Enabled = string.Equals(enabledText, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(enabledText, "1", StringComparison.OrdinalIgnoreCase),
                Answer = string.IsNullOrWhiteSpace(answer) ? string.Empty : answer,
                DelaySeconds = ClampDelaySeconds(ParseDelaySeconds(
                    BotLib.Db.Sqlite.PersistentParams.GetParam2Key(DelayKey, SettingsScope, "0")))
            };
        }

        private static int ParseDelaySeconds(string value)
        {
            int seconds;
            return int.TryParse(value, out seconds) ? seconds : 0;
        }

        private static void CleanupRuntimeState(string key, DateTime now)
        {
            PendingReply pending;
            if (PendingReplies.TryGetValue(key, out pending) && (pending == null || pending.ExpiresAt < now))
            {
                PendingReply ignored;
                PendingReplies.TryRemove(key, out ignored);
            }
            DateTime triggered;
            if (TriggeredAt.TryGetValue(key, out triggered) && triggered < now.AddMinutes(-SessionResetMinutes))
            {
                DateTime ignored;
                TriggeredAt.TryRemove(key, out ignored);
            }
        }

        private static string RuntimeKey(string seller, string buyer)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            var scope = ShopSettingsScope.Current;
            if (scope == null || string.IsNullOrWhiteSpace(scope.ShopKey)
                || !string.Equals((scope.SellerNick ?? string.Empty).Trim(), seller, StringComparison.Ordinal)
                || buyer.Length == 0)
                return string.Empty;
            return scope.ShopKey.Trim() + "#" + seller + "#" + buyer;
        }

        private static T RunInShopScope<T>(string seller, Func<T> action, string shopKey = null)
        {
            if (action == null) return default(T);
            // Empty-seller calls are the explicit legacy-global compatibility path.
            // A non-empty identity must resolve to the exact current ShopKey; failure
            // must never execute a read/write against the unscoped global store.
            if (string.IsNullOrWhiteSpace(seller)) return action();

            ShopContext shop;
            try
            {
                shopKey = (shopKey ?? string.Empty).Trim();
                shop = shopKey.Length > 0
                    ? ShopContextLocator.ResolveRuntimeByShopKey(shopKey)
                    : ShopContextLocator.ResolveCurrentOrBySellerNick(seller);
            }
            catch (Exception ex)
            {
                Log.Info("首条咨询固定回复已阻止未确认店铺作用域: " + ex.GetType().Name);
                return default(T);
            }
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || (shopKey.Length > 0 && !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal))
                || !string.Equals((shop.SellerNick ?? string.Empty).Trim(), seller.Trim(), StringComparison.Ordinal))
                return default(T);

            var current = ShopSettingsScope.Current;
            if (current != null)
            {
                if (!string.Equals(current.ShopKey, shop.ShopKey, StringComparison.Ordinal))
                    return default(T);
                return action();
            }

            using (ShopSettingsScope.Enter(shop)) return action();
        }
    }

    public partial class QN
    {
        internal static List<QN> GetRuntimeSafetySnapshot()
        {
            lock (QNSetLock) return QNSet == null ? new List<QN>() : QNSet.Where(x => x != null).ToList();
        }

        internal void CancelActiveBuyerGeneration(string seller, string buyer, string reason)
        {
            if (_buyerMessageBurstCoordinator == null) return;
            string shopKey;
            try { shopKey = ShopContextLocator.ResolveRuntimeBySeller(this).ShopKey; }
            catch { shopKey = string.Empty; }
            _buyerMessageBurstCoordinator.CancelBuyer(seller, buyer, reason, shopKey);
        }

        internal bool HasBuyerMessageAfter(string seller, string buyer, DateTime threshold)
        {
            DateTime observedAt;
            return _latestBuyerMessageObserved.TryGetValue(RecoveryKey(seller, buyer), out observedAt)
                && observedAt > threshold.AddMilliseconds(5);
        }
    }
}
