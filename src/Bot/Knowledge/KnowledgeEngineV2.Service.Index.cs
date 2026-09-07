using Bot.ChromeNs;
using Bot.ShopScope;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Bot.Knowledge
{
    internal static partial class KnowledgeEngineV2Service
    {
        private static Snapshot GetSnapshot(string seller)
        {
            var shop = ResolveShopRequired(seller);
            Snapshot existing;
            if (Snapshots.TryGetValue(shop.ShopKey, out existing) && existing != null) return existing;
            lock (BuildLocks.GetOrAdd(shop.ShopKey, _ => new object()))
            {
                if (Snapshots.TryGetValue(shop.ShopKey, out existing) && existing != null) return existing;
                var records = KnowledgeEngineV2Repository.LoadAll(seller)
                    .Where(x => x != null && x.Enabled)
                    .ToList();
                var snapshot = BuildSnapshot(shop.ShopKey, records);
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
                foreach (var entity in record.Entities ?? new List<string>())
                    Add(snapshot.Entity, KnowledgeEngineV2Semantics.Compact(entity), i);
                foreach (var alias in (record.Aliases ?? new List<string>()).Concat(new[] { record.Title }))
                {
                    var exact = KnowledgeEngineV2Semantics.Compact(alias);
                    if (exact.Length >= 2) Add(snapshot.Exact, exact, i);
                    foreach (var gram in KnowledgeEngineV2Semantics.Ngrams(exact, 2).Take(24)) Add(snapshot.Ngram, gram, i);
                }
            }
            return snapshot;
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
            foreach (var gram in KnowledgeEngineV2Semantics.Ngrams(query.Normalized, 2).Take(24))
            {
                HashSet<int> ids;
                if (!snapshot.Ngram.TryGetValue(gram, out ids)) continue;
                foreach (var id in ids)
                {
                    int count;
                    gramVotes[id] = gramVotes.TryGetValue(id, out count) ? count + 1 : 1;
                }
            }
            foreach (var item in gramVotes.OrderByDescending(x => x.Value).Take(40)) result.Add(item.Key);
            if (result.Count > 96)
            {
                var ordered = result
                    .OrderByDescending(i => PreliminaryScore(snapshot.Records[i], query))
                    .Take(96)
                    .ToList();
                return new HashSet<int>(ordered);
            }
            return result;
        }

        private static KnowledgeV2Match Score(string seller, KnowledgeV2Record record, KnowledgeV2Query query)
        {
            if (record == null || query == null) return null;
            string applicabilityReason;
            if (!KnowledgeEngineV2Semantics.IsApplicable(record, query, out applicabilityReason))
            {
                return null;
            }
            var alias = (record.Aliases ?? new List<string>()).Concat(new[] { record.Title })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => KnowledgeEngineV2Semantics.TextSimilarity(query.Original, x))
                .DefaultIfEmpty(0).Max();
            var predicate = string.Equals(query.Predicate, record.Predicate, StringComparison.OrdinalIgnoreCase) ? 1.0
                : (query.Predicate == "general" || record.Predicate == "general" ? 0.30 : 0.0);
            var intent = string.Equals(query.Intent, record.Intent, StringComparison.OrdinalIgnoreCase) ? 1.0
                : (query.Intent == "general" || record.Intent == "general" ? 0.35 : 0.0);
            var entity = EntitySimilarity(query.Entities, record.Entities);
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
            score += EntitySimilarity(query.Entities, record.Entities) * 3;
            return score;
        }

        private static bool HasConflict(KnowledgeV2Match best, KnowledgeV2Match second)
        {
            if (best == null || second == null || best.Record == null || second.Record == null) return false;
            if (best.Score < 0.78 || second.Score < 0.74) return false;
            return IsMeaningfulConflictPair(best.Record, second.Record);
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
            if (left == null || right == null) return false;
            if (!string.Equals(KnowledgeEngineV2Semantics.FactKey(left),
                KnowledgeEngineV2Semantics.FactKey(right), StringComparison.Ordinal)) return false;
            if (AnswersEquivalent(left.Answer, right.Answer)) return false;

            var questionSimilarity = KnowledgeEngineV2Semantics.TextSimilarity(left.Title, right.Title);
            if (questionSimilarity < 0.72) return false;

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

        private static bool AnswersEquivalent(string left, string right)
        {
            return KnowledgeEngineV2Semantics.TextSimilarity(left, right) >= 0.68;
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
            if (decision.Mode == KnowledgeEngineV2Constants.ModeShadow) return "当前为Shadow模式，只记录V2结果，不发送";
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

        private static KnowledgeV2WorkingMemory GetWorkingMemory(string seller, string buyer)
        {
            KnowledgeV2WorkingMemory memory;
            if (Working.TryGetValue(WorkingKey(seller, buyer), out memory)
                && memory != null && memory.UpdatedAt >= DateTime.Now.AddMinutes(-45)) return memory;
            return null;
        }

        private static void UpdateWorkingMemory(string seller, string buyer, KnowledgeV2Query query)
        {
            if (query == null) return;
            var key = WorkingKey(seller, buyer);
            var current = Working.GetOrAdd(key, _ => new KnowledgeV2WorkingMemory
            {
                Seller = seller ?? string.Empty,
                Buyer = buyer ?? string.Empty,
                UpdatedAt = DateTime.Now
            });
            lock (current)
            {
                if (!query.ContextDependent || !string.IsNullOrWhiteSpace(query.Subject)) current.Subject = query.Subject;
                if (query.Predicate != "general") current.Predicate = query.Predicate;
                if (query.Intent != "general") current.Intent = query.Intent;
                if (query.Entities != null && query.Entities.Count > 0) current.Entities = query.Entities.Take(8).ToList();
                current.UpdatedAt = DateTime.Now;
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
            try
            {
                var values = new ShopScopedSettingsStore(shop, Paths).ExportValues();
                string enabledRaw;
                string modeRaw;
                string thresholdRaw;
                string confidenceRaw;
                double threshold;
                double confidence;
                var enabled = !values.TryGetValue(KnowledgeEngineV2Constants.SettingsEnabled, out enabledRaw)
                    || !string.Equals((enabledRaw ?? string.Empty).Trim(), "0", StringComparison.Ordinal);
                var mode = values.TryGetValue(KnowledgeEngineV2Constants.SettingsMode, out modeRaw)
                    && string.Equals(modeRaw, KnowledgeEngineV2Constants.ModeShadow, StringComparison.OrdinalIgnoreCase)
                    ? KnowledgeEngineV2Constants.ModeShadow : KnowledgeEngineV2Constants.ModeProduction;
                threshold = values.TryGetValue(KnowledgeEngineV2Constants.SettingsDirectThreshold, out thresholdRaw)
                    && double.TryParse(thresholdRaw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out threshold)
                    ? threshold : KnowledgeEngineV2Constants.DefaultDirectThreshold;
                confidence = values.TryGetValue(KnowledgeEngineV2Constants.SettingsMinConfidence, out confidenceRaw)
                    && double.TryParse(confidenceRaw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out confidence)
                    ? confidence : KnowledgeEngineV2Constants.DefaultMinConfidence;
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

        private static ShopContext ResolveShop(string seller)
        {
            try
            {
                var current = ShopSettingsScope.Current;
                if (current != null) return current;
                return ShopContextLocator.ResolveBySellerNick((seller ?? string.Empty).Trim());
            }
            catch { return null; }
        }

        private static ShopContext ResolveShopRequired(string seller)
        {
            var shop = ResolveShop(seller);
            if (shop == null) throw new InvalidOperationException("Knowledge Engine V2无法确定当前店铺身份。");
            return shop;
        }

        private static string WorkingKey(string seller, string buyer)
        {
            return (seller ?? string.Empty).Trim() + "|" + (buyer ?? string.Empty).Trim();
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
            return Math.Max(0, Math.Min(1, value));
        }
    }
}
