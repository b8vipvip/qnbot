using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
            return IsTrustedProductionSource(entry.SourceType) || IsUnapprovedLegacyLearningEntry(entry);
        }

        public static bool IsUnapprovedLegacyLearningEntry(Bot.ChromeNs.KnowledgeBaseEntry entry)
        {
            if (entry == null || IsTrustedProductionSource(entry.SourceType)) return false;
            // AiGenerated is authoritative provenance even when an older writer left
            // SourceType/Category blank. A generic manual source label alone is not
            // enough to demote a human-authored legacy FAQ.
            if (entry.AiGenerated) return true;
            var source = entry.SourceType ?? string.Empty;
            var value = source + " " + (entry.Category ?? string.Empty);
            return value.IndexOf("学习", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("人工回复", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("manual_ai_generated", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("session", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("AI生成", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsPersistedStateSynchronized(KnowledgeV2Record record, Bot.ChromeNs.KnowledgeBaseEntry entry)
        {
            if (record == null || entry == null) return false;
            var expectedSource = string.IsNullOrWhiteSpace(entry.SourceType) ? "legacy_learning" : entry.SourceType.Trim();
            if (!string.Equals((record.SourceType ?? string.Empty).Trim(), expectedSource, StringComparison.OrdinalIgnoreCase))
                return false;
            // Candidate and disabled V2 records are intentionally disabled in the
            // legacy mirror. Compare the effective mirror state, not the V2 editor toggle.
            var isCandidate = IsCandidate(record);
            var isDisabledStatus = string.Equals(record.Status, "disabled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.Status, "deleted", StringComparison.OrdinalIgnoreCase);
            var expectedLegacyEnabled = record.Enabled
                && !isCandidate
                && !isDisabledStatus
                && string.Equals(record.Status, "active", StringComparison.OrdinalIgnoreCase);
            if (entry.Enabled != expectedLegacyEnabled) return false;
            if (!record.Enabled || isDisabledStatus) return true;
            if (isCandidate)
                return string.Equals(record.Status, "candidate", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase);

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
            if (IsExplicitHumanConfirmationSource(record.SourceType)) return false;
            if (IsTrustedProductionSource(record.SourceType)) return false;
            var source = record.SourceType ?? string.Empty;
            return string.Equals(record.Status, "candidate", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase)
                || source.IndexOf("学习", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("人工回复", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("AI生成", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("manual_ai_generated", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("session", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsProductionApproved(KnowledgeV2Record record)
        {
            return record != null
                && record.Enabled
                && string.Equals(record.Status, "active", StringComparison.OrdinalIgnoreCase)
                && !IsCandidate(record);
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
            if (record == null || !record.Enabled
                || string.Equals(record.Status, "disabled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.Status, "deleted", StringComparison.OrdinalIgnoreCase))
                return;
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
            if (record == null || !record.Enabled
                || string.Equals(record.Status, "disabled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.Status, "deleted", StringComparison.OrdinalIgnoreCase))
                return;
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
