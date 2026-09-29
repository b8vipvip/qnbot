using Bot.Knowledge;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal sealed class ConversationStateSnapshot
    {
        public string CurrentTopic { get; set; }
        public string CurrentEntity { get; set; }
        public string BuyerGoal { get; set; }
        public string PendingQuestion { get; set; }
        public string ConversationStage { get; set; }
        public List<string> ConfirmedFacts { get; set; }
        public List<string> Entities { get; set; }
        public ConversationProgressSnapshot Progress { get; set; }
        public CommerceContextSnapshot CommerceContext { get; set; }

        // Compatibility aliases keep policy/validator modules decoupled from the original Phase 1 naming.
        public string PendingSellerQuestion
        {
            get { return PendingQuestion; }
            set { PendingQuestion = value; }
        }

        public string Stage
        {
            get { return ConversationStage; }
            set { ConversationStage = value; }
        }

        public ConversationStateSnapshot()
        {
            ConfirmedFacts = new List<string>();
            Entities = new List<string>();
        }
    }

    internal sealed class ContextualQueryResolution
    {
        public string OriginalQuery { get; set; }
        public string ResolvedQuery { get; set; }
        public bool Rewritten { get; set; }
        public string Reason { get; set; }
    }

    internal static class ConversationStateService
    {
        private static readonly string[] PendingQuestionCues =
        {
            "请问", "确认一下", "确认下", "是否", "有没有", "有吗", "需要", "提供", "告诉我", "发一下", "发下",
            "什么型号", "哪个", "哪款", "多少", "能否", "可以吗", "方便吗"
        };

        private static readonly string[] PositiveShortReplies =
        {
            "有", "有的", "是", "是的", "对", "对的", "可以", "能", "支持", "好的", "好", "嗯", "行", "没问题", "已经"
        };

        private static readonly string[] NegativeShortReplies =
        {
            "没有", "没", "不是", "不可以", "不能", "不支持", "不行", "无"
        };

        public static ConversationStateSnapshot Build(
            string seller,
            string buyer,
            string currentQuestion,
            IList<ConversationContextTurn> turns)
        {
            var state = new ConversationStateSnapshot();
            var ordered = (turns ?? new List<ConversationContextTurn>())
                .Where(x => x != null && !x.Withdrawn && !string.IsNullOrWhiteSpace(x.Text))
                .OrderBy(x => x.Timestamp)
                .ToList();

            var contextText = string.Join(" ", ordered.Select(x => x.Text ?? string.Empty))
                + " " + (currentQuestion ?? string.Empty);
            state.ConversationStage = DetectStage(contextText);
            state.PendingQuestion = FindPendingQuestion(ordered);
            state.CurrentTopic = FindCurrentTopic(ordered, currentQuestion);
            state.Entities = ExtractKnownEntities(contextText);
            state.CurrentEntity = ResolvePrimaryEntity(state.Entities, ordered, currentQuestion);
            state.BuyerGoal = DetectIntent((currentQuestion ?? string.Empty) + " " + state.CurrentTopic);

            var compactCurrent = Compact(currentQuestion);
            if (!string.IsNullOrWhiteSpace(state.PendingQuestion)
                && (ContainsExactOrPrefix(compactCurrent, PositiveShortReplies)
                    || ContainsExactOrPrefix(compactCurrent, NegativeShortReplies)))
            {
                state.ConfirmedFacts.Add(
                    "买家对客服问题“" + Safe(state.PendingQuestion, 180) + "”的回答是“" + Safe(currentQuestion, 60) + "”");
            }

            ConversationProgressGuardService.EnrichState(
                state, seller, buyer, currentQuestion, ordered);

            // CommerceContext is the final enrichment step. Verified order facts are stronger than
            // regex/text inference and may upgrade the stage produced by the legacy progress guard.
            CommerceContextService.EnrichState(
                state, seller, buyer, currentQuestion, ordered);
            return state;
        }

        public static string BuildPromptAddon(ConversationStateSnapshot state)
        {
            if (state == null) return string.Empty;
            var sb = new StringBuilder();
            sb.Append("\n\n【当前会话状态】以下是从本买家最近对话中本地提取的状态，只用于帮助理解指代和承接，不得把推断内容当成新的店铺事实。\n");
            if (!string.IsNullOrWhiteSpace(state.CurrentTopic))
                sb.Append("当前主题：").Append(Safe(state.CurrentTopic, 220)).Append("\n");
            if (!string.IsNullOrWhiteSpace(state.CurrentEntity))
                sb.Append("当前对象：").Append(Safe(state.CurrentEntity, 120)).Append("\n");
            if (!string.IsNullOrWhiteSpace(state.BuyerGoal))
                sb.Append("买家当前意图：").Append(state.BuyerGoal).Append("\n");
            if (!string.IsNullOrWhiteSpace(state.PendingQuestion))
                sb.Append("上一轮待确认问题：").Append(Safe(state.PendingQuestion, 220)).Append("\n");
            if (!string.IsNullOrWhiteSpace(state.ConversationStage))
                sb.Append("会话阶段：").Append(state.ConversationStage).Append("\n");
            if (state.ConfirmedFacts != null && state.ConfirmedFacts.Count > 0)
            {
                sb.Append("本轮已确认信息：")
                    .Append(string.Join("；", state.ConfirmedFacts.Take(3).Select(x => Safe(x, 220))))
                    .Append("\n");
            }

            sb.Append(CommerceContextService.BuildPromptAddon(state.CommerceContext));
            sb.Append(ConversationProgressGuardService.BuildPromptAddon(state));

            // 详细店铺规则不再作为每次都携带的固定提示词；这里按当前会话状态本地选择Top 3。
            sb.Append(StorePromptProfileService.BuildTextRulesAddon(state));
            return sb.ToString();
        }

        private static string FindPendingQuestion(IList<ConversationContextTurn> turns)
        {
            if (turns == null) return string.Empty;
            for (var i = turns.Count - 1; i >= 0; i--)
            {
                var turn = turns[i];
                if (turn == null || turn.Role != "assistant" || string.IsNullOrWhiteSpace(turn.Text)) continue;
                var text = turn.Text.Trim();
                if (LooksLikePendingQuestion(text)) return text;
                break;
            }
            return string.Empty;
        }

        private static string FindCurrentTopic(IList<ConversationContextTurn> turns, string currentQuestion)
        {
            var current = (currentQuestion ?? string.Empty).Trim();
            if (IsStandaloneTopicText(current)) return current;
            if (turns == null) return current;

            for (var i = turns.Count - 1; i >= 0; i--)
            {
                var turn = turns[i];
                if (turn == null || string.IsNullOrWhiteSpace(turn.Text)) continue;
                if (turn.Role == "user" && IsStandaloneTopicText(turn.Text)) return turn.Text.Trim();
            }
            for (var i = turns.Count - 1; i >= 0; i--)
            {
                var turn = turns[i];
                if (turn != null && turn.Role == "assistant" && !string.IsNullOrWhiteSpace(turn.Text))
                    return turn.Text.Trim();
            }
            return current;
        }

        private static List<string> ExtractKnownEntities(string context)
        {
            var compactContext = Compact(context);
            if (compactContext.Length == 0) return new List<string>();
            var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var knowledge = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
            foreach (var item in knowledge.Where(x => x != null && x.Enabled))
            {
                foreach (var term in CandidateEntityTerms(item))
                {
                    var normalized = Compact(term);
                    if (normalized.Length < 2 || normalized.Length > 20) continue;
                    if (!compactContext.Contains(normalized)) continue;
                    int score;
                    scores.TryGetValue(term, out score);
                    scores[term] = score + Math.Min(12, normalized.Length);
                }
            }
            return scores
                .OrderByDescending(x => x.Value)
                .ThenByDescending(x => x.Key.Length)
                .Take(6)
                .Select(x => x.Key)
                .ToList();
        }

        private static IEnumerable<string> CandidateEntityTerms(KnowledgeBaseEntry item)
        {
            foreach (var keyword in SplitTerms(item.Keywords)) yield return keyword;
            if (!string.IsNullOrWhiteSpace(item.Category)) yield return item.Category.Trim();

            var title = item.Title ?? string.Empty;
            foreach (Match match in Regex.Matches(title, @"[\u4e00-\u9fa5A-Za-z0-9]+"))
            {
                var value = match.Value.Trim();
                if (value.Length >= 2 && value.Length <= 12 && !IsStopEntity(value)) yield return value;
            }
        }

        private static string ResolvePrimaryEntity(
            IList<string> entities,
            IList<ConversationContextTurn> turns,
            string currentQuestion)
        {
            if (entities == null || entities.Count == 0) return string.Empty;
            var current = Compact(currentQuestion);
            foreach (var entity in entities)
            {
                if (current.Contains(Compact(entity))) return entity;
            }
            var recent = string.Join(" ", (turns ?? new List<ConversationContextTurn>())
                .Skip(Math.Max(0, (turns == null ? 0 : turns.Count) - 4))
                .Select(x => x.Text ?? string.Empty));
            var compactRecent = Compact(recent);
            return entities
                .OrderByDescending(x => compactRecent.LastIndexOf(Compact(x), StringComparison.Ordinal))
                .FirstOrDefault() ?? entities[0];
        }

        private static string DetectStage(string text)
        {
            var value = Compact(text);
            if (Regex.IsMatch(value, @"退款|退货|售后|赔偿|投诉|不到账|不能用|异常|故障")) return "售后/问题处理";
            if (Regex.IsMatch(value, @"已下单|订单|付款|支付|发货|物流|收货")) return "下单后服务";
            if (Regex.IsMatch(value, @"怎么买|购买|价格|多少钱|能不能|是否支持|区别|适合|选择")) return "售前咨询";
            return "一般咨询";
        }

        internal static string DetectIntent(string text)
        {
            var value = Compact(text);
            if (value.Length == 0) return string.Empty;
            if (Regex.IsMatch(value, @"退款|退货|售后|赔偿|投诉")) return "售后处理";
            if (Regex.IsMatch(value, @"多少钱|价格|费用|收费")) return "询问价格";
            if (Regex.IsMatch(value, @"多久|什么时候|几天|时效")) return "询问时间/时效";
            if (Regex.IsMatch(value, @"怎么|如何|步骤|操作|登录|绑定|充值方法")) return "询问操作方法";
            if (Regex.IsMatch(value, @"为什么|失败|不行|报错|异常|不能用")) return "故障排查";
            if (Regex.IsMatch(value, @"可以|能不能|能否|是否|支持|可不可以")) return "确认是否支持";
            if (Regex.IsMatch(value, @"需要什么|提供什么|要什么|资料|条件")) return "询问所需条件";
            return "一般咨询";
        }

        private static bool LooksLikePendingQuestion(string text)
        {
            var value = (text ?? string.Empty).Trim();
            if (value.IndexOf('？') >= 0 || value.IndexOf('?') >= 0) return true;
            return PendingQuestionCues.Any(x => value.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsStandaloneTopicText(string text)
        {
            var compact = Compact(text);
            if (compact.Length < 6) return false;
            if (ContainsExactOrPrefix(compact, PositiveShortReplies) || ContainsExactOrPrefix(compact, NegativeShortReplies)) return false;
            return !Regex.IsMatch(compact, @"^(这个|那个|它|那呢|这个呢|那个呢|为什么|怎么不|还是|然后|所以)");
        }

        private static bool ContainsExactOrPrefix(string compact, IEnumerable<string> values)
        {
            if (string.IsNullOrWhiteSpace(compact)) return false;
            return values.Any(x => compact == Compact(x) || compact.StartsWith(Compact(x), StringComparison.Ordinal));
        }

        private static IEnumerable<string> SplitTerms(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '|', ' ', '\r', '\n', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length >= 2);
        }

        private static bool IsStopEntity(string value)
        {
            var compact = Compact(value);
            return compact == "怎么"
                || compact == "如何"
                || compact == "可以"
                || compact == "支持"
                || compact == "是否"
                || compact == "为什么"
                || compact == "问题"
                || compact == "答案"
                || compact == "操作"
                || compact == "使用";
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
    }

    internal static class ContextualQueryRewriteService
    {
        private static readonly string[] FollowUpCues =
        {
            "这个", "那个", "它", "这款", "那款", "那呢", "这个呢", "那个呢", "为什么", "怎么不", "还是", "然后呢", "可以吗", "行吗"
        };

        public static ContextualQueryResolution Resolve(
            string currentQuestion,
            double contextDependencyScore,
            ConversationStateSnapshot state,
            IList<ConversationContextTurn> turns)
        {
            var original = (currentQuestion ?? string.Empty).Trim();
            var result = new ContextualQueryResolution
            {
                OriginalQuery = original,
                ResolvedQuery = original,
                Rewritten = false,
                Reason = "当前问题可独立用于知识检索"
            };
            if (string.IsNullOrWhiteSpace(original) || contextDependencyScore < 0.30) return result;

            var compact = Compact(original);
            var pieces = new List<string>();
            if (state != null && !string.IsNullOrWhiteSpace(state.CurrentEntity)) pieces.Add(state.CurrentEntity);

            if (state != null && !string.IsNullOrWhiteSpace(state.PendingQuestion) && compact.Length <= 16)
            {
                pieces.Add(state.PendingQuestion);
                pieces.Add("买家回答/追问：" + original);
                result.Reason = "短回复承接上一轮客服待确认问题";
            }
            else
            {
                if (state != null && !string.IsNullOrWhiteSpace(state.CurrentTopic)
                    && !SameMeaning(state.CurrentTopic, original))
                {
                    pieces.Add(state.CurrentTopic);
                }
                var previousBuyer = (turns ?? new List<ConversationContextTurn>())
                    .LastOrDefault(x => x != null && x.Role == "user" && !string.IsNullOrWhiteSpace(x.Text));
                if (pieces.Count == 0 && previousBuyer != null && !SameMeaning(previousBuyer.Text, original))
                    pieces.Add(previousBuyer.Text);
                pieces.Add("买家当前追问：" + original);
                result.Reason = FollowUpCues.Any(x => compact.Contains(Compact(x)))
                    ? "当前消息含指代或承接表达，已继承最近主题"
                    : "当前消息上下文依赖较高，已补充最近会话主题";
            }

            var resolved = string.Join("；", pieces
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => Safe(x, 240))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(resolved) || SameMeaning(resolved, original)) return result;
            result.ResolvedQuery = resolved;
            result.Rewritten = true;
            return result;
        }

        private static bool SameMeaning(string left, string right)
        {
            return Compact(left) == Compact(right);
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
    }
}
