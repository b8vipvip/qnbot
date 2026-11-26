using Bot.Common;
using BotLib;
using BotLib.Db.Sqlite;
using Bot.ShopScope;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal sealed class AccountVisualEvidence
    {
        public string ApplicationName { get; set; }
        public string Nickname { get; set; }
        public string UserId { get; set; }
        public string MessageKey { get; set; }
        public DateTime ObservedAt { get; set; }
        public string Summary { get; set; }
    }

    /// <summary>
    /// Reuses privacy-safe visual semantics that have already been persisted by
    /// VisualKnowledgeLearningService. Raw image URLs/bytes are never read here.
    ///
    /// The cache serves two runtime purposes:
    /// 1. later text turns may refer to facts that were visible in an earlier image even if
    ///    that image's original reply became stale or was suppressed by a human reply;
    /// 2. a post-order photo-request preset can be suppressed when a recent pre-order image
    ///    has already established the exact interface the preset asks the buyer to photograph.
    /// </summary>
    internal static class RecentVisualContextService
    {
        public const int PromptWindowMinutes = 90;
        public const int OrderEvidenceWindowMinutes = 90;
        public const int MaxPromptObservations = 2;

        private static readonly Regex PhotoRequestRegex = new Regex(
            "拍照|照片|截图|发图|图片|拍一张|拍下",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex BuiltInOrUntrustedRegex = new Regex(
            "电视自带|系统自带|系统内置|电视内置|机顶盒自带|第三方|聚合应用|浏览器|网页版|非官方|仿版|山寨",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex AppCueRegex = new Regex(
            "app|应用|电视版|电视端|大屏版|大屏端|首页|我的页面|个人中心|会员中心|超级vip|导航栏|设置页|最近播放",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ExplicitOfficialAppRegex = new Regex(
            "官方\\s*(?:app|应用|版|电视版|电视端|大屏版|大屏端)|(?:app|应用|电视版|电视端|大屏版|大屏端)\\s*官方",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex AccountNicknameRegex = new Regex(
            @"(?:账号)?昵称\s*(?:[:：]|为)\s*([^\s,，;；。|]{1,40})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex AccountIdRegex = new Regex(
            @"(?:[\u4e00-\u9fff]{0,12})?(?:用户|账号)?ID\s*(?:[:：]|为)\s*([A-Za-z0-9_-]{3,64})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static bool IsWithdrawnObservation(
            string shopKey, string seller, string buyer, VisualKnowledgeObservationEntity item)
        {
            if (item == null
                || string.Equals(item.Status, "图片已撤回", StringComparison.Ordinal))
                return true;
            if (string.IsNullOrWhiteSpace(item.MessageKey)) return false;
            // The cache is an immediate in-memory signal, while the visual-learning
            // store is the durable tombstone authority after restart or a failed cache
            // persistence path. Either source must exclude the observation.
            return VisionImageCacheService.IsMessageKeyWithdrawn(
                    seller, buyer, item.MessageKey, shopKey)
                || VisualKnowledgeLearningService.IsObservationWithdrawn(
                    shopKey, seller, buyer, item.MessageKey);
        }

        private static bool ExplicitlyReferencesWithdrawnImage(string question)
        {
            if (VisionFollowUpContextPipeline.IsVisionReferentialFollowUp(question)) return true;
            var compact = Regex.Replace(question ?? string.Empty, @"\s+", string.Empty).ToLowerInvariant();
            if (compact.Length == 0) return false;
            return Regex.IsMatch(compact,
                @"(刚才|刚刚|之前|上次|前面|上一张|那张|这张|这个).{0,12}(图片|截图|照片|图|界面|页面)")
                || Regex.IsMatch(compact, @"(图里|图片里|截图里|照片里|上面那张|前面那张)");
        }

        public static string BuildPromptAddon(string seller, string buyer, string currentQuestion)
        {
            var shop = ResolveShop(string.Empty, seller);
            return shop == null ? string.Empty : BuildPromptAddon(shop.ShopKey, seller, buyer, currentQuestion);
        }

        public static string BuildPromptAddon(string shopKey, string seller, string buyer, string currentQuestion)
        {
            var shop = ResolveShop(shopKey, seller);
            if (shop == null) return string.Empty;
            var allowWithdrawnContext = ExplicitlyReferencesWithdrawnImage(currentQuestion);
            var items = LoadRecent(shop.ShopKey, seller, buyer, DateTime.Now.AddMinutes(-PromptWindowMinutes), DateTime.Now.AddMinutes(2), 12)
                .Where(x => allowWithdrawnContext || !IsWithdrawnObservation(shop.ShopKey, seller, buyer, x))
                .OrderByDescending(x => SafeTime(x.ObservedAtTicks))
                .Take(MaxPromptObservations)
                .OrderBy(x => SafeTime(x.ObservedAtTicks))
                .ToList();
            if (items.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.Append("\n\n最近图片理解缓存（后台视觉分析结果，仅属于当前客服+当前买家；即使当时回复因人工接管或新消息而没有发送，这些图片语义仍然有效）：");
            sb.Append("\n只在与当前问题相关时使用。它只描述图片中可见内容，不代表订单、付款、账号、充值等实时状态；不要把缓存内容说成买家刚刚重新发送的图片。");
            sb.Append("\n如果图片已撤回，只能在买家明确引用该历史图片时作为对话上下文；绝不能将撤回图片用作新的流程发布、订单证明或自动业务决策证据。");
            foreach (var item in items)
            {
                var at = SafeTime(item.ObservedAtTicks);
                var summary = CleanForPrompt(item.VisualSummary, 520);
                var tags = CleanForPrompt(item.VisualTags, 220);
                if (summary.Length == 0) continue;
                var withdrawn = IsWithdrawnObservation(shop.ShopKey, seller, buyer, item);
                sb.Append("\n[").Append(at == DateTime.MinValue ? "时间未知" : at.ToString("HH:mm:ss"))
                    .Append(" 图片分析")
                    .Append(withdrawn ? "（已撤回，仅限买家明确引用时使用）" : string.Empty)
                    .Append("] ").Append(summary);
                if (tags.Length > 0) sb.Append("；标签：").Append(tags);
            }
            return sb.ToString();
        }

        internal static List<VisualKnowledgeObservationEntity> GetRecentWorkflowEvidence(
            string shopKey, string seller, string buyer, int maxCount)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0 || buyer.Length == 0)
                return new List<VisualKnowledgeObservationEntity>();
            var items = LoadRecent(
                shopKey, seller, buyer,
                DateTime.Now.AddMinutes(-PromptWindowMinutes), DateTime.Now.AddMinutes(2),
                Math.Max(1, Math.Min(30, maxCount)));
            return items
                .Where(x => x != null
                    && !string.IsNullOrWhiteSpace(x.MessageKey)
                    && !string.IsNullOrWhiteSpace(x.VisualSummary)
                    && !IsWithdrawnObservation(shopKey, seller, buyer, x))
                .OrderByDescending(x => x.ObservedAtTicks)
                .Take(Math.Max(1, Math.Min(30, maxCount)))
                .ToList();
        }

        // Return the newest image observation even when it is unmatched or withdrawn.
        // Callers must not fall back to an older matched image when a newer image supersedes it.
        internal static VisualKnowledgeObservationEntity GetLatestImageObservation(
            string shopKey, string seller, string buyer)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0 || buyer.Length == 0)
                return null;
            var latest = LoadRecent(
                    shopKey, seller, buyer,
                    DateTime.Now.AddMinutes(-PromptWindowMinutes), DateTime.Now.AddMinutes(2), 30)
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.MessageKey))
                .OrderByDescending(x => x.ObservedAtTicks)
                .FirstOrDefault();
            if (latest == null || IsWithdrawnObservation(shopKey, seller, buyer, latest))
                return null;
            return latest;
        }

        public static bool TryGetRecentAccountEvidence(
            string seller,
            string buyer,
            out AccountVisualEvidence evidence)
        {
            evidence = null;
            var shop = ResolveShop(string.Empty, seller);
            return shop != null && TryGetRecentAccountEvidence(shop.ShopKey, seller, buyer, out evidence);
        }

        public static bool TryGetRecentAccountEvidence(
            string shopKey,
            string seller,
            string buyer,
            out AccountVisualEvidence evidence)
        {
            evidence = null;
            var shop = ResolveShop(shopKey, seller);
            if (shop == null) return false;
            var items = LoadRecent(
                    shop.ShopKey,
                    seller,
                    buyer,
                    DateTime.Now.AddMinutes(-PromptWindowMinutes),
                    DateTime.Now.AddMinutes(2),
                    20)
                .Where(x => !string.IsNullOrWhiteSpace(x.MessageKey))
                .Where(x => !IsWithdrawnObservation(shop.ShopKey, seller, buyer, x))
                .OrderByDescending(x => SafeTime(x.ObservedAtTicks))
                .ToList();

            foreach (var item in items)
            {
                var combined = CleanForPrompt(
                    (item.VisualSummary ?? string.Empty) + " " + (item.VisualTags ?? string.Empty),
                    1800);
                string applicationName;
                if (!HasConfiguredOfficialAppEvidence(shop.ShopKey, seller, combined, out applicationName))
                    continue;

                var nicknameMatch = AccountNicknameRegex.Match(combined);
                var idMatch = AccountIdRegex.Match(combined);
                var nickname = nicknameMatch.Success
                    ? CleanAccountField(nicknameMatch.Groups[1].Value, 40)
                    : string.Empty;
                var userId = idMatch.Success
                    ? CleanAccountField(idMatch.Groups[1].Value, 64)
                    : string.Empty;
                if (nickname.Length == 0 && userId.Length == 0) continue;

                evidence = new AccountVisualEvidence
                {
                    ApplicationName = applicationName,
                    Nickname = nickname,
                    UserId = userId,
                    MessageKey = item.MessageKey,
                    ObservedAt = SafeTime(item.ObservedAtTicks),
                    Summary = CleanForPrompt(item.VisualSummary, 220)
                };
                return true;
            }
            return false;
        }

        public static bool TrySatisfyOrderPhotoRequirement(
            string seller,
            string buyer,
            string guidanceText,
            DateTime orderEventTime,
            out string evidence)
        {
            evidence = string.Empty;
            var shop = ResolveShop(string.Empty, seller);
            return shop != null && TrySatisfyOrderPhotoRequirement(
                shop.ShopKey, seller, buyer, guidanceText, orderEventTime,
                out evidence);
        }

        public static bool TrySatisfyOrderPhotoRequirement(
            string shopKey,
            string seller,
            string buyer,
            string guidanceText,
            DateTime orderEventTime,
            out string evidence)
        {
            string ignoredApplicationName;
            return TrySatisfyOrderPhotoRequirement(
                shopKey, seller, buyer, guidanceText, orderEventTime,
                out ignoredApplicationName, out evidence);
        }

        public static bool TrySatisfyOrderPhotoRequirement(
            string shopKey,
            string seller,
            string buyer,
            string guidanceText,
            DateTime orderEventTime,
            out string applicationName,
            out string evidence)
        {
            applicationName = string.Empty;
            evidence = string.Empty;
            var shop = ResolveShop(shopKey, seller);
            if (shop == null) return false;
            var config = ShopReceptionConfigService.GetConfig(shop);
            if (config == null || !config.Enabled
                || !LooksLikeConfiguredPhotoGuidance(guidanceText, config))
                return false;

            var anchor = orderEventTime == DateTime.MinValue ? DateTime.Now : orderEventTime;
            if (anchor > DateTime.Now.AddMinutes(5) || anchor < DateTime.Now.AddDays(-7)) anchor = DateTime.Now;
            var since = anchor.AddMinutes(-OrderEvidenceWindowMinutes);
            var until = anchor.AddMinutes(5);

            string humanEvidence;
            string humanApplicationName;
            if (TryFindHumanConfiguredAppConfirmation(
                shop.ShopKey, seller, buyer, since, until,
                out humanApplicationName, out humanEvidence))
            {
                applicationName = humanApplicationName;
                evidence = humanEvidence;
                return true;
            }

            // This is deliberately a read-only, non-blocking cache lookup. It never starts a
            // second vision request and never serializes order handling behind a slow AI task.
            var items = LoadRecent(shop.ShopKey, seller, buyer, since, until, 20)
                .Where(x => !IsWithdrawnObservation(shop.ShopKey, seller, buyer, x))
                .OrderByDescending(x => SafeTime(x.ObservedAtTicks))
                .ToList();
            foreach (var item in items)
            {
                var combined = (item.VisualSummary ?? string.Empty) + " " + (item.VisualTags ?? string.Empty);
                VisualUnderstandingRuleEntity matchedRule;
                string candidateApplicationName;
                if (!VisualUnderstandingRuleService.TryResolveConfiguredApplication(
                    shop.ShopKey, seller, combined, out candidateApplicationName, out matchedRule))
                    continue;

                // The newest observation for a configured application is authoritative.
                // If it is a built-in, third-party or otherwise unverified UI, do not fall
                // back to an older qualifying image from an earlier topic/device.
                if (!HasConfiguredOfficialAppEvidence(
                    shop.ShopKey, seller, combined, out candidateApplicationName))
                    return false;

                applicationName = candidateApplicationName;
                evidence = CleanForPrompt(item.VisualSummary, 180);
                if (evidence.Length == 0) evidence = CleanForPrompt(item.VisualTags, 180);
                return true;
            }
            return false;
        }

        internal static bool LooksLikeConfiguredPhotoGuidance(
            string guidanceText,
            ShopReceptionConfig config)
        {
            var text = (guidanceText ?? string.Empty).Trim();
            if (!PhotoRequestRegex.IsMatch(text) || config == null || !config.Enabled) return false;
            var all = config.PhotoInstructionAllCues ?? new List<string>();
            var any = config.PhotoInstructionAnyCues ?? new List<string>();
            var excluded = config.PhotoInstructionExcludedCues ?? new List<string>();
            if (all.Count == 0 && any.Count == 0) return false;
            if (all.Any(cue => text.IndexOf(cue, StringComparison.OrdinalIgnoreCase) < 0)) return false;
            if (any.Count > 0 && !any.Any(cue => text.IndexOf(cue, StringComparison.OrdinalIgnoreCase) >= 0))
                return false;
            if (excluded.Any(cue => text.IndexOf(cue, StringComparison.OrdinalIgnoreCase) >= 0))
                return false;
            return true;
        }

        internal static bool HasConfiguredOfficialAppEvidence(
            string shopKey,
            string seller,
            string visualText,
            out string applicationName)
        {
            applicationName = string.Empty;
            var text = Regex.Replace((visualText ?? string.Empty).ToLowerInvariant(), @"\s+", string.Empty);
            VisualUnderstandingRuleEntity rule;
            try
            {
                if (!VisualUnderstandingRuleService.TryResolveConfiguredApplication(
                    shopKey, seller, text, out applicationName, out rule))
                    return false;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取店铺视觉理解应用配置失败，账号证据按未命中处理：" + ex.Message, 5);
                applicationName = string.Empty;
                return false;
            }

            var explicitOfficial = ExplicitOfficialAppRegex.IsMatch(text);
            if (BuiltInOrUntrustedRegex.IsMatch(text) && !explicitOfficial) return false;

            var cues = ((rule.RequiredKeywords ?? string.Empty) + "\n"
                    + (rule.OptionalKeywords ?? string.Empty) + "\n"
                    + (rule.ScreenName ?? string.Empty))
                .Split(new[] { '\r', '\n', '|', ';', '；', ',', '，' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Regex.Replace(x.Trim(), @"\s+", string.Empty))
                .Where(x => x.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var matchedCueCount = cues.Count(cue =>
                text.IndexOf(cue, StringComparison.OrdinalIgnoreCase) >= 0);
            return explicitOfficial || (AppCueRegex.IsMatch(text) && matchedCueCount >= 2);
        }

        private static bool TryFindHumanConfiguredAppConfirmation(
            string shopKey,
            string seller,
            string buyer,
            DateTime since,
            DateTime until,
            out string applicationName,
            out string evidence)
        {
            applicationName = string.Empty;
            evidence = string.Empty;
            try
            {
                var shop = ResolveShop(shopKey, seller);
                if (shop == null) return false;
                using (ShopSettingsScope.Enter(shop))
                {
                    var turns = ConversationContextStore.GetRecentTurns(seller, buyer, string.Empty, 24)
                        .Where(x => x != null && !x.Withdrawn)
                        .Where(x => x.Timestamp == DateTime.MinValue || (x.Timestamp >= since && x.Timestamp <= until))
                        .OrderBy(x => x.Timestamp)
                        .ToList();

                    var imageIndex = -1;
                    for (var i = turns.Count - 1; i >= 0; i--)
                    {
                        if (turns[i].Role != "user") continue;
                        var text = (turns[i].Text ?? string.Empty).Trim();
                        if (turns[i].HasImage
                            || string.Equals(text, "[图片]", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(text, "【图片】", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(text, "图片消息", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(text, "[image]", StringComparison.OrdinalIgnoreCase))
                        {
                            imageIndex = i;
                            break;
                        }
                    }
                    if (imageIndex < 0) return false;

                    for (var i = turns.Count - 1; i > imageIndex; i--)
                    {
                        if (turns[i].Role != "assistant") continue;
                        var text = (turns[i].Text ?? string.Empty).Trim();
                        if (text.Length == 0 || IsBotMarked(text)) continue;
                        string candidateApplicationName;
                        if (!HasConfiguredOfficialAppEvidence(
                            shopKey, seller, text, out candidateApplicationName))
                            continue;
                        applicationName = candidateApplicationName;
                        evidence = "人工客服已确认：" + CleanForPrompt(text, 160);
                        return true;
                    }
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsBotMarked(string text)
        {
            var compact = Regex.Replace((text ?? string.Empty), @"\s+", string.Empty);
            return compact.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)
                || compact.EndsWith("【AI】", StringComparison.OrdinalIgnoreCase)
                || compact.EndsWith("［AI］", StringComparison.OrdinalIgnoreCase);
        }

        private static List<VisualKnowledgeObservationEntity> LoadRecent(
            string shopKey,
            string seller,
            string buyer,
            DateTime since,
            DateTime until,
            int limit)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            var shop = ResolveShop(shopKey, seller);
            if (shop == null || buyer.Length == 0) return new List<VisualKnowledgeObservationEntity>();

            try
            {
                using (ShopSettingsScope.Enter(shop))
                {
                var raw = (DbHelper.Db.Select(
                    typeof(VisualKnowledgeObservationEntity),
                    "where ShopKey = ? and Seller = ? and ObservedAtTicks >= ? and ObservedAtTicks <= ? order by ObservedAtTicks desc limit "
                        + Math.Max(1, Math.Min(50, limit)),
                    shop.ShopKey,
                    seller,
                    since.Ticks,
                    until.Ticks) ?? new List<object>())
                    .OfType<VisualKnowledgeObservationEntity>()
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.VisualSummary))
                    .ToList();

                return raw.Where(x => SameBuyer(seller, x.Buyer, buyer)).ToList();
                }
            }
            catch
            {
                // The table is created lazily by VisualKnowledgeLearningService. Before the first
                // successful vision analysis there is simply no visual context to contribute.
                return new List<VisualKnowledgeObservationEntity>();
            }
        }

        private static ShopContext ResolveShop(string shopKey, string seller)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return null;
            try
            {
                ShopContext shop;
                if (shopKey.Length > 0)
                    shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
                else
                {
                    var current = ShopSettingsScope.Current;
                    shop = current != null
                        && string.Equals(current.SellerNick, seller, StringComparison.Ordinal)
                        ? current
                        : ShopContextLocator.ResolveRuntimeBySellerNick(seller);
                }
                if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                    || !string.Equals(shop.SellerNick, seller, StringComparison.Ordinal)
                    || (shopKey.Length > 0 && !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)))
                    return null;
                return shop;
            }
            catch
            {
                // A duplicate seller nickname without an explicit ShopKey is ambiguous.
                return null;
            }
        }

        private static bool SameBuyer(string seller, string left, string right)
        {
            left = (left ?? string.Empty).Trim();
            right = (right ?? string.Empty).Trim();
            if (left.Length == 0 || right.Length == 0) return false;
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
            try { return BuyerIdentityAliasService.AreEquivalent(seller, left, right); }
            catch { return false; }
        }

        private static DateTime SafeTime(long ticks)
        {
            try
            {
                if (ticks <= DateTime.MinValue.Ticks || ticks >= DateTime.MaxValue.Ticks) return DateTime.MinValue;
                return new DateTime(ticks);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static string CleanAccountField(string value, int max)
        {
            value = (value ?? string.Empty).Trim().Trim('"', '\'', '“', '”', '【', '】', '[', ']');
            value = Regex.Replace(value, @"\s+", string.Empty);
            if (value.Length > max) value = value.Substring(0, max);
            return value;
        }

        private static string CleanForPrompt(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            value = Regex.Replace(value, @"\s+", " ");
            if (max > 0 && value.Length > max) value = value.Substring(0, max).Trim() + "...";
            return value;
        }
    }
}
