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
            public readonly string Generation = Guid.NewGuid().ToString("N");
            public long CorpusVersion;
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
        private static readonly ConcurrentDictionary<string, object> SettingsLocks = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        private static readonly IShopScopedPathProvider Paths = new ShopScopedPathProvider();

        public static bool IsEnabled(string seller) { return GetSettings(seller).Enabled; }
        public static string GetMode(string seller) { return GetSettings(seller).Mode; }

        internal static bool IsDecisionSnapshotCurrent(string seller, KnowledgeV2Decision decision)
        {
            if (decision == null || string.IsNullOrWhiteSpace(decision.SnapshotGeneration)) return false;
            var shop = ResolveShop(seller);
            Snapshot current;
            return shop != null && Snapshots.TryGetValue(shop.ShopKey, out current)
                && current != null && current.CorpusVersion == KnowledgeEngineV2Repository.GetCorpusVersion(seller)
                && string.Equals(current.Generation, decision.SnapshotGeneration, StringComparison.Ordinal);
        }

        public static bool IsSnapshotReady(string seller)
        {
            var shop = ResolveShop(seller);
            if (shop == null) return false;
            Snapshot snapshot;
            return Snapshots.TryGetValue(shop.ShopKey, out snapshot) && snapshot != null
                && snapshot.CorpusVersion == KnowledgeEngineV2Repository.GetCorpusVersion(seller);
        }

        public static KnowledgeV2Settings GetSettingsView(string seller)
        {
            var settings = GetSettings(seller);
            return new KnowledgeV2Settings { Enabled = true, Mode = settings.Mode, DirectThreshold = settings.DirectThreshold, MinConfidence = settings.MinConfidence };
        }

        public static void SetSettings(string seller, bool enabled, string mode, double threshold, double minConfidence)
        {
            if (double.IsNaN(threshold) || double.IsInfinity(threshold)
                || double.IsNaN(minConfidence) || double.IsInfinity(minConfidence))
                throw new ArgumentOutOfRangeException(nameof(threshold), "知识库检索阈值必须是有限数字。");
            var shop = ResolveShopRequired(seller);
            lock (SettingsLocks.GetOrAdd(shop.ShopKey, _ => new object()))
            {
                var store = new ShopScopedSettingsStore(shop, Paths);
                var direct = Math.Max(0.70, Math.Min(0.96, threshold));
                var confidence = Math.Max(0.50, Math.Min(0.95, minConfidence));
                // Persist one complete setting revision before publishing the cache.
                var values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { KnowledgeEngineV2Constants.SettingsEnabled, "1" },
                    { KnowledgeEngineV2Constants.SettingsMode, KnowledgeEngineV2Constants.ModeProduction },
                    { KnowledgeEngineV2Constants.SettingsDirectThreshold, direct.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) },
                    { KnowledgeEngineV2Constants.SettingsMinConfidence, confidence.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) }
                };
                store.MergeValues(values, true);
                var persisted = store.ExportValues();
                foreach (var pair in values)
                {
                    string value;
                    if (!persisted.TryGetValue(pair.Key, out value)
                        || !string.Equals(value, pair.Value, StringComparison.Ordinal))
                    {
                        RuntimeSettings ignored;
                        SettingsCache.TryRemove(shop.ShopKey, out ignored);
                        throw new InvalidOperationException("Knowledge V2设置写入后校验失败。");
                    }
                }
                SettingsCache[shop.ShopKey] = new RuntimeSettings
                {
                    Enabled = true,
                    Mode = KnowledgeEngineV2Constants.ModeProduction,
                    DirectThreshold = double.Parse(values[KnowledgeEngineV2Constants.SettingsDirectThreshold], System.Globalization.CultureInfo.InvariantCulture),
                    MinConfidence = double.Parse(values[KnowledgeEngineV2Constants.SettingsMinConfidence], System.Globalization.CultureInfo.InvariantCulture),
                    ExpiresAt = DateTime.Now.AddMinutes(5)
                };
            }
        }

        internal static bool TryRestorePortableSettings(string seller, KnowledgeV2Settings before, KnowledgeV2Settings target)
        {
            if (before == null || target == null) return false;
            var shop = ResolveShopRequired(seller);
            lock (SettingsLocks.GetOrAdd(shop.ShopKey, _ => new object()))
            {
                RuntimeSettings ignored;
                SettingsCache.TryRemove(shop.ShopKey, out ignored);
                var current = GetSettingsView(seller);
                var direct = Math.Round(Math.Max(0.70, Math.Min(0.96, target.DirectThreshold)), 3);
                var confidence = Math.Round(Math.Max(0.50, Math.Min(0.95, target.MinConfidence)), 3);
                if (current.DirectThreshold == direct && current.MinConfidence == confidence) return true;
                if (current.DirectThreshold != before.DirectThreshold || current.MinConfidence != before.MinConfidence)
                    return false;
                SetSettings(seller, true, KnowledgeEngineV2Constants.ModeProduction, direct, confidence);
                return true;
            }
        }

        public static KnowledgeV2Decision Resolve(string seller, string buyer, string message)
        {
            return ResolveInternal(seller, buyer, message, false, string.Empty, string.Empty, false);
        }

        internal static KnowledgeV2Decision ResolveWithCommerceContext(
            string seller, string buyer, string message, CommerceContextSnapshot commerce)
        {
            return ResolveInternal(seller, buyer, message, false, string.Empty, string.Empty, false, commerce);
        }

        // Visual/OCR evidence enriches entity recall without becoming the buyer's question.
        // Intent, predicate, exact aliases, high-risk checks and working memory remain
        // derived only from the buyer's own message.
        public static KnowledgeV2Decision ResolveWithEvidence(
            string seller,
            string buyer,
            string message,
            string evidenceText,
            string evidenceSubject)
        {
            return ResolveInternal(seller, buyer, message, false, evidenceText, evidenceSubject, false);
        }

        // A ShopKey-scoped, operator-curated image match may complete a short visual
        // follow-up such as "你看看". It still requires an approved, normal-risk V2
        // answer and the existing score/conflict checks; OCR alone never enables this path.
        public static KnowledgeV2Decision ResolveWithCuratedImageKnowledgeEvidence(
            string seller,
            string buyer,
            string message,
            string evidenceText,
            string evidenceSubject,
            CommerceContextSnapshot commerce = null,
            string observedEvidenceText = null)
        {
            var hasCuratedImageKnowledge = !string.IsNullOrWhiteSpace(evidenceText)
                && evidenceText.IndexOf("【已命中本店人工维护的图片知识】", StringComparison.Ordinal) >= 0;
            return ResolveInternal(
                seller, buyer, message, false, evidenceText, evidenceSubject, hasCuratedImageKnowledge, commerce,
                observedEvidenceText ?? string.Empty);
        }

        // Simulator-specific safety evaluation: retain every knowledge, conflict,
        // risk and confidence check but ignore the LIVE delivery mode switches.
        // Never modify settings or authorize an outbound buyer message here.
        internal static KnowledgeV2Decision ResolveSandbox(string seller, string buyer, string message)
        {
            return ResolveInternal(seller, buyer, message, true, string.Empty, string.Empty, false);
        }

        private static KnowledgeV2Decision ResolveInternal(
            string seller,
            string buyer,
            string message,
            bool sandbox,
            string evidenceText,
            string evidenceSubject,
            bool curatedImageEvidence,
            CommerceContextSnapshot commerce = null,
            string observedEvidenceText = null)
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
            var hasEvidence = !string.IsNullOrWhiteSpace(evidenceText);
            if (IsMediaPlaceholder(message)) message = string.Empty;
            if (message.Length == 0 && !hasEvidence)
            {
                decision.Reason = "空消息或媒体消息不走文本知识引擎";
                decision.TotalMs = total.ElapsedMilliseconds;
                return decision;
            }

            var parseSw = Stopwatch.StartNew();
            var shop = ResolveShopRequired(seller);
            var memory = GetWorkingMemory(shop.ShopKey, seller, buyer);
            var query = KnowledgeEngineV2Semantics.Parse(message, memory);
            if (commerce != null && commerce.HasStructuredOrder)
                query.VerifiedCommerceScope = CommerceContextService.BuildPolicyScopeToken(commerce);
            // Persist only buyer-authored query semantics. OCR/menu labels must never
            // become the next turn's working-memory intent, predicate or subject.
            // An image-only message has no buyer-authored query to update memory with.
            // Preserve the previous subject instead of replacing it with an empty parse.
            if (!sandbox && message.Length > 0)
                UpdateWorkingMemory(shop.ShopKey, seller, buyer, query);
            if (hasEvidence)
            {
                var buyerMessageHasProductId = KnowledgeEngineV2Semantics.ExtractEntities(message)
                    .Any(x => (x ?? string.Empty).StartsWith("product:", StringComparison.OrdinalIgnoreCase));
                var evidenceEntities = KnowledgeEngineV2Semantics.ExtractEntities(evidenceText)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    // Prefer an explicitly labeled OCR product ID over generic OCR terms.
                    .OrderByDescending(x => x.StartsWith("product:", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                // Product identifiers may only originate in the current OCR text,
                // not in a reference screenshot, rule label or configured description.
                var actualProducts = KnowledgeEngineV2Semantics.ExtractEntities(observedEvidenceText ?? evidenceText)
                    .Where(x => x.StartsWith("product:", StringComparison.OrdinalIgnoreCase)).ToList();
                evidenceEntities = actualProducts.Concat(evidenceEntities.Where(x => !x.StartsWith("product:", StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var evidenceHasProductId = actualProducts.Count > 0;
                if (actualProducts.Count > 10)
                    query.ProductEvidenceConflict = true;
                var buyerProducts = KnowledgeEngineV2Semantics.ExtractEntities(message)
                    .Where(x => x.StartsWith("product:", StringComparison.OrdinalIgnoreCase)).ToList();
                if (buyerMessageHasProductId && evidenceHasProductId &&
                    !new HashSet<string>(buyerProducts, StringComparer.OrdinalIgnoreCase).SetEquals(actualProducts))
                    query.ProductEvidenceConflict = true;
                if (buyerMessageHasProductId)
                {
                    // The current buyer-authored product ID is authoritative over OCR.
                    evidenceEntities = evidenceEntities
                        .Where(x => !x.StartsWith("product:", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
                else if (evidenceHasProductId)
                {
                    // A labeled product ID in the current image supersedes a stale product
                    // inherited from working memory; keep memory only when OCR has no product ID.
                    query.Entities = (query.Entities ?? new List<string>())
                        .Where(x => !(x ?? string.Empty).StartsWith("product:", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
                var configuredSubject = (evidenceSubject ?? string.Empty).Trim();
                if (configuredSubject.Contains("{") || configuredSubject.Contains("}"))
                    configuredSubject = string.Empty;
                if (configuredSubject.Length >= 2 && !configuredSubject.StartsWith("product:", StringComparison.OrdinalIgnoreCase))
                    evidenceEntities.Insert(0, configuredSubject);

                if (!buyerMessageHasProductId && evidenceHasProductId)
                {
                    // The image is the freshest product evidence. Put its explicitly labeled
                    // product ID before other OCR and inherited query entities so the bounded
                    // ten-entity query cannot silently drop the product key.
                    var evidenceProductIds = evidenceEntities
                        .Where(x => x.StartsWith("product:", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var evidenceNonProductEntities = evidenceEntities
                        .Where(x => !x.StartsWith("product:", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var queryEntities = (query.Entities ?? new List<string>())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();
                    query.Entities = evidenceProductIds
                        .Concat(evidenceNonProductEntities)
                        .Concat(queryEntities)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(10)
                        .ToList();
                }
                else
                {
                    query.Entities = (query.Entities ?? new List<string>())
                        .Concat(evidenceEntities)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(10)
                        .ToList();
                }
                if (configuredSubject.Length >= 2)
                    query.Subject = string.IsNullOrWhiteSpace(query.Subject)
                        ? configuredSubject
                        : configuredSubject + "/" + query.Subject;
            }
            if (commerce != null && commerce.HasStructuredOrder && !string.IsNullOrWhiteSpace(commerce.ItemId))
            {
                var authoritative = "product:" + commerce.ItemId.Trim();
                var queryProducts = (query.Entities ?? new List<string>()).Where(x => x.StartsWith("product:", StringComparison.OrdinalIgnoreCase)).ToList();
                if (queryProducts.Any(x => !string.Equals(x, authoritative, StringComparison.OrdinalIgnoreCase)))
                    query.ProductEvidenceConflict = true;
                else if (!queryProducts.Contains(authoritative, StringComparer.OrdinalIgnoreCase))
                    query.Entities.Insert(0, authoritative);
            }
            decision.Query = query;
            decision.ParseMs = parseSw.ElapsedMilliseconds;
            if (query.ProductEvidenceConflict)
            {
                decision.HasConflict = true;
                decision.Reason = "买家问题、当前图片或已核验订单商品不一致，禁止自动采用知识答案";
                decision.TotalMs = total.ElapsedMilliseconds;
                return decision;
            }

            var snapshot = GetSnapshot(seller);
            decision.SnapshotGeneration = snapshot.Generation;
            var recallSw = Stopwatch.StartNew();
            var candidates = Recall(snapshot, query);
            decision.CandidateCount = candidates.Count;
            decision.HasConflict = query.RecallBudgetAmbiguous;
            decision.RecallMs = recallSw.ElapsedMilliseconds;

            var rankSw = Stopwatch.StartNew();
            var rankedMatches = candidates.Select(i => Score(seller, snapshot.Records[i], query)).Where(x => x != null && x.Score >= 0.30).OrderByDescending(x => x.Score).ThenByDescending(x => x.ConfidenceScore).ToList();
            var allProductionMatches = rankedMatches.Where(x => IsEligibleProductionMatch(x, query)).ToList();
            var productionMatches = allProductionMatches.Take(5).ToList();
            // Operator-reviewed direct replies take precedence at the exact question
            // boundary; confidence observations never delay an explicit manual save.
            // Other semantically similar questions continue through normal safety ranking.
            var manualExactMatches = allProductionMatches
                .Where(x => x != null
                    && x.Record != null
                    && string.Equals((x.Record.SourceType ?? "").Trim(),
                        "manual_reviewed_direct", StringComparison.OrdinalIgnoreCase)
                    && (x.Record.Aliases ?? new List<string>()).Concat(new[] { x.Record.Title })
                        .Any(a => ExactQuestionMatches(a, query.Original)))
                .ToList();
            var manualExactConflict = manualExactMatches.Count > 1;
            var manualExact = manualExactConflict ? null : manualExactMatches.FirstOrDefault();
            var manualSimilar = manualExact == null && !manualExactConflict
                ? productionMatches
                    .Where(x => IsStrongManualReviewedSimilarMatch(x, query)
                        || IsStrongAutoPublishedHumanEvidenceSimilarMatch(x, query))
                    .OrderByDescending(x => x.AliasScore)
                    .FirstOrDefault()
                : null;
            var best = manualExact ?? manualSimilar ?? productionMatches.FirstOrDefault();
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
            var second = productionMatches.FirstOrDefault(x => x != null
                && !object.ReferenceEquals(x, best));
            // An explicit human correction of this exact standalone question wins over
            // older generic answers; unrelated queries still use conflict rejection.
            decision.HasConflict = decision.HasConflict || manualExactConflict || (manualExact == null
                && allProductionMatches.Any(x => !ReferenceEquals(x, best) && HasConflict(best, x)))
                || allProductionMatches.Any(x => !ReferenceEquals(x, best) && HasApplicableScopedConflict(best, x, query));
            var margin = second == null ? best.Score : best.Score - second.Score;
            var threshold = settings.DirectThreshold;
            var minConfidence = settings.MinConfidence;
            var highRisk = KnowledgeEngineV2Semantics.IsHighRisk(message) || KnowledgeEngineV2Semantics.IsHighRisk(best.Record.Answer)
                || string.Equals(best.Record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase)
                || string.Equals(best.Record.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase);
            var sameFactSecond = second != null && string.Equals(KnowledgeEngineV2Semantics.FactKey(best.Record), KnowledgeEngineV2Semantics.FactKey(second.Record), StringComparison.Ordinal);
            var safeConsensus = HasSafeDirectConsensus(best, productionMatches, query, threshold, minConfidence, highRisk);
            var effectiveMargin = (sameFactSecond && AnswersEquivalent(best.Record.Answer, second.Record.Answer)) || safeConsensus ? Math.Max(margin, 0.12) : margin;
            var standaloneSafe = IsSafeStandaloneLocalDirect(query, message)
                || (curatedImageEvidence && IsSafeCuratedImageFollowUp(query, message));

            var approvedExact = manualExact != null && object.ReferenceEquals(best, manualExact);
            var approvedSimilar = manualSimilar != null && object.ReferenceEquals(best, manualSimilar);
            decision.CanDirectReply = !decision.HasConflict
                && !highRisk
                && standaloneSafe
                && best.Record.Enabled
                && best.ConfidenceScore >= minConfidence
                && (approvedExact || (best.Score >= threshold
                    && (best.AliasScore >= 0.94 || best.PredicateScore >= 0.99)
                    && (effectiveMargin >= 0.08 || best.AliasScore >= 0.98)));
            decision.SelectedMatch = decision.CanDirectReply ? best : null;
            decision.Answer = decision.CanDirectReply ? (best.Record.Answer ?? string.Empty).Trim() : string.Empty;
            decision.Reason = decision.CanDirectReply
                ? (approvedExact ? "人工审核精确问法直答即时生效：score="
                    : (approvedSimilar ? "人工审核相似问法通过同意图/同谓词/主体校验直答：score="
                    : "V2结构化知识已通过直答安全检查：score="))
                    + best.Score.ToString("0.00") + ", predicate=" + query.Predicate + ", candidates=" + candidates.Count + (safeConsensus ? ", consensus=safe" : string.Empty)
                : (!standaloneSafe ? "当前消息依赖Working Memory或属于状态/投诉/人工等控制语义，禁止V2本地直答，继续完整上下文/AI/人工链路" : BuildRejectReason(best, decision, threshold, minConfidence, effectiveMargin, highRisk));
            Finish(decision, total, decideSw);
            return decision;
        }

        internal static bool IsEligibleProductionMatch(KnowledgeV2Match match, KnowledgeV2Query query)
        {
            if (!IsApprovedProductionMatch(match) || query == null || query.ProductEvidenceConflict) return false;
            // Numeric qualifiers in a buyer question must be backed by a saved
            // wording, not by a generic predicate score or punctuation folding.
            // Product IDs independently proven in the structured scope are excluded.
            if (!HasCompatibleQuestionNumbers(match.Record, query)) return false;
            string reason;
            if (!KnowledgeEngineV2Semantics.IsApplicable(match.Record, query, out reason)) return false;
            if (match == null || match.Record == null
                || !KnowledgeV2AuthorityPolicy.IsAutoPublishedHumanEvidenceSource(match.Record.SourceType))
                return true;

            // Auto-published FAQ facts may answer an exact saved title/alias or a
            // rigorously validated semantic paraphrase. Do not let the generic
            // production fallback bypass the stronger human-evidence retrieval gate.
            var exactAlias = (match.Record.Aliases ?? new List<string>())
                .Concat(new[] { match.Record.Title })
                .Any(alias => ExactQuestionMatches(alias, query.Original));
            return exactAlias || IsStrongAutoPublishedHumanEvidenceSimilarMatch(match, query);
        }

        // Context/conditional paths must not turn the debugging display list
        // into an alternate source of authority.
        internal static bool HasContextConflict(IEnumerable<KnowledgeV2Match> matches)
        {
            return HasAnswerDisagreement(matches.Select(x => x.Record).ToList());
        }

        internal static bool IsEligibleContextMatch(KnowledgeV2Match match, KnowledgeV2Query query)
        {
            return IsEligibleProductionMatch(match, query)
                && !string.Equals(match.Record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(match.Record.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase)
                && !KnowledgeEngineV2Semantics.IsHighRisk(match.Record.Answer)
                && !(match.Record.RequiredContext ?? new List<string>()).Any(ConditionalBusinessAdviceService.IsEvidenceRequirement);
        }

        private static bool IsStrongManualReviewedSimilarMatch(
            KnowledgeV2Match match,
            KnowledgeV2Query query)
        {
            if (match == null || match.Record == null || query == null
                || query.ContextDependent || !string.IsNullOrWhiteSpace(query.WorkingMemoryReason))
                return false;
            if (!string.Equals((match.Record.SourceType ?? string.Empty).Trim(),
                    "manual_reviewed_direct", StringComparison.OrdinalIgnoreCase))
                return false;
            if (match.Score < 0.82 || match.AliasScore < 0.80
                || match.PredicateScore < 0.99 || match.IntentScore < 0.99
                || match.ConfidenceScore < 0.94)
                return false;
            if (KnowledgeEngineV2Semantics.IsHighRisk(query.Original)
                || KnowledgeEngineV2Semantics.IsHighRisk(match.Record.Answer))
                return false;

            var subjectSimilarity = KnowledgeEngineV2Semantics.TextSimilarity(
                query.Subject ?? string.Empty, match.Record.Subject ?? string.Empty);
            return match.EntityScore >= 0.66 || subjectSimilarity >= 0.82;
        }

        private static bool IsStrongAutoPublishedHumanEvidenceSimilarMatch(
            KnowledgeV2Match match,
            KnowledgeV2Query query)
        {
            if (match == null || match.Record == null || query == null
                || query.ContextDependent || !string.IsNullOrWhiteSpace(query.WorkingMemoryReason))
                return false;
            if (!KnowledgeV2AuthorityPolicy.IsAutoPublishedHumanEvidenceSource(match.Record.SourceType))
                return false;
            // The answer is backed by an exact live buyer message and a subsequent
            // non-Bot human reply. Permit a carefully bounded semantic match without
            // pretending the AI confidence score alone is proof.
            if (match.Score < 0.82 || match.AliasScore < 0.80
                || match.PredicateScore < 0.99 || match.IntentScore < 0.99
                || match.ConfidenceScore < 0.90)
                return false;
            if (KnowledgeEngineV2Semantics.IsHighRisk(query.Original)
                || KnowledgeEngineV2Semantics.IsHighRisk(match.Record.Answer))
                return false;

            var subjectSimilarity = KnowledgeEngineV2Semantics.TextSimilarity(
                query.Subject ?? string.Empty, match.Record.Subject ?? string.Empty);
            return match.EntityScore >= 0.66 || subjectSimilarity >= 0.82;
        }

        private static bool IsSafeStandaloneLocalDirect(KnowledgeV2Query query, string message)
        {
            if (query == null) return false;
            var compact = KnowledgeEngineV2Semantics.Compact(message);
            if (string.IsNullOrWhiteSpace(compact)) return false;

            // A local direct answer must be grounded by the current buyer message itself. Working
            // Memory may help AI/contextual reasoning, but it must never turn “充没/1?/不满意/人工”
            // into the previous account_binding fact and auto-send it as a fresh answer.
            if (query.ContextDependent || !string.IsNullOrWhiteSpace(query.WorkingMemoryReason)) return false;
            if (Regex.IsMatch(compact, @"^\d{5,20}$")) return false;
            if (Regex.IsMatch(compact, @"^[?？!！.。]+$")) return false;
            if (Regex.IsMatch(compact, @"(人工|客服|转人工|投诉|不满意|有病|垃圾|骗子|差评|举报|生气|退款|退货|充没|充上没|到账没|付款没|发货没)")) return false;
            return true;
        }

        private static bool IsSafeCuratedImageFollowUp(KnowledgeV2Query query, string message)
        {
            if (query == null || !query.ContextDependent) return false;
            var compact = KnowledgeEngineV2Semantics.Compact(message);
            if (compact.Length == 0 || compact.Length > 16
                || KnowledgeEngineV2Semantics.IsHighRisk(message))
                return false;
            if (Regex.IsMatch(compact,
                @"(人工|客服|转人工|投诉|不满意|有病|垃圾|骗子|差评|举报|生气|退款|退货|充没|充上没|到账没|付款没|发货没)"))
                return false;

            // Only short image-follow-up utterances are allowed to reuse the current
            // curated image as evidence. Arbitrary context-dependent text remains fail-closed.
            return Regex.IsMatch(compact,
                @"^(?:图片)?(?:你)?(?:看看|看下|看一下|看一看|帮我看看|帮看看|你帮我看看|这个|这种|这类|这张|这页|这个支持吗|这种支持吗|这类能用吗|这个能用吗|支持吗|能用吗|可以吗|对吗)$");
        }

        private static bool HasSafeDirectConsensus(KnowledgeV2Match best, List<KnowledgeV2Match> productionMatches, KnowledgeV2Query query, double threshold, double minConfidence, bool highRisk)
        {
            if (best == null || best.Record == null || query == null || highRisk || productionMatches == null || productionMatches.Count < 2 || best.Score < threshold || best.ConfidenceScore < minConfidence) return false;
            if (query.ContextDependent && (query.Entities == null || query.Entities.Count < 1) && string.IsNullOrWhiteSpace(query.Subject)) return false;
            var close = productionMatches.Where(x => x != null && x.Record != null).Where(x => best.Score - x.Score < 0.08).Where(x => x.Score >= threshold && x.ConfidenceScore >= minConfidence).Take(4).ToList();
            if (close.Count < 2) return false;
            foreach (var candidate in close.Where(x => !ReferenceEquals(x, best)))
            {
                if (!candidate.Record.Enabled || string.Equals(candidate.Record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.Record.RiskLevel, "critical", StringComparison.OrdinalIgnoreCase)
                    || KnowledgeEngineV2Semantics.IsHighRisk(candidate.Record.Answer)) return false;
                if (!SameConsensusScope(best.Record, candidate.Record)) return false;
                if (!AnswersEquivalent(best.Record.Answer, candidate.Record.Answer)) return false;
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
            const string negativePattern = "不支持|不能使用|不能用|无法使用|无法用|不可以|不可使用|不可用|不适用|不允许|不能|无法|禁止|不提供|不包含|未开通|已失效|已过期|无需|不需要";
            var negative = Regex.IsMatch(value, negativePattern);
            var positive = Regex.IsMatch(Regex.Replace(value, negativePattern, string.Empty),
                "支持|可以|适用于|能用|可用|允许|提供|包含|已开通|有效|需要|必须");
            if (negative && !positive) return -1;
            if (positive && !negative) return 1;
            return 0;
        }

        private static bool IsApprovedProductionMatch(KnowledgeV2Match match)
        {
            return KnowledgeV2AuthorityPolicy.IsProductionApproved(match == null ? null : match.Record);
        }

        // Context extraction reads the same committed in-memory snapshot as retrieval;
        // the management API below still reads the full repository including tombstones.
        internal static IEnumerable<KnowledgeV2Record> GetProductionContextRecords(string seller)
        {
            return GetSnapshot(seller).Records
                .Where(KnowledgeV2AuthorityPolicy.IsProductionApproved)
                .Where(x => !ShopReceptionPolicy.IsQuarantined(seller, x.Id, x.Title, x.Answer));
        }

        public static List<KnowledgeV2Record> GetRecords(string seller)
        {
            return KnowledgeEngineV2Repository.LoadAll(seller).Where(x => x != null).Select(Clone).Select(KnowledgeV2AuthorityPolicy.NormalizeForRead).ToList();
        }

        public static List<KnowledgeV2Conflict> GetConflicts(string seller)
        {
            return GetConflictsForRecords(KnowledgeEngineV2Repository.LoadAll(seller));
        }

        internal static List<KnowledgeV2Conflict> GetConflictsForRecords(IEnumerable<KnowledgeV2Record> records)
        {
            var approved = records.Where(KnowledgeV2AuthorityPolicy.IsProductionApproved).ToList();
            var conflicts = approved.GroupBy(KnowledgeEngineV2Semantics.FactKey)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1)
                .Select(g => new KnowledgeV2Conflict { FactKey = g.Key, Subject = g.First().Subject, Predicate = g.First().Predicate,
                    Records = FindConflictMembers(g) })
                .Where(x => x.Records.Count > 1).ToList();

            // Different applicability signatures can conflict for a subset of
            // products/conditions. Surface these for scope editing, not the
            // destructive "keep one, disable the rest" workflow.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in approved.Where(x => !string.IsNullOrWhiteSpace(x.Title))
                .GroupBy(x => KnowledgeEngineV2Semantics.Compact(x.Title) + "|" + string.Join(",", NumericTokens(x.Title))))
            {
                var items = group.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
                if (items.Count < 2) continue;
                var factKeys = items.ToDictionary(x => x.Id, KnowledgeEngineV2Semantics.FactKey, StringComparer.Ordinal);
                if (factKeys.Values.Distinct(StringComparer.Ordinal).Count() < 2) continue;
                var answerKeys = items.Select(x => KnowledgeEngineV2Semantics.Compact(x.Answer)
                    + "|" + string.Join(",", NumericTokens(x.Answer))).ToArray();
                if (answerKeys.Distinct(StringComparer.Ordinal).Count() < 2) continue;
                // Disjoint products cannot contradict one another. Index by
                // structured product scope instead of forming the Cartesian
                // product of every pair of different answers in a title bucket.
                var products = items.Select(x => KnowledgeEngineV2Semantics.NormalizeProductIds(x.ProductIds)).ToArray();
                var productIndex = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                var unbound = new List<int>();
                for (var i = 0; i < items.Count; i++)
                {
                    if (products[i].Count == 0) unbound.Add(i);
                    foreach (var product in products[i])
                    {
                        List<int> bucket;
                        if (!productIndex.TryGetValue(product, out bucket))
                            productIndex[product] = bucket = new List<int>();
                        bucket.Add(i);
                    }
                }
                var compatibility = new Dictionary<Tuple<string, string>, bool>();
                for (var i = 0; i < items.Count; i++)
                {
                    IEnumerable<int> candidates;
                    if (products[i].Count == 0) candidates = Enumerable.Range(i + 1, items.Count - i - 1);
                    else
                    {
                        var overlapping = new HashSet<int>(unbound.Where(j => j > i));
                        foreach (var product in products[i])
                            overlapping.UnionWith(productIndex[product].Where(j => j > i));
                        candidates = overlapping.OrderBy(j => j);
                    }
                    var left = items[i];
                    foreach (var j in candidates)
                    {
                        var right = items[j];
                        if (factKeys[left.Id] == factKeys[right.Id]
                            || answerKeys[i] == answerKeys[j]
                            || !KnowledgeEngineV2Semantics.ProductScopesOverlap(left, right)) continue;
                        var pair = Tuple.Create(left.Answer ?? string.Empty, right.Answer ?? string.Empty);
                        bool compatible;
                        if (!compatibility.TryGetValue(pair, out compatible))
                        {
                            compatible = AnswerFactsCompatible(left.Answer, right.Answer);
                            if (compatibility.Count < 2048) compatibility[pair] = compatible;
                        }
                        if (compatible) continue;
                        var ids = new[] { left.Id, right.Id }.OrderBy(x => x, StringComparer.Ordinal);
                        var key = "scope-review:" + string.Join("|", ids);
                        if (!seen.Add(key)) continue;
                        conflicts.Add(new KnowledgeV2Conflict { FactKey = key, Subject = left.Subject,
                            Predicate = left.Predicate, RequiresScopeReview = true,
                            Records = new List<KnowledgeV2Record> { left, right } });
                        // Bound advisory output; runtime applicability still checks
                        // all recalled eligible records independently.
                        if (seen.Count >= 200) return conflicts;
                    }
                }
            }
            return conflicts;
        }

        private static List<KnowledgeV2Record> FindConflictMembers(IEnumerable<KnowledgeV2Record> records)
        {
            // Duplicate answer text alone cannot contradict itself. Collapse
            // identical title/alias variants inside each answer group before
            // pairwise comparison, then map the result back to every record.
            var groups = records.GroupBy(x => x.Answer ?? string.Empty, StringComparer.Ordinal)
                .Select(g => g.GroupBy(x => new
                {
                    Title = x.Title ?? string.Empty,
                    Aliases = string.Concat((x.Aliases ?? new List<string>()).Select(a =>
                        (a ?? string.Empty).Length + ":" + (a ?? string.Empty)))
                }).Select(v => v.ToList()).ToList()).ToList();
            var conflicting = new HashSet<KnowledgeV2Record>();
            for (var i = 0; i < groups.Count; i++)
            for (var j = i + 1; j < groups.Count; j++)
            foreach (var left in groups[i])
            foreach (var right in groups[j])
                if (IsMeaningfulConflictPair(left[0], right[0]))
                {
                    conflicting.UnionWith(left);
                    conflicting.UnionWith(right);
                }
            return records.Where(conflicting.Contains).ToList();
        }

        internal static bool TryResolveConflict(string seller, string expectedFingerprint, string factKey, string selectedId, bool asCandidate, string operationReceipt = null)
        {
            var records = KnowledgeEngineV2Repository.LoadAll(seller);
            if (string.IsNullOrWhiteSpace(expectedFingerprint) ||
                KnowledgeEngineV2Repository.ComputeCloudSyncFingerprint(records) != expectedFingerprint) return false;
            var conflict = GetConflictsForRecords(records).SingleOrDefault(x => x.FactKey == factKey);
            if (conflict != null && conflict.RequiresScopeReview) return false;
            var selected = conflict == null ? null : conflict.Records.FirstOrDefault(x => x.Id == selectedId);
            if (selected == null) return false;
            var conflicting = conflict.Records.Where(x => x.Id != selectedId && IsMeaningfulConflictPair(selected, x)).ToList();
            if (conflicting.Count == 0) return false;
            // A fact-key bucket can contain unrelated procedures or disjoint
            // conflicts. Preserve every record that does not contradict the selection.
            foreach (var record in conflicting)
            {
                record.Enabled = false;
                record.Status = asCandidate ? "candidate" : "disabled";
                if (asCandidate) record.Type = "learning_candidate";
                record.UpdatedAt = DateTime.Now;
            }
            // One CAS transaction for the entire group; never apply a partial resolution.
            return KnowledgeEngineV2Repository.TryReplaceAllIfUnchanged(seller, expectedFingerprint, records, null, operationReceipt);
        }

        public static KnowledgeV2Stats GetStats(string seller)
        {
            var snapshot = GetSnapshot(seller);
            var all = KnowledgeEngineV2Repository.LoadAll(seller)
                .Where(x => x != null && !string.Equals(x.Status, "deleted", StringComparison.OrdinalIgnoreCase))
                .Select(KnowledgeV2AuthorityPolicy.NormalizeForRead).ToList();
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
            lock (BuildLocks.GetOrAdd(shop.ShopKey, _ => new object()))
            {
                Snapshot ignored;
                Snapshots.TryRemove(shop.ShopKey, out ignored);
            }
        }

        public static void Warm(string seller) { GetSnapshot(seller); }

        public static void PromoteCandidate(string seller, string id)
        {
            var record = KnowledgeEngineV2Repository.LoadCommittedRecord(seller, id);
            if (record != null)
                TryPromoteCandidate(seller, id, KnowledgeEngineV2Repository.EditorRecordFingerprint(record));
        }

        internal static bool TryPromoteCandidate(string seller, string id, string expectedFingerprint)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(expectedFingerprint)) return false;
            var record = KnowledgeEngineV2Repository.LoadCommittedRecord(seller, id);
            if (record == null || !string.Equals(record.Status, "candidate", StringComparison.OrdinalIgnoreCase)
                || !KnowledgeV2AuthorityPolicy.IsCandidate(record)
                || !string.Equals(KnowledgeEngineV2Repository.EditorRecordFingerprint(record), expectedFingerprint, StringComparison.Ordinal))
                return false;
            // An explicit operator approval is new provenance. Retain the source ID,
            // scope, conditions and risk rather than treating the candidate as an
            // unrestricted FAQ or reviving a disabled/deleted row.
            record.SourceType = "manual_confirmed";
            record.Enabled = true;
            record.LastVerifiedAt = DateTime.Now;
            KnowledgeV2AuthorityPolicy.Promote(record);
            return KnowledgeEngineV2Repository.TrySaveEditorRecord(seller, record, expectedFingerprint);
        }
    }
}