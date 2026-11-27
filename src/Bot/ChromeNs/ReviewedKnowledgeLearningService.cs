using Bot.Knowledge;
using Bot.ShopScope;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Explicitly accepted operator corrections are written directly to the
    /// authoritative Knowledge V2 repository. This service never reads or
    /// mutates the retired KnowledgeBaseJson store.
    /// </summary>
    internal static class ReviewedKnowledgeLearningService
    {
        public static KnowledgeLearningResult ApplyReviewedKnowledge(
            string question,
            string answer,
            string category,
            string keywords,
            string sourceType,
            double confidence,
            string evidenceType)
        {
            question = (question ?? string.Empty).Trim();
            answer = BotOutboundMessageFormatter.StripAiMarker((answer ?? string.Empty).Trim());
            evidenceType = (evidenceType ?? string.Empty).Trim().ToLowerInvariant();

            if (question.Length < 5 || question.Length > 500 || answer.Length < 2 || answer.Length > 4000)
                return Failure("问题或答案长度不合法，未写入 Knowledge V2。");
            if (answer.StartsWith("错误：", StringComparison.Ordinal)
                || answer.IndexOf("已跳过", StringComparison.Ordinal) >= 0)
                return Failure("答案属于错误或跳过状态，未写入 Knowledge V2。");
            if (double.IsNaN(confidence) || double.IsInfinity(confidence)
                || confidence < 0.90 || confidence > 1.0)
                return Failure("证据置信度未达到 Knowledge V2 直接发布门槛。");
            if (evidenceType != "manual_reply"
                && evidenceType != "manual_correction"
                && evidenceType != "withdrawn_bot_then_manual"
                && evidenceType != "repeated_human_pattern"
                && evidenceType != "operator_reviewed")
                return Failure("缺少可接受的真人或操作员审核证据类型。");

            var shop = ShopSettingsScope.Current;
            if (shop == null || !shop.HasStableSellerId || string.IsNullOrWhiteSpace(shop.SellerNick))
                return Failure("当前没有已确认的店铺 ShopKey，拒绝跨店写入 Knowledge V2。");

            try
            {
                var before = KnowledgeEngineV2Repository.LoadAll(shop.SellerNick);
                var normalizedQuestion = KnowledgeEngineV2Semantics.Compact(question);
                var existing = (before ?? new List<KnowledgeV2Record>())
                    .FirstOrDefault(x => x != null && x.Enabled
                        && string.Equals(KnowledgeEngineV2Semantics.Compact(x.Title), normalizedQuestion, StringComparison.Ordinal));
                string result;
                string savedId;
                var saved = KnowledgeEngineV2Repository.TrySaveReviewedDirectAnswer(
                    shop.SellerNick, question, answer, string.Empty, out result, out savedId);
                if (!saved)
                    return Failure(string.IsNullOrWhiteSpace(result) ? "Knowledge V2 拒绝了本次直答写入。" : result);

                var record = KnowledgeEngineV2Repository.LoadAll(shop.SellerNick)
                    .FirstOrDefault(x => x != null && string.Equals(x.Id, savedId, StringComparison.Ordinal));
                if (record != null)
                {
                    var aliases = (record.Aliases ?? new List<string>())
                        .Concat((keywords ?? string.Empty).Split(new[] { ',', '，', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                        .Select(x => (x ?? string.Empty).Trim())
                        .Where(x => x.Length >= 2 && x.Length <= 80)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(40)
                        .ToList();
                    record.Aliases = aliases;
                    KnowledgeEngineV2Repository.Save(shop.SellerNick, record);
                }

                ReplyQualityMetricsService.RecordHumanEvidence(evidenceType);
                return new KnowledgeLearningResult
                {
                    Success = true,
                    Added = existing == null,
                    Updated = existing != null,
                    Message = string.IsNullOrWhiteSpace(result)
                        ? "已写入 Knowledge V2 并刷新检索索引。"
                        : result
                };
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("Knowledge V2人工审核写入失败: " + ex.GetType().Name, 10);
                return Failure("Knowledge V2 写入失败：" + ex.Message);
            }
        }

        private static KnowledgeLearningResult Failure(string message)
        {
            return new KnowledgeLearningResult { Success = false, Message = message ?? "Knowledge V2 写入失败。" };
        }
    }
}
