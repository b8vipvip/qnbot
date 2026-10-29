using Bot.Knowledge;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal enum SmartReplyRouteKind
    {
        DirectKnowledge,
        ContextualKnowledge,
        AiGeneral
    }

    internal sealed class SmartKnowledgeCandidate
    {
        public KnowledgeBaseEntry Entry { get; set; }
        public double RetrievalScore { get; set; }
        public double ResolvedQueryScore { get; set; }
        public double EntityScore { get; set; }
        public double SemanticScore { get; set; }
        public double FinalScore { get; set; }
        public bool ExactQuestionMatch { get; set; }
        public string Intent { get; set; }
        public KnowledgePolicyEvaluation PolicyEvaluation { get; set; }
    }

    internal sealed class SmartReplyPlan
    {
        public SmartReplyRouteKind Route { get; set; }
        public double ContextDependencyScore { get; set; }
        public string Reason { get; set; }
        public List<SmartKnowledgeCandidate> Candidates { get; set; }
        public List<ConversationContextTurn> RecentTurns { get; set; }
        public string ContextDigest { get; set; }
        public ConversationStateSnapshot ConversationState { get; set; }
        public ContextualQueryResolution QueryResolution { get; set; }
        public bool SemanticRetrievalApplied { get; set; }
        public string SemanticModel { get; set; }
        public long SemanticLatencyMs { get; set; }

        public SmartKnowledgeCandidate BestCandidate
        {
            get { return Candidates == null || Candidates.Count == 0 ? null : Candidates[0]; }
        }
    }

    internal static class SmartReplyRouterService
    {
        private const int RetrievalPoolSize = 10;
        private const int PromptCandidateCount = 3;
        private const int RecentPromptTurns = 8;

        private static readonly string[] ContextCues =
        {
            "这个", "那个", "这款", "那款", "它", "这样", "那样", "刚才", "上面", "前面",
            "还是", "然后呢", "那呢", "这个呢", "那个呢", "为什么不", "怎么不", "我发了",
            "收到了吗", "可以吗", "行吗", "对吗", "是不是"
        };

        private static readonly string[] ConditionalAnswerCues =
        {
            "如果", "若", "只要", "需要先", "请提供", "请确认", "视情况", "根据实际", "以页面为准",
            "联系客服", "人工确认", "订单情况"
        };

        private static readonly string[] HighRiskTerms =
        {
            "退款", "退货", "赔偿", "投诉", "差评", "举报", "仲裁", "验证码", "密码", "身份证",
            "银行卡", "订单号", "手机号", "账号安全", "封号", "解封"
        };

        public static SmartReplyPlan BuildPlan(string seller, string buyer, string question)
        {
            return BuildPlanAsync(seller, buyer, question, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }

        public static async Task<SmartReplyPlan> BuildPlanAsync(
            string seller,
            string buyer,
            string question,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            question = (question ?? string.Empty).Trim();
            var turns = ConversationContextStore.GetRecentTurns(seller, buyer, question, 16)
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Text))
                .OrderBy(x => x.Timestamp)
                .ToList();
            var dependency = CalculateContextDependency(question, turns);
            var state = ConversationStateService.Build(seller, buyer, question, turns);
            var queryResolution = ContextualQueryRewriteService.Resolve(question, dependency, state, turns);
            var candidates = RetrieveCandidates(seller, question, queryResolution, state, turns);
            var plan = new SmartReplyPlan
            {
                ContextDependencyScore = dependency,
                Candidates = candidates.Take(PromptCandidateCount).ToList(),
                RecentTurns = turns.Skip(Math.Max(0, turns.Count - RecentPromptTurns)).ToList(),
                ContextDigest = BuildContextDigest(turns),
                ConversationState = state,
                QueryResolution = queryResolution,
                Route = SmartReplyRouteKind.AiGeneral,
                Reason = "未找到足够可靠且适用于当前场景的本地知识，交给AI结合上下文处理",
                SemanticModel = string.Empty
            };

            var provisionalBest = candidates.FirstOrDefault();
            var rewritten = queryResolution != null && queryResolution.Rewritten;
            var exactIndependent = provisionalBest != null
                && provisionalBest.ExactQuestionMatch
                && dependency <= 0.20
                && !rewritten
                && PolicyAllowsDirect(provisionalBest);
            if (!exactIndependent && SemanticEmbeddingService.IsConfigured())
            {
                var resolvedText = queryResolution == null || string.IsNullOrWhiteSpace(queryResolution.ResolvedQuery)
                    ? question
                    : queryResolution.ResolvedQuery;
                var semantic = await SemanticEmbeddingService.TryScoreAsync(
                    resolvedText,
                    BotFeatureStore.GetKnowledgeBase(),
                    candidates,
                    cancellationToken).ConfigureAwait(false);
                if (semantic != null && semantic.Applied)
                {
                    ApplySemanticScores(plan, candidates, semantic, question, resolvedText);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var best = plan.BestCandidate;
            if (ConversationProgressGuardService.RequiresContextualHandling(state))
            {
                plan.Route = best == null
                    ? SmartReplyRouteKind.AiGeneral
                    : SmartReplyRouteKind.ContextualKnowledge;
                plan.Reason = "当前消息属于订单/代充流程中的结构化承接，必须结合已完成步骤继续处理，禁止固定知识直答";
                if (best != null) KnowledgePolicyProfileService.RecordRouteSelection(best.Entry, false);
                return plan;
            }
            if (best == null) return plan;

            var second = plan.Candidates.Count > 1 ? plan.Candidates[1] : null;
            var margin = second == null ? best.FinalScore : best.FinalScore - second.FinalScore;
            if (CanDirectReply(question, best, dependency, margin, queryResolution))
            {
                plan.Route = SmartReplyRouteKind.DirectKnowledge;
                plan.Reason = "独立问题、知识适用条件满足且可靠度允许直接回复";
                KnowledgePolicyProfileService.RecordRouteSelection(best.Entry, true);
                return plan;
            }

            var forceContextual = best.PolicyEvaluation != null && best.PolicyEvaluation.ForceContextual;
            if (forceContextual
                || best.FinalScore >= 0.55
                || best.RetrievalScore >= 0.70
                || best.ResolvedQueryScore >= 0.72
                || best.SemanticScore >= 0.72)
            {
                plan.Route = SmartReplyRouteKind.ContextualKnowledge;
                if (best.PolicyEvaluation != null && best.PolicyEvaluation.ConstraintOnly)
                {
                    plan.Reason = "命中的知识被设置为事实约束，只能约束AI结论，禁止机械原样回复";
                }
                else if (best.PolicyEvaluation != null && best.PolicyEvaluation.ForceContextual)
                {
                    plan.Reason = "知识回答模式、必要上下文或历史可靠度要求必须结合当前对话生成答案";
                }
                else
                {
                    plan.Reason = dependency >= 0.35 || rewritten
                        ? "当前消息依赖上下文，已先还原完整问题，再把本地知识作为事实依据"
                        : (best.SemanticScore >= 0.72 && best.RetrievalScore < 0.55
                            ? "文本表面相似度较低，但语义向量找到高相关知识，将交给AI结合上下文使用"
                            : "知识相关但不满足直接发送条件，交给AI结合上下文重写");
                }
                KnowledgePolicyProfileService.RecordRouteSelection(best.Entry, false);
            }
            return plan;
        }

        public static bool CanUseOfflineKnowledgeFallback(SmartReplyPlan plan)
        {
            var best = plan == null ? null : plan.BestCandidate;
            return best != null
                && plan.ContextDependencyScore < 0.28
                && (plan.QueryResolution == null || !plan.QueryResolution.Rewritten)
                && PolicyAllowsDirect(best)
                && best.RetrievalScore >= 0.80
                && best.FinalScore >= 0.90
                && !IsUnsafeDirectAnswer(best.Entry == null ? string.Empty : best.Entry.Answer);
        }

        public static string BuildPromptAddon(SmartReplyPlan plan)
        {
            if (plan == null) return string.Empty;
            var sb = new StringBuilder();
            sb.Append(ConversationStateService.BuildPromptAddon(plan.ConversationState));

            if (plan.QueryResolution != null && plan.QueryResolution.Rewritten)
            {
                sb.Append("\n【上下文问题还原】\n")
                    .Append("买家原话：").Append(Safe(plan.QueryResolution.OriginalQuery, 300)).Append("\n")
                    .Append("用于检索和理解的完整问题：").Append(Safe(plan.QueryResolution.ResolvedQuery, 700)).Append("\n")
                    .Append("还原原因：").Append(Safe(plan.QueryResolution.Reason, 220)).Append("\n")
                    .Append("回复时仍然只回答买家当前真正想问的内容，不要把还原过程告诉买家。\n");
            }

            if (plan.Candidates == null || plan.Candidates.Count == 0) return sb.ToString();
            sb.Append("\n【智能知识路由】\n")
                .Append("当前路由：").Append(RouteName(plan.Route)).Append("。")
                .Append("上下文依赖度：").Append(plan.ContextDependencyScore.ToString("0.00")).Append("。");
            if (plan.SemanticRetrievalApplied)
            {
                sb.Append("已使用语义向量辅助检索，但向量相似只用于找候选知识，不能单独证明业务事实正确。");
            }
            sb.Append("这些知识是候选事实依据，不是必须原样发送的固定答案。")
                .Append("必须先理解当前买家消息、还原后的问题和最近对话，只采用真正相关且满足适用条件的知识；候选之间冲突或与上下文不符时应忽略不相关候选。")
                .Append("不得编造候选知识和店铺固定提示词之外的价格、库存、订单状态、服务范围或售后承诺。")
                .Append("回复应像真人客服自然承接上下文，避免机械重复上一条回复。\n");

            for (var i = 0; i < plan.Candidates.Count; i++)
            {
                var candidate = plan.Candidates[i];
                var item = candidate.Entry;
                if (item == null) continue;
                sb.Append("候选知识").Append(i + 1).Append("：\n")
                    .Append("问题：").Append(Safe(item.Title, 260)).Append("\n")
                    .Append("答案：").Append(Safe(item.Answer, 900)).Append("\n")
                    .Append("综合相关度：").Append(candidate.FinalScore.ToString("0.00")).Append("\n");
                if (candidate.SemanticScore > 0)
                {
                    sb.Append("语义相关度：").Append(candidate.SemanticScore.ToString("0.00")).Append("\n");
                }
                if (!string.IsNullOrWhiteSpace(item.Category))
                {
                    sb.Append("分类：").Append(Safe(item.Category, 100)).Append("\n");
                }
                if (!string.IsNullOrWhiteSpace(item.Keywords))
                {
                    sb.Append("关键词：").Append(Safe(item.Keywords, 240)).Append("\n");
                }
                sb.Append(KnowledgePolicyProfileService.BuildPromptAddon(item, candidate.PolicyEvaluation));
                sb.Append("\n");
            }
            return sb.ToString();
        }

        private static void ApplySemanticScores(
            SmartReplyPlan plan,
            List<SmartKnowledgeCandidate> localCandidates,
            SemanticEmbeddingResult semantic,
            string originalQuestion,
            string resolvedQuestion)
        {
            plan.SemanticRetrievalApplied = true;
            plan.SemanticModel = semantic.Model ?? string.Empty;
            plan.SemanticLatencyMs = semantic.LatencyMs;

            var merged = new List<SmartKnowledgeCandidate>(localCandidates ?? new List<SmartKnowledgeCandidate>());
            var recentContext = string.Join(" ", (plan.RecentTurns ?? new List<ConversationContextTurn>())
                .Select(x => x == null ? string.Empty : x.Text ?? string.Empty));
            foreach (var scored in semantic.Scores ?? new List<SemanticKnowledgeScore>())
            {
                if (scored == null || scored.Entry == null) continue;
                var existing = merged.FirstOrDefault(x => x != null && x.Entry != null && SameKnowledge(x.Entry, scored.Entry));
                if (existing != null)
                {
                    existing.SemanticScore = Clamp(scored.Score);
                    if (!existing.ExactQuestionMatch)
                    {
                        existing.FinalScore = Clamp(existing.FinalScore * 0.72 + existing.SemanticScore * 0.28);
                    }
                    continue;
                }

                if (scored.Score < 0.72) continue;
                var policy = KnowledgePolicyProfileService.Evaluate(
                    scored.Entry,
                    originalQuestion,
                    resolvedQuestion,
                    plan.ConversationState,
                    recentContext);
                if (policy.Excluded) continue;
                merged.Add(new SmartKnowledgeCandidate
                {
                    Entry = scored.Entry,
                    RetrievalScore = 0,
                    ResolvedQueryScore = 0,
                    EntityScore = 0,
                    SemanticScore = Clamp(scored.Score),
                    FinalScore = Clamp(scored.Score * 0.78 + policy.ScoreAdjustment),
                    ExactQuestionMatch = false,
                    Intent = ResolvePolicyIntent(scored.Entry, policy),
                    PolicyEvaluation = policy
                });
            }

            plan.Candidates = merged
                .Where(x => x != null && x.Entry != null)
                .Where(x => x.PolicyEvaluation == null || !x.PolicyEvaluation.Excluded)
                .OrderByDescending(x => x.FinalScore)
                .Take(PromptCandidateCount)
                .ToList();
            Log.Info("Smart Reply语义检索完成: model=" + plan.SemanticModel
                + ", latencyMs=" + plan.SemanticLatencyMs
                + ", candidates=" + plan.Candidates.Count);
        }

        private static bool SameKnowledge(KnowledgeBaseEntry left, KnowledgeBaseEntry right)
        {
            if (left == null || right == null) return false;
            var leftId = Convert.ToString(left.Id);
            var rightId = Convert.ToString(right.Id);
            if (!string.IsNullOrWhiteSpace(leftId) && !string.IsNullOrWhiteSpace(rightId))
            {
                return string.Equals(leftId, rightId, StringComparison.Ordinal);
            }
            return string.Equals(
                KnowledgeAiService.NormalizeQuestion(left.Title),
                KnowledgeAiService.NormalizeQuestion(right.Title),
                StringComparison.Ordinal);
        }

        private static List<SmartKnowledgeCandidate> RetrieveCandidates(
            string seller,
            string question,
            ContextualQueryResolution queryResolution,
            ConversationStateSnapshot state,
            List<ConversationContextTurn> turns)
        {
            var knowledge = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
            var originalQuery = KnowledgeAiService.NormalizeQuestion(question);
            if (originalQuery.Length == 0) return new List<SmartKnowledgeCandidate>();
            var resolvedText = queryResolution == null || string.IsNullOrWhiteSpace(queryResolution.ResolvedQuery)
                ? question
                : queryResolution.ResolvedQuery;
            var resolvedQuery = KnowledgeAiService.NormalizeQuestion(resolvedText);
            var queryIntent = state == null || string.IsNullOrWhiteSpace(state.BuyerGoal)
                ? DetectIntent(resolvedText)
                : NormalizeIntent(state.BuyerGoal);
            var context = string.Join(" ", (turns ?? new List<ConversationContextTurn>())
                .Skip(Math.Max(0, (turns == null ? 0 : turns.Count) - 5))
                .Select(x => x.Text ?? string.Empty));

            var pool = knowledge
                .Where(x => x != null && x.Enabled && !string.IsNullOrWhiteSpace(x.Answer))
                .Where(x => !KugouReceptionPolicy.IsQuarantined(seller, x.Id, x.Title, x.Answer))
                .Select(x => ScoreCandidate(x, question, resolvedText, originalQuery, resolvedQuery, queryIntent, state, context))
                .Where(x => x.PolicyEvaluation == null || !x.PolicyEvaluation.Excluded)
                .Where(x => ConversationProgressGuardService.AllowKnowledge(x.Entry, state, question))
                .Where(x => x.RetrievalScore >= 0.20
                    || x.ResolvedQueryScore >= 0.24
                    || x.EntityScore >= 0.35
                    || (x.PolicyEvaluation != null && x.PolicyEvaluation.ScoreAdjustment > 0))
                .OrderByDescending(x => x.FinalScore)
                .Take(RetrievalPoolSize)
                .ToList();

            return pool.OrderByDescending(x => x.FinalScore).ToList();
        }

        private static SmartKnowledgeCandidate ScoreCandidate(
            KnowledgeBaseEntry item,
            string rawOriginalQuestion,
            string rawResolvedQuestion,
            string originalQuery,
            string resolvedQuery,
            string queryIntent,
            ConversationStateSnapshot state,
            string context)
        {
            var title = KnowledgeAiService.NormalizeQuestion(item.Title);
            var exact = originalQuery == title && title.Length > 0;
            var originalScore = BaseTextScore(originalQuery, title);
            var resolvedScore = BaseTextScore(resolvedQuery, title);
            var policy = KnowledgePolicyProfileService.Evaluate(
                item,
                rawOriginalQuestion,
                rawResolvedQuestion,
                state,
                context);

            var keywordScore = 0.0;
            foreach (var keyword in SplitKeywords(item.Keywords))
            {
                var normalizedKeyword = KnowledgeAiService.NormalizeQuestion(keyword);
                if (normalizedKeyword.Length < 2) continue;
                if (originalQuery.Contains(normalizedKeyword) || resolvedQuery.Contains(normalizedKeyword))
                    keywordScore = Math.Max(keywordScore, 1.0);
                else
                    keywordScore = Math.Max(keywordScore,
                        Math.Max(TextSimilarity(originalQuery, normalizedKeyword), TextSimilarity(resolvedQuery, normalizedKeyword)));
            }

            var profileEntities = policy == null || policy.Profile == null ? string.Empty : policy.Profile.Entities;
            var itemText = (item.Title ?? string.Empty) + " "
                + (item.Keywords ?? string.Empty) + " "
                + (item.Category ?? string.Empty) + " "
                + profileEntities;
            var contextScore = TextSimilarity(context, itemText);
            var entityScore = CalculateEntityScore(state, itemText);
            var itemIntent = ResolvePolicyIntent(item, policy);
            var intentAdjustment = 0.0;
            if (!string.IsNullOrWhiteSpace(queryIntent) && !string.IsNullOrWhiteSpace(itemIntent))
            {
                intentAdjustment = queryIntent == itemIntent ? 0.08 : -0.06;
            }

            var final = originalScore * 0.38
                + resolvedScore * 0.28
                + keywordScore * 0.12
                + entityScore * 0.10
                + contextScore * 0.06
                + Math.Max(0, intentAdjustment)
                + (policy == null ? 0 : policy.ScoreAdjustment);
            if (intentAdjustment < 0) final += intentAdjustment;
            if (exact) final = Math.Max(final, 0.99 + Math.Min(0, policy == null ? 0 : policy.ScoreAdjustment));

            return new SmartKnowledgeCandidate
            {
                Entry = item,
                RetrievalScore = Clamp(originalScore),
                ResolvedQueryScore = Clamp(resolvedScore),
                EntityScore = Clamp(entityScore),
                SemanticScore = 0,
                FinalScore = Clamp(final),
                ExactQuestionMatch = exact,
                Intent = itemIntent,
                PolicyEvaluation = policy
            };
        }

        private static string ResolvePolicyIntent(KnowledgeBaseEntry entry, KnowledgePolicyEvaluation policy)
        {
            if (policy != null && policy.Profile != null && !string.IsNullOrWhiteSpace(policy.Profile.Intent))
                return policy.Profile.Intent.Trim();
            return DetectIntent((entry == null ? string.Empty : entry.Title) + " " + (entry == null ? string.Empty : entry.Keywords));
        }

        private static double BaseTextScore(string query, string title)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(title)) return 0;
            if (query == title) return 1.0;
            if (Math.Min(query.Length, title.Length) >= 4
                && (query.Contains(title) || title.Contains(query)))
            {
                return 0.92;
            }
            return TextSimilarity(query, title);
        }

        private static double CalculateEntityScore(ConversationStateSnapshot state, string itemText)
        {
            if (state == null || state.Entities == null || state.Entities.Count == 0) return 0;
            var compactItem = Compact(itemText);
            var matched = 0;
            var total = 0;
            foreach (var entity in state.Entities.Take(5))
            {
                var compactEntity = Compact(entity);
                if (compactEntity.Length < 2) continue;
                total++;
                if (compactItem.Contains(compactEntity)) matched++;
            }
            if (total == 0) return 0;
            return Math.Min(1.0, matched / (double)Math.Min(3, total));
        }

        private static double CalculateContextDependency(string question, List<ConversationContextTurn> turns)
        {
            var compact = Compact(question);
            if (compact.Length == 0) return 0;
            var score = 0.0;
            if (compact.Length <= 6) score += 0.24;
            else if (compact.Length <= 12) score += 0.12;

            if (ContextCues.Any(x => compact.Contains(Compact(x)))) score += 0.38;
            if (Regex.IsMatch(compact, @"^(那|这个|那个|它|这样|还是|所以|然后|为啥|为什么|怎么不)")) score += 0.24;
            if (Regex.IsMatch(compact, @"^(有|没有|是|不是|可以|不可以|能|不能|支持|不支持|好的|好|嗯|对|不对)$")) score += 0.42;

            var previousAssistant = (turns ?? new List<ConversationContextTurn>())
                .Where(x => x.Role == "assistant" && !string.IsNullOrWhiteSpace(x.Text))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();
            if (previousAssistant != null
                && (previousAssistant.Timestamp == DateTime.MinValue || previousAssistant.Timestamp >= DateTime.Now.AddMinutes(-20)))
            {
                if (compact.Length <= 16 || ContextCues.Any(x => compact.Contains(Compact(x)))) score += 0.16;
                if (SharedBigramCount(compact, Compact(previousAssistant.Text)) > 0) score += 0.08;
            }

            if (compact.Length >= 10
                && Regex.IsMatch(compact, @"怎么|如何|能不能|是否|支持|多少钱|价格|多久|什么时候|哪里|为什么"))
            {
                score -= 0.12;
            }
            return Clamp(score);
        }

        private static bool CanDirectReply(
            string question,
            SmartKnowledgeCandidate best,
            double dependency,
            double margin,
            ContextualQueryResolution resolution)
        {
            if (best == null || best.Entry == null) return false;
            var selfContainedDemonstrative = IsSelfContainedDemonstrativeQuestion(question, best);
            if (dependency > 0.20 && !selfContainedDemonstrative) return false;
            if (resolution != null && resolution.Rewritten && !selfContainedDemonstrative) return false;
            if (!PolicyAllowsDirect(best)) return false;
            if (Compact(question).Length < 4) return false;
            if (ContextCues.Any(x => Compact(question).Contains(Compact(x))) && !selfContainedDemonstrative) return false;
            if (IsUnsafeDirectAnswer(best.Entry.Answer)) return false;

            var policy = best.PolicyEvaluation == null ? null : best.PolicyEvaluation.Profile;
            var mode = policy == null ? KnowledgeAnswerModes.Auto : KnowledgeAnswerModes.Normalize(policy.AnswerMode);
            var reliability = policy == null ? 0.75 : policy.ReliabilityScore;
            if (best.ExactQuestionMatch && best.RetrievalScore >= 0.95) return reliability >= 0.58;
            if (selfContainedDemonstrative
                && reliability >= 0.58
                && best.FinalScore >= 0.88
                && (best.RetrievalScore >= 0.80 || best.ResolvedQueryScore >= 0.84 || best.SemanticScore >= 0.86)
                && margin >= 0.08) return true;
            if (mode == KnowledgeAnswerModes.Direct && reliability >= 0.78)
            {
                return best.FinalScore >= 0.86 && margin >= 0.10 && best.RetrievalScore >= 0.82;
            }
            return reliability >= 0.65
                && best.FinalScore >= 0.90
                && margin >= 0.14
                && best.RetrievalScore >= 0.88;
        }

        private static bool IsSelfContainedDemonstrativeQuestion(string question, SmartKnowledgeCandidate best)
        {
            if (best == null || best.Entry == null) return false;
            var compact = Compact(question);
            if (!Regex.IsMatch(compact, @"^(这个|那个|这款|那款|这种|那种)")) return false;
            var stripped = Regex.Replace(compact, @"^(这个|那个|这款|那款|这种|那种)", string.Empty);
            if (stripped.Length < 8) return false;
            if (!Regex.IsMatch(stripped, @"会员|电视|tv|手机|电脑|平板|酷狗|音乐|充值|账号|设备|软件|app|价格|退款|订单", RegexOptions.IgnoreCase)) return false;
            if (HighRiskTerms.Any(x => stripped.Contains(Compact(x)))) return false;
            return best.FinalScore >= 0.84
                && (best.RetrievalScore >= 0.72 || best.ResolvedQueryScore >= 0.78 || best.SemanticScore >= 0.82);
        }

        private static bool PolicyAllowsDirect(SmartKnowledgeCandidate candidate)
        {
            return candidate != null
                && (candidate.PolicyEvaluation == null
                    || (candidate.PolicyEvaluation.AllowDirect
                        && !candidate.PolicyEvaluation.ForceContextual
                        && !candidate.PolicyEvaluation.ConstraintOnly
                        && !candidate.PolicyEvaluation.Excluded));
        }

        private static bool IsUnsafeDirectAnswer(string answer)
        {
            answer = answer ?? string.Empty;
            if (ConditionalAnswerCues.Any(x => answer.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            return HighRiskTerms.Any(x => answer.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string BuildContextDigest(List<ConversationContextTurn> turns)
        {
            if (turns == null || turns.Count <= RecentPromptTurns) return string.Empty;
            var older = turns.Take(Math.Max(0, turns.Count - RecentPromptTurns)).ToList();
            var sb = new StringBuilder();
            foreach (var turn in older)
            {
                if (turn == null || string.IsNullOrWhiteSpace(turn.Text)) continue;
                var speaker = turn.Role == "assistant" ? "客服" : (turn.Role == "user" ? "买家" : "系统");
                var line = speaker + "：" + Safe(turn.Text, 180);
                if (sb.Length + line.Length > 900) break;
                if (sb.Length > 0) sb.Append("；");
                sb.Append(line);
            }
            return sb.ToString();
        }

        private static string DetectIntent(string text)
        {
            var value = Compact(text);
            if (value.Length == 0) return string.Empty;
            if (Regex.IsMatch(value, @"退款|退货|售后|赔偿|投诉")) return "after_sale";
            if (Regex.IsMatch(value, @"多少钱|价格|费用|收费")) return "price";
            if (Regex.IsMatch(value, @"多久|什么时候|几天|时效")) return "time";
            if (Regex.IsMatch(value, @"怎么|如何|步骤|操作|登录|绑定|充值方法")) return "how_to";
            if (Regex.IsMatch(value, @"为什么|失败|不行|报错|异常|不能用")) return "troubleshoot";
            if (Regex.IsMatch(value, @"可以|能不能|能否|是否|支持|可不可以")) return "capability";
            if (Regex.IsMatch(value, @"需要什么|提供什么|要什么|资料|条件")) return "requirement";
            return string.Empty;
        }

        private static string NormalizeIntent(string value)
        {
            value = value ?? string.Empty;
            if (value.Contains("售后")) return "after_sale";
            if (value.Contains("价格")) return "price";
            if (value.Contains("时间") || value.Contains("时效")) return "time";
            if (value.Contains("操作")) return "how_to";
            if (value.Contains("故障")) return "troubleshoot";
            if (value.Contains("支持")) return "capability";
            if (value.Contains("条件")) return "requirement";
            return string.Empty;
        }

        private static IEnumerable<string> SplitKeywords(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '|', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim());
        }

        private static double TextSimilarity(string left, string right)
        {
            var a = Bigrams(Compact(left));
            var b = Bigrams(Compact(right));
            if (a.Count == 0 || b.Count == 0) return 0;
            var common = a.Intersect(b).Count();
            return (2.0 * common) / (a.Count + b.Count);
        }

        private static int SharedBigramCount(string left, string right)
        {
            return Bigrams(left).Intersect(Bigrams(right)).Count();
        }

        private static HashSet<string> Bigrams(string value)
        {
            var compact = Compact(value);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < compact.Length; i++) set.Add(compact.Substring(i, 2));
            return set;
        }

        private static string Compact(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s，。！？、；：,.!?:;\-—_()（）\[\]【】]+", string.Empty);
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static double Clamp(double value)
        {
            return Math.Max(0, Math.Min(1, value));
        }

        private static string RouteName(SmartReplyRouteKind route)
        {
            if (route == SmartReplyRouteKind.DirectKnowledge) return "DIRECT_KNOWLEDGE";
            if (route == SmartReplyRouteKind.ContextualKnowledge) return "CONTEXTUAL_KNOWLEDGE";
            return "AI_GENERAL";
        }
    }
}
