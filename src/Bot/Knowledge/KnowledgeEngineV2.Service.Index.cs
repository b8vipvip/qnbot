using Bot.ChromeNs;
using Bot.ShopScope;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Text.RegularExpressions;

namespace Bot.Knowledge
{
    internal static partial class KnowledgeEngineV2Service
    {
        private static Snapshot GetSnapshot(string seller)
        {
            var shop = ResolveShopRequired(seller);
            Snapshot existing;
            if (Snapshots.TryGetValue(shop.ShopKey, out existing) && existing != null
                    && existing.CorpusVersion == KnowledgeEngineV2Repository.GetCorpusVersion(seller)) return existing;
            lock (BuildLocks.GetOrAdd(shop.ShopKey, _ => new object()))
            {
                if (Snapshots.TryGetValue(shop.ShopKey, out existing) && existing != null
                    && existing.CorpusVersion == KnowledgeEngineV2Repository.GetCorpusVersion(seller)) return existing;
                long version;
                var records = KnowledgeEngineV2Repository.LoadSnapshotCorpus(seller, out version)
                    .Where(x => x != null)
                    .Select(KnowledgeV2AuthorityPolicy.NormalizeForRead)
                    .Where(x => x != null && x.Enabled
                        && !string.Equals(x.Status, "disabled", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(x.Status, "deleted", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var snapshot = BuildSnapshot(shop.ShopKey, records);
                snapshot.CorpusVersion = version;
                Snapshots[shop.ShopKey] = snapshot;
                BotLib.Log.Info("Knowledge Engine V2内存索引已构建: shop=" + shop.ShopKey
                    + ", records=" + records.Count + ", builtAt=" + snapshot.BuiltAt.ToString("HH:mm:ss.fff"));
                return snapshot;
            }
        }

        private static Snapshot BuildSnapshot(string shopKey, List<KnowledgeV2Record> records)
        {
            var snapshot = new Snapshot
            {
                ShopKey = shopKey,
                BuiltAt = DateTime.Now,
                Records = records ?? new List<KnowledgeV2Record>(),
                Exact = NewIndex(),
                Intent = NewIndex(),
                Predicate = NewIndex(),
                Entity = NewIndex(),
                Ngram = NewIndex()
            };
            for (var i = 0; i < snapshot.Records.Count; i++)
            {
                var record = snapshot.Records[i];
                Add(snapshot.Intent, record.Intent, i);
                Add(snapshot.Predicate, record.Predicate, i);
                foreach (var entity in RecordEntities(record))
                    Add(snapshot.Entity, KnowledgeEngineV2Semantics.Compact(entity), i);
                foreach (var alias in (record.Aliases ?? new List<string>()).Concat(new[] { record.Title }))
                {
                    var exact = KnowledgeEngineV2Semantics.Compact(alias);
                    if (exact.Length >= 2) Add(snapshot.Exact, exact, i);
                    foreach (var gram in KnowledgeEngineV2Semantics.RecallNgrams(exact, 2, 24)) Add(snapshot.Ngram, gram, i);
                }
            }
            return snapshot;
        }

        private static IEnumerable<string> RecordEntities(KnowledgeV2Record record)
        {
            if (record == null) return Enumerable.Empty<string>();
            var entities = record.Entities ?? new List<string>();
            var products = (record.ProductIds ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x =>
                {
                    var value = x.Trim();
                    if (value.StartsWith("product:", StringComparison.OrdinalIgnoreCase))
                        value = value.Substring("product:".Length).Trim();
                    return value.Length == 0 ? string.Empty : "product:" + value;
                });
            return entities.Concat(products)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<int> Recall(Snapshot snapshot, KnowledgeV2Query query)
        {
            var result = new HashSet<int>();
            HashSet<int> exact;
            if (snapshot.Exact.TryGetValue(query.Normalized, out exact)) result.UnionWith(exact);
            AddFromIndex(snapshot.Predicate, query.Predicate, result);
            AddFromIndex(snapshot.Intent, query.Intent, result);
            foreach (var entity in query.Entities ?? new List<string>())
                AddFromIndex(snapshot.Entity, KnowledgeEngineV2Semantics.Compact(entity), result);

            var gramVotes = new Dictionary<int, int>();
            foreach (var gram in KnowledgeEngineV2Semantics.RecallNgrams(query.Normalized, 2, 24))
            {
                HashSet<int> ids;
                if (!snapshot.Ngram.TryGetValue(gram, out ids)) continue;
                foreach (var id in ids)
                {
                    int count;
                    gramVotes[id] = gramVotes.TryGetValue(id, out count) ? count + 1 : 1;
                }
            }
            // Do not consume the lexical budget with wrong-product results
            // before checking applicability. Retain votes for final tie-breaking.
            foreach (var item in gramVotes) result.Add(item.Key);
            query.RecallBudgetAmbiguous = false;
            if (result.Count > 96)
            {
                // Never prune exact aliases before applicability and conflict checks.
                // A broad predicate/intent bucket can otherwise displace a reviewed FAQ.
                var applicableExact = (exact ?? new HashSet<int>()).Where(i =>
                    {
                        string ignored;
                        return KnowledgeEngineV2Semantics.IsApplicable(snapshot.Records[i], query, out ignored);
                    })
                    .OrderByDescending(i => KnowledgeV2AuthorityPolicy.IsProductionApproved(snapshot.Records[i]))
                    .ThenBy(i => snapshot.Records[i].Id, StringComparer.Ordinal).ToList();
                // More than a full ranking budget of production exact matches
                // cannot safely be treated as consensus from a truncated sample.
                if (applicableExact.Count(i => KnowledgeV2AuthorityPolicy.IsProductionApproved(snapshot.Records[i])) > 96)
                    query.RecallBudgetAmbiguous = true;
                var exactIds = new HashSet<int>(applicableExact.Take(96));
                // Filter scope-inapplicable candidates before consuming the
                // bounded ranking budget. Otherwise thousands of same-intent but
                // wrong-product or excluded records can hide the only valid match.
                var ordered = result.Where(i => !exactIds.Contains(i))
                    .Where(i =>
                    {
                        string ignored;
                        return KnowledgeEngineV2Semantics.IsApplicable(snapshot.Records[i], query, out ignored);
                    })
                    .OrderByDescending(i => KnowledgeV2AuthorityPolicy.IsProductionApproved(snapshot.Records[i]))
                    .ThenByDescending(i => PreliminaryScore(snapshot.Records[i], query))
                    .ThenByDescending(i => gramVotes.ContainsKey(i) ? gramVotes[i] : 0)
                    .ThenBy(i => snapshot.Records[i].Id, StringComparer.Ordinal).ToList();
                var selected = new HashSet<int>(exactIds);
                selected.UnionWith(ordered.Take(Math.Max(0, 96 - exactIds.Count)));
                // If a strong lexical candidate was pruned, do not silently
                // claim that the retained candidates prove an unambiguous fact.
                var overflow = ordered.Skip(Math.Max(0, 96 - exactIds.Count))
                    .Where(i => KnowledgeV2AuthorityPolicy.IsProductionApproved(snapshot.Records[i]));
                if (!query.RecallBudgetAmbiguous)
                    query.RecallBudgetAmbiguous = overflow.Any(i =>
                        (snapshot.Records[i].Aliases ?? new List<string>()).Concat(new[] { snapshot.Records[i].Title })
                            .Any(alias => SameAnswerNumbers(query.Original, alias)
                                && KnowledgeEngineV2Semantics.TextSimilarity(query.Original, alias) >= 0.90));
                return selected;
            }
            return result;
        }

        private static KnowledgeV2Match Score(string seller, KnowledgeV2Record record, KnowledgeV2Query query)
        {
            if (record == null || query == null) return null;
            string applicabilityReason;
            if (ShopReceptionPolicy.IsQuarantined(seller, record.Id, record.Title, record.Answer))
                return null;
            if (!KnowledgeEngineV2Semantics.IsApplicable(record, query, out applicabilityReason))
            {
                return null;
            }
            var alias = (record.Aliases ?? new List<string>()).Concat(new[] { record.Title })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => SameAnswerNumbers(query.Original, x)
                    ? KnowledgeEngineV2Semantics.TextSimilarity(query.Original, x) : 0)
                .DefaultIfEmpty(0).Max();
            var predicate = string.Equals(query.Predicate, record.Predicate, StringComparison.OrdinalIgnoreCase) ? 1.0
                : (query.Predicate == "general" || record.Predicate == "general" ? 0.30 : 0.0);
            var intent = string.Equals(query.Intent, record.Intent, StringComparison.OrdinalIgnoreCase) ? 1.0
                : (query.Intent == "general" || record.Intent == "general" ? 0.35 : 0.0);
            var entity = EntitySimilarity(query.Entities, RecordEntities(record));
            var feedbackAdjustment = KnowledgeEngineV2FeedbackService.GetQualityAdjustment(seller, record.Id);
            var confidence = Clamp(record.Confidence * 0.72 + record.Authority * 0.28 + feedbackAdjustment);
            var score = Clamp(predicate * 0.34 + entity * 0.25 + intent * 0.17 + alias * 0.16 + confidence * 0.08);
            if (alias >= 0.98) score = Math.Max(score, 0.97);
            else if (predicate >= 0.99 && entity >= 0.66 && intent >= 0.99) score = Math.Max(score, 0.89);
            return new KnowledgeV2Match
            {
                Record = record,
                Score = score,
                AliasScore = alias,
                EntityScore = entity,
                PredicateScore = predicate,
                IntentScore = intent,
                ConfidenceScore = confidence,
                Reason = "predicate=" + predicate.ToString("0.00")
                    + ", entity=" + entity.ToString("0.00")
                    + ", intent=" + intent.ToString("0.00")
                    + ", alias=" + alias.ToString("0.00")
                    + ", confidence=" + confidence.ToString("0.00")
                    + ", feedback=" + feedbackAdjustment.ToString("+0.000;-0.000;0.000")
            };
        }

        private static double PreliminaryScore(KnowledgeV2Record record, KnowledgeV2Query query)
        {
            if (record == null) return 0;
            var score = 0.0;
            if (record.Predicate == query.Predicate) score += 4;
            if (record.Intent == query.Intent) score += 2;
            score += EntitySimilarity(query.Entities, RecordEntities(record)) * 3;
            return score;
        }

        private static bool HasConflict(KnowledgeV2Match best, KnowledgeV2Match second)
        {
            if (best == null || second == null || best.Record == null || second.Record == null) return false;
            if (best.Score < 0.78 || second.Score < 0.74) return false;
            return IsMeaningfulConflictPair(best.Record, second.Record);
        }

        private static bool HasApplicableScopedConflict(KnowledgeV2Match best, KnowledgeV2Match other, KnowledgeV2Query query)
        {
            if (best == null || other == null || best.Record == null || other.Record == null
                || query == null || best.Score < 0.78 || other.Score < 0.74
                || !KnowledgeEngineV2Semantics.ProductScopesOverlap(best.Record, other.Record))
                return false;
            var left = best.Record;
            var right = other.Record;
            if ((left.ProductIds == null || left.ProductIds.Count == 0)
                && (right.ProductIds == null || right.ProductIds.Count == 0)
                && (left.Conditions == null || left.Conditions.Count == 0)
                && (right.Conditions == null || right.Conditions.Count == 0)
                && (left.RequiredContext == null || left.RequiredContext.Count == 0)
                && (right.RequiredContext == null || right.RequiredContext.Count == 0)
                && (left.Exclusions == null || left.Exclusions.Count == 0)
                && (right.Exclusions == null || right.Exclusions.Count == 0))
                return false;
            // Both candidates have already passed the same query applicability
            // gate. Different condition signatures do not resolve overlapping
            // business claims by themselves.
            if (IsMeaningfulConflictPair(left, right)) return true;
            if (best.PredicateScore < 0.99 || other.PredicateScore < 0.99
                || best.IntentScore < 0.75 || other.IntentScore < 0.75
                || KnowledgeEngineV2Semantics.TextSimilarity(left.Title, right.Title) < 0.86
                || !SameAnswerNumbers(left.Title, right.Title))
                return false;
            return !AnswerFactsCompatible(left.Answer, right.Answer);
        }

        private static bool HasAnswerDisagreement(List<KnowledgeV2Record> records)
        {
            records = records ?? new List<KnowledgeV2Record>();
            for (var i = 0; i < records.Count; i++)
            {
                for (var j = i + 1; j < records.Count; j++)
                {
                    if (IsMeaningfulConflictPair(records[i], records[j])) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The FactKey intentionally groups a broad business scope for retrieval, so two records sharing
        /// one FactKey are not automatically contradictory. A real conflict must also describe nearly the
        /// same buyer question and contain incompatible conclusions. This prevents unrelated procedures
        /// such as “how to bind”, “how to confirm” and “what to do after binding” from appearing as a
        /// destructive conflict group merely because their answer wording is different.
        /// </summary>
        private static bool IsMeaningfulConflictPair(KnowledgeV2Record left, KnowledgeV2Record right)
        {
            if (left == null || right == null
                || !KnowledgeEngineV2Semantics.ProductScopesOverlap(left, right)) return false;
            var exactQuestion = (left.Aliases ?? new List<string>()).Concat(new[] { left.Title })
                .Any(a => (right.Aliases ?? new List<string>()).Concat(new[] { right.Title })
                    .Any(b => ExactQuestionMatches(a, b)));
            if (!exactQuestion && !string.Equals(KnowledgeEngineV2Semantics.FactKey(left),
                KnowledgeEngineV2Semantics.FactKey(right), StringComparison.Ordinal)) return false;
            if (AnswersEquivalent(left.Answer, right.Answer)) return false;
            // Two applicable records claiming the exact same wording but different
            // conclusions cannot be resolved safely by ranking alone.
            if (exactQuestion) return true;

            if (!SameAnswerNumbers(left.Title, right.Title)) return false;
            var questionSimilarity = KnowledgeEngineV2Semantics.TextSimilarity(left.Title, right.Title);
            if (questionSimilarity < 0.72) return false;
            if (questionSimilarity >= 0.86 && !SameAnswerNumbers(left.Answer, right.Answer)) return true;

            var leftDirection = AnswerDirection(left.Answer);
            var rightDirection = AnswerDirection(right.Answer);
            if (leftDirection != 0 && rightDirection != 0)
                return leftDirection != rightDirection;

            // Near-identical questions where one answer explicitly says "unsupported/cannot" and the
            // other answer no longer carries that restriction are still review-worthy. Keep this narrow:
            // it catches stale-vs-current business rules without turning generic wording differences into conflicts.
            return questionSimilarity >= 0.86
                && leftDirection != rightDirection
                && (leftDirection < 0 || rightDirection < 0);
        }

        internal static bool AnswerFactsCompatible(string left, string right)
        {
            return !HasAnswerPolarityConflict(left, right) && SameAnswerNumbers(left, right);
        }

        private static bool AnswersEquivalent(string left, string right)
        {
            // Similar strings can have opposite meaning or different quantities:
            // "supports" versus "does not support", or 1.2 versus 12.
            return !HasAnswerPolarityConflict(left, right)
                && SameAnswerNumbers(left, right)
                && KnowledgeEngineV2Semantics.TextSimilarity(left, right) >= 0.68;
        }

        private static IEnumerable<string> NumericTokens(string value)
        {
            // Preserve signs, decimal points and multi-part versions, unlike Compact.
            const string pattern = @"[+\-−]?\d+(?:[.,]\d+)*";
            return Regex.Matches(value ?? string.Empty, pattern).Cast<Match>().Select(x => x.Value);
        }

        private static bool SameAnswerNumbers(string left, string right)
        {
            return NumericTokens(left).SequenceEqual(NumericTokens(right), StringComparer.Ordinal);
        }

        private static bool ExactQuestionMatches(string left, string right)
        {
            var key = KnowledgeEngineV2Semantics.Compact(left);
            return key.Length > 0 && key == KnowledgeEngineV2Semantics.Compact(right) && SameAnswerNumbers(left, right);
        }

        private static bool HasCompatibleQuestionNumbers(KnowledgeV2Record record, KnowledgeV2Query query)
        {
            if (record == null || query == null) return false;
            var bound = new HashSet<string>((record.ProductIds ?? new List<string>())
                .Select(x => (x ?? string.Empty).Trim())
                .Select(x => x.StartsWith("product:", StringComparison.OrdinalIgnoreCase) ? x.Substring(8) : x),
                StringComparer.OrdinalIgnoreCase);
            var verifiedProducts = new HashSet<string>((query.Entities ?? new List<string>())
                .Where(x => x.StartsWith("product:", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Substring(8)).Where(bound.Contains), StringComparer.OrdinalIgnoreCase);
            var numbers = NumericTokens(query.Original).Where(x => !verifiedProducts.Contains(x)).ToList();
            return (record.Aliases ?? new List<string>()).Concat(new[] { record.Title })
                .Any(alias => !string.IsNullOrWhiteSpace(alias) && numbers.SequenceEqual(
                    NumericTokens(alias).Where(x => !verifiedProducts.Contains(x)), StringComparer.Ordinal));
        }

        private static double EntitySimilarity(IEnumerable<string> left, IEnumerable<string> right)
        {
            var a = (left ?? Enumerable.Empty<string>()).Select(KnowledgeEngineV2Semantics.Compact)
                .Where(x => x.Length >= 2).Distinct().ToList();
            var b = (right ?? Enumerable.Empty<string>()).Select(KnowledgeEngineV2Semantics.Compact)
                .Where(x => x.Length >= 2).Distinct().ToList();
            if (a.Count == 0 || b.Count == 0) return 0;
            var matched = a.Count(x => b.Any(y => x == y || x.Contains(y) || y.Contains(x)));
            return Clamp(matched / (double)Math.Min(4, a.Count));
        }

        private static string BuildRejectReason(KnowledgeV2Match best, KnowledgeV2Decision decision,
            double threshold, double minConfidence, double margin, bool highRisk)
        {
            if (best == null) return "没有候选";
            if (decision.HasConflict) return "同一明确业务问题存在相互矛盾的生产知识";
            if (highRisk) return "高风险问题继续交给安全/AI链路";
            if (best.Record.Status == "candidate") return "学习候选尚未批准，不能直接发送";
            if (best.Score < threshold) return "结构化匹配分不足：" + best.Score.ToString("0.00") + " < " + threshold.ToString("0.00");
            if (best.ConfidenceScore < minConfidence) return "知识可信度不足：" + best.ConfidenceScore.ToString("0.00") + " < " + minConfidence.ToString("0.00");
            if (best.PredicateScore < 0.99 && best.AliasScore < 0.94) return "Predicate尚未明确且问法不是精确/近似精确命中";
            if (margin < 0.08 && best.AliasScore < 0.98) return "候选分差不足，需要上下文/AI确认";
            return "当前回复模式或安全门控不允许本地直答";
        }

        private static void Finish(KnowledgeV2Decision decision, Stopwatch total, Stopwatch decisionSw)
        {
            decision.DecisionMs = decisionSw.ElapsedMilliseconds;
            decision.TotalMs = total.ElapsedMilliseconds;
        }

        private const int SoftWorkingMemoryLimit = 256;
        private static long _lastWorkingMemorySweepTicks;

        internal static int CachedWorkingMemoryCount { get { return Working.Count; } }

        private static KnowledgeV2WorkingMemory GetWorkingMemory(string shopKey, string seller, string buyer)
        {
            if (string.IsNullOrWhiteSpace(buyer)) return null;
            KnowledgeV2WorkingMemory memory;
            if (!Working.TryGetValue(WorkingKey(shopKey, seller, buyer), out memory) || memory == null) return null;
            lock (memory)
            {
                if (memory.UpdatedAt < DateTime.Now.AddMinutes(-45)) return null;
                return new KnowledgeV2WorkingMemory
                {
                    Seller = memory.Seller, Buyer = memory.Buyer, Subject = memory.Subject,
                    Predicate = memory.Predicate, Intent = memory.Intent, UpdatedAt = memory.UpdatedAt,
                    Entities = (memory.Entities ?? new List<string>()).ToList(),
                    ProductEvidenceConflict = memory.ProductEvidenceConflict
                };
            }
        }

        private static void UpdateWorkingMemory(string shopKey, string seller, string buyer, KnowledgeV2Query query)
        {
            if (query == null || string.IsNullOrWhiteSpace(buyer)) return;
            var key = WorkingKey(shopKey, seller, buyer);
            var current = Working.GetOrAdd(key, _ => new KnowledgeV2WorkingMemory
            {
                Seller = seller ?? string.Empty,
                Buyer = buyer ?? string.Empty,
                UpdatedAt = DateTime.Now
            });
            lock (current)
            {
                if (!query.ContextDependent || current.UpdatedAt < DateTime.Now.AddMinutes(-45))
                {
                    // A standalone/new turn starts a new context, including empty fields.
                    // A timed-out object's previous product/intent must not be revived.
                    current.Subject = query.Subject ?? string.Empty;
                    current.Predicate = query.Predicate ?? "general";
                    current.Intent = query.Intent ?? "general";
                    current.Entities = (query.Entities ?? new List<string>()).OrderByDescending(KnowledgeEngineV2Semantics.IsProductEntity).Take(10).ToList();
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(query.Subject)) current.Subject = query.Subject;
                    if (query.Predicate != "general") current.Predicate = query.Predicate;
                    if (query.Intent != "general") current.Intent = query.Intent;
                    if (query.Entities != null && query.Entities.Count > 0)
                        current.Entities = query.Entities.OrderByDescending(KnowledgeEngineV2Semantics.IsProductEntity).Take(10).ToList();
                }
                current.ProductEvidenceConflict = query.ProductEvidenceConflict;
                current.UpdatedAt = DateTime.Now;
            }
            SweepExpiredWorkingMemoryIfDue();
        }

        private static void SweepExpiredWorkingMemoryIfDue()
        {
            var now = DateTime.Now;
            var nowTicks = DateTime.UtcNow.Ticks;
            var previous = Interlocked.Read(ref _lastWorkingMemorySweepTicks);
            if (previous != 0 && nowTicks - previous < TimeSpan.FromMinutes(10).Ticks)
                return;
            if (Interlocked.CompareExchange(ref _lastWorkingMemorySweepTicks, nowTicks, previous)
                != previous) return;

            // The resolver already ignores memory older than 45 minutes.
            // Retain slightly longer to avoid races with in-flight queries;
            // always remove entries idle for 24 hours, and pressure-prune
            // only entries outside the resolver's 45-minute context window.
            foreach (var pair in Working.ToArray().OrderBy(x =>
                x.Value == null ? DateTime.MinValue : x.Value.UpdatedAt))
            {
                var memory = pair.Value;
                if (memory == null) continue;
                lock (memory)
                {
                    var idle = now - memory.UpdatedAt;
                    if (idle < TimeSpan.FromDays(1)
                        && (Working.Count <= SoftWorkingMemoryLimit
                            || idle < TimeSpan.FromMinutes(60))) continue;
                    KnowledgeV2WorkingMemory current;
                    if (!Working.TryGetValue(pair.Key, out current)
                        || !ReferenceEquals(current, memory)) continue;
                    KnowledgeV2WorkingMemory ignored;
                    Working.TryRemove(pair.Key, out ignored);
                }
            }
        }

        private static Dictionary<string, HashSet<int>> NewIndex()
        {
            return new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        }

        private static void Add(Dictionary<string, HashSet<int>> index, string key, int id)
        {
            key = (key ?? string.Empty).Trim();
            if (key.Length == 0 || key == "general") return;
            HashSet<int> set;
            if (!index.TryGetValue(key, out set))
            {
                set = new HashSet<int>();
                index[key] = set;
            }
            set.Add(id);
        }

        private static void AddFromIndex(Dictionary<string, HashSet<int>> index, string key, HashSet<int> target)
        {
            if (string.IsNullOrWhiteSpace(key) || key == "general") return;
            HashSet<int> ids;
            if (index.TryGetValue(key, out ids)) target.UnionWith(ids);
        }

        private static RuntimeSettings GetSettings(string seller)
        {
            var shop = ResolveShop(seller);
            if (shop == null)
            {
                return new RuntimeSettings
                {
                    Enabled = true,
                    Mode = KnowledgeEngineV2Constants.ModeProduction,
                    DirectThreshold = KnowledgeEngineV2Constants.DefaultDirectThreshold,
                    MinConfidence = KnowledgeEngineV2Constants.DefaultMinConfidence,
                    ExpiresAt = DateTime.Now.AddMinutes(1)
                };
            }
            RuntimeSettings cached;
            if (SettingsCache.TryGetValue(shop.ShopKey, out cached)
                && cached != null && cached.ExpiresAt >= DateTime.Now) return cached;
            lock (SettingsLocks.GetOrAdd(shop.ShopKey, _ => new object()))
            {
                if (SettingsCache.TryGetValue(shop.ShopKey, out cached)
                    && cached != null && cached.ExpiresAt >= DateTime.Now) return cached;
                try
                {
                    var values = new ShopScopedSettingsStore(shop, Paths).ExportValues();
                    string enabledRaw;
                    string thresholdRaw;
                    string confidenceRaw;
                    double threshold;
                    double confidence;
                    // V2 is mandatory. Legacy settings cannot turn it off.
                    var enabled = true;
                    var mode = KnowledgeEngineV2Constants.ModeProduction;
                    threshold = values.TryGetValue(KnowledgeEngineV2Constants.SettingsDirectThreshold, out thresholdRaw)
                        && double.TryParse(thresholdRaw, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out threshold)
                        ? threshold : KnowledgeEngineV2Constants.DefaultDirectThreshold;
                    confidence = values.TryGetValue(KnowledgeEngineV2Constants.SettingsMinConfidence, out confidenceRaw)
                        && double.TryParse(confidenceRaw, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out confidence)
                        ? confidence : KnowledgeEngineV2Constants.DefaultMinConfidence;
                    // Older installations or restored settings may already contain
                    // non-finite values; Math.Min/Max alone do not sanitize NaN.
                    if (double.IsNaN(threshold) || double.IsInfinity(threshold))
                        threshold = KnowledgeEngineV2Constants.DefaultDirectThreshold;
                    if (double.IsNaN(confidence) || double.IsInfinity(confidence))
                        confidence = KnowledgeEngineV2Constants.DefaultMinConfidence;
                    cached = new RuntimeSettings
                    {
                        Enabled = enabled,
                        Mode = mode,
                        DirectThreshold = Math.Max(0.70, Math.Min(0.96, threshold)),
                        MinConfidence = Math.Max(0.50, Math.Min(0.95, confidence)),
                        ExpiresAt = DateTime.Now.AddMinutes(5)
                    };
                    SettingsCache[shop.ShopKey] = cached;
                    return cached;
                }
                catch
                {
                    return new RuntimeSettings
                    {
                        Enabled = true,
                        Mode = KnowledgeEngineV2Constants.ModeProduction,
                        DirectThreshold = KnowledgeEngineV2Constants.DefaultDirectThreshold,
                        MinConfidence = KnowledgeEngineV2Constants.DefaultMinConfidence,
                        ExpiresAt = DateTime.Now.AddSeconds(30)
                    };
                }
            }
        }

        private static ShopContext ResolveShop(string seller)
        {
            try
            {
                var current = ShopSettingsScope.Current;
                var shop = current ?? ShopContextLocator.ResolveBySellerNick((seller ?? string.Empty).Trim());
                if (shop == null || !shop.HasStableSellerId || string.IsNullOrWhiteSpace(shop.ShopKey)
                    || (!DirectOrderIdentityResolver.IdentityEquals(shop.SellerNick, seller)
                        && !DirectOrderIdentityResolver.IdentityEquals(shop.DisplayName, seller)))
                    return null;
                return shop;
            }
            catch { return null; }
        }

        private static ShopContext ResolveShopRequired(string seller)
        {
            var shop = ResolveShop(seller);
            if (shop == null) throw new InvalidOperationException("Knowledge Engine V2无法确定当前店铺身份。");
            return shop;
        }

        private static string WorkingKey(string shopKey, string seller, string buyer)
        {
            var shop = (shopKey ?? string.Empty).Trim();
            var account = (seller ?? string.Empty).Trim();
            var contact = (buyer ?? string.Empty).Trim();
            return shop.Length + ":" + shop + account.Length + ":" + account + contact.Length + ":" + contact;
        }

        private static bool IsMediaPlaceholder(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value == "[图片]" || value == "[视频]" || value == "[语音]" || value == "[表情]";
        }

        private static KnowledgeV2Record Clone(KnowledgeV2Record source)
        {
            return JsonConvert.DeserializeObject<KnowledgeV2Record>(JsonConvert.SerializeObject(source));
        }

        private static double Clamp(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            return Math.Max(0, Math.Min(1, value));
        }
    }
}
