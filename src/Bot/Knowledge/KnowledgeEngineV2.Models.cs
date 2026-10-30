using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib;
using BotLib.Db.Sqlite;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.Knowledge
{
    internal static class KnowledgeEngineV2Constants
    {
        public const int SchemaVersion = 2;
        public const string ModeProduction = "production";
        public const string ModeShadow = "shadow";
        public const string SettingsEnabled = "knowledge.engine_v2.enabled";
        public const string SettingsMode = "knowledge.engine_v2.mode";
        public const string SettingsDirectThreshold = "knowledge.engine_v2.direct_threshold";
        public const string SettingsMinConfidence = "knowledge.engine_v2.min_confidence";
        public const string SettingsMigrationVersion = "knowledge.engine_v2.migration_version";
        public const double DefaultDirectThreshold = 0.82;
        public const double DefaultMinConfidence = 0.68;
    }

    internal sealed class KnowledgeV2RecordRow
    {
        [PrimaryKey]
        public string Id { get; set; }
        public string Type { get; set; }
        public string Title { get; set; }
        public string Intent { get; set; }
        public string Subject { get; set; }
        public string Predicate { get; set; }
        public string EntitiesJson { get; set; }
        public string AliasesJson { get; set; }
        public string Answer { get; set; }
        public string ShortAnswer { get; set; }
        public string ConditionsJson { get; set; }
        public string ExclusionsJson { get; set; }
        public string RequiredContextJson { get; set; }
        public string ProductIdsJson { get; set; }
        public string RiskLevel { get; set; }
        public string SourceType { get; set; }
        public string SourceId { get; set; }
        public double Authority { get; set; }
        public double Confidence { get; set; }
        public int UseCount { get; set; }
        public int AcceptedCount { get; set; }
        public int CorrectionCount { get; set; }
        public int WithdrawCount { get; set; }
        public bool Enabled { get; set; }
        public string Status { get; set; }
        public long CreatedAtTicks { get; set; }
        public long UpdatedAtTicks { get; set; }
        public long LastVerifiedAtTicks { get; set; }
    }

    // Immutable operator corrections are kept in the shop-scoped V2 database,
    // not in a fuzzy Q/A cache. The full previous snapshot permits safe rollback.
    internal sealed class KnowledgeV2ManualEditRevisionRow
    {
        [PrimaryKey]
        public string Id { get; set; }
        public string KnowledgeId { get; set; }
        public string MessageRecordId { get; set; }
        public string Action { get; set; }
        public string PreviousJson { get; set; }
        public string CurrentJson { get; set; }
        public long CreatedAtTicks { get; set; }
    }

    internal sealed class KnowledgeV2MetaRow
    {
        [PrimaryKey]
        public string Key { get; set; }
        public string Value { get; set; }
    }

    internal sealed class KnowledgeV2Record
    {
        private double _confidence;

        public string Id { get; set; }
        public string Type { get; set; }
        public string Title { get; set; }
        public string Intent { get; set; }
        public string Subject { get; set; }
        public string Predicate { get; set; }
        public List<string> Entities { get; set; }
        public List<string> Aliases { get; set; }
        public string Answer { get; set; }
        public string ShortAnswer { get; set; }
        public List<string> Conditions { get; set; }
        public List<string> Exclusions { get; set; }
        public List<string> RequiredContext { get; set; }
        public List<string> ProductIds { get; set; }
        public string RiskLevel { get; set; }
        public string SourceType { get; set; }
        public string SourceId { get; set; }
        public double Authority { get; set; }
        public double Confidence
        {
            get { return KnowledgeV2ConfidencePolicy.Resolve(this, _confidence); }
            set { _confidence = value; }
        }
        public int UseCount { get; set; }
        public int AcceptedCount { get; set; }
        public int CorrectionCount { get; set; }
        public int WithdrawCount { get; set; }
        public bool Enabled { get; set; }
        public string Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime LastVerifiedAt { get; set; }

        public KnowledgeV2Record()
        {
            Entities = new List<string>();
            Aliases = new List<string>();
            Conditions = new List<string>();
            Exclusions = new List<string>();
            RequiredContext = new List<string>();
            ProductIds = new List<string>();
        }

        public string ConfidenceText
        {
            get { return (Confidence * 100).ToString("0") + "%"; }
        }

        public string UpdatedAtText
        {
            get { return UpdatedAt == DateTime.MinValue ? "-" : UpdatedAt.ToString("yyyy-MM-dd HH:mm"); }
        }
    }

    internal static class KnowledgeV2ConfidencePolicy
    {
        // Historical V2 migration assigned every profile-less record exactly
        // 0.80 * 0.55 + 0.75 * 0.45 = 0.7775. Treat only that exact legacy
        // sentinel (and missing confidence) as uncalibrated; preserve deliberate
        // operator/feedback confidence values unchanged.
        private const double LegacyUniformSentinel = 0.7775;

        public static double Resolve(KnowledgeV2Record record, double stored)
        {
            if (stored > 0 && Math.Abs(stored - LegacyUniformSentinel) > 0.000001)
                return Clamp(stored);
            return Estimate(record);
        }

        public static double Estimate(KnowledgeV2Record record)
        {
            if (record == null) return 0.72;
            var source = (record.SourceType ?? string.Empty).Trim().ToLowerInvariant();
            double value;
            if (source.Contains("人工") || source.Contains("manual")) value = 0.92;
            else if (source.Contains("fixed") || source.Contains("固定")) value = 0.90;
            else if (source.Contains("导入") || source.Contains("import")) value = 0.84;
            else if (source.Contains("学习") || source.Contains("candidate")) value = 0.74;
            else if (source.Contains("legacy") || source.Length == 0) value = 0.76;
            else value = 0.80;

            var title = (record.Title ?? string.Empty).Trim();
            var answer = (record.Answer ?? string.Empty).Trim();
            if (title.Length >= 4 && title.Length <= 80) value += 0.012;
            if (answer.Length >= 20 && answer.Length <= 600) value += 0.018;
            else if (answer.Length > 600) value += 0.008;
            if (!string.Equals(record.Intent, "general", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(record.Intent)) value += 0.022;
            if (!string.Equals(record.Predicate, "general", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(record.Predicate)) value += 0.032;
            if (!string.IsNullOrWhiteSpace(record.Subject)) value += 0.012;
            if (record.Entities != null && record.Entities.Count > 0) value += 0.018;
            if (record.Aliases != null && record.Aliases.Count >= 2) value += 0.012;
            if (record.ProductIds != null && record.ProductIds.Count > 0) value += 0.025;
            if (record.Conditions != null && record.Conditions.Count > 0) value += 0.008;
            if (record.RequiredContext != null && record.RequiredContext.Count > 0) value += 0.008;

            value += Math.Min(0.05, Math.Max(0, record.AcceptedCount) * 0.008);
            value -= Math.Min(0.12, Math.Max(0, record.CorrectionCount) * 0.025);
            value -= Math.Min(0.08, Math.Max(0, record.WithdrawCount) * 0.020);
            return Math.Max(0.55, Math.Min(0.97, value));
        }

        private static double Clamp(double value)
        {
            return Math.Max(0, Math.Min(1, value));
        }
    }

    internal sealed class KnowledgeV2Query
    {
        public string Original { get; set; }
        public string Normalized { get; set; }
        public string Intent { get; set; }
        public string Subject { get; set; }
        public string Predicate { get; set; }
        public List<string> Entities { get; set; }
        public bool ContextDependent { get; set; }
        public string WorkingMemoryReason { get; set; }

        public KnowledgeV2Query()
        {
            Entities = new List<string>();
        }
    }

    internal sealed class KnowledgeV2Match
    {
        public KnowledgeV2Record Record { get; set; }
        public double Score { get; set; }
        public double AliasScore { get; set; }
        public double EntityScore { get; set; }
        public double PredicateScore { get; set; }
        public double IntentScore { get; set; }
        public double ConfidenceScore { get; set; }
        public string Reason { get; set; }
    }

    internal sealed class KnowledgeV2Decision
    {
        public bool Enabled { get; set; }
        public string Mode { get; set; }
        public bool CanDirectReply { get; set; }
        public bool HasConflict { get; set; }
        public string Reason { get; set; }
        public string Answer { get; set; }
        public KnowledgeV2Query Query { get; set; }
        public List<KnowledgeV2Match> Matches { get; set; }
        public int CandidateCount { get; set; }
        public long ParseMs { get; set; }
        public long RecallMs { get; set; }
        public long RankMs { get; set; }
        public long DecisionMs { get; set; }
        public long TotalMs { get; set; }

        public KnowledgeV2Decision()
        {
            Matches = new List<KnowledgeV2Match>();
            Reason = string.Empty;
            Answer = string.Empty;
        }
    }

    internal sealed class KnowledgeV2Conflict
    {
        public string FactKey { get; set; }
        public string Subject { get; set; }
        public string Predicate { get; set; }
        public List<KnowledgeV2Record> Records { get; set; }

        public KnowledgeV2Conflict()
        {
            Records = new List<KnowledgeV2Record>();
        }
    }

    internal sealed class KnowledgeV2Stats
    {
        public int Total { get; set; }
        public int BusinessFacts { get; set; }
        public int Procedures { get; set; }
        public int SafetyRules { get; set; }
        public int LearningCandidates { get; set; }
        public int ProductBound { get; set; }
        public int Conflicts { get; set; }
        public DateTime SnapshotBuiltAt { get; set; }
        public string DatabasePath { get; set; }
    }

    internal sealed class KnowledgeV2Settings
    {
        public bool Enabled { get; set; }
        public string Mode { get; set; }
        public double DirectThreshold { get; set; }
        public double MinConfidence { get; set; }
    }

    internal sealed class KnowledgeV2WorkingMemory
    {
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Subject { get; set; }
        public string Predicate { get; set; }
        public string Intent { get; set; }
        public List<string> Entities { get; set; }
        public DateTime UpdatedAt { get; set; }

        public KnowledgeV2WorkingMemory()
        {
            Entities = new List<string>();
        }
    }
}
