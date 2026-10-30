using Bot.Knowledge;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class KnowledgeEngineV2LearningBridge
    {
        private static int _initialized;

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
            QN[] qns;
            try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray(); }
            catch { return; }
            foreach (var seller in qns
                .Where(x => x != null && x.Seller != null && !string.IsNullOrWhiteSpace(x.Seller.Nick))
                .Select(x => x.Seller.Nick.Trim())
                .Distinct(StringComparer.Ordinal))
            {
                var captured = seller;
                Task.Run(() => SynchronizeSeller(captured));
            }
        }

        internal static void SynchronizeSeller(string seller)
        {
            ImportLearningChanges(seller);
        }

        private static void ImportLearningChanges(string seller)
        {
            try
            {
                var shop = ShopContextLocator.ResolveBySellerNick(seller);
                if (shop == null) return;
                List<KnowledgeBaseEntry> legacy;
                using (ShopSettingsScope.Enter(shop))
                    legacy = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();

                var current = KnowledgeEngineV2Repository.LoadAll(seller);
                var byId = current.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                    .ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
                var added = 0;
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
                    try
                    {
                        using (ShopSettingsScope.Enter(shop))
                            profile = KnowledgePolicyProfileService.GetProfile(entry);
                    }
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
                    added++;
                }
                if (added > 0)
                {
                    KnowledgeEngineV2Service.Warm(seller);
                    Log.Info("Knowledge Engine V2已同步知识来源权威状态: seller=" + seller + ", changed=" + added);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("同步V2知识来源失败: seller=" + seller + ", error=" + ex.Message, 20);
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
                || source.IndexOf("human_reviewed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool ShouldImportLegacyEntry(Bot.ChromeNs.KnowledgeBaseEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Answer)) return false;
            if (IsExplicitHumanConfirmationSource(entry.SourceType)) return true;
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

            record.Type = "learning_candidate";
            record.Status = "candidate";
            record.Confidence = Math.Min(record.Confidence, 0.82);
        }

        public static KnowledgeV2Record NormalizeForRead(KnowledgeV2Record record)
        {
            if (record != null && IsExplicitHumanConfirmationSource(record.SourceType))
                ApplyHumanConfirmed(record);
            return record;
        }

        public static bool IsCandidate(KnowledgeV2Record record)
        {
            if (record == null) return false;
            if (IsExplicitHumanConfirmationSource(record.SourceType)) return false;
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
