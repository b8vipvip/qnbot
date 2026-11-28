using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeV2HistoryPromotionResult
    {
        public int SourceItems { get; set; }
        public int Added { get; set; }
        public int Published { get; set; }
        public int Candidates { get; set; }
        public int DuplicateSkipped { get; set; }
        public List<KnowledgeV2Record> AddedItems { get; private set; }

        public KnowledgeV2HistoryPromotionResult()
        {
            AddedItems = new List<KnowledgeV2Record>();
        }
    }

    /// <summary>
    /// Promotes analyzed historical human Q/A material directly into the single
    /// Knowledge V2 repository. The import DTO is never persisted to a V1 store.
    /// </summary>
    internal static class KnowledgeV2HistoryImportService
    {
        public static KnowledgeV2HistoryPromotionResult PromoteHistoryImport(
            string seller, string shopKey, KnowledgeImportResult importResult)
        {
            seller = (seller ?? string.Empty).Trim();
            shopKey = (shopKey ?? string.Empty).Trim();
            if (seller.Length == 0 || shopKey.Length == 0)
                throw new InvalidOperationException("历史聊天导入必须提供Seller和精确ShopKey。");

            var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
            if (shop == null || !shop.HasStableSellerId
                || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)
                || !string.Equals((shop.SellerNick ?? string.Empty).Trim(), seller, StringComparison.Ordinal))
                throw new InvalidOperationException("Seller与ShopKey不匹配，已阻止跨店写入 Knowledge V2。");

            using (ShopSettingsScope.Enter(shop))
            {
                importResult = importResult ?? new KnowledgeImportResult();
                var sourceItems = (importResult.AddedItems ?? new List<KnowledgeV2Record>())
                    .Where(x => x != null).ToList();
                var result = new KnowledgeV2HistoryPromotionResult { SourceItems = sourceItems.Count };
                var existingHashes = new HashSet<string>(
                    KnowledgeEngineV2Repository.LoadAll(seller).Where(x => x != null)
                        .Select(x => KnowledgeAiService.ContentHash(x.Title, x.Answer)),
                    StringComparer.Ordinal);
                var importId = "chat-history-" + DateTime.Now.ToString("yyyyMMddHHmmss")
                    + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

                foreach (var source in sourceItems)
                {
                    var title = (source.Title ?? string.Empty).Trim();
                    var answer = (source.Answer ?? string.Empty).Trim();
                    if (title.Length < 4 || answer.Length < 2) continue;
                    var hash = KnowledgeAiService.ContentHash(title, answer);
                    if (!existingHashes.Add(hash))
                    {
                        result.DuplicateSkipped++;
                        continue;
                    }

                    var tokens = (source.ImportEvidenceTokens ?? new List<string>())
                        .Select(x => (x ?? string.Empty).Trim())
                        .Where(x => x.Length > 0)
                        .Distinct(StringComparer.Ordinal)
                        .Take(12)
                        .ToList();
                    var evidenceMap = importResult.SourceEvidenceByToken
                        ?? new Dictionary<string, KnowledgeV2ImportEvidence>(StringComparer.Ordinal);
                    var evidence = tokens.Select(token =>
                    {
                        KnowledgeV2ImportEvidence item;
                        return evidenceMap.TryGetValue(token, out item) ? item : null;
                    }).ToList();
                    var allTokensKnown = tokens.Count > 0
                        && evidence.Count == tokens.Count
                        && evidence.All(x => x != null
                            && string.Equals(x.EvidenceType, "historical_human_reply", StringComparison.Ordinal));
                    var questionIds = evidence.Where(x => x != null)
                        .SelectMany(x => x.QuestionMessageIds ?? new List<string>())
                        .Select(x => (x ?? string.Empty).Trim())
                        .Where(x => x.Length > 0 && !x.StartsWith("scan-turn:", StringComparison.Ordinal))
                        .Distinct(StringComparer.Ordinal).Take(40).ToList();
                    var answerIds = evidence.Where(x => x != null)
                        .SelectMany(x => x.AnswerMessageIds ?? new List<string>())
                        .Select(x => (x ?? string.Empty).Trim())
                        .Where(x => x.Length > 0 && !x.StartsWith("scan-turn:", StringComparison.Ordinal))
                        .Distinct(StringComparer.Ordinal).Take(40).ToList();
                    var contextIds = evidence.Where(x => x != null)
                        .SelectMany(x => x.ContextMessageIds ?? new List<string>())
                        .Select(x => (x ?? string.Empty).Trim())
                        .Where(x => x.Length > 0 && !x.StartsWith("scan-turn:", StringComparison.Ordinal))
                        .Distinct(StringComparer.Ordinal).Take(12).ToList();
                    var evidenceValid = allTokensKnown
                        && evidence.All(x => x.QuestionMessageIds != null && x.QuestionMessageIds.Count > 0
                            && x.AnswerMessageIds != null && x.AnswerMessageIds.Count > 0)
                        && questionIds.Count > 0
                        && answerIds.Count > 0
                        && !questionIds.Intersect(answerIds, StringComparer.Ordinal).Any()
                        && evidence.All(x => MatchesSourceText(title, x.QuestionText)
                            && MatchesSourceText(answer, x.AnswerText));

                    // Preserve the transient tokens before cloning: they are intentionally
                    // JsonIgnore and never become an independent V2 fact or cloud payload.
                    var record = JsonConvert.DeserializeObject<KnowledgeV2Record>(
                        JsonConvert.SerializeObject(source)) ?? new KnowledgeV2Record();
                    var parsed = KnowledgeEngineV2Semantics.Parse(title, null);
                    var risk = string.Equals(record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase)
                        || KnowledgeEngineV2Semantics.IsHighRisk(title + " " + answer);
                    var modelCandidate = string.Equals(record.Status, "candidate", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase);
                    var confidence = record.Confidence;
                    var authority = record.Authority;
                    var autoPublish = evidenceValid && !risk && !modelCandidate
                        && !parsed.ContextDependent
                        && !double.IsNaN(confidence) && !double.IsInfinity(confidence)
                        && confidence >= 0.90 && !double.IsNaN(authority) && !double.IsInfinity(authority)
                        && authority >= 0.90;

                    // Import is an add-only operation. The model's ID must never overwrite
                    // an existing knowledge row or resurrect a deleted record.
                    record.Id = Guid.NewGuid().ToString("N");
                    record.Title = title;
                    record.Answer = answer;
                    record.Type = autoPublish
                        ? KnowledgeEngineV2Semantics.NormalizeType(record.Type)
                        : "learning_candidate";
                    record.Intent = KnowledgeEngineV2Semantics.NormalizeIntent(
                        string.IsNullOrWhiteSpace(record.Intent) ? parsed.Intent : record.Intent);
                    record.Subject = string.IsNullOrWhiteSpace(record.Subject)
                        ? KnowledgeEngineV2Semantics.ResolveSubject(title, parsed.Entities)
                        : record.Subject;
                    record.Predicate = KnowledgeEngineV2Semantics.NormalizePredicate(
                        string.IsNullOrWhiteSpace(record.Predicate) ? parsed.Predicate : record.Predicate);
                    record.Entities = record.Entities == null || record.Entities.Count == 0
                        ? (parsed.Entities == null ? new List<string>() : parsed.Entities.ToList())
                        : record.Entities;
                    record.Aliases = autoPublish ? new List<string> { title }
                        : Clean((record.Aliases ?? new List<string>()).Concat(new[] { title }));
                    // A model-generated short answer is also answer content; do not
                    // allow it to bypass the source-text check for published records.
                    record.ShortAnswer = autoPublish ? answer : string.IsNullOrWhiteSpace(record.ShortAnswer)
                        ? (answer.Length > 160 ? answer.Substring(0, 160) : answer)
                        : record.ShortAnswer;
                    record.Conditions = Clean(record.Conditions);
                    record.Exclusions = Clean(record.Exclusions);
                    record.RequiredContext = Clean(record.RequiredContext);
                    record.ProductIds = Clean(record.ProductIds);
                    record.RiskLevel = risk ? "high" : "normal";
                    record.SourceType = autoPublish
                        ? "ai_auto_learned_human_evidence"
                        : "chat_history_unverified_candidate";
                    // Active historical facts use stable original message IDs, not the
                    // random scan tokens/import ID. Cloud sync compares SourceId when
                    // reconciling independently learned facts across devices.
                    record.SourceId = autoPublish
                        ? BuildVerifiedHistorySourceId(questionIds, answerIds, contextIds)
                        : JsonConvert.SerializeObject(new
                        {
                            import_id = importId,
                            evidence_type = "unverified_history_import",
                            source_evidence_ids = tokens,
                            question_message_ids = questionIds,
                            answer_message_ids = answerIds,
                            context_message_ids = contextIds
                        });
                    record.Authority = autoPublish ? Math.Max(record.Authority, 0.90) : Math.Min(record.Authority, 0.69);
                    record.Confidence = autoPublish ? Math.Max(record.Confidence, 0.90) : Math.Min(record.Confidence, 0.69);
                    record.Enabled = autoPublish;
                    record.Status = autoPublish ? "active" : "candidate";
                    record.CreatedAt = record.CreatedAt == DateTime.MinValue ? DateTime.Now : record.CreatedAt;
                    record.UpdatedAt = DateTime.Now;
                    record.LastVerifiedAt = autoPublish ? DateTime.Now : DateTime.MinValue;
                    record.ImportEvidenceTokens = new List<string>();

                    KnowledgeEngineV2Repository.Save(seller, record);
                    result.AddedItems.Add(record);
                    result.Added++;
                    if (autoPublish) result.Published++;
                    else result.Candidates++;
                }

                if (result.Added > 0) KnowledgeEngineV2Service.Warm(seller);
                AppendAudit(seller, importId, result);
                Log.Info(string.Format(
                    "Knowledge V2 history import complete shopRef={0} source={1} added={2} duplicate_skipped={3}",
                    MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey),
                    result.SourceItems, result.Added, result.DuplicateSkipped));
                return result;
            }
        }

        private static string BuildVerifiedHistorySourceId(
            IEnumerable<string> questionIds, IEnumerable<string> answerIds, IEnumerable<string> contextIds)
        {
            return JsonConvert.SerializeObject(new
            {
                evidence_type = "historical_human_reply",
                question_message_ids = questionIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                answer_message_ids = answerIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                context_message_ids = contextIds.OrderBy(x => x, StringComparer.Ordinal).ToArray()
            });
        }

        // Deliberately ignore whitespace only; do not erase negation, amounts,
        // punctuation, case-sensitive identifiers, or other factual differences.
        private static bool MatchesSourceText(string generated, string original)
        {
            if (string.IsNullOrWhiteSpace(generated) || string.IsNullOrWhiteSpace(original)) return false;
            return string.Equals(Regex.Replace(generated.Trim(), @"\s+", " "),
                Regex.Replace(original.Trim(), @"\s+", " "), StringComparison.Ordinal);
        }

        private static List<string> Clean(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>())
                .Select(x => (x ?? string.Empty).Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToList();
        }

        private static void AppendAudit(string seller, string importId, KnowledgeV2HistoryPromotionResult result)
        {
            try
            {
                string ignored;
                KnowledgeEngineV2GovernanceAuditService.TryAppendAction(
                    seller, "chat_history_import", "knowledge_import", importId, string.Empty,
                    "历史聊天整理", string.Empty,
                    string.Format("schema=knowledge_v2;source={0};added={1};published={2};candidates={3};duplicate_skipped={4}",
                        result.SourceItems, result.Added, result.Published, result.Candidates, result.DuplicateSkipped),
                    "历史聊天导入仅将原始真人问答文本匹配且带完整消息ID、通过置信度/风险门禁的知识自动发布；其余保留为V2候选。", "success", out ignored);
            }
            catch { }
        }
    }
}
