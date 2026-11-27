using Bot.Knowledge;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    // Retained as a presentation state for the existing answer pipeline. It is
    // no longer a knowledge retriever; Knowledge V2 performs the only retrieval
    // and direct-answer decision in KnowledgeEngineV2RuntimeBridge.
    internal enum SmartReplyRouteKind
    {
        DirectKnowledge,
        ContextualKnowledge,
        AiGeneral
    }

    internal sealed class SmartKnowledgeCandidate
    {
        public KnowledgeV2Record Record { get; set; }
        public double FinalScore { get; set; }
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

        public SmartReplyPlan()
        {
            Candidates = new List<SmartKnowledgeCandidate>();
            RecentTurns = new List<ConversationContextTurn>();
            Reason = string.Empty;
        }
    }

    internal static class SmartReplyRouterService
    {
        private static readonly string[] ContextCues =
        {
            "这个", "那个", "这款", "那款", "它", "这样", "那样", "刚才", "上面", "前面",
            "还是", "然后呢", "那呢", "这个呢", "那个呢", "为什么不", "怎么不", "我发了",
            "收到了吗", "可以吗", "行吗", "对吗", "是不是"
        };

        public static SmartReplyPlan BuildPlan(string seller, string buyer, string question)
        {
            return BuildPlanAsync(seller, buyer, question, CancellationToken.None)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        }

        public static Task<SmartReplyPlan> BuildPlanAsync(
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
            var state = ConversationStateService.Build(
                seller, buyer, question, turns);
            var queryResolution = ContextualQueryRewriteService.Resolve(
                question, dependency, state, turns);
            var recent = turns.Skip(Math.Max(0, turns.Count - 8)).ToList();
            var plan = new SmartReplyPlan
            {
                Route = SmartReplyRouteKind.AiGeneral,
                ContextDependencyScore = dependency,
                Reason = "Knowledge V2 已完成唯一知识检索；此处仅组织会话上下文，不再召回旧知识库或执行离线知识兜底。",
                Candidates = new List<SmartKnowledgeCandidate>(),
                RecentTurns = recent,
                ContextDigest = BuildContextDigest(turns),
                ConversationState = state,
                QueryResolution = queryResolution,
                SemanticRetrievalApplied = false,
                SemanticModel = string.Empty,
                SemanticLatencyMs = 0
            };
            return Task.FromResult(plan);
        }

        // Offline fixed-answer fallbacks are intentionally disabled. If V2 has
        // no approved direct answer, the caller must use the configured AI path
        // or stop safely; no legacy corpus may take over during an outage.
        public static bool CanUseOfflineKnowledgeFallback(SmartReplyPlan plan)
        {
            return false;
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
                    .Append("用于理解当前问题的完整表达：").Append(Safe(plan.QueryResolution.ResolvedQuery, 700)).Append("\n")
                    .Append("还原原因：").Append(Safe(plan.QueryResolution.Reason, 220)).Append("\n")
                    .Append("只回答买家当前真正想问的内容，不要向买家解释内部还原过程。\n");
            }
            return sb.ToString();
        }

        private static double CalculateContextDependency(
            string question,
            IList<ConversationContextTurn> turns)
        {
            var compact = Compact(question);
            if (compact.Length == 0) return 0.5;
            if (ContextCues.Any(cue => compact.Contains(Compact(cue)))) return 0.62;
            if (compact.Length <= 8 && !compact.Contains("?") && !compact.Contains("？")) return 0.58;
            if (turns != null && turns.Count > 0
                && (compact.StartsWith("那", StringComparison.Ordinal)
                    || compact.StartsWith("然后", StringComparison.Ordinal)
                    || compact.StartsWith("还", StringComparison.Ordinal)))
                return 0.48;
            return 0.12;
        }

        private static string BuildContextDigest(IList<ConversationContextTurn> turns)
        {
            if (turns == null || turns.Count <= 8) return string.Empty;
            var older = turns.Take(turns.Count - 8)
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Text))
                .Take(12)
                .Select(x => (x.Role == "assistant" ? "客服：" : "买家：") + Safe(x.Text, 180))
                .ToList();
            if (older.Count == 0) return string.Empty;
            return string.Join("\n", older);
        }

        private static string Compact(string value)
        {
            return new string((value ?? string.Empty)
                .Where(ch => !char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
                .ToArray());
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
