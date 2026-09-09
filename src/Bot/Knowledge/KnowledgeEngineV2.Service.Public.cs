using Bot.ChromeNs;
using Bot.ShopScope;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bot.Knowledge
{
    internal static partial class KnowledgeEngineV2Service
    {
        private sealed class Snapshot
        {
            public string ShopKey;
            public DateTime BuiltAt;
            public List<KnowledgeV2Record> Records;
            public Dictionary<string, HashSet<int>> Exact;
            public Dictionary<string, HashSet<int>> Intent;
            public Dictionary<string, HashSet<int>> Predicate;
            public Dictionary<string, HashSet<int>> Entity;
            public Dictionary<string, HashSet<int>> Ngram;
        }

        private sealed class RuntimeSettings
        {
            public bool Enabled;
            public string Mode;
            public double DirectThreshold;
            public double MinConfidence;
            public DateTime ExpiresAt;
        }

        private static readonly ConcurrentDictionary<string, Snapshot> Snapshots = new ConcurrentDictionary<string, Snapshot>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, KnowledgeV2WorkingMemory> Working = new ConcurrentDictionary<string, KnowledgeV2WorkingMemory>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, object> BuildLocks = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, RuntimeSettings> SettingsCache = new ConcurrentDictionary<string, RuntimeSettings>(StringComparer.Ordinal);
        private static readonly IShopScopedPathProvider Paths = new ShopScopedPathProvider();

        public static bool IsEnabled(string seller) { return GetSettings(seller).Enabled; }
        public static string GetMode(string seller) { return GetSettings(seller).Mode; }

        public static bool IsSnapshotReady(string seller)
        {
            var shop = ResolveShop(seller);
            if (shop == null) return false;
            Snapshot snapshot;
            return Snapshots.TryGetValue(shop.ShopKey, out snapshot) && snapshot != null;
        }

        public static KnowledgeV2Settings GetSettingsView(string seller)
        {
            var settings = GetSettings(seller);
            return new KnowledgeV2Settings { Enabled = settings.Enabled, Mode = settings.Mode, DirectThreshold = settings.DirectThreshold, MinConfidence = settings.MinConfidence };
        }

        public static void SetSettings(string seller, bool enabled, string mode, double threshold, double minConfidence)
        {
            var shop = ResolveShopRequired(seller);
            var store = new ShopScopedSettingsStore(shop, Paths);
            store.SetString(KnowledgeEngineV2Constants.SettingsEnabled, enabled ? "1" : "0");
            store.SetString(KnowledgeEngineV2Constants.SettingsMode, string.Equals(mode, KnowledgeEngineV2Constants.ModeShadow, StringComparison.OrdinalIgnoreCase) ? KnowledgeEngineV2Constants.ModeShadow : KnowledgeEngineV2Constants.ModeProduction);
            store.SetString(KnowledgeEngineV2Constants.SettingsDirectThreshold, Math.Max(0.70, Math.Min(0.96, threshold)).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            store.SetString(KnowledgeEngineV2Constants.SettingsMinConfidence, Math.Max(0.50, Math.Min(0.95, minConfidence)).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            SettingsCache[shop.ShopKey] = new RuntimeSettings
            {
                Enabled = enabled,
                Mode = string.Equals(mode, KnowledgeEngineV2Constants.ModeShadow, StringComparison.OrdinalIgnoreCase) ? KnowledgeEngineV2Constants.ModeShadow : KnowledgeEngineV2Constants.ModeProduction,
                DirectThreshold = Math.Max(0.70, Math.Min(0.96, threshold)),
                MinConfidence = Math.Max(0.50, Math.Min(0.95, minConfidence)),
                ExpiresAt = DateTime.Now.AddMinutes(5)
            };
        }

        public static KnowledgeV2Decision Resolve(string seller, string buyer, string message)
        {
            var total = Stopwatch.StartNew();
            var settings = GetSettings(seller);
            var decision = new KnowledgeV2Decision { Enabled = settings.Enabled, Mode = settings.Mode };
            if (!decision.Enabled)
            {
                decision.Reason = "Knowledge Engine V2已关闭";
                decision.TotalMs = total.ElapsedMilliseconds;
                return decision;
            }
            message = (message ?? string.Empty).Trim();
            if (message.Length == 0 || IsMediaPlaceholder(message))
            {
                decision.Reason = "空消息或媒体消息不走文本知识引擎";
                decision.TotalMs = total.ElapsedMilliseconds;
                return decision;
            }

            var parseSw = Stopwatch.StartNew();
            var memory = GetWorkingMemory(seller, buyer);
            var query = KnowledgeEngineV2Semantics.Parse(message, memory);
            decision.Query = query;
            UpdateWorkingMemory(seller, buyer, query);
            decision.ParseMs = parseSw.ElapsedMilliseconds;

            var snapshot = GetSnapshot(seller);
            var recallSw = Stopwatch.StartNew();
            var candidates = Recall(snapshot, query);
            decision.CandidateCount = candidates.Count;
            decision.RecallMs = recallSw.ElapsedMilliseconds;

            var rankSw = Stopwatch.StartNew();
            var rankedMatches = candidates.Select(i => Score(seller, snapshot.Records[i], query)).Where(x => x != null && x.Score >= 0.30).OrderByDescending(x => x.Score).ThenByDescending(x => x.ConfidenceScore).ToList();
            var productionMatches = rankedMatches.Where(IsApprovedProductionMatch).Take(5).ToList();
            var best = productionMatches.FirstOrDefault();
            var visibleMatches = rankedMatches.Take(5).ToList();
            if (best != null && !visibleMatches.Contains(best)) visibleMatches.Add(best);
            decision.Matches = visibleMatches;
            decision.RankMs = rankSw.ElapsedMilliseconds;

            var decideSw = Stopwatch.StartNew();
            if (best == null)
            {
                decision.Reason = rankedMatches.Count > 0 ? "当前只命中尚未批准的学习候选，继续兼容上下文/AI链路" : "结构化索引没有找到足够相关的候选知识";
                Finish(decision, total, decideSw);
                return decision;
            }
            var second = productionMatches.Count > 1 ? productionMatches[1] : null;
            decision.HasConflict = HasConflict(best, second);
            var margin = second == null ? best.Score : best.Score - second.Score;
            var threshold = settings.DirectThreshold;
            var minConfidence = settings.MinConfidence;
            var highRisk = KnowledgeEngineV2Semantics.IsHighRisk(message) || KnowledgeEngineV2Semantics.IsHighRisk(best.Record.Answer) || string.Equals(best.Record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase);
            var sameFactSecond = second != null && string.Equals(KnowledgeEngineV2Semantics.FactKey(best.Record), KnowledgeEngineV2Semantics.FactKey(second.Record), StringComparison.Ordinal);
            var safeConsensus = HasSafeDirectConsensus(best, productionMatches, query, threshold, minConfidence, highRisk);
            var effectiveMargin = (sameFactSecond && AnswersEquivalent(best.Record.Answer, second.Record.Answer)) || safeConsensus ? Math.Max(margin, 0.12) : margin;
            var standaloneSafe = IsSafeStandaloneLocalDirect(query, message);

            decision.CanDirectReply = ReplyModeService.IsLocalFirst(seller)
                && decision.Mode == KnowledgeEngineV2Constants.ModeProduction
                && !decision.HasConflict
                && !highRisk
                && standaloneSafe
                && best.Record.Enabled
                && best.Score >= threshold
                && best.ConfidenceScore >= minConfidence
                && (best.AliasScore >= 0.94 || best.PredicateScore >= 0.99)
                && (effectiveMargin >= 0.08 || best.AliasScore >= 0.98);
            decision.Answer = decision.CanDirectReply ? (best.Record.Answer ?? string.Empty).Trim() : string.Empty;
            decision.Reason = decision.CanDirectReply
                ? "V2结构化知识高置信直答：score=" + best.Score.ToString("0.00") + ", predicate=" + query.Predicate + ", candidates=" + candidates.Count + (safeConsensus ? ", consensus=safe" : string.Empty)
                : (!standaloneSafe ? "当前消息依赖Working Memory或属于状态/投诉/人工等控制语义，禁止V2本地直答，继续完整上下文/AI/人工链路" : BuildRejectReason(best, decision, threshold, minConfidence, effectiveMargin, highRisk));
            Finish(decision, total, decideSw);
            return decision;
        }

        private static bool IsSafeStandaloneLocalDirect(KnowledgeV2Query query, string message)
        {
            if (query == null) return false;
            var compact = KnowledgeEngineV2Semantics.Compact(message);
            if (string.IsNullOrWhiteSpace(compact)) return false;

            // A local direct answer must be grounded by the current buyer message itself. Working
            // Memory may help AI/contextual reasoning, but it must never turn “充没/1?/不满意/人工”
            // into the previous account_binding fact and auto-send it as a fresh answer.
            if (!string.IsNullOrWhiteSpace(query.WorkingMemoryReason)) return false;
            if (Regex.IsMatch(compact, @"^\d{5,20}$")) return false;
            if (Regex.IsMatch(compact, @"^[?？!！.。]+$")) return false;
            if (Regex.IsMatch(compact, @"(人工|客服|转人工|投诉|不满意|有病|垃圾|骗子|差评|举报|生气|退款|退货|充没|充上没|到账没|付款没|发货没)")) return false;
            return true;
        }

        private static bool HasSafeDirectConsensus(KnowledgeV2Match best, List<KnowledgeV2Match> productionMatches, KnowledgeV2Query query, double threshold, double minConfidence, bool highRisk)
        {
            if (best == null || best.Record == null || query == null || highRisk || productionMatches == null || productionMatches.Count < 2 || best.Score < threshold || best.ConfidenceScore < minConfidence) return false;
            if (query.ContextDependent && (query.Entities == null || query.Entities.Count < 1) && string.IsNullOrWhiteSpace(query.Subject)) return false;
            var close = productionMatches.Where(x => x != null && x.Record != null).Where(x => best.Score - x.Score < 0.08).Where(x => x.Score >= threshold && x.ConfidenceScore >= minConfidence).Take(4).ToList();
            if (close.Count < 2) return false;
            foreach (var candidate in close.Skip(1))
            {
                if (!candidate.Record.Enabled || string.Equals(candidate.Record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase) || KnowledgeEngineV2Semantics.IsHighRisk(candidate.Record.Answer)) return false;
                if (!SameConsensusScope(best.Record, candidate.Record)) return false;
                if (HasAnswerPolarityConflict(best.Record.Answer, candidate.Record.Answer)) return false;
            }
            return true;
        }

        private static bool SameConsensusScope(KnowledgeV2Record left, KnowledgeV2Record right)
        {
            if (left == null || right == null) return false;
            var leftPredicate = KnowledgeEngineV2Semantics.NormalizePredicate(left.Predicate);
            var rightPredicate = KnowledgeEngineV2Semantics.NormalizePredicate(right.Predicate);
            var leftIntent = KnowledgeEngineV2Semantics.NormalizeIntent(left.Intent);
            var rightIntent = KnowledgeEngineV2Semantics.NormalizeIntent(right.Intent);
            if (leftPredicate != "general" && rightPredicate != "general" && !string.Equals(leftPredicate, rightPredicate, StringComparison.OrdinalIgnoreCase)) return false;
            if (leftIntent != "general" && rightIntent != "general" && !string.Equals(leftIntent, rightIntent, StringComparison.OrdinalIgnoreCase)) return false;
            if (AnswersEquivalent(left.Answer, right.Answer)) return true;
            return EntitySimilarity(left.Entities, right.Entities) >= 0.50;
        }

        private static bool HasAnswerPolarityConflict(string left, string right)
        {
            var leftDirection = AnswerDirection(left);
            var rightDirection = AnswerDirection(right);
            return leftDirection != 0 && rightDirection != 0 && leftDirection != rightDirection;
        }

        private static int AnswerDirection(string answer)
        {
            var value = KnowledgeEngineV2Semantics.Compact(answer);
            if (value.Length == 0) return 0;
            var negative = value.Contains("不支持") || value.Contains("不能使用") || value.Contains("无法使用") || value.Contains("不可以") || value.Contains("不可使用");
            var positive = value.Contains("可以使用") || value.Contains("支持使用") || value.Contains("可以用于") || value.Contains("适用于") || value.Contains("是电视端") || value.Contains("可以支持");
            if (negative && !positive) return -1;
            if (positive && !negative) return 1;
            return 0;
        }

        private static bool IsApprovedProductionMatch(KnowledgeV2Match match)
        {
            return KnowledgeV2AuthorityPolicy.IsProductionApproved(match == null ? null : match.Record);
        }

        public static List<KnowledgeV2Record> GetRecords(string seller)
        {
            return KnowledgeEngineV2Repository.LoadAll(seller).Where(x => x != null).Select(Clone).Select(KnowledgeV2AuthorityPolicy.NormalizeForRead).ToList();
        }

        public static List<KnowledgeV2Conflict> GetConflicts(string seller)
        {
            return GetSnapshot(seller).Records.Where(KnowledgeV2AuthorityPolicy.IsProductionApproved).GroupBy(KnowledgeEngineV2Semantics.FactKey).Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1).Select(g => new KnowledgeV2Conflict { FactKey = g.Key, Subject = g.First().Subject, Predicate = g.First().Predicate, Records = g.ToList() }).Where(x => HasAnswerDisagreement(x.Records)).ToList();
        }

        public static KnowledgeV2Stats GetStats(string seller)
        {
            var snapshot = GetSnapshot(seller);
            var all = KnowledgeEngineV2Repository.LoadAll(seller).Where(x => x != null).Select(KnowledgeV2AuthorityPolicy.NormalizeForRead).ToList();
            return new KnowledgeV2Stats
            {
                Total = all.Count,
                BusinessFacts = all.Count(x => x.Type == "business_fact" || x.Type == "presale"),
                Procedures = all.Count(x => x.Type == "procedure"),
                SafetyRules = all.Count(x => x.Type == "safety_rule"),
                LearningCandidates = all.Count(KnowledgeV2AuthorityPolicy.IsCandidate),
                ProductBound = all.Count(x => x.ProductIds != null && x.ProductIds.Count > 0),
                Conflicts = GetConflicts(seller).Count,
                SnapshotBuiltAt = snapshot.BuiltAt,
                DatabasePath = KnowledgeEngineV2Repository.GetDatabasePath(seller)
            };
        }

        public static void Invalidate(string seller)
        {
            var shop = ResolveShop(seller);
            if (shop == null) return;
            Snapshot ignored;
            Snapshots.TryRemove(shop.ShopKey, out ignored);
        }

        public static void Warm(string seller) { GetSnapshot(seller); }

        public static void RebuildFromLegacy(string seller)
        {
            KnowledgeEngineV2Repository.ResetFromLegacy(seller);
            Invalidate(seller);
            Warm(seller);
        }

        public static void PromoteCandidate(string seller, string id)
        {
            var record = GetRecords(seller).FirstOrDefault(x => x.Id == id);
            if (record == null) return;
            KnowledgeV2AuthorityPolicy.Promote(record);
            KnowledgeEngineV2Repository.Save(seller, record);
        }
    }
}