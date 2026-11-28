using Bot.ShopScope;
using Bot.ChromeNs;
using System.Threading;
using BotLib;
using BotLib.Db.Sqlite;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeV2RevisionCandidateRow
    {
        [PrimaryKey]
        public string Id { get; set; }
        public string Seller { get; set; }
        public string KnowledgeId { get; set; }
        public string KnowledgeTitle { get; set; }
        public string RiskLevel { get; set; }
        public string OriginalAnswer { get; set; }
        public string OriginalFingerprint { get; set; }
        public string ProposedAnswer { get; set; }
        public string EvidenceJson { get; set; }
        public int EvidenceCount { get; set; }
        public int DistinctBuyerCount { get; set; }
        public double ClusterScore { get; set; }
        public string Status { get; set; }
        public string ResolutionNote { get; set; }
        public long CreatedAtTicks { get; set; }
        public long UpdatedAtTicks { get; set; }
        public long AppliedAtTicks { get; set; }
    }

    internal sealed class KnowledgeV2CandidateMutationReceiptRow
    {
        [PrimaryKey] public string Key { get; set; }
        public string CandidateId { get; set; }
        public string Action { get; set; }
    }

    internal sealed class KnowledgeV2RevisionEvidence
    {
        public string Buyer { get; set; }
        public string Reply { get; set; }
        public long CreatedAtTicks { get; set; }

        public string CreatedAtText
        {
            get
            {
                try { return CreatedAtTicks <= 0 ? "-" : new DateTime(CreatedAtTicks).ToString("MM-dd HH:mm"); }
                catch { return "-"; }
            }
        }
    }

    internal sealed class KnowledgeV2RevisionCandidate
    {
        public string Id { get; set; }
        public string KnowledgeId { get; set; }
        public string KnowledgeTitle { get; set; }
        public string RiskLevel { get; set; }
        public string OriginalAnswer { get; set; }
        public string ProposedAnswer { get; set; }
        public List<KnowledgeV2RevisionEvidence> Evidence { get; set; }
        public int EvidenceCount { get; set; }
        public int DistinctBuyerCount { get; set; }
        public double ClusterScore { get; set; }
        public string Status { get; set; }
        public string ResolutionNote { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime AppliedAt { get; set; }

        public KnowledgeV2RevisionCandidate()
        {
            Evidence = new List<KnowledgeV2RevisionEvidence>();
        }

        public string ClusterScoreText { get { return (ClusterScore * 100).ToString("0") + "%"; } }
        public string CreatedAtText { get { return CreatedAt == DateTime.MinValue ? "-" : CreatedAt.ToString("MM-dd HH:mm"); } }
        public string StatusText
        {
            get
            {
                switch ((Status ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "pending": return "待复核";
                    case "applied": return "已应用";
                    case "rejected": return "已驳回";
                    case "stale": return "已过期";
                    default: return Status ?? string.Empty;
                }
            }
        }
    }

    internal sealed class KnowledgeV2RevisionGenerationResult
    {
        public int ScannedKnowledge { get; set; }
        public int CorrectionEvents { get; set; }
        public int Generated { get; set; }
        public int SkippedInsufficientEvidence { get; set; }
        public int SkippedUnchanged { get; set; }
        public int ExistingPending { get; set; }
    }

    internal static class KnowledgeEngineV2RevisionService
    {
        private sealed class StoreState
        {
            public readonly object Sync = new object();
            public SQLiteHelper Db;
            public string Path;
        }

        private sealed class CorrectionSample
        {
            public KnowledgeV2FeedbackEventRow Event;
            public string Text;
        }

        private sealed class CorrectionCluster
        {
            public List<CorrectionSample> Samples = new List<CorrectionSample>();
            public string Representative;
            public double AverageSimilarity;
        }

        private static readonly IShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ConcurrentDictionary<string, StoreState> Stores =
            new ConcurrentDictionary<string, StoreState>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, object> OperationLocks =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        public static KnowledgeV2RevisionGenerationResult GenerateCandidates(string seller, CancellationToken token = default(CancellationToken))
        {
            var shop = ResolveShopRequired(seller);
            using (ShopSettingsScope.Enter(shop))
            lock (OperationLocks.GetOrAdd(shop.ShopKey, _ => new object()))
                return GenerateCandidatesCore(seller, token);
        }

        private static KnowledgeV2RevisionGenerationResult GenerateCandidatesCore(string seller, CancellationToken token)
        {
            seller = Clean(seller);
            if (seller.Length == 0) throw new InvalidOperationException("无法识别当前店铺客服账号。");

            RecoverCommittedCandidates(seller);
            var result = new KnowledgeV2RevisionGenerationResult();
            var records = KnowledgeEngineV2Repository.LoadAll(seller)
                .Where(KnowledgeV2AuthorityPolicy.IsProductionApproved)
                .ToDictionary(x => x.Id ?? string.Empty, x => x, StringComparer.Ordinal);
            var quality = KnowledgeEngineV2FeedbackService.GetQualityItems(seller)
                .Where(x => x != null && (x.HealthStatus == "低质量" || x.HealthStatus == "观察") && x.CorrectionCount > 0)
                .OrderByDescending(x => x.CorrectionCount)
                .ThenBy(x => x.QualityScore)
                .ToList();

            foreach (var item in quality)
            {
                token.ThrowIfCancellationRequested();
                result.ScannedKnowledge++;
                KnowledgeV2Record record;
                if (!records.TryGetValue(item.KnowledgeId ?? string.Empty, out record) || record == null) continue;

                MarkStalePendingIfSourceChanged(seller, record);
                var events = KnowledgeEngineV2FeedbackService.GetRecentEvents(seller, record.Id, 100)
                    .Where(x => x != null && string.Equals(x.EventType, "correction", StringComparison.OrdinalIgnoreCase))
                    .Where(x => SafeDate(x.CreatedAtTicks) >= DateTime.Now.AddDays(-120))
                    .Where(x => string.Equals(Clean(x.Answer), Clean(record.Answer), StringComparison.Ordinal)
                        && SafeDate(x.CreatedAtTicks) >= record.LastVerifiedAt)
                    .ToList();
                result.CorrectionEvents += events.Count;

                var samples = events
                    .Select(x => new CorrectionSample { Event = x, Text = ExtractManualReply(x.Evidence) })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Text) && !string.IsNullOrWhiteSpace(x.Event.Buyer))
                    .GroupBy(x => Clean(x.Event.Buyer).ToLowerInvariant() + "|" + NormalizeComparable(x.Text), StringComparer.Ordinal)
                    .Select(x => x.OrderByDescending(y => y.Event.CreatedAtTicks).First())
                    .Take(60)
                    .ToList();
                if (samples.Count < 2)
                {
                    result.SkippedInsufficientEvidence++;
                    continue;
                }

                var cluster = BuildClusters(samples)
                    .OrderByDescending(x => DistinctBuyerCount(x))
                    .ThenByDescending(x => x.Samples.Count)
                    .ThenByDescending(x => x.AverageSimilarity)
                    .FirstOrDefault();
                if (cluster == null)
                {
                    result.SkippedInsufficientEvidence++;
                    continue;
                }

                var distinctBuyers = DistinctBuyerCount(cluster);
                var highRisk = IsHighRisk(record);
                var minEvidence = highRisk ? 3 : 2;
                var minBuyers = highRisk ? 3 : 2;
                var minSimilarity = highRisk ? 0.82 : 0.74;
                var dominance = distinctBuyers / (double)Math.Max(1, samples.Select(x => Clean(x.Event.Buyer))
                    .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                if (cluster.Samples.Count < minEvidence || distinctBuyers < minBuyers
                    || cluster.AverageSimilarity < minSimilarity || dominance <= 0.50)
                {
                    result.SkippedInsufficientEvidence++;
                    continue;
                }

                var proposal = CleanReply(cluster.Representative);
                if (proposal.Length < 2 || proposal.Length > 600)
                {
                    result.SkippedInsufficientEvidence++;
                    continue;
                }
                var similarityToCurrent = KnowledgeEngineV2Semantics.TextSimilarity(record.Answer ?? string.Empty, proposal);
                if (similarityToCurrent >= 0.88 && KnowledgeEngineV2Service.AnswerFactsCompatible(record.Answer, proposal))
                {
                    result.SkippedUnchanged++;
                    continue;
                }

                var existing = FindPendingByProposal(seller, record.Id, proposal);
                if (existing != null)
                {
                    result.ExistingPending++;
                    continue;
                }

                var now = DateTime.Now;
                var evidence = cluster.Samples
                    .OrderByDescending(x => x.Event.CreatedAtTicks)
                    .Take(12)
                    .Select(x => new KnowledgeV2RevisionEvidence
                    {
                        Buyer = x.Event.Buyer ?? string.Empty,
                        Reply = x.Text,
                        CreatedAtTicks = x.Event.CreatedAtTicks
                    })
                    .ToList();
                var score = ComputeClusterScore(cluster, distinctBuyers, dominance);
                SaveRow(seller, new KnowledgeV2RevisionCandidateRow
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Seller = seller,
                    KnowledgeId = record.Id,
                    KnowledgeTitle = record.Title ?? string.Empty,
                    RiskLevel = string.IsNullOrWhiteSpace(record.RiskLevel) ? "normal" : record.RiskLevel,
                    OriginalAnswer = record.Answer ?? string.Empty,
                    OriginalFingerprint = KnowledgeEngineV2Repository.EditorRecordFingerprint(record),
                    ProposedAnswer = proposal,
                    EvidenceJson = JsonConvert.SerializeObject(evidence),
                    EvidenceCount = cluster.Samples.Count,
                    DistinctBuyerCount = distinctBuyers,
                    ClusterScore = score,
                    Status = "pending",
                    ResolutionNote = "由真实人工纠正聚类生成；未自动修改知识。",
                    CreatedAtTicks = now.Ticks,
                    UpdatedAtTicks = now.Ticks,
                    AppliedAtTicks = 0
                });
                result.Generated++;
            }

            string auditError;
            KnowledgeEngineV2GovernanceAuditService.TryAppendAction(seller,
                "generate_revision_candidates", "governance", string.Empty, string.Empty, "Knowledge V2修订候选",
                "correction_events=" + result.CorrectionEvents,
                "generated=" + result.Generated + ";existing_pending=" + result.ExistingPending,
                "人工触发纠正证据聚类：扫描知识=" + result.ScannedKnowledge + "，新增候选=" + result.Generated
                    + "，证据不足=" + result.SkippedInsufficientEvidence + "，答案未变化=" + result.SkippedUnchanged + "。",
                "success", out auditError);
            return result;
        }

        public static List<KnowledgeV2RevisionCandidate> GetCandidates(string seller, string status, int maxCount)
        {
            var shop = ResolveShopRequired(seller);
            List<KnowledgeV2RevisionCandidateRow> rows;
            using (ShopSettingsScope.Enter(shop))
            lock (OperationLocks.GetOrAdd(shop.ShopKey, _ => new object()))
            {
                RecoverCommittedCandidates(seller);
                rows = ReadRows(seller);
            }
            status = Clean(status).ToLowerInvariant();
            IEnumerable<KnowledgeV2RevisionCandidateRow> query = rows.Where(x => x != null);
            if (status.Length > 0 && status != "all")
                query = query.Where(x => string.Equals(x.Status, status, StringComparison.OrdinalIgnoreCase));
            return query
                .OrderBy(x => string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(x => x.ClusterScore)
                .ThenByDescending(x => x.CreatedAtTicks)
                .Take(Math.Max(1, Math.Min(500, maxCount <= 0 ? 200 : maxCount)))
                .Select(ToView)
                .ToList();
        }

        public static bool ApplyCandidate(string seller, string candidateId, out string error, string operationReceipt = null)
        {
            var shop = ResolveShopRequired(seller);
            using (ShopSettingsScope.Enter(shop))
            lock (OperationLocks.GetOrAdd(shop.ShopKey, _ => new object()))
                return ApplyCandidateCore(seller, candidateId, out error, operationReceipt);
        }

        private static bool ApplyCandidateCore(string seller, string candidateId, out string error, string operationReceipt)
        {
            error = string.Empty;
            seller = Clean(seller);
            RecoverCommittedCandidates(seller);
            var row = FindRow(seller, candidateId);
            if (row == null)
            {
                error = "修订候选不存在。";
                return false;
            }
            if (string.Equals(row.Status, "applied", StringComparison.OrdinalIgnoreCase))
                return true; // idempotent retry after a committed revision or lost response
            if (!string.Equals(row.Status, "pending", StringComparison.OrdinalIgnoreCase))
            {
                error = "该候选已处理，当前状态：" + row.Status;
                return false;
            }

            var record = KnowledgeEngineV2Repository.LoadAll(seller)
                .FirstOrDefault(x => x != null && string.Equals(x.Id, row.KnowledgeId, StringComparison.Ordinal));
            if (!KnowledgeV2AuthorityPolicy.IsProductionApproved(record))
            {
                MarkStatus(seller, row, "stale", "原知识已不存在或已停用，候选自动过期。", false);
                error = "原知识已不存在，候选已标记过期。";
                return false;
            }
            var currentFingerprint = KnowledgeEngineV2Repository.EditorRecordFingerprint(record);
            if (string.IsNullOrEmpty(row.OriginalFingerprint)
                || !string.Equals(currentFingerprint, row.OriginalFingerprint, StringComparison.Ordinal))
            {
                MarkStatus(seller, row, "stale", "原知识在候选生成后已被修改，拒绝覆盖。", false);
                error = "原知识在候选生成后已变化，为避免覆盖人工修改，候选已标记过期。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(row.ProposedAnswer))
            {
                error = "候选答案为空，不能应用。";
                return false;
            }

            var beforeState = KnowledgeEngineV2GovernanceAuditService.DescribeRecord(record);
            record.Answer = row.ProposedAnswer.Trim();
            // A stale short answer must not keep serving the exact fact being corrected.
            record.ShortAnswer = record.Answer;
            record.LastVerifiedAt = DateTime.Now;
            if (!KnowledgeEngineV2Repository.TrySaveEditorRecord(seller, record, currentFingerprint, "revision-candidate:" + row.Id, operationReceipt))
            {
                MarkStatus(seller, row, "stale", "应用期间原知识被修改，未覆盖。", false);
                error = "知识已发生并发变化，修订未应用。";
                return false;
            }
            MarkStatus(seller, row, "applied", "人工复核通过并应用；原答案已保留在修订审计记录中。", true);
            string auditError;
            KnowledgeEngineV2GovernanceAuditService.TryAppendAction(seller,
                "apply_revision", "revision", row.KnowledgeId, row.Id, row.KnowledgeTitle,
                beforeState, KnowledgeEngineV2GovernanceAuditService.DescribeRecord(record),
                "人工应用修订候选；证据=" + row.EvidenceCount + "，不同买家=" + row.DistinctBuyerCount + "。",
                "success", out auditError);
            KnowledgeEngineV2Service.Warm(seller);
            Log.Info("Knowledge V2修订候选已应用: seller=" + seller + ", knowledgeId=" + row.KnowledgeId
                + ", evidence=" + row.EvidenceCount + ", buyers=" + row.DistinctBuyerCount);
            return true;
        }

        internal static bool HasCommittedCandidateOutcome(string seller, string candidateId, string action, string reason, string operationReceipt)
        {
            if (string.IsNullOrWhiteSpace(candidateId) || string.IsNullOrWhiteSpace(operationReceipt)) return false;
            var shop = ResolveShopRequired(seller);
            using (ShopSettingsScope.Enter(shop))
            lock (OperationLocks.GetOrAdd(shop.ShopKey, _ => new object()))
            {
                // A different command or an earlier local action is not proof of
                // this claim. Applying uses the knowledge DB's atomic receipt.
                if (action == "revision_apply")
                    return KnowledgeEngineV2Repository.HasRemoteMutationReceipt(seller, operationReceipt, action);
                if (action != "revision_reject") return false;
                var state = GetState(shop);
                lock (state.Sync)
                {
                    var receipt = state.Db.ReadOneRecord<KnowledgeV2CandidateMutationReceiptRow>(x => x.Key == operationReceipt);
                    return receipt != null && receipt.CandidateId == candidateId && receipt.Action == action;
                }
            }
        }

        public static bool RejectCandidate(string seller, string candidateId, string reason, out string error, string operationReceipt = null)
        {
            var shop = ResolveShopRequired(seller);
            using (ShopSettingsScope.Enter(shop))
            lock (OperationLocks.GetOrAdd(shop.ShopKey, _ => new object()))
                return RejectCandidateCore(seller, candidateId, reason, out error, operationReceipt);
        }

        private static bool RejectCandidateCore(string seller, string candidateId, string reason, out string error, string operationReceipt)
        {
            error = string.Empty;
            seller = Clean(seller);
            RecoverCommittedCandidates(seller);
            var row = FindRow(seller, candidateId);
            if (row == null)
            {
                error = "修订候选不存在。";
                return false;
            }
            if (!string.Equals(row.Status, "pending", StringComparison.OrdinalIgnoreCase))
            {
                error = "该候选已处理，当前状态：" + row.Status;
                return false;
            }
            var beforeState = "status=" + Clean(row.Status) + ";updated_at_ticks=" + row.UpdatedAtTicks;
            var note = string.IsNullOrWhiteSpace(reason) ? "人工复核驳回。" : reason.Trim();
            if (string.IsNullOrEmpty(operationReceipt))
                MarkStatus(seller, row, "rejected", note, false);
            else
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(operationReceipt, @"^[0-9a-f]{64}$"))
                    throw new InvalidOperationException("远程修订凭证无效");
                row.Status = "rejected";
                row.ResolutionNote = note;
                row.UpdatedAtTicks = DateTime.Now.Ticks;
                var state = GetState(ResolveShopRequired(seller));
                lock (state.Sync)
                    state.Db.SaveRecordsInTransaction(new List<object>
                    {
                        row, new KnowledgeV2CandidateMutationReceiptRow
                        { Key = operationReceipt, CandidateId = row.Id, Action = "revision_reject" }
                    });
            }
            string auditError;
            KnowledgeEngineV2GovernanceAuditService.TryAppendAction(seller,
                "reject_revision", "revision", row.KnowledgeId, row.Id, row.KnowledgeTitle,
                beforeState, "status=" + Clean(row.Status) + ";updated_at_ticks=" + row.UpdatedAtTicks,
                note, "success", out auditError);
            return true;
        }

        public static string GetDatabasePath(string seller)
        {
            return GetState(ResolveShopRequired(seller)).Path;
        }

        private static List<CorrectionCluster> BuildClusters(List<CorrectionSample> samples)
        {
            var clusters = new List<CorrectionCluster>();
            foreach (var sample in samples.OrderByDescending(x => x.Event.CreatedAtTicks))
            {
                CorrectionCluster best = null;
                var bestSimilarity = 0.0;
                foreach (var cluster in clusters)
                {
                    // Single-link similarity allows opposite or numeric facts to
                    // drift together via an intermediate representative. Require
                    // factual compatibility with every existing cluster member.
                    if (cluster.Samples.Any(x => !KnowledgeEngineV2Service.AnswerFactsCompatible(x.Text, sample.Text))) continue;
                    var similarity = KnowledgeEngineV2Semantics.TextSimilarity(cluster.Representative ?? string.Empty, sample.Text);
                    if (similarity > bestSimilarity)
                    {
                        best = cluster;
                        bestSimilarity = similarity;
                    }
                }
                if (best != null && bestSimilarity >= 0.66)
                {
                    best.Samples.Add(sample);
                    RecomputeCluster(best);
                }
                else
                {
                    var cluster = new CorrectionCluster();
                    cluster.Samples.Add(sample);
                    cluster.Representative = sample.Text;
                    cluster.AverageSimilarity = 1.0;
                    clusters.Add(cluster);
                }
            }
            foreach (var cluster in clusters) RecomputeCluster(cluster);
            return clusters;
        }

        private static void RecomputeCluster(CorrectionCluster cluster)
        {
            if (cluster == null || cluster.Samples.Count == 0) return;
            string bestText = cluster.Samples[0].Text;
            var bestAverage = -1.0;
            foreach (var candidate in cluster.Samples.Take(20))
            {
                var average = cluster.Samples.Average(x => KnowledgeEngineV2Semantics.TextSimilarity(candidate.Text, x.Text));
                if (average > bestAverage)
                {
                    bestAverage = average;
                    bestText = candidate.Text;
                }
            }
            cluster.Representative = bestText;
            cluster.AverageSimilarity = Math.Max(0, Math.Min(1, bestAverage));
        }

        private static int DistinctBuyerCount(CorrectionCluster cluster)
        {
            return cluster == null ? 0 : cluster.Samples
                .Select(x => Clean(x.Event.Buyer))
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        private static double ComputeClusterScore(CorrectionCluster cluster, int distinctBuyers, double dominance)
        {
            if (cluster == null) return 0;
            var score = 0.30
                + Math.Min(0.22, cluster.Samples.Count * 0.055)
                + Math.Min(0.20, distinctBuyers * 0.065)
                + Math.Min(0.18, cluster.AverageSimilarity * 0.18)
                + Math.Min(0.10, dominance * 0.10);
            return Math.Max(0, Math.Min(0.99, score));
        }

        private static bool IsHighRisk(KnowledgeV2Record record)
        {
            if (record == null) return true;
            if (string.Equals(record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase)) return true;
            return KnowledgeEngineV2Semantics.IsHighRisk((record.Title ?? string.Empty) + " " + (record.Answer ?? string.Empty));
        }

        private static KnowledgeV2RevisionCandidateRow FindPendingByProposal(string seller, string knowledgeId, string proposal)
        {
            var normalized = NormalizeComparable(proposal);
            return ReadRows(seller).FirstOrDefault(x => x != null
                && string.Equals(x.KnowledgeId, knowledgeId, StringComparison.Ordinal)
                && string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeComparable(x.ProposedAnswer), normalized, StringComparison.Ordinal));
        }

        private static void MarkStalePendingIfSourceChanged(string seller, KnowledgeV2Record record)
        {
            if (record == null) return;
            foreach (var row in ReadRows(seller).Where(x => x != null
                && string.Equals(x.KnowledgeId, record.Id, StringComparison.Ordinal)
                && string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (string.IsNullOrEmpty(row.OriginalFingerprint)
                    || !string.Equals(row.OriginalFingerprint, KnowledgeEngineV2Repository.EditorRecordFingerprint(record), StringComparison.Ordinal))
                    MarkStatus(seller, row, "stale", "原知识已在候选生成后修改。", false);
            }
        }

        private static void RecoverCommittedCandidates(string seller)
        {
            var rows = ReadRows(seller).Where(x => x != null
                && string.Equals(x.Status, "pending", StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 0) return;
            var receipts = KnowledgeEngineV2Repository.GetCommittedRevisionCandidateReceipts(seller);
            foreach (var row in rows)
            {
                long committedAt;
                if (!receipts.TryGetValue(row.Id + "\n" + row.KnowledgeId, out committedAt)) continue;
                row.Status = "applied";
                row.ResolutionNote = "根据知识库同事务修订凭证恢复已应用状态，未重复写入知识。";
                row.AppliedAtTicks = committedAt;
                row.UpdatedAtTicks = DateTime.Now.Ticks;
                SaveRow(seller, row);
            }
        }

        private static KnowledgeV2RevisionCandidateRow FindRow(string seller, string candidateId)
        {
            candidateId = Clean(candidateId);
            if (candidateId.Length == 0) return null;
            return ReadRows(seller).FirstOrDefault(x => x != null && string.Equals(x.Id, candidateId, StringComparison.Ordinal));
        }

        private static List<KnowledgeV2RevisionCandidateRow> ReadRows(string seller)
        {
            var state = GetState(ResolveShopRequired(seller));
            lock (state.Sync) return state.Db.ReadRecords<KnowledgeV2RevisionCandidateRow>(null);
        }

        private static void SaveRow(string seller, KnowledgeV2RevisionCandidateRow row)
        {
            var state = GetState(ResolveShopRequired(seller));
            lock (state.Sync) state.Db.SaveOneRecord(row);
        }

        private static void MarkStatus(string seller, KnowledgeV2RevisionCandidateRow row, string status, string note, bool applied)
        {
            if (row == null) return;
            row.Status = status;
            row.ResolutionNote = note ?? string.Empty;
            row.UpdatedAtTicks = DateTime.Now.Ticks;
            if (applied) row.AppliedAtTicks = row.UpdatedAtTicks;
            SaveRow(seller, row);
        }

        private static KnowledgeV2RevisionCandidate ToView(KnowledgeV2RevisionCandidateRow row)
        {
            List<KnowledgeV2RevisionEvidence> evidence;
            try { evidence = JsonConvert.DeserializeObject<List<KnowledgeV2RevisionEvidence>>(row.EvidenceJson ?? "[]") ?? new List<KnowledgeV2RevisionEvidence>(); }
            catch { evidence = new List<KnowledgeV2RevisionEvidence>(); }
            return new KnowledgeV2RevisionCandidate
            {
                Id = row.Id,
                KnowledgeId = row.KnowledgeId,
                KnowledgeTitle = row.KnowledgeTitle,
                RiskLevel = row.RiskLevel,
                OriginalAnswer = row.OriginalAnswer,
                ProposedAnswer = row.ProposedAnswer,
                Evidence = evidence,
                EvidenceCount = row.EvidenceCount,
                DistinctBuyerCount = row.DistinctBuyerCount,
                ClusterScore = row.ClusterScore,
                Status = row.Status,
                ResolutionNote = row.ResolutionNote,
                CreatedAt = SafeDate(row.CreatedAtTicks),
                UpdatedAt = SafeDate(row.UpdatedAtTicks),
                AppliedAt = SafeDate(row.AppliedAtTicks)
            };
        }

        private static StoreState GetState(ShopContext shop)
        {
            return Stores.GetOrAdd(shop.ShopKey, _ =>
            {
                var root = Paths.GetKnowledgeRoot(shop);
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                var path = Path.Combine(root, "knowledge-revision-v2.db");
                return new StoreState
                {
                    Path = path,
                    Db = new SQLiteHelper(path, new List<Type> { typeof(KnowledgeV2RevisionCandidateRow), typeof(KnowledgeV2CandidateMutationReceiptRow) })
                };
            });
        }

        private static ShopContext ResolveShopRequired(string seller)
        {
            var shop = ShopContextLocator.ResolveCurrentOrBySellerNick(Clean(seller));
            if (shop == null || !shop.HasStableSellerId || string.IsNullOrWhiteSpace(shop.ShopKey)) throw new InvalidOperationException("Knowledge V2修订服务无法确定当前店铺身份。");
            return shop;
        }

        private static string ExtractManualReply(string evidence)
        {
            evidence = (evidence ?? string.Empty).Trim();
            const string prefix = "manual_reply:";
            if (!evidence.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            return CleanReply(evidence.Substring(prefix.Length));
        }

        private static string CleanReply(string value)
        {
            value = (value ?? string.Empty).Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value;
        }

        private static string NormalizeComparable(string value)
        {
            return CleanReply(value).Replace(" ", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        }

        private static string Clean(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static DateTime SafeDate(long ticks)
        {
            try { return ticks <= 0 ? DateTime.MinValue : new DateTime(ticks); }
            catch { return DateTime.MinValue; }
        }
    }
}
