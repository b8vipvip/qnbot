using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bot.ChromeNs
{
    internal sealed class ReplyDeduplicationResult
    {
        public string Answer { get; set; }
        public string PreviousAnswer { get; set; }
        public string Source { get; set; }
        public bool Regenerated { get; set; }
    }

    internal static class ReplyDeduplicationService
    {
        private sealed class DeliveredAnswerStamp
        {
            public string Answer;
            public DateTime SentAt;
        }

        private static readonly ConcurrentDictionary<string, DeliveredAnswerStamp> LastDelivered =
            new ConcurrentDictionary<string, DeliveredAnswerStamp>(StringComparer.Ordinal);

        public static ReplyDeduplicationResult EnsureDistinct(
            string seller,
            string buyer,
            string question,
            string candidateAnswer)
        {
            return EnsureDistinct(
                seller, buyer, question, candidateAnswer, CancellationToken.None, false);
        }

        public static ReplyDeduplicationResult EnsureDistinct(
            string seller,
            string buyer,
            string question,
            string candidateAnswer,
            CancellationToken cancellationToken,
            bool preserveTrustedAnswer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preserveTrustedAnswer)
            {
                // Knowledge V2 has already made the production-authority decision. Do not run a
                // hidden AI validator/regenerator here: that would both mutate authoritative facts
                // and let a "local" reply outlive its BuyerSessionAgent generation.
                return new ReplyDeduplicationResult
                {
                    Answer = BotOutboundMessageFormatter.EnsureAiMarker(candidateAnswer),
                    PreviousAnswer = string.Empty,
                    Source = "权威知识V2",
                    Regenerated = false
                };
            }

            string aiFailureFallbackAnswer;
            KnowledgeBaseEntry aiFailureFallbackKnowledge;
            double aiFailureFallbackScore;
            cancellationToken.ThrowIfCancellationRequested();
            var aiFailureFallbackApplied = AiFailureKnowledgeFallbackService.TryResolve(
                seller,
                buyer,
                question,
                candidateAnswer,
                out aiFailureFallbackAnswer,
                out aiFailureFallbackKnowledge,
                out aiFailureFallbackScore);
            if (aiFailureFallbackApplied)
            {
                candidateAnswer = aiFailureFallbackAnswer;
                KnowledgeLearningService.RegisterAnswerSource(
                    seller, buyer, question, candidateAnswer, "AI异常本地兜底");
            }

            var knowledge = aiFailureFallbackKnowledge
                ?? ResolveKnowledge(seller, buyer, question, candidateAnswer);
            var validationRegenerated = aiFailureFallbackApplied;
            var validationSource = aiFailureFallbackApplied ? "AI异常本地兜底" : string.Empty;
            // 50%是故障时的应急阈值，低于正常知识直答置信度；即使命中本地知识，
            // 仍强制走一次发送前事实/风险校验，不能因为答案来自知识库就跳过安全检查。
            var exactTrustedKnowledge = knowledge != null
                && SameAnswer(knowledge.Answer, candidateAnswer)
                && !aiFailureFallbackApplied;

            if (!exactTrustedKnowledge
                && !string.IsNullOrWhiteSpace(candidateAnswer)
                && !candidateAnswer.StartsWith("错误：", StringComparison.Ordinal))
            {
                var validation = PreSendAnswerValidator.Validate(
                    seller, buyer, question, candidateAnswer, knowledge, false);
                ReplyQualityMetricsService.RecordValidation(
                    validation.Action, validation.Issues, false);
                if (validation.Action == AnswerValidationAction.Manual)
                    return BuildBlockedResult("发送前校验要求人工确认：" + validation.Reason);
                if (validation.Action == AnswerValidationAction.Regenerate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var repaired = RegenerateInvalidAnswer(
                        seller, buyer, question, candidateAnswer, knowledge, validation,
                        cancellationToken);
                    repaired = BotFeatureStore.ApplyOutputPolicy(repaired);
                    if (string.IsNullOrWhiteSpace(repaired)
                        || repaired.StartsWith("错误：", StringComparison.Ordinal))
                    {
                        ReplyQualityMetricsService.RecordRepair(false);
                        return BuildBlockedResult("发送前校验重答失败，已阻止自动发送");
                    }

                    var secondValidation = PreSendAnswerValidator.Validate(
                        seller, buyer, question, repaired, knowledge, true);
                    ReplyQualityMetricsService.RecordValidation(
                        secondValidation.Action, secondValidation.Issues, true);
                    if (secondValidation.Action != AnswerValidationAction.Pass)
                    {
                        ReplyQualityMetricsService.RecordRepair(false);
                        return BuildBlockedResult("修正后的答案仍未通过发送前校验：" + secondValidation.Reason);
                    }

                    ReplyQualityMetricsService.RecordRepair(true);
                    candidateAnswer = repaired;
                    validationRegenerated = true;
                    validationSource = "AI校验重答";
                    KnowledgeLearningService.RegisterAnswerSource(
                        seller, buyer, question, candidateAnswer, validationSource);
                    Log.Info("本店发送前答案校验已完成一次安全重答: seller="
                        + seller + ", buyer=" + buyer);
                }
            }

            var result = new ReplyDeduplicationResult
            {
                Answer = BotOutboundMessageFormatter.EnsureAiMarker(candidateAnswer),
                PreviousAnswer = string.Empty,
                Source = validationSource,
                Regenerated = validationRegenerated
            };
            if (string.IsNullOrWhiteSpace(result.Answer)
                || result.Answer.StartsWith("错误：", StringComparison.Ordinal)) return result;

            string previousAnswer;
            DateTime previousAt;
            if (!TryGetLastDelivered(seller, buyer, out previousAnswer, out previousAt)
                || !SameAnswer(previousAnswer, result.Answer)) return result;

            ReplyQualityMetricsService.RecordDuplicateRewrite();
            knowledge = knowledge ?? ResolveKnowledge(seller, buyer, question, result.Answer);
            cancellationToken.ThrowIfCancellationRequested();
            var regenerated = result.Regenerated
                ? BuildSafeFallback(question)
                : Regenerate(seller, buyer, question, previousAnswer, knowledge, cancellationToken);
            if (string.IsNullOrWhiteSpace(regenerated)
                || regenerated.StartsWith("错误：", StringComparison.Ordinal)
                || SameAnswer(previousAnswer, regenerated))
                regenerated = BuildSafeFallback(question);

            regenerated = BotFeatureStore.ApplyOutputPolicy(regenerated);
            if (string.IsNullOrWhiteSpace(regenerated) || SameAnswer(previousAnswer, regenerated))
                regenerated = "如果前面的步骤都试过仍无效，建议转人工进一步核查。";

            var duplicateValidation = PreSendAnswerValidator.Validate(
                seller, buyer, question, regenerated, knowledge, true);
            ReplyQualityMetricsService.RecordValidation(
                duplicateValidation.Action, duplicateValidation.Issues, true);
            if (duplicateValidation.Action != AnswerValidationAction.Pass)
                return BuildBlockedResult("重复答案重答未通过发送前校验：" + duplicateValidation.Reason);

            result.Answer = BotOutboundMessageFormatter.EnsureAiMarker(regenerated);
            result.PreviousAnswer = previousAnswer;
            result.Source = result.Regenerated
                ? "AI校验重答+重复兜底"
                : (knowledge == null ? "AI重答" : "本地知识库重答");
            result.Regenerated = true;
            KnowledgeLearningService.RegisterAnswerSource(
                seller, buyer, question,
                BotOutboundMessageFormatter.StripAiMarker(result.Answer),
                result.Source);
            Log.Info("检测到本店上一轮完全相同答案，已重新生成。seller="
                + seller + ", buyer=" + buyer + ", source=" + result.Source);
            return result;
        }

        public static void RememberDelivered(string seller, string buyer, string answer)
        {
            if (string.IsNullOrWhiteSpace(answer)
                || answer.StartsWith("错误：", StringComparison.Ordinal)) return;
            LastDelivered[Key(seller, buyer)] = new DeliveredAnswerStamp
            {
                Answer = BotOutboundMessageFormatter.EnsureAiMarker(answer),
                SentAt = DateTime.Now
            };
            Cleanup();
        }

        public static bool TryGetLastDelivered(
            string seller,
            string buyer,
            out string answer,
            out DateTime sentAt)
        {
            answer = string.Empty;
            sentAt = DateTime.MinValue;
            DeliveredAnswerStamp stamp;
            if (LastDelivered.TryGetValue(Key(seller, buyer), out stamp)
                && stamp != null
                && stamp.SentAt >= DateTime.Now.AddMinutes(-30)
                && !string.IsNullOrWhiteSpace(stamp.Answer))
            {
                answer = stamp.Answer;
                sentAt = stamp.SentAt;
                return true;
            }

            var latest = ConversationContextStore
                .GetRecentTurns(seller, buyer, string.Empty, 12)
                .Where(x => x != null
                    && x.Role == "assistant"
                    && !x.Withdrawn
                    && !string.IsNullOrWhiteSpace(x.Text))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();
            if (latest == null) return false;
            if (latest.Timestamp != DateTime.MinValue
                && latest.Timestamp < DateTime.Now.AddMinutes(-30)) return false;
            answer = latest.Text.Trim();
            sentAt = latest.Timestamp == DateTime.MinValue ? DateTime.Now : latest.Timestamp;
            return true;
        }

        private static KnowledgeBaseEntry ResolveKnowledge(
            string seller,
            string buyer,
            string question,
            string candidateAnswer)
        {
            var answerKey = Canonical(candidateAnswer);
            var knowledge = BotFeatureStore.GetKnowledgeBase()
                .FirstOrDefault(x => x != null
                    && x.Enabled
                    && !string.IsNullOrWhiteSpace(x.Answer)
                    && (Canonical(x.Answer) == answerKey
                        || Canonical(BotFeatureStore.ApplyOutputPolicy(x.Answer)) == answerKey));
            if (knowledge != null) return knowledge;
            KnowledgeBaseEntry matched;
            double score;
            return KnowledgeLearningService.TryFindLocalAnswer(
                seller, buyer, question, out matched, out score) ? matched : null;
        }

        private static string RegenerateInvalidAnswer(
            string seller,
            string buyer,
            string question,
            string invalidAnswer,
            KnowledgeBaseEntry knowledge,
            AnswerValidationResult validation,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            {
                var timeline = ConversationContextStore.BuildTimelineText(seller, buyer, question, 12);
                var evidence = PreSendAnswerValidator.BuildEvidenceText(knowledge);
                var messages = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] = "你是电商客服答案修正器。" + validation.RegenerationInstruction
                    },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = (string.IsNullOrWhiteSpace(evidence)
                                ? "【可靠事实】未提供可确认的店铺事实，禁止自行补充。"
                                : evidence)
                            + "\n【买家当前问题】\n" + (question ?? string.Empty)
                            + "\n【未通过校验的原答案】\n"
                            + BotOutboundMessageFormatter.StripAiMarker(invalidAnswer)
                            + (string.IsNullOrWhiteSpace(timeline)
                                ? string.Empty
                                : "\n【同一买家最近时间线】\n" + timeline)
                    }
                };
                var response = MyOpenAI.CallStructuredChat(
                    messages, 220, 0.10, 45, cancellationToken);
                return response != null && response.Success
                    ? (response.Answer ?? string.Empty).Trim()
                    : string.Empty;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Info("本店发送前答案校验重答失败：" + ex.Message);
                return string.Empty;
            }
        }

        private static string Regenerate(
            string seller,
            string buyer,
            string question,
            string previousAnswer,
            KnowledgeBaseEntry knowledge,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timeline = ConversationContextStore.BuildTimelineText(seller, buyer, question, 12);
                var factBoundary = knowledge == null
                    ? "上一轮答案是当前唯一事实边界，不得增加新的商品承诺或结论。"
                    : "知识库问题：" + knowledge.Title + "\n知识库答案：" + knowledge.Answer;
                var messages = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] = "你是电商客服续答助手。候选答案与上一轮客服回复完全相同，禁止再次原样回复，也不要只做同义改写。必须结合买家当前新消息推进对话：买家表示已解决时简短确认；表示没解决、否定或追问时，承认前一步未解决，并给出事实范围内的下一步；没有新步骤时建议转人工核查。只回复一句，最多60字，不得编造价格、库存、到账状态、时效或售后承诺。"
                    },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = factBoundary
                            + "\n上一轮客服答案：" + BotOutboundMessageFormatter.StripAiMarker(previousAnswer)
                            + "\n当前买家消息：" + (question ?? string.Empty)
                            + (string.IsNullOrWhiteSpace(timeline)
                                ? string.Empty
                                : "\n同一买家最近时间线：\n" + timeline)
                    }
                };
                var response = MyOpenAI.CallStructuredChat(
                    messages, 180, 0.35, 90, cancellationToken);
                return response != null && response.Success
                    ? (response.Answer ?? string.Empty).Trim()
                    : string.Empty;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Info("本店重复答案重新生成失败，使用安全兜底：" + ex.Message);
                return string.Empty;
            }
        }

        private static ReplyDeduplicationResult BuildBlockedResult(string reason)
        {
            return new ReplyDeduplicationResult
            {
                Answer = "错误：" + (reason ?? "发送前校验未通过"),
                PreviousAnswer = string.Empty,
                Source = "发送前校验",
                Regenerated = false
            };
        }

        private static string BuildSafeFallback(string question)
        {
            var compact = Canonical(question);
            if (ContainsAny(compact, "好了", "可以了", "解决了", "能用了", "正常了"))
                return "好的，能正常使用就行，有其他问题再告诉我。";
            if (ContainsAny(compact, "没有", "没到", "不行", "不可以", "不能", "还是", "没解决"))
                return "明白，刚才的方法还没解决，我换个方向继续帮您排查。";
            if (ContainsAny(compact, "怎么回事", "为什么", "怎么了"))
                return "这说明刚才的处理还没生效，我继续帮您核查下一步。";
            return "明白，我换个思路继续帮您处理，避免重复前面的步骤。";
        }

        private static bool ContainsAny(string value, params string[] cues)
        {
            return cues.Any(x => value.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool SameAnswer(string left, string right)
        {
            return string.Equals(Canonical(left), Canonical(right), StringComparison.Ordinal);
        }

        private static string Canonical(string value)
        {
            value = BotOutboundMessageFormatter.StripAiMarker(value);
            return Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
        }

        private static string Key(string seller, string buyer)
        {
            return ScopeKey(seller) + "|" + (seller ?? string.Empty).Trim().ToLowerInvariant()
                + "|" + (buyer ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string ScopeKey(string seller)
        {
            var current = ShopSettingsScope.Current;
            if (current != null) return current.ShopKey;
            try { return ShopContextLocator.ResolveRuntimeBySellerNick(seller).ShopKey; }
            catch { return "legacy-" + (seller ?? string.Empty).Trim().ToLowerInvariant(); }
        }

        private static void Cleanup()
        {
            var cutoff = DateTime.Now.AddHours(-2);
            foreach (var key in LastDelivered
                .Where(x => x.Value == null || x.Value.SentAt < cutoff)
                .Select(x => x.Key).ToList())
            {
                DeliveredAnswerStamp ignored;
                LastDelivered.TryRemove(key, out ignored);
            }
        }
    }

    internal static class AiFailureKnowledgeFallbackService
    {
        internal const double MinimumFallbackScore = 0.50;

        public static bool TryResolve(
            string seller,
            string buyer,
            string question,
            string aiError,
            out string answer,
            out KnowledgeBaseEntry knowledge,
            out double score)
        {
            answer = string.Empty;
            knowledge = null;
            score = 0;

            ShopContext shop = null;
            if (ShopSettingsScope.Current == null && !string.IsNullOrWhiteSpace(seller))
            {
                try { shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller); }
                catch { shop = null; }
            }

            if (shop != null)
            {
                using (ShopSettingsScope.Enter(shop))
                {
                    return TryResolveCore(seller, buyer, question, aiError, out answer, out knowledge, out score);
                }
            }
            return TryResolveCore(seller, buyer, question, aiError, out answer, out knowledge, out score);
        }

        private static bool TryResolveCore(
            string seller,
            string buyer,
            string question,
            string aiError,
            out string answer,
            out KnowledgeBaseEntry knowledge,
            out double score)
        {
            answer = string.Empty;
            knowledge = null;
            score = 0;
            question = (question ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(question)) return false;
            if (!IsAuthenticatedControlPlaneUpstreamFailure(aiError)) return false;

            var policy = BotFeatureStore.GetMessagePolicy();
            if (policy == null || !policy.EnableKnowledgeBase)
            {
                Log.Info("AI上游异常，但本店知识库未启用，不能执行50%本地兜底。seller=" + seller + ", buyer=" + buyer);
                return false;
            }

            ConversationContextTurn latestAgentPrompt = null;
            if (IsShortContextReply(question))
            {
                var turns = ConversationContextStore.GetRecentTurns(seller, buyer, question, 8);
                latestAgentPrompt = turns.LastOrDefault(x => x != null
                    && x.Role == "assistant"
                    && !string.IsNullOrWhiteSpace(x.Text));
            }

            foreach (var item in BotFeatureStore.GetKnowledgeBase()
                .Where(x => x != null && x.Enabled && !string.IsNullOrWhiteSpace(x.Answer)))
            {
                var currentScore = Score(item, question, false);
                if (latestAgentPrompt != null)
                {
                    currentScore = Math.Max(currentScore, Score(item, latestAgentPrompt.Text, true));
                }
                if (currentScore > score)
                {
                    score = currentScore;
                    knowledge = item;
                }
            }

            if (knowledge == null || score < MinimumFallbackScore)
            {
                Log.Info("AI上游异常且Bot令牌已通过服务端鉴权，但本店知识库最高匹配不足50%，不自动发送。seller="
                    + seller + ", buyer=" + buyer + ", bestScore=" + score.ToString("0.00"));
                return false;
            }

            answer = BotFeatureStore.ApplyOutputPolicy(knowledge.Answer);
            if (string.IsNullOrWhiteSpace(answer)
                || ConversationContextStore.IsWithdrawnAnswer(seller, buyer, answer))
            {
                Log.Info("AI上游异常命中本店知识库，但答案为空或已被客服撤回，已阻止50%兜底发送。seller="
                    + seller + ", buyer=" + buyer + ", knowledgeId=" + knowledge.Id);
                answer = string.Empty;
                return false;
            }

            Log.Info("AI上游异常且Bot令牌已通过服务端鉴权，启用本店知识库50%兜底。seller="
                + seller + ", buyer=" + buyer + ", knowledgeId=" + knowledge.Id
                + ", score=" + score.ToString("0.00"));
            return true;
        }

        private static bool IsAuthenticatedControlPlaneUpstreamFailure(string aiError)
        {
            aiError = (aiError ?? string.Empty).Trim();
            if (!aiError.StartsWith("错误：AI接口调用失败", StringComparison.Ordinal)) return false;
            if (aiError.IndexOf("HTTP 502", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (aiError.IndexOf("upstream_exhausted", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (aiError.IndexOf("所有供应商、模型和请求协议均调用失败", StringComparison.Ordinal) < 0) return false;

            // Control Plane 的 /v1/chat/completions 先执行 Bearer 客户端令牌鉴权，
            // 只有鉴权通过后才会进入上游供应商路由，并以 502 + upstream_exhausted
            // 表示“服务端可达、Bot令牌有效，但所有AI上游均失败”。
            var controlPlaneEndpoints = AiEndpointStore.GetEnabledEndpoints()
                .Where(x => x != null
                    && string.Equals(x.Type, "服务端控制面", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (controlPlaneEndpoints.Count < 1) return false;

            return controlPlaneEndpoints.Any(endpoint =>
                string.IsNullOrWhiteSpace(endpoint.Name)
                || aiError.IndexOf(endpoint.Name + "：", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsShortContextReply(string value)
        {
            var compact = Normalize(value);
            if (compact.Length == 0 || compact.Length > 32) return false;
            if (compact.IndexOf('?') >= 0 || compact.IndexOf('？') >= 0) return false;
            if (Regex.IsMatch(compact, @"^[a-z0-9@._+\-:/]+$", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(compact, @"^\d+$")) return true;
            return compact.Length <= 8;
        }

        private static double Score(KnowledgeBaseEntry item, string query, bool contextOnly)
        {
            var q = KnowledgeAiService.NormalizeQuestion(query);
            var title = KnowledgeAiService.NormalizeQuestion(item.Title);
            if (string.IsNullOrWhiteSpace(q) || string.IsNullOrWhiteSpace(title)) return 0;
            if (q == title) return contextOnly ? 0.91 : 1.0;
            if (Math.Min(q.Length, title.Length) >= 4 && (q.Contains(title) || title.Contains(q)))
                return contextOnly ? 0.87 : 0.95;
            foreach (var keyword in SplitKeywords(item.Keywords))
            {
                var normalizedKeyword = KnowledgeAiService.NormalizeQuestion(keyword);
                if (normalizedKeyword.Length >= 2 && q.Contains(normalizedKeyword))
                    return contextOnly ? 0.85 : 0.90;
            }
            var similarity = BigramSimilarity(q, title);
            if (similarity >= 0.68) return contextOnly ? 0.84 : 0.86;
            return similarity * 0.75;
        }

        private static IEnumerable<string> SplitKeywords(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '|', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim());
        }

        private static double BigramSimilarity(string a, string b)
        {
            var aa = Bigrams(a);
            var bb = Bigrams(b);
            if (aa.Count == 0 || bb.Count == 0) return 0;
            var common = aa.Intersect(bb).Count();
            return (2.0 * common) / (aa.Count + bb.Count);
        }

        private static HashSet<string> Bigrams(string value)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < (value ?? string.Empty).Length; i++)
            {
                set.Add(value.Substring(i, 2));
            }
            return set;
        }

        private static string Normalize(string value)
        {
            return KnowledgeAiService.NormalizeQuestion(value ?? string.Empty);
        }
    }

    internal static class BotOutboundMessageFormatter
    {
        public const string AiMarker = "[AI]";
        public const string StreamAbortMarker = "[[QN_STREAM_ABORTED]]";

        public static string EnsureAiMarker(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.IndexOf(StreamAbortMarker, StringComparison.Ordinal) >= 0)
            {
                Log.Info("检测到AI流式输出中断标识，已阻止发送半截答案。");
                return "错误：AI流式输出中断，已阻止发送半截答案，请重新获取完整答案。";
            }
            if (value.Length == 0 || value.StartsWith("错误：", StringComparison.Ordinal)) return value;
            var suffix = BotMessageSuffixService.GetCurrentSuffix();
            value = StripAiMarker(value);
            if (suffix.Length == 0) return value;
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return value;
            return value + " " + suffix;
        }

        public static string StripAiMarker(string value)
        {
            value = (value ?? string.Empty).Trim();
            var configuredSuffix = BotMessageSuffixService.GetCurrentSuffix();
            if (!string.IsNullOrWhiteSpace(configuredSuffix))
            {
                while (value.EndsWith(configuredSuffix, StringComparison.OrdinalIgnoreCase))
                    value = value.Substring(0, value.Length - configuredSuffix.Length).TrimEnd();
            }
            while (value.EndsWith(AiMarker, StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - AiMarker.Length).TrimEnd();
            return value;
        }
    }
}
