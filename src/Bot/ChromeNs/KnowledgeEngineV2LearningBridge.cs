using Bot.ChromeNs;
using System;

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
            // An explicit lifecycle state outranks historical provenance. A conflict
            // or operator demotion must never be undone by reading the old source.
            if (string.Equals(record.Status, "candidate", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase)) return true;
            if (IsTrustedProductionSource(record.SourceType)) return false;
            var source = record.SourceType ?? string.Empty;
            return source.IndexOf("candidate", StringComparison.OrdinalIgnoreCase) >= 0
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
                && record.Authority > 0 && record.Authority <= 1
                && record.Confidence > 0 && record.Confidence <= 1
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
            if (record == null || !record.Enabled || IsCandidate(record)
                || !string.Equals(record.Status, "active", StringComparison.OrdinalIgnoreCase))
                return;
            record.Status = "active";
            if (string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(record.Type))
                record.Type = "business_fact";
            // Provenance alone cannot raise an explicitly lowered score.
            // Only the actual verification/promotion write may do that.
            // Reading a legacy record must not invent a new verification time or change its fingerprint.
        }

        private static void ApplyHumanConfirmed(KnowledgeV2Record record)
        {
            if (record == null || !record.Enabled || IsCandidate(record)
                || !string.Equals(record.Status, "active", StringComparison.OrdinalIgnoreCase))
                return;
            record.Status = "active";
            if (string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(record.Type))
            {
                record.Type = "business_fact";
            }
            // Provenance alone cannot raise an explicitly lowered score.
            // Only the actual verification/promotion write may do that.
            // Reading a legacy record must not invent a new verification time or change its fingerprint.
        }
    }
}

