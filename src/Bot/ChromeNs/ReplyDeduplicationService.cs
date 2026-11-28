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
            bool preserveTrustedAnswer,
            string approvedV2ContextEvidence = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(seller))
            {
                var currentShop = ShopSettingsScope.Current;
                ShopContext resolvedShop;
                try { resolvedShop = ShopContextLocator.ResolveCurrentOrBySellerNick(seller); }
                catch
                {
                    return BuildBlockedResult("无法确认当前店铺身份，已阻止跨店答案校验。");
                }
                if (resolvedShop == null || string.IsNullOrWhiteSpace(resolvedShop.ShopKey)
                    || (currentShop != null
                        && !string.Equals(currentShop.ShopKey, resolvedShop.ShopKey, StringComparison.Ordinal)))
                    return BuildBlockedResult("当前店铺身份与回复来源不一致，已阻止发送。");
                if (currentShop == null)
                {
                    using (ShopSettingsScope.Enter(resolvedShop))
                    {
                        return EnsureDistinct(seller, buyer, question, candidateAnswer,
                            cancellationToken, preserveTrustedAnswer, approvedV2ContextEvidence);
                    }
                }
            }
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

            cancellationToken.ThrowIfCancellationRequested();
            var validationRegenerated = false;
            var validationSource = string.Empty;
            if (!string.IsNullOrWhiteSpace(candidateAnswer)
                && !candidateAnswer.StartsWith("错误：", StringComparison.Ordinal))
            {
                var validation = PreSendAnswerValidator.Validate(
                    seller, buyer, question, candidateAnswer, false, approvedV2ContextEvidence);
                ReplyQualityMetricsService.RecordValidation(
                    validation.Action, validation.Issues, false);
                if (validation.Action == AnswerValidationAction.Manual)
                    return BuildBlockedResult("发送前校验要求人工确认：" + validation.Reason);
                if (validation.Action == AnswerValidationAction.Regenerate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var repaired = RegenerateInvalidAnswer(
                        seller, buyer, question, candidateAnswer, validation,
                        approvedV2ContextEvidence, cancellationToken);
                    repaired = BotFeatureStore.ApplyOutputPolicy(repaired);
                    if (string.IsNullOrWhiteSpace(repaired)
                        || repaired.StartsWith("错误：", StringComparison.Ordinal))
                    {
                        ReplyQualityMetricsService.RecordRepair(false);
                        return BuildBlockedResult("发送前校验重答失败，已阻止自动发送");
                    }

                    var secondValidation = PreSendAnswerValidator.Validate(
                        seller, buyer, question, repaired, true, approvedV2ContextEvidence);
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
            cancellationToken.ThrowIfCancellationRequested();
            var regenerated = result.Regenerated
                ? BuildSafeFallback(question)
                : Regenerate(seller, buyer, question, previousAnswer, approvedV2ContextEvidence, cancellationToken);
            if (string.IsNullOrWhiteSpace(regenerated)
                || regenerated.StartsWith("错误：", StringComparison.Ordinal)
                || SameAnswer(previousAnswer, regenerated))
                regenerated = BuildSafeFallback(question);

            regenerated = BotFeatureStore.ApplyOutputPolicy(regenerated);
            if (string.IsNullOrWhiteSpace(regenerated) || SameAnswer(previousAnswer, regenerated))
                regenerated = "如果前面的步骤都试过仍无效，建议转人工进一步核查。";

            var duplicateValidation = PreSendAnswerValidator.Validate(
                seller, buyer, question, regenerated, true, approvedV2ContextEvidence);
            ReplyQualityMetricsService.RecordValidation(
                duplicateValidation.Action, duplicateValidation.Issues, true);
            if (duplicateValidation.Action != AnswerValidationAction.Pass)
                return BuildBlockedResult("重复答案重答未通过发送前校验：" + duplicateValidation.Reason);

            result.Answer = BotOutboundMessageFormatter.EnsureAiMarker(regenerated);
            result.PreviousAnswer = previousAnswer;
            result.Source = result.Regenerated
                ? "AI校验重答+重复兜底"
                : "AI重答（Knowledge V2上下文约束）";
            result.Regenerated = true;
            KnowledgeLearningService.RegisterAnswerSource(
                seller, buyer, question,
                BotOutboundMessageFormatter.StripAiMarker(result.Answer),
                result.Source);
            Log.Info("检测到本店上一轮完全相同答案，已重新生成。seller="
                + seller + ", buyer=" + buyer + ", source=" + result.Source);
            return result;
        }

        internal static ReplyDeduplicationResult EnsureWorkflowReplyDistinct(
            string seller,
            string buyer,
            string question,
            string candidateAnswer,
            string shopKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShopContext shop;
            try
            {
                shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
            }
            catch
            {
                return BuildBlockedResult("无法按精确ShopKey验证接待流程回复，已阻止发送。");
            }
            if (shop == null || !shop.HasStableSellerId
                || !string.Equals(shop.ShopKey, (shopKey ?? string.Empty).Trim(), StringComparison.Ordinal)
                || !string.Equals(shop.SellerNick, (seller ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                return BuildBlockedResult("接待流程回复的Seller/ShopKey身份不匹配，已阻止发送。");

            using (ShopSettingsScope.Enter(shop))
            {
                string previousAnswer;
                DateTime previousAt;
                if (TryGetLastDelivered(seller, buyer, out previousAnswer, out previousAt)
                    && SameAnswer(previousAnswer, candidateAnswer))
                    return BuildBlockedResult("接待流程回复与上一条有效回复重复，已停止自动重发。");

                return EnsureDistinct(
                    seller, buyer, question, candidateAnswer, cancellationToken, true);
            }
        }

        internal static void RememberDelivered(
            string seller, string buyer, string answer, string shopKey)
        {
            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey); }
            catch { return; }
            if (shop == null || !shop.HasStableSellerId
                || !string.Equals(shop.ShopKey, (shopKey ?? string.Empty).Trim(), StringComparison.Ordinal)
                || !string.Equals(shop.SellerNick, (seller ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                return;
            using (ShopSettingsScope.Enter(shop)) RememberDelivered(seller, buyer, answer);
        }

        public static void RememberDelivered(string seller, string buyer, string answer)
        {
            if (string.IsNullOrWhiteSpace(answer)
                || answer.StartsWith("错误：", StringComparison.Ordinal)) return;
            var key = Key(seller, buyer);
            if (key.Length == 0) return;
            LastDelivered[key] = new DeliveredAnswerStamp
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
            var key = Key(seller, buyer);
            if (key.Length == 0) return false;
            DeliveredAnswerStamp stamp;
            if (LastDelivered.TryGetValue(key, out stamp)
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

        private static string RegenerateInvalidAnswer(
            string seller,
            string buyer,
            string question,
            string invalidAnswer,
            AnswerValidationResult validation,
            string approvedV2ContextEvidence,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timeline = ConversationContextStore.BuildTimelineText(seller, buyer, question, 12);
                var evidence = PreSendAnswerValidator.BuildEvidenceText(approvedV2ContextEvidence);
                if (!string.IsNullOrWhiteSpace(approvedV2ContextEvidence))
                    evidence = (evidence + "\n" + approvedV2ContextEvidence).Trim();
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
            string approvedV2ContextEvidence,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timeline = ConversationContextStore.BuildTimelineText(seller, buyer, question, 12);
                var factBoundary = string.IsNullOrWhiteSpace(approvedV2ContextEvidence)
                    ? "未提供可确认的 Knowledge V2 事实；上一轮答案仅用于理解上下文，不得增加新的商品承诺或结论。"
                    : "【Knowledge V2已批准上下文证据】\n" + approvedV2ContextEvidence;
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
            var shopKey = ScopeKey(seller);
            if (shopKey.Length == 0) return string.Empty;
            return shopKey + "|" + (seller ?? string.Empty).Trim().ToLowerInvariant()
                + "|" + (buyer ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string ScopeKey(string seller)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return string.Empty;
            var current = ShopSettingsScope.Current;
            if (current != null)
            {
                try
                {
                    var resolved = ShopContextLocator.ResolveCurrentOrBySellerNick(seller);
                    return resolved != null
                        && string.Equals(current.ShopKey, resolved.ShopKey, StringComparison.Ordinal)
                        ? current.ShopKey
                        : string.Empty;
                }
                catch { return string.Empty; }
            }
            try { return ShopContextLocator.ResolveRuntimeBySellerNick(seller).ShopKey; }
            catch { return string.Empty; }
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
