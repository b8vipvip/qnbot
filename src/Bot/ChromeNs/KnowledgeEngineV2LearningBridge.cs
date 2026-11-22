using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class KnowledgeEngineV2LearningBridge
    {
        private static int _initialized;
        private static readonly ConcurrentDictionary<string, byte> SynchronizingShops =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> PendingShopSynchronizations =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                KnowledgeLearningService.KnowledgeBaseChanged += OnLegacyKnowledgeChanged;
                Log.Info("Knowledge Engine V2学习桥已启动：候选/生产资格统一由V2权威策略根据来源决定。");
            }
            return new object();
        }

        private static void OnLegacyKnowledgeChanged(object sender, EventArgs e)
        {
            List<QN> qns;
            try
            {
                qns = QN.QNSet == null ? new List<QN>() : QN.QNSet.ToArray().ToList();
                var current = QN.CurQN;
                if (current != null && !qns.Any(x => ReferenceEquals(x, current)))
                    qns.Add(current);
            }
            catch { return; }

            var scheduled = new HashSet<string>(StringComparer.Ordinal);
            foreach (var qn in qns.Where(x => x != null && x.Seller != null
                && !string.IsNullOrWhiteSpace(x.Seller.Nick)))
            {
                ShopContext shop;
                try
                {
                    // Seller nick is display data. Stable TargetId identities may share it,
                    // so resolve each source QN independently before coalescing by ShopKey.
                    shop = ShopContextLocator.ResolveRuntimeBySeller(qn);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount(
                        "Knowledge V2店铺学习同步无法解析来源QN身份，已安全跳过: " + ex.Message,
                        20);
                    continue;
                }

                if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                    || !scheduled.Add(shop.ShopKey))
                    continue;

                QueueSynchronizeShop(shop, qn.Seller.Nick.Trim());
            }
        }

        private static void QueueSynchronizeShop(ShopContext shop, string seller)
        {
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return;
            if (!SynchronizingShops.TryAdd(shop.ShopKey, 0))
            {
                // Coalesce changes that arrive while a shop import is running. The worker
                // performs another pass after the current snapshot has been reconciled.
                PendingShopSynchronizations[shop.ShopKey] = 0;
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    byte ignored;
                    do
                    {
                        PendingShopSynchronizations.TryRemove(shop.ShopKey, out ignored);
                        ImportLearningChanges(shop, seller);
                    }
                    while (PendingShopSynchronizations.TryRemove(shop.ShopKey, out ignored));
                }
                finally
                {
                    byte ignored;
                    SynchronizingShops.TryRemove(shop.ShopKey, out ignored);
                    // Close the race where a change arrives after the worker's last
                    // pending check but before its running marker is removed.
                    if (PendingShopSynchronizations.TryRemove(shop.ShopKey, out ignored))
                        QueueSynchronizeShop(shop, seller);
                }
            });
        }

        internal static void SynchronizeSeller(string seller)
        {
            ImportLearningChanges(seller);
        }

        // Compatibility entry point for older callers that only have a seller nick.
        // Ambiguous nicknames intentionally fail closed; event-driven sync uses the QN ShopKey.
        private static void ImportLearningChanges(string seller)
        {
            try
            {
                var shop = ShopContextLocator.ResolveBySellerNick(seller);
                if (shop == null) return;
                ImportLearningChanges(shop, seller);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("同步V2知识来源失败（昵称兼容入口）: error=" + ex.Message, 20);
            }
        }

        private static void ImportLearningChanges(ShopContext shop, string seller)
        {
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return;
            var changed = 0;
            try
            {
                // Keep every repository operation, legacy read, policy lookup, write and
                // index warm inside one immutable ShopContext scope. This prevents a
                // same-nickname account from changing the ambient store between operations.
                using (ShopSettingsScope.Enter(shop))
                {
                    var legacy = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
                    var current = KnowledgeEngineV2Repository.LoadAll(seller);
                    var byId = current.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                        .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);

                    foreach (var entry in legacy.Where(KnowledgeV2AuthorityPolicy.ShouldImportLegacyEntry))
                    {
                        KnowledgeV2Record existing = null;
                        if (!string.IsNullOrWhiteSpace(entry.Id))
                            byId.TryGetValue(entry.Id, out existing);
                        if (existing != null
                            && Same(existing.Answer, entry.Answer)
                            && KnowledgeV2AuthorityPolicy.IsPersistedStateSynchronized(existing, entry))
                        {
                            continue;
                        }

                        KnowledgePolicyProfile profile = null;
                        try { profile = KnowledgePolicyProfileService.GetProfile(entry); }
                        catch { }

                        var record = KnowledgeEngineV2Semantics.FromLegacy(entry, profile);
                        if (record == null) continue;
                        record.Enabled = entry.Enabled;
                        KnowledgeV2AuthorityPolicy.ApplyImportedLegacyProvenance(record, entry);
                        if (existing != null
                            && !Same(existing.Answer, entry.Answer)
                            && !KnowledgeV2AuthorityPolicy.IsExplicitHumanConfirmationSource(record.SourceType))
                        {
                            record.Id = "candidate-" + KnowledgeAiService.ContentHash(
                                entry.Id ?? string.Empty, entry.Answer ?? string.Empty);
                            record.Title = "候选修正：" + (entry.Title ?? string.Empty);
                        }

                        KnowledgeEngineV2Repository.Save(seller, record);
                        if (!string.IsNullOrWhiteSpace(record.Id))
                            byId[record.Id] = record;
                        changed++;
                    }

                    if (changed > 0)
                        KnowledgeEngineV2Service.Warm(seller);
                }

                if (changed > 0)
                {
                    Log.Info("Knowledge Engine V2已同步知识来源权威状态: shopRef="
                        + MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey)
                        + ", changed=" + changed);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("同步V2知识来源失败: shopRef="
                    + MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey)
                    + ", error=" + ex.Message, 20);
            }
        }

        private static bool Same(string left, string right)
        {
            return KnowledgeEngineV2Semantics.TextSimilarity(left, right) >= 0.96;
        }
    }
}

