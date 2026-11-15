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
        public int LegacyTransientRemoved { get; set; }
        public List<KnowledgeV2Record> AddedItems { get; private set; }

        public KnowledgeV2HistoryPromotionResult()
        {
            AddedItems = new List<KnowledgeV2Record>();
        }
    }

    /// <summary>
    /// Promotes the history scanner's temporary legacy FAQ output into the current Knowledge V2 schema.
    /// Only records produced by the current scan are promoted/removed; unrelated legacy knowledge is untouched.
    /// </summary>
    internal static class KnowledgeV2LegacyDeltaImportService
    {
        public static KnowledgeV2HistoryPromotionResult PromoteHistoryImport(
            string seller, KnowledgeImportResult importResult)
        {
            return PromoteHistoryImport(seller, string.Empty, importResult);
        }

        public static KnowledgeV2HistoryPromotionResult PromoteHistoryImport(
            string seller, string shopKey, KnowledgeImportResult importResult)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0)
                throw new InvalidOperationException("无法确定当前店铺，历史聊天结果不能写入 Knowledge V2。");
            var shop = ResolveShop(seller, shopKey);
            if (shop == null) throw new InvalidOperationException("无法确定当前店铺身份。");

            // The window's attached ShopKey is authoritative. Keep the complete
            // read/dedupe/write/warm/legacy-cleanup/audit operation in that scope.
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
                    if (string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrWhiteSpace(entry.Answer)) continue;
                    var hash = KnowledgeAiService.ContentHash(entry.Title, entry.Answer);
                    if (!existingHashes.Add(hash))
                    {
                        result.DuplicateSkipped++;
                        continue;
                    }

                    var record = KnowledgeEngineV2Semantics.FromLegacy(entry, null);
                    if (record == null || string.IsNullOrWhiteSpace(record.Title)
                        || string.IsNullOrWhiteSpace(record.Answer)) continue;
                    NormalizeCurrentV2(record, importId);
                    KnowledgeEngineV2Repository.Save(seller, record);
                    result.AddedItems.Add(record);
                    result.Added++;
                }

                if (result.Added > 0) KnowledgeEngineV2Service.Warm(seller);
                result.LegacyTransientRemoved = RemoveCurrentScanLegacyEntries(shop, sourceItems);
                AppendAudit(seller, importId, result);
                Log.Info(string.Format(
                    "KnowledgeV2 history promotion complete shopRef={0} source={1} added={2} dup={3} legacy_removed={4}",
                    MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey),
                    result.SourceItems, result.Added, result.DuplicateSkipped, result.LegacyTransientRemoved));
                return result;
            }
        }

        public static int ImportMissingHistoryKnowledge(string seller)
        {
            return ImportMissingHistoryKnowledge(seller, string.Empty);
        }

        public static int ImportMissingHistoryKnowledge(string seller, string shopKey)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return 0;
            var shop = ResolveShop(seller, shopKey);
            if (shop == null) throw new InvalidOperationException("无法确定当前店铺身份。");

            using (ShopSettingsScope.Enter(shop))
            {
                var legacy = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
                var candidates = legacy.Where(x => x != null
                    && string.Equals((x.SourceType ?? string.Empty).Trim(), "历史聊天扫描", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(x.Answer)).ToList();
                if (candidates.Count == 0) return 0;
                var promoted = PromoteHistoryImport(
                    seller, shop.ShopKey,
                    new KnowledgeImportResult { AddedItems = candidates, Added = candidates.Count });
                return promoted.Added;
            }
        }

        private static ShopContext ResolveShop(string seller, string shopKey)
        {
            if (!string.IsNullOrWhiteSpace(shopKey))
                return ShopContextLocator.ResolveRuntimeByShopKey(shopKey);

            var current = ShopSettingsScope.Current;
            if (current != null) return current;

            // Nickname-only fallback is retained for legacy callers, and remains
            // fail-closed when multiple registered shops share that nickname.
            return ShopContextLocator.ResolveBySellerNick(seller);
        }

        private static void NormalizeCurrentV2(KnowledgeV2Record record, string importId)
        {
            var text = (record.Title ?? string.Empty) + " " + (record.Answer ?? string.Empty);
            record.Type = KnowledgeEngineV2Semantics.NormalizeType(record.Type);
            record.Intent = KnowledgeEngineV2Semantics.NormalizeIntent(string.IsNullOrWhiteSpace(record.Intent)
                ? KnowledgeEngineV2Semantics.DetectIntent(text) : record.Intent);
            if (record.Entities == null || record.Entities.Count == 0)
                record.Entities = KnowledgeEngineV2Semantics.ExtractEntities(text);
            if (string.IsNullOrWhiteSpace(record.Subject))
                record.Subject = KnowledgeEngineV2Semantics.ResolveSubject(record.Title, record.Entities);
            record.Predicate = KnowledgeEngineV2Semantics.NormalizePredicate(string.IsNullOrWhiteSpace(record.Predicate)
                ? KnowledgeEngineV2Semantics.DetectPredicate(text) : record.Predicate);
            record.Entities = Clean(record.Entities);
            var aliases = record.Aliases ?? new List<string>();
            aliases.Insert(0, record.Title ?? string.Empty);
            record.Aliases = Clean(aliases);
            record.Conditions = Clean(record.Conditions);
            record.Exclusions = Clean(record.Exclusions);
            record.RequiredContext = Clean(record.RequiredContext);
            record.ProductIds = Clean(record.ProductIds);
            record.RiskLevel = KnowledgeEngineV2Semantics.IsHighRisk(text) ? "high" : "normal";
            record.SourceType = "chat_history_import";
            record.SourceId = importId;
            record.Authority = Math.Max(0.90, Math.Min(1.0, record.Authority));
            record.Confidence = Math.Max(0.82, Math.Min(1.0, record.Confidence));
            record.Enabled = true;
            record.Status = "active";
            var now = DateTime.Now;
            if (record.CreatedAt == default(DateTime)) record.CreatedAt = now;
            record.UpdatedAt = now;
            record.LastVerifiedAt = now;
        }

        private static List<string> Clean(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>()).Select(x => (x ?? string.Empty).Trim())
                .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        }

        private static int RemoveCurrentScanLegacyEntries(ShopContext shop, List<KnowledgeBaseEntry> sourceItems)
        {
            if (shop == null || sourceItems == null || sourceItems.Count == 0) return 0;
            var ids = new HashSet<string>(sourceItems.Select(x => (x.Id ?? string.Empty).Trim()).Where(x => x.Length > 0), StringComparer.Ordinal);
            var hashes = new HashSet<string>(sourceItems.Select(x => KnowledgeAiService.ContentHash(x.Title, x.Answer)), StringComparer.Ordinal);
            using (ShopSettingsScope.Enter(shop))
            {
                var list = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
                var before = list.Count;
                list = list.Where(x => x == null || !IsCurrentTransient(x, ids, hashes)).ToList();
                if (list.Count != before) BotFeatureStore.SaveKnowledgeBase(list);
                return before - list.Count;
            }
        }

        private static bool IsCurrentTransient(KnowledgeBaseEntry entry, HashSet<string> ids, HashSet<string> hashes)
        {
            if (entry == null) return false;
            if (!string.Equals((entry.SourceType ?? string.Empty).Trim(), "历史聊天扫描", StringComparison.Ordinal)) return false;
            var id = (entry.Id ?? string.Empty).Trim();
            if (id.Length > 0 && ids.Contains(id)) return true;
            return hashes.Contains(KnowledgeAiService.ContentHash(entry.Title, entry.Answer));
        }

        private static void AppendAudit(string seller, string importId, KnowledgeV2HistoryPromotionResult result)
        {
            try
            {
                string ignored;
                KnowledgeEngineV2GovernanceAuditService.TryAppendAction(
                    seller, "chat_history_import", "knowledge_import", importId, string.Empty, "历史聊天整理", string.Empty,
                    string.Format("schema=knowledge_v2;source={0};added={1};duplicate_skipped={2};legacy_transient_removed={3}",
                        result.SourceItems, result.Added, result.DuplicateSkipped, result.LegacyTransientRemoved),
                    "历史聊天整理结果已写入当前 Knowledge V2", "success", out ignored);
            }
            catch { }
        }
    }
}
