using Bot.Common;
using BotLib;
using BotLib.Db.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal sealed class KugouAccountEvidence
    {
        public string Nickname { get; set; }
        public string UserId { get; set; }
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

        private static readonly Regex KugouUiCueRegex = new Regex(
            "推荐|mv|k歌|淘唱会|乐库|全量曲|儿童|宅家|我喜欢|我的歌单|最近播放|已购音乐|超级vip|大屏vip|酷狗账号",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex HumanOfficialKugouRegex = new Regex(
            "酷狗.{0,12}(?:官方app|官方应用|官方版|电视版|电视端).{0,18}(?:支持|可以|能用|可用)|(?:官方app|官方应用|酷狗音乐app|酷狗app).{0,18}(?:支持|可以|能用|可用)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex KugouNicknameRegex = new Regex(
            @"(?:酷狗)?(?:账号)?昵称\s*(?:[:：]|为)\s*([^\s,，;；。|]{1,40})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex KugouUserIdRegex = new Regex(
            @"(?:酷狗|用户|账号)?(?:ID|id)\s*(?:[:：]|为)\s*([A-Za-z0-9_-]{3,64})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string BuildPromptAddon(string seller, string buyer, string currentQuestion)
        {
            var items = LoadRecent(seller, buyer, DateTime.Now.AddMinutes(-PromptWindowMinutes), DateTime.Now.AddMinutes(2), 12)
                .OrderByDescending(x => SafeTime(x.ObservedAtTicks))
                .Take(MaxPromptObservations)
                .OrderBy(x => SafeTime(x.ObservedAtTicks))
                .ToList();
            if (items.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.Append("\n\n最近图片理解缓存（后台视觉分析结果，仅属于当前客服+当前买家；即使当时回复因人工接管或新消息而没有发送，这些图片语义仍然有效）：");
            sb.Append("\n只在与当前问题相关时使用。它只描述图片中可见内容，不代表订单、付款、账号、充值等实时状态；不要把缓存内容说成买家刚刚重新发送的图片。");
            foreach (var item in items)
            {
                var at = SafeTime(item.ObservedAtTicks);
                var summary = CleanForPrompt(item.VisualSummary, 520);
                var tags = CleanForPrompt(item.VisualTags, 220);
                if (summary.Length == 0) continue;
                sb.Append("\n[").Append(at == DateTime.MinValue ? "时间未知" : at.ToString("HH:mm:ss"))
                    .Append(" 图片分析] ").Append(summary);
                if (tags.Length > 0) sb.Append("；标签：").Append(tags);
            }
            return sb.ToString();
        }

        public static bool TryGetRecentKugouAccountEvidence(
            string seller,
            string buyer,
            out KugouAccountEvidence evidence)
        {
            evidence = null;
            var items = LoadRecent(
                    seller,
                    buyer,
                    DateTime.Now.AddMinutes(-PromptWindowMinutes),
                    DateTime.Now.AddMinutes(2),
                    20)
                .OrderByDescending(x => SafeTime(x.ObservedAtTicks))
                .ToList();

            foreach (var item in items)
            {
                var combined = CleanForPrompt(
                    (item.VisualSummary ?? string.Empty) + " " + (item.VisualTags ?? string.Empty),
                    1800);
                if (combined.IndexOf("酷狗", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!HasKugouOfficialAppEvidence(combined)) continue;

                var nicknameMatch = KugouNicknameRegex.Match(combined);
                var idMatch = KugouUserIdRegex.Match(combined);
                var nickname = nicknameMatch.Success
                    ? CleanAccountField(nicknameMatch.Groups[1].Value, 40)
                    : string.Empty;
                var userId = idMatch.Success
                    ? CleanAccountField(idMatch.Groups[1].Value, 64)
                    : string.Empty;
                if (nickname.Length == 0 && userId.Length == 0) continue;

                evidence = new KugouAccountEvidence
                {
                    Nickname = nickname,
                    UserId = userId,
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
            if (!LooksLikeKugouPhotoGuidance(guidanceText)) return false;

            var anchor = orderEventTime == DateTime.MinValue ? DateTime.Now : orderEventTime;
            if (anchor > DateTime.Now.AddMinutes(5) || anchor < DateTime.Now.AddDays(-7)) anchor = DateTime.Now;
            var since = anchor.AddMinutes(-OrderEvidenceWindowMinutes);
            var until = anchor.AddMinutes(5);

            string humanEvidence;
            if (TryFindHumanOfficialKugouConfirmation(seller, buyer, since, until, out humanEvidence))
            {
                evidence = humanEvidence;
                return true;
            }

            // This is deliberately a read-only, non-blocking cache lookup. It never starts a
            // second vision request and never serializes order handling behind a slow AI task.
            var items = LoadRecent(seller, buyer, since, until, 20)
                .OrderByDescending(x => SafeTime(x.ObservedAtTicks))
                .ToList();
            foreach (var item in items)
            {
                var combined = (item.VisualSummary ?? string.Empty) + " " + (item.VisualTags ?? string.Empty);
                if (combined.IndexOf("酷狗", StringComparison.OrdinalIgnoreCase) < 0) continue;

                // The newest KuGou-related visual observation is authoritative. If it explicitly
                // looks like a TV built-in/third-party/non-official UI, do not fall back to an
                // older qualifying image from an earlier topic/device.
                if (!HasKugouOfficialAppEvidence(combined)) return false;

                evidence = CleanForPrompt(item.VisualSummary, 180);
                if (evidence.Length == 0) evidence = CleanForPrompt(item.VisualTags, 180);
                return true;
            }
            return false;
        }

        internal static bool LooksLikeKugouPhotoGuidance(string guidanceText)
        {
            var text = (guidanceText ?? string.Empty).Trim();
            return text.IndexOf("酷狗", StringComparison.OrdinalIgnoreCase) >= 0
                && PhotoRequestRegex.IsMatch(text);
        }

        internal static bool HasKugouOfficialAppEvidence(string visualText)
        {
            var text = Regex.Replace((visualText ?? string.Empty).ToLowerInvariant(), @"\s+", string.Empty);
            if (text.IndexOf("酷狗", StringComparison.Ordinal) < 0) return false;

            var explicitOfficial = text.Contains("酷狗官方")
                || text.Contains("官方app")
                || text.Contains("官方应用")
                || text.Contains("酷狗音乐app")
                || text.Contains("酷狗app")
                || text.Contains("酷狗tv")
                || text.Contains("酷狗大屏")
                || text.Contains("酷狗音乐电视版")
                || text.Contains("酷狗音乐电视端");

            if (BuiltInOrUntrustedRegex.IsMatch(text) && !explicitOfficial) return false;

            var appCue = AppCueRegex.IsMatch(text);
            var uiCueCount = KugouUiCueRegex.Matches(text).Cast<Match>()
                .Select(x => x.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var brandedApplication = text.Contains("酷狗音乐") && (appCue || uiCueCount >= 2);

            // Login state is deliberately not part of this decision. The purpose is only to
            // identify that the buyer already photographed the official KuGou application UI.
            return explicitOfficial || brandedApplication;
        }

        private static bool TryFindHumanOfficialKugouConfirmation(
            string seller,
            string buyer,
            DateTime since,
            DateTime until,
            out string evidence)
        {
            evidence = string.Empty;
            try
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
                    var text = turns[i].Text ?? string.Empty;
                    if (text.Contains("[图片]") || text.Contains("【图片】"))
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
                    if (!HumanOfficialKugouRegex.IsMatch(text)) continue;
                    evidence = "人工客服已确认：" + CleanForPrompt(text, 160);
                    return true;
                }
                return false;
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
            string seller,
            string buyer,
            DateTime since,
            DateTime until,
            int limit)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0) return new List<VisualKnowledgeObservationEntity>();

            try
            {
                var raw = (DbHelper.Db.Select(
                    typeof(VisualKnowledgeObservationEntity),
                    "where Seller = ? and ObservedAtTicks >= ? and ObservedAtTicks <= ? order by ObservedAtTicks desc limit "
                        + Math.Max(1, Math.Min(50, limit)),
                    seller,
                    since.Ticks,
                    until.Ticks) ?? new List<object>())
                    .OfType<VisualKnowledgeObservationEntity>()
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.VisualSummary))
                    .ToList();

                return raw.Where(x => SameBuyer(seller, x.Buyer, buyer)).ToList();
            }
            catch
            {
                // The table is created lazily by VisualKnowledgeLearningService. Before the first
                // successful vision analysis there is simply no visual context to contribute.
                return new List<VisualKnowledgeObservationEntity>();
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