namespace Bot.Knowledge
{
    /// <summary>
    /// Knowledge V2 provenance authority. Importers, UI and runtime retrieval may enrich or consume
    /// records, but they do not independently decide whether knowledge is candidate or production.
    /// Explicit human confirmation is authoritative provenance; AI metadata enrichment can never
    /// downgrade a human-confirmed answer when enrichment or JSON recovery fails.
    /// </summary>
    internal static class KnowledgeV2AuthorityPolicy
    {
        public static bool IsExplicitHumanConfirmationSource(string sourceType)
        {
            var source = (sourceType ?? string.Empty).Trim();
            return source.IndexOf("人工修改", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("人工确认", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("人工审核", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("人工接待复盘", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("manual_reviewed_direct", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("manual_edit", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("manual_confirmed", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("human_reviewed", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("human_evidence_auto_reviewed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsAutoPublishedHumanEvidenceSource(string sourceType)
        {
            var source = (sourceType ?? string.Empty).Trim();
            return source.IndexOf("ai_auto_learned_human_evidence", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("human_evidence_auto_learned", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsTrustedProductionSource(string sourceType)
        {
            return IsExplicitHumanConfirmationSource(sourceType)
                || IsAutoPublishedHumanEvidenceSource(sourceType);
        }

        public static bool ShouldImportLegacyEntry(Bot.ChromeNs.KnowledgeBaseEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Answer)) return false;
            if (IsTrustedProductionSource(entry.SourceType)) return true;
            var value = (entry.SourceType ?? string.Empty) + " " + (entry.Category ?? string.Empty);
            return value.IndexOf("学习", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("人工回复", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("manual", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("session", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsPersistedStateSynchronized(KnowledgeV2Record record, Bot.ChromeNs.KnowledgeBaseEntry entry)
        {
            if (record == null || entry == null) return false;
            var expectedSource = string.IsNullOrWhiteSpace(entry.SourceType) ? "legacy_learning" : entry.SourceType.Trim();
            if (!string.Equals((record.SourceType ?? string.Empty).Trim(), expectedSource, StringComparison.OrdinalIgnoreCase))
                return false;
            if (record.Enabled != entry.Enabled) return false;

            if (IsExplicitHumanConfirmationSource(expectedSource))
            {
                return string.Equals(record.Status, "active", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase)
                    && record.Authority >= 0.98
                    && record.Confidence >= 0.94;
            }
            if (IsAutoPublishedHumanEvidenceSource(expectedSource))
            {
                return string.Equals(record.Status, "active", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase)
                    && record.Authority >= 0.90
                    && record.Confidence >= 0.90;
            }

            return string.Equals(record.Status, "candidate", StringComparison.OrdinalIgnoreCase)
                && string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase);
        }

        public static void ApplyImportedLegacyProvenance(KnowledgeV2Record record, Bot.ChromeNs.KnowledgeBaseEntry entry)
        {
            if (record == null || entry == null) return;
            record.SourceType = string.IsNullOrWhiteSpace(entry.SourceType) ? "legacy_learning" : entry.SourceType;
            record.SourceId = entry.Id;
            if (IsExplicitHumanConfirmationSource(record.SourceType))
            {
                ApplyHumanConfirmed(record);
                return;
            }
            if (IsAutoPublishedHumanEvidenceSource(record.SourceType))
            {
                ApplyAutoPublished(record);
                return;
            }

            record.Type = "learning_candidate";
            record.Status = "candidate";
            record.Confidence = Math.Min(record.Confidence, 0.82);
        }

        public static KnowledgeV2Record NormalizeForRead(KnowledgeV2Record record)
        {
            if (record != null && IsExplicitHumanConfirmationSource(record.SourceType))
                ApplyHumanConfirmed(record);
            else if (record != null && IsAutoPublishedHumanEvidenceSource(record.SourceType))
                ApplyAutoPublished(record);
            return record;
        }

        public static bool IsCandidate(KnowledgeV2Record record)
        {
            if (record == null) return false;
            if (IsTrustedProductionSource(record.SourceType)) return false;
            return string.Equals(record.Status, "candidate", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsProductionApproved(KnowledgeV2Record record)
        {
            return record != null && record.Enabled && !IsCandidate(record);
        }

        public static void Promote(KnowledgeV2Record record)
        {
            if (record == null) return;
            record.Status = "active";
            if (string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase))
                record.Type = "business_fact";
            record.Confidence = Math.Max(record.Confidence, 0.82);
        }

        private static void ApplyAutoPublished(KnowledgeV2Record record)
        {
            if (record == null) return;
            record.Status = "active";
            if (string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(record.Type))
                record.Type = "business_fact";
            record.Authority = Math.Max(record.Authority, 0.90);
            record.Confidence = Math.Max(record.Confidence, 0.90);
            if (record.LastVerifiedAt == DateTime.MinValue) record.LastVerifiedAt = DateTime.Now;
        }

        private static void ApplyHumanConfirmed(KnowledgeV2Record record)
        {
            record.Status = "active";
            if (string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(record.Type))
            {
                record.Type = "business_fact";
            }
            record.Authority = Math.Max(record.Authority, 0.98);
            record.Confidence = Math.Max(record.Confidence, 0.94);
            if (record.LastVerifiedAt == DateTime.MinValue) record.LastVerifiedAt = DateTime.Now;
        }
    }
}

namespace Bot
{
    public partial class App
    {
        private readonly object _knowledgeEngineV2LearningBootstrap = ChromeNs.KnowledgeEngineV2LearningBridge.InitializeForApp();
    }
}
