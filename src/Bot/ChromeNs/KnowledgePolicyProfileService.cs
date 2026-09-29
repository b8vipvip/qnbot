using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal static class KnowledgeAnswerModes
    {
        public const string Auto = "auto";
        public const string Direct = "direct";
        public const string Contextual = "contextual";
        public const string Constraint = "constraint";

        public static readonly string[] All = { Auto, Direct, Contextual, Constraint };

        public static string Normalize(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return All.Contains(value) ? value : Auto;
        }

        public static string Display(string value)
        {
            value = Normalize(value);
            if (value == Direct) return "优先直答";
            if (value == Contextual) return "必须结合上下文";
            if (value == Constraint) return "仅作为事实约束";
            return "自动判断";
        }
    }

    internal sealed class KnowledgePolicyProfile
    {
        public string KnowledgeId { get; set; }
        public string QuestionSnapshot { get; set; }
        public string Intent { get; set; }
        public string Entities { get; set; }
        public string ApplyWhen { get; set; }
        public string DoNotApplyWhen { get; set; }
        public string RequiredContext { get; set; }
        public string AnswerMode { get; set; }
        public double Confidence { get; set; }
        public int DirectSelectedCount { get; set; }
        public int ContextualSelectedCount { get; set; }
        public int AcceptedCount { get; set; }
        public int SellerCorrectionCount { get; set; }
        public int SellerWithdrawCount { get; set; }
        public string LastEvidenceType { get; set; }
        public string UpdatedAt { get; set; }

        [JsonIgnore]
        public double ReliabilityScore
        {
            get
            {
                var successes = 3.0 + Math.Max(0, AcceptedCount);
                var exposureCredit = Math.Max(0,
                    DirectSelectedCount - SellerCorrectionCount - SellerWithdrawCount) * 0.08;
                var failures = 1.0
                    + Math.Max(0, SellerCorrectionCount) * 2.0
                    + Math.Max(0, SellerWithdrawCount) * 2.8;
                var score = (successes + exposureCredit)
                    / (successes + exposureCredit + failures);
                return Math.Max(0.08, Math.Min(0.99, score));
            }
        }

        [JsonIgnore]
        public string AnswerModeDisplay { get { return KnowledgeAnswerModes.Display(AnswerMode); } }

        [JsonIgnore]
        public string ReliabilityDisplay { get { return (ReliabilityScore * 100).ToString("0") + "%"; } }
    }

    internal sealed class KnowledgePolicyEvaluation
    {
        public KnowledgePolicyProfile Profile { get; set; }
        public bool Excluded { get; set; }
        public bool ForceContextual { get; set; }
        public bool ConstraintOnly { get; set; }
        public bool AllowDirect { get; set; }
        public double ScoreAdjustment { get; set; }
        public string Reason { get; set; }
    }

    internal static class KnowledgePolicyProfileService
    {
        private sealed class PolicyFile
        {
            public int Version { get; set; }
            public List<KnowledgePolicyProfile> Profiles { get; set; }

            public PolicyFile()
            {
                Version = 1;
                Profiles = new List<KnowledgePolicyProfile>();
            }
        }

        private static readonly object Sync = new object();
        private static PolicyFile _cache;
        internal const string EnabledSettingsKey = "knowledge.policy_reliability_enabled";
        private static readonly IShopScopedPathProvider SettingsPaths = new ShopScopedPathProvider();

        public static bool IsEnabled(ShopContext shop = null)
        {
            shop = shop ?? ShopSettingsScope.Current;
            if (shop == null) return true;
            try
            {
                string value;
                var store = new ShopScopedSettingsStore(shop, SettingsPaths);
                if (!store.TryGetString(EnabledSettingsKey, out value)) return true;
                value = (value ?? string.Empty).Trim();
                return !(value == "0"
                    || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("off", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取知识策略与可靠度开关失败，按启用运行: " + ex.Message, 10);
                return true;
            }
        }

        public static void SetEnabled(ShopContext shop, bool enabled)
        {
            shop = shop ?? ShopSettingsScope.Current;
            if (shop == null) throw new InvalidOperationException("保存知识策略开关需要当前店铺上下文。");
            var store = new ShopScopedSettingsStore(shop, SettingsPaths);
            store.SetString(EnabledSettingsKey, enabled ? "1" : "0");
            Log.Info("知识策略与可靠度已" + (enabled ? "启用" : "关闭") + ": shop=" + shop.ShopKey);
        }

        public static KnowledgePolicyProfile GetProfile(KnowledgeBaseEntry entry)
        {
            if (entry == null) return NewProfile(null);
            lock (Sync)
            {
                var file = LoadInternal();
                var id = StableId(entry);
                var profile = file.Profiles.FirstOrDefault(x => x != null
                    && string.Equals(x.KnowledgeId, id, StringComparison.Ordinal));
                return Clone(profile ?? NewProfile(entry));
            }
        }

        public static List<KnowledgePolicyProfile> GetProfilesForKnowledge(IEnumerable<KnowledgeBaseEntry> knowledge)
        {
            return (knowledge ?? Enumerable.Empty<KnowledgeBaseEntry>())
                .Where(x => x != null)
                .Select(GetProfile)
                .OrderBy(x => x.QuestionSnapshot ?? string.Empty)
                .ToList();
        }

        public static void SaveProfile(KnowledgeBaseEntry entry, KnowledgePolicyProfile edited)
        {
            if (entry == null || edited == null) return;
            lock (Sync)
            {
                var file = LoadInternal();
                var id = StableId(entry);
                var existing = file.Profiles.FirstOrDefault(x => x != null
                    && string.Equals(x.KnowledgeId, id, StringComparison.Ordinal));
                if (existing == null)
                {
                    existing = NewProfile(entry);
                    file.Profiles.Add(existing);
                }
                existing.KnowledgeId = id;
                existing.QuestionSnapshot = Clean(entry.Title, 400);
                existing.Intent = Clean(edited.Intent, 80);
                existing.Entities = Clean(edited.Entities, 500);
                existing.ApplyWhen = Clean(edited.ApplyWhen, 1000);
                existing.DoNotApplyWhen = Clean(edited.DoNotApplyWhen, 1000);
                existing.RequiredContext = Clean(edited.RequiredContext, 1000);
                existing.AnswerMode = KnowledgeAnswerModes.Normalize(edited.AnswerMode);
                existing.Confidence = Clamp(edited.Confidence <= 0 ? 0.80 : edited.Confidence);
                existing.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                SaveInternal(file);
            }
        }

        public static void ImportCompleteProfile(KnowledgeBaseEntry entry, KnowledgePolicyProfile imported)
        {
            if (entry == null || imported == null) return;
            lock (Sync)
            {
                var file = LoadInternal();
                var id = StableId(entry);
                var existing = file.Profiles.FirstOrDefault(x => x != null
                    && string.Equals(x.KnowledgeId, id, StringComparison.Ordinal));
                if (existing == null)
                {
                    existing = NewProfile(entry);
                    file.Profiles.Add(existing);
                }
                existing.KnowledgeId = id;
                existing.QuestionSnapshot = Clean(entry.Title, 400);
                existing.Intent = Clean(imported.Intent, 80);
                existing.Entities = Clean(imported.Entities, 500);
                existing.ApplyWhen = Clean(imported.ApplyWhen, 1000);
                existing.DoNotApplyWhen = Clean(imported.DoNotApplyWhen, 1000);
                existing.RequiredContext = Clean(imported.RequiredContext, 1000);
                existing.AnswerMode = KnowledgeAnswerModes.Normalize(imported.AnswerMode);
                existing.Confidence = Clamp(imported.Confidence <= 0 ? 0.80 : imported.Confidence);
                existing.DirectSelectedCount = Math.Max(0, imported.DirectSelectedCount);
                existing.ContextualSelectedCount = Math.Max(0, imported.ContextualSelectedCount);
                existing.AcceptedCount = Math.Max(0, imported.AcceptedCount);
                existing.SellerCorrectionCount = Math.Max(0, imported.SellerCorrectionCount);
                existing.SellerWithdrawCount = Math.Max(0, imported.SellerWithdrawCount);
                existing.LastEvidenceType = Clean(imported.LastEvidenceType, 120);
                existing.UpdatedAt = string.IsNullOrWhiteSpace(imported.UpdatedAt)
                    ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    : Clean(imported.UpdatedAt, 40);
                SaveInternal(file);
            }
        }

        public static KnowledgePolicyEvaluation Evaluate(
            KnowledgeBaseEntry entry,
            string currentQuestion,
            string resolvedQuestion,
            ConversationStateSnapshot state,
            string recentContext)
        {
            var profile = GetProfile(entry);

            // A commerce_scope token is not an ordinary soft ApplyWhen hint. It is a scenario
            // identity boundary created from human correction evidence. Enforce it here, before the
            // global policy toggle and before generic condition splitting, so every retrieval path
            // (lexical, semantic embedding, direct/contextual) gets the same terminal decision.
            var requiredCommerceScope = ReadAtomicCommerceScope(profile.RequiredContext);
            if (!string.IsNullOrWhiteSpace(requiredCommerceScope))
            {
                var currentCommerceScope = CommerceContextService.BuildPolicyScopeToken(
                    state == null ? null : state.CommerceContext);
                if (!string.Equals(requiredCommerceScope, currentCommerceScope, StringComparison.OrdinalIgnoreCase))
                {
                    return new KnowledgePolicyEvaluation
                    {
                        Profile = profile,
                        Excluded = true,
                        ForceContextual = true,
                        ConstraintOnly = false,
                        AllowDirect = false,
                        ScoreAdjustment = -1,
                        Reason = "电商场景键不匹配，禁止跨订单阶段/履约/商品/SKU复用该答案"
                    };
                }
            }

            if (!IsEnabled())
            {
                return new KnowledgePolicyEvaluation
                {
                    Profile = profile,
                    Excluded = false,
                    ForceContextual = false,
                    ConstraintOnly = false,
                    AllowDirect = true,
                    ScoreAdjustment = 0,
                    Reason = "知识策略与可靠度已关闭，仅保留基础相关度、上下文和高风险安全判断"
                };
            }
            var mode = KnowledgeAnswerModes.Normalize(profile.AnswerMode);
            var haystack = BuildContextText(currentQuestion, resolvedQuestion, state, recentContext);
            var doNotApply = MatchConditions(profile.DoNotApplyWhen, haystack);
            var applyConfigured = SplitConditions(profile.ApplyWhen).Count > 0;
            var applyMatched = !applyConfigured || MatchConditions(profile.ApplyWhen, haystack);
            var requiredConfigured = SplitConditions(profile.RequiredContext).Count > 0;
            var requiredMatched = !requiredConfigured || MatchConditions(profile.RequiredContext, haystack);
            var reliability = profile.ReliabilityScore;

            var evaluation = new KnowledgePolicyEvaluation
            {
                Profile = profile,
                Excluded = doNotApply,
                ConstraintOnly = mode == KnowledgeAnswerModes.Constraint,
                ForceContextual = mode == KnowledgeAnswerModes.Contextual
                    || mode == KnowledgeAnswerModes.Constraint
                    || !requiredMatched
                    || reliability < 0.48,
                AllowDirect = mode != KnowledgeAnswerModes.Contextual
                    && mode != KnowledgeAnswerModes.Constraint
                    && requiredMatched
                    && reliability >= 0.58,
                ScoreAdjustment = 0,
                Reason = string.Empty
            };

            if (doNotApply)
            {
                evaluation.ScoreAdjustment = -1;
                evaluation.Reason = "命中知识禁用条件";
                return evaluation;
            }
            if (applyConfigured && !applyMatched)
            {
                evaluation.ScoreAdjustment -= 0.14;
                evaluation.ForceContextual = true;
                evaluation.Reason = "未明确满足知识适用条件";
            }
            else if (applyConfigured)
            {
                evaluation.ScoreAdjustment += 0.06;
            }
            if (!requiredMatched)
            {
                evaluation.ScoreAdjustment -= 0.12;
                evaluation.Reason = "缺少知识要求的上下文";
            }

            if (mode == KnowledgeAnswerModes.Direct && reliability >= 0.75)
            {
                evaluation.ScoreAdjustment += 0.05;
            }
            else if (mode == KnowledgeAnswerModes.Constraint)
            {
                evaluation.ScoreAdjustment += 0.02;
            }

            if (reliability >= 0.90 && profile.AcceptedCount >= 3)
                evaluation.ScoreAdjustment += 0.04;
            else if (reliability < 0.55)
                evaluation.ScoreAdjustment -= 0.08;

            if (string.IsNullOrWhiteSpace(evaluation.Reason))
            {
                evaluation.Reason = "模式=" + KnowledgeAnswerModes.Display(mode)
                    + "，可靠度=" + (reliability * 100).ToString("0") + "%";
            }
            return evaluation;
        }

        public static void RecordRouteSelection(KnowledgeBaseEntry entry, bool direct)
        {
            Mutate(entry, profile =>
            {
                if (direct) profile.DirectSelectedCount++;
                else profile.ContextualSelectedCount++;
                profile.LastEvidenceType = direct ? "route_direct" : "route_contextual";
            });
        }

        public static void RecordReviewEvidence(
            string question,
            string oldAnswer,
            string finalAnswer,
            string evidenceType)
        {
            evidenceType = (evidenceType ?? string.Empty).Trim().ToLowerInvariant();
            var knowledge = FindKnowledge(question, oldAnswer);
            if (knowledge == null) return;
            Mutate(knowledge, profile =>
            {
                if (evidenceType == "withdrawn_bot_then_manual")
                {
                    profile.SellerWithdrawCount++;
                    profile.SellerCorrectionCount++;
                }
                else if (evidenceType == "manual_correction")
                {
                    profile.SellerCorrectionCount++;
                }
                else if (evidenceType == "repeated_human_pattern")
                {
                    if (SameAnswer(oldAnswer, finalAnswer)) profile.AcceptedCount++;
                    else profile.SellerCorrectionCount++;
                }
                else if (evidenceType == "manual_reply" && SameAnswer(oldAnswer, finalAnswer))
                {
                    profile.AcceptedCount++;
                }
                profile.LastEvidenceType = evidenceType;
            });
        }

        public static void RecordKnowledgeAccepted(string question, string answer)
        {
            var knowledge = FindKnowledge(question, answer);
            if (knowledge == null) return;
            Mutate(knowledge, profile =>
            {
                profile.AcceptedCount++;
                profile.LastEvidenceType = "human_confirmed";
            });
        }

        public static string BuildPromptAddon(KnowledgeBaseEntry entry, KnowledgePolicyEvaluation evaluation)
        {
            if (entry == null || evaluation == null || evaluation.Profile == null) return string.Empty;
            var profile = evaluation.Profile;
            var sb = new StringBuilder();
            sb.Append("\n知识策略：")
                .Append("回答模式=").Append(KnowledgeAnswerModes.Display(profile.AnswerMode))
                .Append("；可靠度=").Append((profile.ReliabilityScore * 100).ToString("0")).Append("%。");
            if (!string.IsNullOrWhiteSpace(profile.ApplyWhen))
                sb.Append("适用条件：").Append(Clean(profile.ApplyWhen, 500)).Append("。");
            if (!string.IsNullOrWhiteSpace(profile.DoNotApplyWhen))
                sb.Append("禁用条件：").Append(Clean(profile.DoNotApplyWhen, 500)).Append("。");
            if (!string.IsNullOrWhiteSpace(profile.RequiredContext))
                sb.Append("必要上下文：").Append(Clean(profile.RequiredContext, 500)).Append("。");
            if (evaluation.ConstraintOnly)
                sb.Append("这条知识只能作为事实边界，禁止机械原样发送整段答案。");
            return sb.ToString();
        }

        private static void Mutate(KnowledgeBaseEntry entry, Action<KnowledgePolicyProfile> action)
        {
            if (entry == null || action == null) return;
            lock (Sync)
            {
                var file = LoadInternal();
                var id = StableId(entry);
                var profile = file.Profiles.FirstOrDefault(x => x != null
                    && string.Equals(x.KnowledgeId, id, StringComparison.Ordinal));
                if (profile == null)
                {
                    profile = NewProfile(entry);
                    file.Profiles.Add(profile);
                }
                action(profile);
                profile.QuestionSnapshot = Clean(entry.Title, 400);
                profile.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                SaveInternal(file);
            }
        }

        private static KnowledgeBaseEntry FindKnowledge(string question, string answer)
        {
            var list = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
            var answerKey = Normalize(answer);
            if (answerKey.Length > 0)
            {
                var byAnswer = list.FirstOrDefault(x => x != null
                    && Normalize(x.Answer) == answerKey);
                if (byAnswer != null) return byAnswer;
            }
            var questionKey = KnowledgeAiService.NormalizeQuestion(question);
            if (questionKey.Length == 0) return null;
            return list.FirstOrDefault(x => x != null
                && KnowledgeAiService.NormalizeQuestion(x.Title) == questionKey);
        }

        private static KnowledgePolicyProfile NewProfile(KnowledgeBaseEntry entry)
        {
            return new KnowledgePolicyProfile
            {
                KnowledgeId = entry == null ? string.Empty : StableId(entry),
                QuestionSnapshot = entry == null ? string.Empty : Clean(entry.Title, 400),
                Intent = InferIntent(entry == null ? string.Empty : entry.Title + " " + entry.Keywords),
                Entities = entry == null ? string.Empty : InferEntities(entry.Title + " " + entry.Keywords),
                ApplyWhen = string.Empty,
                DoNotApplyWhen = string.Empty,
                RequiredContext = string.Empty,
                AnswerMode = KnowledgeAnswerModes.Auto,
                Confidence = 0.80,
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }

        private static string InferIntent(string text)
        {
            text = Compact(text);
            if (Regex.IsMatch(text, "退款|退货|售后|赔偿|投诉")) return "after_sale";
            if (Regex.IsMatch(text, "价格|多少钱|费用|收费")) return "price";
            if (Regex.IsMatch(text, "多久|什么时候|时效|几天")) return "time";
            if (Regex.IsMatch(text, "怎么|如何|步骤|操作|登录|绑定|充值")) return "how_to";
            if (Regex.IsMatch(text, "失败|不行|报错|异常|不能用")) return "troubleshoot";
            if (Regex.IsMatch(text, "支持|可以|能不能|是否")) return "capability";
            return string.Empty;
        }

        private static string InferEntities(string text)
        {
            var matches = Regex.Matches(text ?? string.Empty,
                @"[A-Za-z][A-Za-z0-9\-]{2,}|[\u4e00-\u9fa5]{2,8}(?:会员|账号|设备|电视|手机|软件|应用|服务|链接)");
            return string.Join(",", matches.Cast<Match>()
                .Select(x => x.Value.Trim())
                .Where(x => x.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8));
        }

        private static string BuildContextText(
            string currentQuestion,
            string resolvedQuestion,
            ConversationStateSnapshot state,
            string recentContext)
        {
            var sb = new StringBuilder();
            sb.Append(currentQuestion).Append(' ')
                .Append(resolvedQuestion).Append(' ')
                .Append(recentContext).Append(' ');
            if (state != null)
            {
                sb.Append(state.CurrentTopic).Append(' ')
                    .Append(state.CurrentEntity).Append(' ')
                    .Append(state.BuyerGoal).Append(' ')
                    .Append(state.PendingSellerQuestion).Append(' ')
                    .Append(state.Stage).Append(' ')
                    .Append(string.Join(" ", state.Entities ?? new List<string>())).Append(' ')
                    .Append(string.Join(" ", state.ConfirmedFacts ?? new List<string>()));
            }
            return Compact(sb.ToString());
        }

        private static string ReadAtomicCommerceScope(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.StartsWith("commerce_scope[", StringComparison.OrdinalIgnoreCase)
                && value.EndsWith("]", StringComparison.Ordinal)
                ? value
                : string.Empty;
        }

        private static bool MatchConditions(string conditions, string compactHaystack)
        {
            var items = SplitConditions(conditions);
            return items.Any(item =>
            {
                var compact = Compact(item);
                return compact.Length >= 2 && compactHaystack.Contains(compact);
            });
        }

        private static List<string> SplitConditions(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { '\r', '\n', ';', '；', '|', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool SameAnswer(string left, string right)
        {
            var a = Normalize(left);
            var b = Normalize(right);
            return a.Length > 0 && a == b;
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty)
                .Replace("[AI]", string.Empty)
                .Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }

        private static string StableId(KnowledgeBaseEntry entry)
        {
            if (entry == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(entry.Id)) return entry.Id.Trim();
            return KnowledgeAiService.ContentHash(entry.Title ?? string.Empty, entry.Answer ?? string.Empty);
        }

        private static PolicyFile LoadInternal()
        {
            if (_cache != null) return _cache;
            try
            {
                var path = GetPath();
                _cache = File.Exists(path)
                    ? JsonConvert.DeserializeObject<PolicyFile>(File.ReadAllText(path, Encoding.UTF8))
                    : null;
            }
            catch (Exception ex)
            {
                Log.Info("读取知识策略档案失败，使用空档案：" + ex.Message);
                _cache = null;
            }
            if (_cache == null) _cache = new PolicyFile();
            if (_cache.Profiles == null) _cache.Profiles = new List<KnowledgePolicyProfile>();
            return _cache;
        }

        private static void SaveInternal(PolicyFile file)
        {
            var path = GetPath();
            var directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(file, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            _cache = file;
        }

        private static string GetPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "data",
                "knowledge-policy-profile.json");
        }

        private static KnowledgePolicyProfile Clone(KnowledgePolicyProfile source)
        {
            source = source ?? new KnowledgePolicyProfile();
            return new KnowledgePolicyProfile
            {
                KnowledgeId = source.KnowledgeId,
                QuestionSnapshot = source.QuestionSnapshot,
                Intent = source.Intent,
                Entities = source.Entities,
                ApplyWhen = source.ApplyWhen,
                DoNotApplyWhen = source.DoNotApplyWhen,
                RequiredContext = source.RequiredContext,
                AnswerMode = KnowledgeAnswerModes.Normalize(source.AnswerMode),
                Confidence = source.Confidence,
                DirectSelectedCount = source.DirectSelectedCount,
                ContextualSelectedCount = source.ContextualSelectedCount,
                AcceptedCount = source.AcceptedCount,
                SellerCorrectionCount = source.SellerCorrectionCount,
                SellerWithdrawCount = source.SellerWithdrawCount,
                LastEvidenceType = source.LastEvidenceType,
                UpdatedAt = source.UpdatedAt
            };
        }

        private static string Compact(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s，。！？、；：,.!?:;\-—_()（）\[\]【】]+", string.Empty);
        }

        private static string Clean(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max).Trim();
        }

        private static double Clamp(double value)
        {
            return Math.Max(0, Math.Min(1, value));
        }
    }
}
