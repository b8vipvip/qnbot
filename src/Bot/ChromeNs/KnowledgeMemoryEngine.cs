using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class KnowledgeMemoryCard
    {
        public KnowledgeBaseEntry Entry { get; set; }
        public string KnowledgeId { get; set; }
        public string MemoryType { get; set; }
        public string Intent { get; set; }
        public string CanonicalQuestion { get; set; }
        public string Answer { get; set; }
        public List<string> Aliases { get; set; }
        public List<string> Entities { get; set; }
        public double Confidence { get; set; }
        public double Reliability { get; set; }
        public double Authority { get; set; }
        public string UpdatedAt { get; set; }

        public KnowledgeMemoryCard()
        {
            Aliases = new List<string>();
            Entities = new List<string>();
        }
    }

    internal sealed class KnowledgeMemoryMatch
    {
        public KnowledgeMemoryCard Card { get; set; }
        public double Score { get; set; }
        public double AliasScore { get; set; }
        public double EntityScore { get; set; }
        public double IntentScore { get; set; }
        public double ContextScore { get; set; }
        public double MemoryConfidence { get; set; }
        public bool PolicyAllowsDirect { get; set; }
        public int StrongEntityMatches { get; set; }
        public string Reason { get; set; }
    }

    internal sealed class KnowledgeMemoryDecision
    {
        public bool Enabled { get; set; }
        public bool CanDirectReply { get; set; }
        public bool HasConflict { get; set; }
        public string Reason { get; set; }
        public string Answer { get; set; }
        public KnowledgeMemoryMatch Best { get; set; }
        public List<KnowledgeMemoryMatch> Matches { get; set; }
        public ConversationStateSnapshot WorkingState { get; set; }
        public long ElapsedMilliseconds { get; set; }

        public KnowledgeMemoryDecision()
        {
            Matches = new List<KnowledgeMemoryMatch>();
            Reason = string.Empty;
            Answer = string.Empty;
        }
    }

    internal sealed class KnowledgeMemoryStats
    {
        public bool Enabled { get; set; }
        public int TotalCards { get; set; }
        public int BusinessFacts { get; set; }
        public int Procedures { get; set; }
        public int SafetyBoundaries { get; set; }
        public int Other { get; set; }
        public DateTime BuiltAt { get; set; }
        public string ShopKey { get; set; }
    }

    internal sealed class ConversationWorkingMemorySnapshot
    {
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string CurrentTopic { get; set; }
        public string CurrentEntity { get; set; }
        public string BuyerGoal { get; set; }
        public string Stage { get; set; }
        public List<string> Entities { get; set; }
        public List<string> ConfirmedFacts { get; set; }
        public DateTime UpdatedAt { get; set; }

        public ConversationWorkingMemorySnapshot()
        {
            Entities = new List<string>();
            ConfirmedFacts = new List<string>();
        }
    }

    /// <summary>
    /// ChatGPT-Memory-inspired local knowledge layer.
    ///
    /// It deliberately does not replace the authoritative knowledge base. The knowledge base stays
    /// the source of truth; this engine derives compact memory cards, keeps short-lived per-buyer
    /// working memory, detects conflicting candidates and uses existing human-correction/reliability
    /// evidence before allowing a zero-AI local answer.
    /// </summary>
    internal static class KnowledgeMemoryEngine
    {
        internal const string EnabledSettingsKey = "knowledge.memory_engine.enabled";
        internal const string DirectThresholdSettingsKey = "knowledge.memory_engine.direct_threshold";
        internal const string MinConfidenceSettingsKey = "knowledge.memory_engine.min_confidence";
        internal const string SchemaVersionSettingsKey = "knowledge.memory_engine.schema_version";
        internal const string CurrentSchemaVersion = "1";

        private const double DefaultDirectThreshold = 0.88;
        private const double DefaultMinConfidence = 0.70;
        private const int MaxMatches = 8;

        private static readonly IShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ConcurrentDictionary<string, MemoryIndex> Indexes =
            new ConcurrentDictionary<string, MemoryIndex>(StringComparer.Ordinal);

        private static readonly Regex HighRiskRegex = new Regex(
            @"退款|退货|赔偿|投诉|差评|举报|仲裁|验证码|密码|身份证|银行卡|封号|解封|法律|起诉",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ExplicitSupersedeRegex = new Regex(
            @"不是|不对|说错了|我说的是|改成|算了|不用了|取消|撤回|前面错了",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private sealed class MemoryIndex
        {
            public string ShopKey;
            public long Signature;
            public DateTime BuiltAt;
            public List<KnowledgeMemoryCard> Cards = new List<KnowledgeMemoryCard>();
        }

        public static bool IsEnabled(string seller)
        {
            var shop = ResolveShop(seller);
            if (shop == null) return true;
            try
            {
                string value;
                var store = new ShopScopedSettingsStore(shop, Paths);
                if (!store.TryGetString(EnabledSettingsKey, out value)) return true;
                value = (value ?? string.Empty).Trim();
                return !(value == "0"
                    || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("off", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取Knowledge Memory Engine开关失败，按启用运行: " + ex.Message, 10);
                return true;
            }
        }

        public static void SetEnabled(string seller, bool enabled)
        {
            var shop = ResolveShopRequired(seller);
            var store = new ShopScopedSettingsStore(shop, Paths);
            store.SetString(EnabledSettingsKey, enabled ? "1" : "0");
            store.SetString(SchemaVersionSettingsKey, CurrentSchemaVersion);
            Log.Info("Knowledge Memory Engine已" + (enabled ? "启用" : "关闭") + ": shop=" + shop.ShopKey);
        }

        public static KnowledgeMemoryDecision Resolve(
            string seller,
            string buyer,
            string question,
            IList<ConversationContextTurn> suppliedTurns = null)
        {
            var started = DateTime.UtcNow;
            var decision = new KnowledgeMemoryDecision { Enabled = IsEnabled(seller) };
            if (!decision.Enabled)
            {
                decision.Reason = "Knowledge Memory Engine已关闭";
                return decision;
            }

            question = (question ?? string.Empty).Trim();
            if (question.Length == 0 || IsMediaPlaceholder(question))
            {
                decision.Reason = "空消息或媒体消息不走文本记忆直答";
                return decision;
            }

            var turns = (suppliedTurns ?? ConversationContextStore.GetRecentTurns(seller, buyer, question, 18))
                .Where(x => x != null && !x.Withdrawn && !string.IsNullOrWhiteSpace(x.Text))
                .OrderBy(x => x.Timestamp)
                .ToList();
            var state = ConversationStateService.Build(seller, buyer, question, turns);
            decision.WorkingState = ConversationWorkingMemoryStore.Merge(seller, buyer, state);

            if (ConversationProgressGuardService.RequiresContextualHandling(decision.WorkingState))
            {
                decision.Reason = "当前处于订单/代充等结构化流程，保留上下文链路，不允许记忆卡机械直答";
                decision.ElapsedMilliseconds = Elapsed(started);
                return decision;
            }

            var index = GetOrBuildIndex(seller);
            if (index == null || index.Cards.Count == 0)
            {
                decision.Reason = "当前知识库没有可用记忆卡";
                decision.ElapsedMilliseconds = Elapsed(started);
                return decision;
            }

            var query = BuildQuery(question, decision.WorkingState, turns);
            var matches = index.Cards
                .Select(card => Score(card, query, question, decision.WorkingState, turns))
                .Where(x => x != null && x.Score >= 0.30)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.MemoryConfidence)
                .Take(MaxMatches)
                .ToList();
            decision.Matches = matches;
            decision.Best = matches.FirstOrDefault();
            if (decision.Best == null)
            {
                decision.Reason = "记忆索引没有找到足够相关的候选";
                decision.ElapsedMilliseconds = Elapsed(started);
                return decision;
            }

            var second = matches.Count > 1 ? matches[1] : null;
            var margin = second == null ? decision.Best.Score : decision.Best.Score - second.Score;
            var answersEquivalent = AnswersEquivalent(decision.Best, second);
            var effectiveMargin = answersEquivalent ? Math.Max(margin, 0.08) : margin;
            decision.HasConflict = HasMaterialConflict(decision.Best, second, margin);

            var threshold = ReadDouble(seller, DirectThresholdSettingsKey, DefaultDirectThreshold, 0.78, 0.98);
            var minConfidence = ReadDouble(seller, MinConfidenceSettingsKey, DefaultMinConfidence, 0.55, 0.95);
            var strongMeaning = IsStrongMeaningMatch(question, decision.Best, decision.WorkingState);
            var highRisk = HighRiskRegex.IsMatch(question)
                || HighRiskRegex.IsMatch(decision.Best.Card == null ? string.Empty : decision.Best.Card.Answer ?? string.Empty);

            decision.CanDirectReply = ReplyModeService.IsLocalFirst(seller)
                && !decision.HasConflict
                && !highRisk
                && !ExplicitSupersedeRegex.IsMatch(question)
                && decision.Best.PolicyAllowsDirect
                && decision.Best.Score >= threshold
                && decision.Best.MemoryConfidence >= minConfidence
                && effectiveMargin >= 0.055
                && strongMeaning;

            decision.Answer = decision.CanDirectReply && decision.Best.Card != null
                ? (decision.Best.Card.Answer ?? string.Empty).Trim()
                : string.Empty;
            decision.Reason = decision.CanDirectReply
                ? "本地记忆高置信直答：score=" + decision.Best.Score.ToString("0.00")
                    + ", confidence=" + decision.Best.MemoryConfidence.ToString("0.00")
                    + ", margin=" + effectiveMargin.ToString("0.00")
                    + (answersEquivalent ? "（近似同答案候选已合并判断）" : string.Empty)
                : BuildRejectReason(decision, threshold, minConfidence, effectiveMargin, strongMeaning, highRisk);
            decision.ElapsedMilliseconds = Elapsed(started);
            return decision;
        }

        public static KnowledgeMemoryStats GetStats(string seller)
        {
            var index = GetOrBuildIndex(seller);
            var cards = index == null ? new List<KnowledgeMemoryCard>() : index.Cards;
            return new KnowledgeMemoryStats
            {
                Enabled = IsEnabled(seller),
                ShopKey = index == null ? string.Empty : index.ShopKey,
                BuiltAt = index == null ? DateTime.MinValue : index.BuiltAt,
                TotalCards = cards.Count,
                BusinessFacts = cards.Count(x => x.MemoryType == "business_fact"),
                Procedures = cards.Count(x => x.MemoryType == "procedure"),
                SafetyBoundaries = cards.Count(x => x.MemoryType == "safety_boundary"),
                Other = cards.Count(x => x.MemoryType == "general")
            };
        }

        public static void Rebuild(string seller)
        {
            var shop = ResolveShop(seller);
            var key = shop == null ? NormalizeSeller(seller) : shop.ShopKey;
            MemoryIndex ignored;
            Indexes.TryRemove(key, out ignored);
            GetOrBuildIndex(seller);
        }

        public static string FormatDecision(KnowledgeMemoryDecision decision)
        {
            if (decision == null) return "没有记忆检索结果。";
            var sb = new StringBuilder();
            sb.Append("状态：").Append(decision.Enabled ? "启用" : "关闭").Append("\r\n")
                .Append("可本地直答：").Append(decision.CanDirectReply ? "是" : "否").Append("\r\n")
                .Append("冲突：").Append(decision.HasConflict ? "是" : "否").Append("\r\n")
                .Append("耗时：").Append(decision.ElapsedMilliseconds).Append(" ms\r\n")
                .Append("原因：").Append(decision.Reason ?? string.Empty).Append("\r\n");
            if (decision.WorkingState != null)
            {
                sb.Append("Working Memory：entity=").Append(decision.WorkingState.CurrentEntity ?? string.Empty)
                    .Append("；goal=").Append(decision.WorkingState.BuyerGoal ?? string.Empty)
                    .Append("；stage=").Append(decision.WorkingState.Stage ?? string.Empty).Append("\r\n");
            }
            for (var i = 0; i < decision.Matches.Count; i++)
            {
                var match = decision.Matches[i];
                if (match == null || match.Card == null) continue;
                sb.Append("\r\n#").Append(i + 1)
                    .Append(" score=").Append(match.Score.ToString("0.00"))
                    .Append(" confidence=").Append(match.MemoryConfidence.ToString("0.00"))
                    .Append(" type=").Append(match.Card.MemoryType)
                    .Append(" intent=").Append(match.Card.Intent)
                    .Append(" entities=").Append(match.StrongEntityMatches)
                    .Append("\r\nQ: ").Append(Safe(match.Card.CanonicalQuestion, 260))
                    .Append("\r\nA: ").Append(Safe(match.Card.Answer, 500))
                    .Append("\r\n原因: ").Append(match.Reason ?? string.Empty);
            }
            return sb.ToString();
        }

        private static MemoryIndex GetOrBuildIndex(string seller)
        {
            var shop = ResolveShop(seller);
            var key = shop == null ? NormalizeSeller(seller) : shop.ShopKey;
            var knowledge = (BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>())
                .Where(x => x != null && x.Enabled
                    && !Bot.Knowledge.KnowledgeV2AuthorityPolicy.IsUnapprovedLegacyLearningEntry(x))
                .ToList();
            var signature = ComputeSignature(knowledge);
            MemoryIndex existing;
            if (Indexes.TryGetValue(key, out existing)
                && existing != null
                && existing.Signature == signature
                && existing.BuiltAt >= DateTime.Now.AddMinutes(-10))
            {
                return existing;
            }

            var cards = knowledge
                .Where(x => !string.IsNullOrWhiteSpace(x.Answer))
                .Select(BuildCard)
                .Where(x => x != null)
                .ToList();
            var rebuilt = new MemoryIndex
            {
                ShopKey = key,
                Signature = signature,
                BuiltAt = DateTime.Now,
                Cards = cards
            };
            Indexes[key] = rebuilt;
            TryMarkMigrated(shop);
            Log.Info("Knowledge Memory索引已构建: shop=" + key + ", cards=" + cards.Count
                + ", signature=" + signature);
            return rebuilt;
        }

        private static KnowledgeMemoryCard BuildCard(KnowledgeBaseEntry entry)
        {
            if (entry == null) return null;
            var profile = KnowledgePolicyProfileService.GetProfile(entry);
            var intent = NormalizeIntent(profile.Intent);
            if (string.IsNullOrWhiteSpace(intent) || intent == "general")
            {
                intent = DetectMemoryIntent((entry.Title ?? string.Empty) + " "
                    + (entry.Keywords ?? string.Empty) + " " + (entry.Answer ?? string.Empty));
            }

            var entities = new List<string>();
            entities.AddRange(SplitTerms(profile.Entities));
            entities.AddRange(SplitTerms(entry.Keywords));
            entities.AddRange(ExtractDomainEntities((entry.Title ?? string.Empty) + " "
                + (entry.Category ?? string.Empty) + " " + (entry.Answer ?? string.Empty)));
            entities = entities
                .Select(CleanTerm)
                .Where(x => x.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(18)
                .ToList();

            var aliases = new List<string>
            {
                entry.Title ?? string.Empty,
                StripDemonstratives(entry.Title)
            };
            aliases.AddRange(SplitTerms(entry.Keywords));
            if (!string.IsNullOrWhiteSpace(entry.Category)) aliases.Add(entry.Category);
            aliases.AddRange(entities);
            aliases = aliases
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => x.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(28)
                .ToList();

            var mode = KnowledgeAnswerModes.Normalize(profile.AnswerMode);
            var authority = mode == KnowledgeAnswerModes.Direct ? 0.96
                : (mode == KnowledgeAnswerModes.Constraint ? 0.92 : 0.90);
            return new KnowledgeMemoryCard
            {
                Entry = entry,
                KnowledgeId = StableId(entry),
                MemoryType = ResolveMemoryType(intent, entry.Answer),
                Intent = intent,
                CanonicalQuestion = (entry.Title ?? string.Empty).Trim(),
                Answer = (entry.Answer ?? string.Empty).Trim(),
                Aliases = aliases,
                Entities = entities,
                Confidence = Clamp(profile.Confidence <= 0 ? 0.80 : profile.Confidence),
                Reliability = Clamp(profile.ReliabilityScore),
                Authority = authority,
                UpdatedAt = profile.UpdatedAt ?? string.Empty
            };
        }

        private static KnowledgeMemoryMatch Score(
            KnowledgeMemoryCard card,
            string query,
            string rawQuestion,
            ConversationStateSnapshot state,
            IList<ConversationContextTurn> turns)
        {
            if (card == null || card.Entry == null) return null;
            var normalizedQuery = Compact(query);
            if (normalizedQuery.Length == 0) return null;

            var aliasScore = 0.0;
            foreach (var alias in card.Aliases)
            {
                var normalizedAlias = Compact(alias);
                if (normalizedAlias.Length < 2) continue;
                var local = TextSimilarity(normalizedQuery, normalizedAlias);
                if (normalizedQuery == normalizedAlias) local = 1.0;
                else if (normalizedQuery.Contains(normalizedAlias) || normalizedAlias.Contains(normalizedQuery))
                    local = Math.Max(local, 0.92);
                aliasScore = Math.Max(aliasScore, local);
            }
            aliasScore = Math.Max(aliasScore,
                TextSimilarity(Compact(StripDemonstratives(rawQuestion)), Compact(card.CanonicalQuestion)));

            // Current-message entities are intentionally weighted before inherited context entities.
            // This prevents an older product/topic from overpowering a clear new buyer question.
            var currentEntities = ExtractDomainEntities(rawQuestion)
                .Select(CleanTerm).Where(x => x.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var inheritedEntities = state == null || state.Entities == null
                ? new List<string>()
                : state.Entities.Select(CleanTerm).Where(x => x.Length >= 2)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var currentMatched = CountEntityMatches(currentEntities, card.Entities);
            var inheritedMatched = CountEntityMatches(inheritedEntities, card.Entities);
            var currentEntityScore = currentEntities.Count == 0 ? 0
                : Clamp(currentMatched / (double)Math.Min(4, currentEntities.Count));
            var inheritedEntityScore = inheritedEntities.Count == 0 ? 0
                : Clamp(inheritedMatched / (double)Math.Min(4, inheritedEntities.Count));
            var entityScore = Clamp(currentEntityScore * 0.80 + inheritedEntityScore * 0.20);

            var queryIntent = DetectMemoryIntent(rawQuestion);
            if (queryIntent == "general" && state != null)
                queryIntent = NormalizeIntent(state.BuyerGoal);
            var intentScore = string.IsNullOrWhiteSpace(queryIntent) || string.IsNullOrWhiteSpace(card.Intent)
                ? 0.35
                : (string.Equals(queryIntent, card.Intent, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0);

            var recent = string.Join(" ", (turns ?? new List<ConversationContextTurn>())
                .Skip(Math.Max(0, (turns == null ? 0 : turns.Count) - 4))
                .Select(x => x.Text ?? string.Empty));
            var contextScore = TextSimilarity(
                Compact(recent),
                Compact(card.CanonicalQuestion + " " + string.Join(" ", card.Entities)));

            var memoryConfidence = Clamp(card.Confidence * 0.44 + card.Reliability * 0.36 + card.Authority * 0.20);
            var score = Clamp(aliasScore * 0.50
                + entityScore * 0.22
                + intentScore * 0.15
                + contextScore * 0.05
                + memoryConfidence * 0.08);

            // Semantic-key boost: a paraphrase does not need to look textually identical when its
            // intent and multiple strong business entities line up. This is the local equivalent of
            // resolving a memory by meaning instead of by raw wording.
            if (intentScore >= 0.99 && currentMatched >= 2)
            {
                var semanticFloor = currentMatched >= 3 ? 0.92 : 0.89;
                if (aliasScore >= 0.72) semanticFloor += 0.025;
                score = Math.Max(score, Math.Min(0.96, semanticFloor));
            }

            var policy = KnowledgePolicyProfileService.Evaluate(
                card.Entry,
                rawQuestion,
                query,
                state,
                recent);
            if (policy != null)
            {
                if (policy.Excluded) score = Math.Min(score, 0.20);
                else score = Clamp(score + Math.Max(-0.10, Math.Min(0.08, policy.ScoreAdjustment)));
            }
            var allowsDirect = policy == null
                || (policy.AllowDirect && !policy.ForceContextual && !policy.ConstraintOnly && !policy.Excluded);

            return new KnowledgeMemoryMatch
            {
                Card = card,
                Score = score,
                AliasScore = aliasScore,
                EntityScore = entityScore,
                IntentScore = intentScore,
                ContextScore = contextScore,
                MemoryConfidence = memoryConfidence,
                PolicyAllowsDirect = allowsDirect,
                StrongEntityMatches = currentMatched,
                Reason = "alias=" + aliasScore.ToString("0.00")
                    + ", entity=" + entityScore.ToString("0.00")
                    + ", intent=" + intentScore.ToString("0.00")
                    + ", currentEntityMatches=" + currentMatched
                    + ", context=" + contextScore.ToString("0.00")
            };
        }

        private static bool HasMaterialConflict(KnowledgeMemoryMatch best, KnowledgeMemoryMatch second, double margin)
        {
            if (best == null || second == null || best.Card == null || second.Card == null) return false;
            if (best.Score < 0.82 || second.Score < 0.80 || margin >= 0.07) return false;
            if (!string.Equals(best.Card.Intent, second.Card.Intent, StringComparison.OrdinalIgnoreCase)) return false;
            var answerSimilarity = TextSimilarity(Compact(best.Card.Answer), Compact(second.Card.Answer));
            return answerSimilarity < 0.55;
        }

        private static bool AnswersEquivalent(KnowledgeMemoryMatch best, KnowledgeMemoryMatch second)
        {
            if (best == null || second == null || best.Card == null || second.Card == null) return false;
            if (best.Score < 0.80 || second.Score < 0.76) return false;
            return TextSimilarity(Compact(best.Card.Answer), Compact(second.Card.Answer)) >= 0.72;
        }

        private static bool IsStrongMeaningMatch(
            string question,
            KnowledgeMemoryMatch best,
            ConversationStateSnapshot state)
        {
            if (best == null || best.Card == null) return false;
            var stripped = Compact(StripDemonstratives(question));
            if (stripped.Length < 4) return false;
            if (best.AliasScore >= 0.90) return true;
            if (best.StrongEntityMatches >= 2 && best.IntentScore >= 0.95) return true;
            if (best.EntityScore >= 0.66 && best.IntentScore >= 0.95 && best.AliasScore >= 0.62) return true;
            if (state != null && !string.IsNullOrWhiteSpace(state.CurrentEntity)
                && best.EntityScore >= 0.50 && best.AliasScore >= 0.76) return true;
            return false;
        }

        private static string BuildQuery(
            string question,
            ConversationStateSnapshot state,
            IList<ConversationContextTurn> turns)
        {
            var pieces = new List<string> { StripDemonstratives(question) };
            if (state != null)
            {
                if (!string.IsNullOrWhiteSpace(state.CurrentEntity)) pieces.Add(state.CurrentEntity);
                if (!string.IsNullOrWhiteSpace(state.CurrentTopic)) pieces.Add(state.CurrentTopic);
                if (state.Entities != null) pieces.AddRange(state.Entities.Take(4));
            }
            if (Compact(question).Length <= 10)
            {
                var previousBuyer = (turns ?? new List<ConversationContextTurn>())
                    .LastOrDefault(x => x != null && x.Role == "user" && !string.IsNullOrWhiteSpace(x.Text)
                        && !SameCompact(x.Text, question));
                if (previousBuyer != null) pieces.Add(previousBuyer.Text);
            }
            return string.Join(" ", pieces.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static string DetectMemoryIntent(string text)
        {
            var value = Compact(text);
            if (value.Length == 0) return "general";
            if (Regex.IsMatch(value, @"退款|退货|售后|赔偿|投诉")) return "after_sale";
            if (Regex.IsMatch(value, @"多少钱|价格|费用|收费")) return "price";
            if (Regex.IsMatch(value, @"多久|什么时候|几天|时效")) return "time";
            if (Regex.IsMatch(value, @"怎么|如何|步骤|操作|登录|绑定|充值方法|怎么充|如何充")) return "how_to";
            if (Regex.IsMatch(value, @"为什么|失败|不行|报错|异常|不能用|没反应")) return "troubleshoot";
            if (Regex.IsMatch(value, @"需要什么|提供什么|要什么|资料|条件")) return "requirements";
            if (Regex.IsMatch(value, @"是不是|是否|能不能|能否|可以|支持|可不可以|是.+吗|会员吗|能用吗|可以用吗"))
                return "capability";
            return "general";
        }

        private static string ResolveMemoryType(string intent, string answer)
        {
            if (HighRiskRegex.IsMatch(answer ?? string.Empty)) return "safety_boundary";
            if (intent == "how_to" || Regex.IsMatch(answer ?? string.Empty, @"步骤|打开|进入|点击|绑定|登录|充值|操作"))
                return "procedure";
            if (intent == "capability" || intent == "price" || intent == "time" || intent == "requirements")
                return "business_fact";
            return "general";
        }

        private static string NormalizeIntent(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0) return string.Empty;
            if (value.Contains("售后") || value == "after_sale") return "after_sale";
            if (value.Contains("价格") || value == "price") return "price";
            if (value.Contains("时间") || value.Contains("时效") || value == "time") return "time";
            if (value.Contains("操作") || value.Contains("方法") || value == "how_to") return "how_to";
            if (value.Contains("故障") || value == "troubleshoot") return "troubleshoot";
            if (value.Contains("支持") || value == "capability") return "capability";
            if (value.Contains("条件") || value == "requirements") return "requirements";
            if (value.Contains("一般咨询") || value == "general") return "general";
            return value;
        }

        private static IEnumerable<string> ExtractDomainEntities(string value)
        {
            var result = new List<string>();
            value = value ?? string.Empty;
            foreach (Match match in Regex.Matches(value,
                @"超级会员|豪华会员|电视机|电视|TV|大屏|车机|手机|电脑|平板|账号|会员|充值|验证码|订单|退款|支付宝|微信|APP|软件",
                RegexOptions.IgnoreCase))
            {
                result.Add(match.Value);
            }
            foreach (Match match in Regex.Matches(value,
                @"[\u4e00-\u9fff]{2,8}(?=(?:音乐|视频|应用|软件|APP|TV))",
                RegexOptions.IgnoreCase))
            {
                result.Add(match.Value);
            }
            foreach (Match match in Regex.Matches(value, @"[A-Za-z][A-Za-z0-9\-]{2,}"))
            {
                result.Add(match.Value);
            }
            return result;
        }

        private static int CountEntityMatches(IEnumerable<string> queryEntities, IEnumerable<string> cardEntities)
        {
            var q = (queryEntities ?? Enumerable.Empty<string>()).Select(Compact).Where(x => x.Length >= 2).Distinct().ToList();
            var c = (cardEntities ?? Enumerable.Empty<string>()).Select(Compact).Where(x => x.Length >= 2).Distinct().ToList();
            return q.Count(query => c.Any(card => card.Contains(query) || query.Contains(card)));
        }

        internal static double TextSimilarity(string left, string right)
        {
            left = Compact(left);
            right = Compact(right);
            if (left.Length == 0 || right.Length == 0) return 0;
            if (left == right) return 1;
            if (Math.Min(left.Length, right.Length) >= 4 && (left.Contains(right) || right.Contains(left))) return 0.92;
            var a = Bigrams(left);
            var b = Bigrams(right);
            if (a.Count == 0 || b.Count == 0) return left == right ? 1 : 0;
            var common = a.Intersect(b, StringComparer.Ordinal).Count();
            return Clamp((2.0 * common) / (a.Count + b.Count));
        }

        private static HashSet<string> Bigrams(string value)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (value.Length == 1) { result.Add(value); return result; }
            for (var i = 0; i < value.Length - 1; i++) result.Add(value.Substring(i, 2));
            return result;
        }

        private static string StripDemonstratives(string value)
        {
            value = (value ?? string.Empty).Trim();
            value = Regex.Replace(value, @"^(请问|麻烦问下|想问下|我想问|这个|那个|这款|那款|这种|那种)+", string.Empty,
                RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"(吗|呢|呀|啊|嘛)[？?！!。\.]*$", string.Empty);
            return value.Trim();
        }

        private static IEnumerable<string> SplitTerms(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '|', '/', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length >= 2);
        }

        private static string CleanTerm(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim(), @"^[\s，。！？、；：,.!?:;]+|[\s，。！？、；：,.!?:;]+$", string.Empty);
        }

        private static string Compact(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s，。！？、；：,.!?:;\-—_()（）\[\]【】]+", string.Empty);
        }

        private static bool SameCompact(string left, string right)
        {
            return string.Equals(Compact(left), Compact(right), StringComparison.Ordinal);
        }

        private static long ComputeSignature(IEnumerable<KnowledgeBaseEntry> knowledge)
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                foreach (var item in (knowledge ?? Enumerable.Empty<KnowledgeBaseEntry>()).Where(x => x != null))
                {
                    var text = StableId(item) + "|" + (item.Title ?? string.Empty) + "|" + (item.Answer ?? string.Empty)
                        + "|" + (item.Keywords ?? string.Empty) + "|" + item.Enabled;
                    foreach (var ch in text)
                    {
                        hash ^= ch;
                        hash *= 1099511628211L;
                    }
                }
                return hash;
            }
        }

        private static string StableId(KnowledgeBaseEntry entry)
        {
            if (entry == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(entry.Id)) return entry.Id.Trim();
            return KnowledgeAiService.ContentHash(entry.Title ?? string.Empty, entry.Answer ?? string.Empty);
        }

        private static ShopContext ResolveShop(string seller)
        {
            try
            {
                var current = ShopSettingsScope.Current;
                if (current != null) return current;
                seller = NormalizeSeller(seller);
                return seller.Length == 0 ? null : ShopContextLocator.ResolveBySellerNick(seller);
            }
            catch { return null; }
        }

        private static ShopContext ResolveShopRequired(string seller)
        {
            var shop = ResolveShop(seller);
            if (shop == null) throw new InvalidOperationException("Knowledge Memory Engine需要当前店铺身份。");
            return shop;
        }

        private static void TryMarkMigrated(ShopContext shop)
        {
            if (shop == null) return;
            try
            {
                var store = new ShopScopedSettingsStore(shop, Paths);
                string version;
                if (!store.TryGetString(SchemaVersionSettingsKey, out version)
                    || !string.Equals(version, CurrentSchemaVersion, StringComparison.Ordinal))
                {
                    store.SetString(SchemaVersionSettingsKey, CurrentSchemaVersion);
                    Log.Info("现有知识库已无损升级为Knowledge Memory Engine v1派生记忆卡: shop=" + shop.ShopKey);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("记录Knowledge Memory升级状态失败: " + ex.Message, 10);
            }
        }

        private static double ReadDouble(string seller, string key, double fallback, double min, double max)
        {
            var shop = ResolveShop(seller);
            if (shop == null) return fallback;
            try
            {
                string value;
                double parsed;
                var store = new ShopScopedSettingsStore(shop, Paths);
                if (store.TryGetString(key, out value)
                    && double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    return Math.Max(min, Math.Min(max, parsed));
                }
            }
            catch { }
            return fallback;
        }

        private static bool IsMediaPlaceholder(string value)
        {
            var compact = (value ?? string.Empty).Trim();
            return compact == "[图片]" || compact == "[视频]" || compact == "[语音]" || compact == "[表情]";
        }

        private static string BuildRejectReason(
            KnowledgeMemoryDecision decision,
            double threshold,
            double minConfidence,
            double margin,
            bool strongMeaning,
            bool highRisk)
        {
            var best = decision == null ? null : decision.Best;
            if (best == null) return "没有记忆候选";
            if (decision.HasConflict) return "高分记忆之间存在实质答案冲突，禁止本地直答";
            if (highRisk) return "命中高风险内容，必须走上下文/AI安全链路";
            if (!best.PolicyAllowsDirect) return "知识策略/必要上下文要求禁止机械直答";
            if (!strongMeaning) return "实体/意图/别名组合仍不足以证明当前问题与记忆完全同义";
            if (best.Score < threshold) return "记忆匹配分不足：" + best.Score.ToString("0.00") + " < " + threshold.ToString("0.00");
            if (best.MemoryConfidence < minConfidence) return "记忆可信度不足：" + best.MemoryConfidence.ToString("0.00") + " < " + minConfidence.ToString("0.00");
            if (margin < 0.055) return "前两条记忆分差过小，需要结合上下文判断";
            return "当前回复模式或安全门控不允许本地直答";
        }

        private static string NormalizeSeller(string seller)
        {
            return (seller ?? string.Empty).Trim();
        }

        private static long Elapsed(DateTime startedUtc)
        {
            return Math.Max(0, (long)(DateTime.UtcNow - startedUtc).TotalMilliseconds);
        }

        private static double Clamp(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            return Math.Max(0, Math.Min(1, value));
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }

    internal static class ConversationWorkingMemoryStore
    {
        private static readonly ConcurrentDictionary<string, ConversationWorkingMemorySnapshot> Store =
            new ConcurrentDictionary<string, ConversationWorkingMemorySnapshot>(StringComparer.Ordinal);
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(45);

        public static ConversationStateSnapshot Merge(string seller, string buyer, ConversationStateSnapshot current)
        {
            current = current ?? new ConversationStateSnapshot();
            CleanupExpired();
            var key = (seller ?? string.Empty).Trim() + "|" + (buyer ?? string.Empty).Trim();
            var previous = Store.GetOrAdd(key, _ => new ConversationWorkingMemorySnapshot
            {
                Seller = seller ?? string.Empty,
                Buyer = buyer ?? string.Empty,
                UpdatedAt = DateTime.Now
            });
            lock (previous)
            {
                if (!string.IsNullOrWhiteSpace(current.CurrentTopic)) previous.CurrentTopic = current.CurrentTopic;
                if (!string.IsNullOrWhiteSpace(current.CurrentEntity)) previous.CurrentEntity = current.CurrentEntity;
                if (!string.IsNullOrWhiteSpace(current.BuyerGoal) && current.BuyerGoal != "一般咨询") previous.BuyerGoal = current.BuyerGoal;
                if (!string.IsNullOrWhiteSpace(current.Stage)) previous.Stage = current.Stage;
                previous.Entities = MergeList(previous.Entities, current.Entities, 10);
                previous.ConfirmedFacts = MergeList(previous.ConfirmedFacts, current.ConfirmedFacts, 8);
                previous.UpdatedAt = DateTime.Now;

                return new ConversationStateSnapshot
                {
                    CurrentTopic = First(current.CurrentTopic, previous.CurrentTopic),
                    CurrentEntity = First(current.CurrentEntity, previous.CurrentEntity),
                    BuyerGoal = FirstUsefulGoal(current.BuyerGoal, previous.BuyerGoal),
                    PendingQuestion = current.PendingQuestion,
                    ConversationStage = First(current.Stage, previous.Stage),
                    Entities = MergeList(current.Entities, previous.Entities, 10),
                    ConfirmedFacts = MergeList(current.ConfirmedFacts, previous.ConfirmedFacts, 8),
                    Progress = current.Progress
                };
            }
        }

        public static void Forget(string seller, string buyer)
        {
            ConversationWorkingMemorySnapshot ignored;
            Store.TryRemove((seller ?? string.Empty).Trim() + "|" + (buyer ?? string.Empty).Trim(), out ignored);
        }

        private static List<string> MergeList(IEnumerable<string> first, IEnumerable<string> second, int max)
        {
            return (first ?? Enumerable.Empty<string>())
                .Concat(second ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToList();
        }

        private static string First(string preferred, string fallback)
        {
            return !string.IsNullOrWhiteSpace(preferred) ? preferred : (fallback ?? string.Empty);
        }

        private static string FirstUsefulGoal(string preferred, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(preferred) && preferred != "一般咨询") return preferred;
            return First(fallback, preferred);
        }

        private static void CleanupExpired()
        {
            var cutoff = DateTime.Now - Ttl;
            foreach (var pair in Store.ToArray())
            {
                if (pair.Value == null || pair.Value.UpdatedAt < cutoff)
                {
                    ConversationWorkingMemorySnapshot ignored;
                    Store.TryRemove(pair.Key, out ignored);
                }
            }
        }
    }

    /// <summary>
    /// Runtime wrapper inserted around BuyerMessageBurstCoordinator. It only intercepts a message
    /// when the memory engine can prove a high-confidence local answer; otherwise it delegates to
    /// the existing Smart Reply/streaming pipeline unchanged.
    /// </summary>
    internal static class KnowledgeMemoryRuntimeBridge
    {
        private sealed class MemoryHandlerWrapper
        {
            private readonly QN _qn;
            private readonly Func<BuyerMessageBurstLease, Task> _inner;

            public MemoryHandlerWrapper(QN qn, Func<BuyerMessageBurstLease, Task> inner)
            {
                _qn = qn;
                _inner = inner;
            }

            public Task HandleAsync(BuyerMessageBurstLease lease)
            {
                return KnowledgeMemoryRuntimeBridge.HandleAsync(_qn, _inner, lease);
            }
        }

        private static Timer _timer;
        private static int _initialized;
        private static int _disabledForV2;
        private static readonly object PatchSync = new object();

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                if (Volatile.Read(ref _disabledForV2) != 0) return new object();
                try { Bot.Knowledge.KnowledgeMemoryEngineUi.Initialize(); } catch { }
                // BuyerStreamingReplyPipeline installs its wrapper very early (100ms). Start later
                // and keep checking so Memory remains the outermost text decision layer even if a
                // coordinator is created after startup. V2 shutdown and timer creation share
                // PatchSync so initialization cannot race past a completed V2 disable.
                lock (PatchSync)
                {
                    if (Volatile.Read(ref _disabledForV2) != 0) return new object();
                    _timer = new Timer(_ => PatchExisting(), null, 700, 700);
                }
                Log.Info("Knowledge Memory Engine v1已启动：权威知识库 + Working Memory + Learned Reliability；仅高置信本地直答，其他请求保持原Smart Reply/AI链路。");
            }
            return new object();
        }

        public static void DisableForV2()
        {
            Interlocked.Exchange(ref _disabledForV2, 1);
            // Serialize timer disposal, initialization and handler unpatching with PatchExisting.
            // This prevents a callback or late InitializeForApp from re-installing V1 after V2 starts.
            lock (PatchSync)
            {
                try
                {
                    var timer = Interlocked.Exchange(ref _timer, null);
                    if (timer != null) timer.Dispose();
                }
                catch { }

                try
                {
                    QN[] qns;
                    try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray(); }
                    catch { qns = new QN[0]; }

                    var coordinatorField = typeof(QN).GetField("_buyerMessageBurstCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
                    var handlerField = typeof(BuyerMessageBurstCoordinator).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic);
                    var wrapperType = typeof(KnowledgeMemoryRuntimeBridge).GetNestedType("MemoryHandlerWrapper", BindingFlags.NonPublic);
                    var innerField = wrapperType == null ? null : wrapperType.GetField("_inner", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (coordinatorField == null || handlerField == null || wrapperType == null || innerField == null) return;

                    foreach (var qn in qns)
                    {
                        if (qn == null) continue;
                        var coordinator = coordinatorField.GetValue(qn) as BuyerMessageBurstCoordinator;
                        if (coordinator == null) continue;
                        var current = handlerField.GetValue(coordinator) as Func<BuyerMessageBurstLease, Task>;
                        if (current == null || current.Target == null || !wrapperType.IsInstanceOfType(current.Target)) continue;
                        var inner = innerField.GetValue(current.Target) as Func<BuyerMessageBurstLease, Task>;
                        if (inner == null) continue;
                        handlerField.SetValue(coordinator, inner);
                        Log.Info("Knowledge Memory V1包装器已卸载，恢复原消息处理链: seller="
                            + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("卸载Knowledge Memory V1包装器失败: " + ex.Message, 10);
                }
            }
        }

        private static void PatchExisting()
        {
            if (Volatile.Read(ref _disabledForV2) != 0) return;
            lock (PatchSync)
            {
                if (Volatile.Read(ref _disabledForV2) != 0) return;
                try
                {
                    QN[] qns;
                    try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray(); }
                    catch { return; }

                    var coordinatorField = typeof(QN).GetField("_buyerMessageBurstCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
                    var handlerField = typeof(BuyerMessageBurstCoordinator).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (coordinatorField == null || handlerField == null) return;

                    foreach (var qn in qns)
                    {
                        if (qn == null) continue;
                        var coordinator = coordinatorField.GetValue(qn) as BuyerMessageBurstCoordinator;
                        if (coordinator == null) continue;
                        var current = handlerField.GetValue(coordinator) as Func<BuyerMessageBurstLease, Task>;
                        if (current == null || current.Target is MemoryHandlerWrapper) continue;

                        var wrapper = new MemoryHandlerWrapper(qn, current);
                        handlerField.SetValue(coordinator, new Func<BuyerMessageBurstLease, Task>(wrapper.HandleAsync));
                        Log.Info("已为客服实例挂载Knowledge Memory本地直答层: seller="
                            + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("安装Knowledge Memory运行时桥失败，继续原Smart Reply链路: " + ex.Message, 10);
                }
            }
        }

        private static async Task HandleAsync(QN qn, Func<BuyerMessageBurstLease, Task> inner, BuyerMessageBurstLease lease)
        {
            if (Volatile.Read(ref _disabledForV2) != 0)
            {
                await inner(lease);
                return;
            }
            var burst = lease == null ? null : lease.Burst;
            if (qn == null || burst == null || burst.Items.Count < 1 || burst.LatestVisionItem != null || !burst.HasReplyableItem)
            {
                await inner(lease);
                return;
            }
            if (!ReplyModeService.IsLocalFirst(burst.SellerNick) || !KnowledgeMemoryEngine.IsEnabled(burst.SellerNick))
            {
                await inner(lease);
                return;
            }

            KnowledgeMemoryDecision decision;
            try
            {
                decision = KnowledgeMemoryEngine.Resolve(burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("Knowledge Memory检索失败，回退原Smart Reply: buyer=" + burst.BuyerNick + ", error=" + ex.Message, 20);
                await inner(lease);
                return;
            }

            if (decision == null || !decision.CanDirectReply || string.IsNullOrWhiteSpace(decision.Answer))
            {
                Log.Info("Knowledge Memory未直答，继续Smart Reply: buyer=" + burst.BuyerNick
                    + ", ms=" + (decision == null ? 0 : decision.ElapsedMilliseconds)
                    + ", reason=" + (decision == null ? "无决策" : decision.Reason));
                await inner(lease);
                return;
            }

            var detectedAt = burst.Items.Min(x => x.ReceivedAt);
            var ctl = ResponseProgressTracker.BeginAnswer(
                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt);
            var answer = BotMessageSuffixService.Apply(burst.SellerNick, decision.Answer);
            var dedup = ReplyDeduplicationService.EnsureDistinct(
                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, answer);
            answer = dedup.Answer;

            var readyAt = DateTime.Now;
            ctl = ResponseProgressTracker.SetAnswerReady(
                burst.SellerNick,
                burst.BuyerNick,
                burst.CombinedQuestion,
                answer,
                "本地记忆",
                detectedAt,
                readyAt);
            var autoSend = Params.Robot.GetIsAutoReply();
            BotRuntimeStats.RecordDisplayedAnswer(autoSend);
            Log.Info("Knowledge Memory本地答案已就绪: buyer=" + burst.BuyerNick
                + ", score=" + decision.Best.Score.ToString("0.00")
                + ", confidence=" + decision.Best.MemoryConfidence.ToString("0.00")
                + ", lookupMs=" + decision.ElapsedMilliseconds
                + ", totalToAnswerMs=" + Math.Max(0, (long)(readyAt - detectedAt).TotalMilliseconds));

            if (!autoSend)
            {
                if (ctl != null) ctl.SetStatus("仅生成答案（Knowledge Memory本地命中）", true);
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);
                return;
            }

            if (!lease.IsCurrent || !await lease.ConfirmStableAsync(80))
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：任务已被人工接管或显式取消");
                return;
            }

            string relevanceReason;
            if (!ParallelReplyRelevanceGate.ShouldSend(
                burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt,
                out relevanceReason, false, burst.ShopKey))
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：" + relevanceReason);
                Log.Info("Knowledge Memory发送前被并发相关性门控抑制: buyer=" + burst.BuyerNick
                    + ", reason=" + relevanceReason);
                return;
            }

            if (!lease.MarkSending("knowledge_memory_send_started"))
            {
                if (ctl != null) ctl.SetSendResult(false, "未发送：generation在Knowledge Memory发送前已失效");
                return;
            }
            Func<bool> preSendGuard = () =>
            {
                if (!lease.IsCurrent || lease.CancellationToken.IsCancellationRequested)
                    return false;
                if (!Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply())
                    return false;
                string latestRelevanceReason;
                var shouldSend = ParallelReplyRelevanceGate.ShouldSend(
                    burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion, detectedAt,
                    out latestRelevanceReason, false, burst.ShopKey);
                if (!shouldSend)
                    Log.Info("Knowledge Memory等待发送资源期间会话已变化，阻止迟到直答: buyer="
                        + burst.BuyerNick + ", reason=" + latestRelevanceReason);
                return shouldSend;
            };
            var sendOk = await qn.SendTextWithRetryAsync(
                burst.BuyerNick, answer, 1, lease.CancellationToken, preSendGuard);
            var sendCancelled = !sendOk && qn.Rpa != null && qn.Rpa.LastSendWasCancelled;
            var sendFailureReason = qn.Rpa == null
                ? "发送器不可用，未能确认实际送达状态"
                : qn.Rpa.GetSendFailureReason();
            if (sendOk)
            {
                ReplyDeduplicationService.RememberDelivered(
                    burst.SellerNick, burst.BuyerNick, answer, burst.ShopKey);
                if (decision.Best != null && decision.Best.Card != null && decision.Best.Card.Entry != null)
                    KnowledgePolicyProfileService.RecordRouteSelection(decision.Best.Card.Entry, true);
                lease.MarkCompleted("knowledge_memory_send_completed");
            }
            else if (sendCancelled)
            {
                lease.MarkCompleted("knowledge_memory_send_cancelled");
            }
            else
            {
                lease.MarkFailed("knowledge_memory_send_failed");
            }
            if (ctl != null)
            {
                ctl.SetSendResult(
                    sendOk,
                    sendOk
                        ? "已发送（Knowledge Memory本地直答，无AI调用）"
                        : sendCancelled
                            ? "未发送：人工接管或会话状态变化，已安全取消"
                            : "发送失败：" + sendFailureReason);
            }
            Log.Info("Knowledge Memory本地直答完成: buyer=" + burst.BuyerNick
                + ", success=" + sendOk + ", cancelled=" + sendCancelled
                + ", totalMs=" + Math.Max(0, (long)(DateTime.Now - detectedAt).TotalMilliseconds));
            if (sendCancelled)
                ResponseProgressTracker.Cancel(
                    burst.SellerNick, burst.BuyerNick, "Knowledge Memory发送前会话门禁取消", burst.ShopKey);
            else if (sendOk)
                ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey);
            else
                ResponseProgressTracker.Fail(
                    burst.SellerNick, burst.BuyerNick,
                    "Knowledge Memory答案已生成，但实际发送未确认：" + sendFailureReason, burst.ShopKey);
        }
    }
}

namespace Bot
{
    public partial class App
    {
        private readonly object _knowledgeMemoryEngineBootstrap = ChromeNs.KnowledgeMemoryRuntimeBridge.InitializeForApp();
    }
}
