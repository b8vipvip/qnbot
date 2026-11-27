using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeV2HistoryPromotionResult
    {
        public int SourceItems { get; set; }
        public int Added { get; set; }
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
                var sourceItems = (importResult.AddedItems ?? new List<KnowledgeBaseEntry>())
                    .Where(x => x != null).ToList();
                var result = new KnowledgeV2HistoryPromotionResult { SourceItems = sourceItems.Count };
                var existingHashes = new HashSet<string>(
                    KnowledgeEngineV2Repository.LoadAll(seller).Where(x => x != null)
                        .Select(x => KnowledgeAiService.ContentHash(x.Title, x.Answer)),
                    StringComparer.Ordinal);
                var importId = "chat-history-" + DateTime.Now.ToString("yyyyMMddHHmmss")
                    + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

                foreach (var entry in sourceItems)
                {
                    var title = (entry.Title ?? string.Empty).Trim();
                    var answer = (entry.Answer ?? string.Empty).Trim();
                    if (title.Length < 4 || answer.Length < 2) continue;
                    var hash = KnowledgeAiService.ContentHash(title, answer);
                    if (!existingHashes.Add(hash))
                    {
                        result.DuplicateSkipped++;
                        continue;
                    }

                    var parsed = KnowledgeEngineV2Semantics.Parse(title, null);
                    var text = title + " " + answer;
                    var risk = KnowledgeEngineV2Semantics.IsHighRisk(text);
                    var aliases = (entry.Keywords ?? string.Empty)
                        .Split(new[] { ',', '，', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => (x ?? string.Empty).Trim())
                        .Where(x => x.Length >= 2 && x.Length <= 80)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(20)
                        .ToList();
                    aliases.Insert(0, title);

                    var record = new KnowledgeV2Record
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Type = KnowledgeEngineV2Semantics.NormalizeType(entry.Category),
                        Title = title,
                        Intent = KnowledgeEngineV2Semantics.NormalizeIntent(parsed.Intent),
                        Subject = string.IsNullOrWhiteSpace(parsed.Subject)
                            ? KnowledgeEngineV2Semantics.ResolveSubject(title, parsed.Entities)
                            : parsed.Subject,
                        Predicate = KnowledgeEngineV2Semantics.NormalizePredicate(parsed.Predicate),
                        Entities = parsed.Entities == null ? new List<string>() : parsed.Entities.ToList(),
                        Aliases = Clean(aliases),
                        Answer = answer,
                        ShortAnswer = answer.Length > 160 ? answer.Substring(0, 160) : answer,
                        Conditions = new List<string>(),
                        Exclusions = new List<string>(),
                        RequiredContext = new List<string>(),
                        ProductIds = new List<string>(),
                        RiskLevel = risk ? "high" : "normal",
                        SourceType = "chat_history_import",
                        SourceId = importId,
                        Authority = risk ? 0.55 : 0.90,
                        Confidence = risk ? 0.55 : 0.82,
                        Enabled = !risk,
                        Status = risk ? "candidate" : "active",
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now,
                        LastVerifiedAt = DateTime.Now
                    };

                    KnowledgeEngineV2Repository.Save(seller, record);
                    result.AddedItems.Add(record);
                    result.Added++;
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
                    string.Format("schema=knowledge_v2;source={0};added={1};duplicate_skipped={2}",
                        result.SourceItems, result.Added, result.DuplicateSkipped),
                    "历史聊天整理结果已写入唯一 Knowledge V2 仓库", "success", out ignored);
            }
            catch { }
        }
    }
}
