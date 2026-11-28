using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib.Db.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Bot.Knowledge
{
    internal sealed class KnowledgeV2FeedbackEventRow
    {
        [PrimaryKey]
        public string Id { get; set; }
        public string KnowledgeId { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string EventType { get; set; }
        public string Question { get; set; }
        public string Answer { get; set; }
        public string Evidence { get; set; }
        public long CreatedAtTicks { get; set; }
    }

    internal sealed class KnowledgeV2QualityItem
    {
        public string KnowledgeId { get; set; }
        public string Title { get; set; }
        public string Type { get; set; }
        public int UseCount { get; set; }
        public int AcceptedCount { get; set; }
        public int CorrectionCount { get; set; }
        public int WithdrawCount { get; set; }
        public int SendFailureCount { get; set; }
        public double CorrectionRate { get; set; }
        public double QualityScore { get; set; }
        public DateTime LastUsedAt { get; set; }
        public string HealthStatus { get; set; }
        public string LastEvidence { get; set; }

        public string CorrectionRateText { get { return (CorrectionRate * 100).ToString("0.0") + "%"; } }
        public string QualityText { get { return (QualityScore * 100).ToString("0") + "%"; } }
        public string LastUsedAtText { get { return LastUsedAt == DateTime.MinValue ? "-" : LastUsedAt.ToString("MM-dd HH:mm:ss"); } }
    }

    internal static class KnowledgeEngineV2FeedbackService
    {
        private sealed class StoreState
        {
            public readonly object Sync = new object();
            public SQLiteHelper Db;
            public string Path;
        }

        private sealed class PendingReply
        {
            public readonly object Sync = new object();
            public string ShopKey;
            public string Seller;
            public string Buyer;
            public string KnowledgeId;
            public string Question;
            public string Answer;
            public DateTime SentAt;
            public bool NegativeBuyerReaction;
            public bool BuyerFollowup;
            public bool WithdrawalRecorded;
        }

        private sealed class Aggregate
        {
            public int Sent;
            public int Accepted;
            public int Correction;
            public int Withdraw;
            public int SendFailed;
            public DateTime LastUsedAt;
            public DateTime LastEvidenceAt;
            public string LastEvidence;
        }

        private sealed class AggregateCache
        {
            public ConcurrentDictionary<string, Aggregate> ByKnowledgeId;
        }

        private static readonly IShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ConcurrentDictionary<string, StoreState> Stores =
            new ConcurrentDictionary<string, StoreState>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, PendingReply> Pending =
            new ConcurrentDictionary<string, PendingReply>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, AggregateCache> AggregateCaches =
            new ConcurrentDictionary<string, AggregateCache>(StringComparer.Ordinal);
        private static int _cleanupCounter;

        public static void Warm(string seller)
        {
            GetAggregates(seller);
        }

        public static void RecordDirectSend(string seller, string buyer, string knowledgeId,
            string question, string answer, bool success, string failureReason)
        {
            seller = Clean(seller);
            buyer = Clean(buyer);
            knowledgeId = Clean(knowledgeId);
            if (seller.Length == 0 || buyer.Length == 0 || knowledgeId.Length == 0) return;

            var shop = ResolveShopRequired(seller);
            AppendEvent(seller, buyer, knowledgeId, success ? "sent" : "send_failed",
                question, answer, success ? "direct_local_reply" : Clean(failureReason));
            if (!success) return;

            Pending[PendingKey(shop.ShopKey, buyer)] = new PendingReply
            {
                ShopKey = shop.ShopKey,
                Seller = seller,
                Buyer = buyer,
                KnowledgeId = knowledgeId,
                Question = question ?? string.Empty,
                Answer = answer ?? string.Empty,
                SentAt = DateTime.Now
            };
            CleanupPending();
        }

        public static void ObserveBuyerMessage(string seller, string buyer, string text)
        {
            PendingReply pending;
            if (!TryGetPending(seller, buyer, out pending)) return;
            text = Clean(text);
            if (text.Length == 0) return;

            if (IsPositiveAcknowledgement(text))
            {
                if (!RemovePending(pending)) return;
                AppendEvent(pending.Seller, pending.Buyer, pending.KnowledgeId, "accepted",
                    pending.Question, pending.Answer, "buyer_positive:" + Truncate(text, 100));
                return;
            }

            var negative = IsNegativeReaction(text);
            lock (pending.Sync)
            {
                pending.BuyerFollowup = true;
                if (negative) pending.NegativeBuyerReaction = true;
            }
            if (negative)
            {
                AppendEvent(pending.Seller, pending.Buyer, pending.KnowledgeId, "buyer_negative",
                    pending.Question, pending.Answer, Truncate(text, 160));
            }
        }

        public static void ObserveSellerMessage(string seller, string buyer, string text)
        {
            PendingReply pending;
            if (!TryGetPending(seller, buyer, out pending)) return;
            text = Clean(text);
            if (text.Length == 0) return;

            var age = DateTime.Now - pending.SentAt;
            if (age > TimeSpan.FromMinutes(3))
            {
                RemovePending(pending);
                return;
            }

            var similarity = KnowledgeEngineV2Semantics.TextSimilarity(pending.Answer, text);
            if (similarity >= 0.92 && age <= TimeSpan.FromSeconds(20))
            {
                // Normally the seller-side echo of the Bot message, not human approval.
                return;
            }
            if (similarity >= 0.92)
            {
                if (!RemovePending(pending)) return;
                AppendEvent(pending.Seller, pending.Buyer, pending.KnowledgeId, "accepted",
                    pending.Question, pending.Answer, "manual_same_answer");
                return;
            }

            bool negative;
            lock (pending.Sync) negative = pending.NegativeBuyerReaction || pending.WithdrawalRecorded;
            if (negative && similarity < 0.78)
            {
                if (!RemovePending(pending)) return;
                AppendEvent(pending.Seller, pending.Buyer, pending.KnowledgeId, "correction",
                    pending.Question, pending.Answer, "manual_reply:" + Truncate(text, 220));
            }
        }

        public static void ObserveWithdrawal(string seller, string buyer, string evidence)
        {
            PendingReply pending;
            if (!TryGetPending(seller, buyer, out pending)) return;
            lock (pending.Sync)
            {
                if (pending.WithdrawalRecorded) return;
                pending.WithdrawalRecorded = true;
                pending.NegativeBuyerReaction = true;
            }
            AppendEvent(pending.Seller, pending.Buyer, pending.KnowledgeId, "withdrawal",
                pending.Question, pending.Answer, Truncate(evidence, 180));
        }

        public static double GetQualityAdjustment(string seller, string knowledgeId)
        {
            knowledgeId = Clean(knowledgeId);
            if (knowledgeId.Length == 0) return 0;
            Aggregate aggregate;
            if (!GetAggregates(seller).TryGetValue(knowledgeId, out aggregate) || aggregate == null) return 0;
            int sent;
            int accepted;
            int correction;
            int withdraw;
            lock (aggregate)
            {
                sent = aggregate.Sent;
                accepted = aggregate.Accepted;
                correction = aggregate.Correction;
                withdraw = aggregate.Withdraw;
            }
            if (sent < 2) return 0;

            var uses = Math.Max(1, sent);
            var acceptedRatio = accepted / (double)uses;
            var correctionRatio = correction / (double)uses;
            var withdrawRatio = withdraw / (double)uses;
            // Transport send failures are intentionally excluded from knowledge-quality penalties.
            var adjustment = Math.Min(0.04, acceptedRatio * 0.04)
                - correctionRatio * 0.14
                - withdrawRatio * 0.18;
            if (correction + withdraw >= 2) adjustment -= 0.025;
            return Math.Max(-0.18, Math.Min(0.05, adjustment));
        }

        public static List<KnowledgeV2QualityItem> GetQualityItems(string seller)
        {
            var aggregates = GetAggregates(seller);
            var records = KnowledgeEngineV2Repository.LoadAll(seller)
                .Where(x => x != null && !string.Equals(x.Status, "deleted", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var result = new List<KnowledgeV2QualityItem>();
            foreach (var record in records)
            {
                Aggregate aggregate;
                aggregates.TryGetValue(record.Id ?? string.Empty, out aggregate);
                aggregate = aggregate ?? new Aggregate();
                int sent;
                int acceptedEvidence;
                int correctionEvidence;
                int withdrawEvidence;
                int sendFailed;
                DateTime lastUsed;
                string lastEvidence;
                lock (aggregate)
                {
                    sent = aggregate.Sent;
                    acceptedEvidence = aggregate.Accepted;
                    correctionEvidence = aggregate.Correction;
                    withdrawEvidence = aggregate.Withdraw;
                    sendFailed = aggregate.SendFailed;
                    lastUsed = aggregate.LastUsedAt;
                    lastEvidence = aggregate.LastEvidence;
                }
                var useCount = Math.Max(0, record.UseCount) + sent;
                var accepted = Math.Max(0, record.AcceptedCount) + acceptedEvidence;
                var correction = Math.Max(0, record.CorrectionCount) + correctionEvidence;
                var withdraw = Math.Max(0, record.WithdrawCount) + withdrawEvidence;
                var correctionRate = (correction + withdraw) / (double)Math.Max(1, useCount);
                var acceptedRatio = accepted / (double)Math.Max(1, useCount);
                var quality = Clamp(record.Confidence * 0.55
                    + record.Authority * 0.25
                    + 0.20
                    + Math.Min(0.08, acceptedRatio * 0.08)
                    - correction / (double)Math.Max(1, useCount) * 0.35
                    - withdraw / (double)Math.Max(1, useCount) * 0.45);
                var status = "健康";
                if (useCount >= 3 && (quality < 0.62 || correction + withdraw >= 2 || correctionRate >= 0.20)) status = "低质量";
                else if (useCount >= 2 && (quality < 0.76 || correction > 0 || withdraw > 0)) status = "观察";
                else if (useCount == 0) status = "未使用";

                result.Add(new KnowledgeV2QualityItem
                {
                    KnowledgeId = record.Id,
                    Title = record.Title,
                    Type = record.Type,
                    UseCount = useCount,
                    AcceptedCount = accepted,
                    CorrectionCount = correction,
                    WithdrawCount = withdraw,
                    SendFailureCount = sendFailed,
                    CorrectionRate = correctionRate,
                    QualityScore = quality,
                    LastUsedAt = lastUsed,
                    HealthStatus = status,
                    LastEvidence = lastEvidence ?? string.Empty
                });
            }
            return result
                .OrderBy(x => x.HealthStatus == "低质量" ? 0 : (x.HealthStatus == "观察" ? 1 : (x.HealthStatus == "未使用" ? 3 : 2)))
                .ThenBy(x => x.QualityScore)
                .ThenByDescending(x => x.UseCount)
                .ToList();
        }

        public static List<KnowledgeV2FeedbackEventRow> GetRecentEvents(string seller, string knowledgeId, int maxCount)
        {
            var state = GetState(ResolveShopRequired(seller));
            List<KnowledgeV2FeedbackEventRow> rows;
            lock (state.Sync) rows = state.Db.ReadRecords<KnowledgeV2FeedbackEventRow>(null);
            knowledgeId = Clean(knowledgeId);
            return rows.Where(x => x != null && (knowledgeId.Length == 0 || x.KnowledgeId == knowledgeId))
                .OrderByDescending(x => x.CreatedAtTicks)
                .Take(Math.Max(1, Math.Min(100, maxCount <= 0 ? 20 : maxCount)))
                .ToList();
        }

        private static void AppendEvent(string seller, string buyer, string knowledgeId,
            string eventType, string question, string answer, string evidence)
        {
            var shop = ResolveShopRequired(seller);
            var state = GetState(shop);
            var row = new KnowledgeV2FeedbackEventRow
            {
                Id = Guid.NewGuid().ToString("N"),
                KnowledgeId = Clean(knowledgeId),
                Seller = Clean(seller),
                Buyer = Clean(buyer),
                EventType = Clean(eventType).ToLowerInvariant(),
                Question = Truncate(question, 800),
                Answer = Truncate(answer, 1400),
                Evidence = Truncate(evidence, 500),
                CreatedAtTicks = DateTime.Now.Ticks
            };
            // Serialize writes with cold-cache construction and publication.
            lock (state.Sync)
            {
                state.Db.SaveOneRecord(row);
                AggregateCache cached;
                if (AggregateCaches.TryGetValue(shop.ShopKey, out cached) && cached != null && cached.ByKnowledgeId != null)
                    ApplyEventToAggregate(cached.ByKnowledgeId, row);
            }

            if (Interlocked.Increment(ref _cleanupCounter) % 200 == 0)
            {
                try
                {
                    var cutoff = DateTime.Now.AddDays(-180).Ticks;
                    lock (state.Sync)
                    {
                        state.Db.Execute("delete from KnowledgeV2FeedbackEventRow where CreatedAtTicks < ?", cutoff);
                        AggregateCache ignored;
                        AggregateCaches.TryRemove(shop.ShopKey, out ignored);
                    }
                }
                catch { }
            }
        }

        private static ConcurrentDictionary<string, Aggregate> GetAggregates(string seller)
        {
            var shop = ResolveShopRequired(seller);
            AggregateCache cached;
            if (AggregateCaches.TryGetValue(shop.ShopKey, out cached)
                && cached != null && cached.ByKnowledgeId != null)
                return cached.ByKnowledgeId;

            var state = GetState(shop);
            lock (state.Sync)
            {
                if (AggregateCaches.TryGetValue(shop.ShopKey, out cached)
                    && cached != null && cached.ByKnowledgeId != null)
                    return cached.ByKnowledgeId;
                var rows = state.Db.ReadRecords<KnowledgeV2FeedbackEventRow>(null);
                var result = new ConcurrentDictionary<string, Aggregate>(StringComparer.Ordinal);
                foreach (var row in rows.Where(x => x != null && !string.IsNullOrWhiteSpace(x.KnowledgeId)))
                    ApplyEventToAggregate(result, row);
                var next = new AggregateCache { ByKnowledgeId = result };
                AggregateCaches[shop.ShopKey] = next;
                return next.ByKnowledgeId;
            }
        }

        private static void ApplyEventToAggregate(ConcurrentDictionary<string, Aggregate> target, KnowledgeV2FeedbackEventRow row)
        {
            if (target == null || row == null || string.IsNullOrWhiteSpace(row.KnowledgeId)) return;
            var item = target.GetOrAdd(row.KnowledgeId, _ => new Aggregate());
            lock (item)
            {
                var type = (row.EventType ?? string.Empty).Trim().ToLowerInvariant();
                if (type == "sent") item.Sent++;
                else if (type == "accepted") item.Accepted++;
                else if (type == "correction") item.Correction++;
                else if (type == "withdrawal") item.Withdraw++;
                else if (type == "send_failed") item.SendFailed++;
                var at = SafeDate(row.CreatedAtTicks);
                if (type == "sent" && at > item.LastUsedAt) item.LastUsedAt = at;
                if (at >= item.LastEvidenceAt)
                {
                    item.LastEvidenceAt = at;
                    item.LastEvidence = type + (string.IsNullOrWhiteSpace(row.Evidence) ? string.Empty : "：" + row.Evidence);
                }
            }
        }

        private static StoreState GetState(ShopContext shop)
        {
            return Stores.GetOrAdd(shop.ShopKey, _ =>
            {
                var root = Paths.GetKnowledgeRoot(shop);
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                var path = Path.Combine(root, "knowledge-feedback-v2.db");
                return new StoreState
                {
                    Path = path,
                    Db = new SQLiteHelper(path, new List<Type> { typeof(KnowledgeV2FeedbackEventRow) })
                };
            });
        }

        private static ShopContext ResolveShopRequired(string seller)
        {
            var current = ShopSettingsScope.Current;
            var shop = current ?? ShopContextLocator.ResolveBySellerNick(Clean(seller));
            if (shop == null || !shop.HasStableSellerId || string.IsNullOrWhiteSpace(shop.ShopKey)
                || (!DirectOrderIdentityResolver.IdentityEquals(shop.SellerNick, seller)
                    && !DirectOrderIdentityResolver.IdentityEquals(shop.DisplayName, seller)))
                throw new InvalidOperationException("Knowledge V2 feedback无法确定当前店铺身份。");
            return shop;
        }

        private static bool TryGetPending(string seller, string buyer, out PendingReply pending)
        {
            pending = null;
            seller = Clean(seller);
            buyer = Clean(buyer);
            var shop = ResolveShopRequired(seller);
            if (!Pending.TryGetValue(PendingKey(shop.ShopKey, buyer), out pending) || pending == null)
            {
                var matches = Pending.Values.Where(x => x != null
                    && string.Equals(x.ShopKey, shop.ShopKey, StringComparison.Ordinal)
                    && string.Equals(x.Seller, seller, StringComparison.Ordinal)
                    && BuyerIdentityAliasService.AreEquivalent(seller, x.Buyer, buyer))
                    .Take(2).ToList();
                if (matches.Count != 1) return false;
                pending = matches[0];
            }
            if (pending.SentAt < DateTime.Now.AddMinutes(-3))
            {
                RemovePending(pending);
                pending = null;
                return false;
            }
            return true;
        }

        private static bool RemovePending(PendingReply pending)
        {
            if (pending == null) return false;
            // Compare-and-remove: an old acknowledgement must never remove a newer reply.
            return ((ICollection<KeyValuePair<string, PendingReply>>)Pending).Remove(
                new KeyValuePair<string, PendingReply>(PendingKey(pending.ShopKey, pending.Buyer), pending));
        }

        private static void CleanupPending()
        {
            if (Pending.Count < 200) return;
            var cutoff = DateTime.Now.AddMinutes(-5);
            foreach (var pair in Pending.Where(x => x.Value == null || x.Value.SentAt < cutoff).Take(100).ToList())
                ((ICollection<KeyValuePair<string, PendingReply>>)Pending).Remove(pair);
        }

        private static bool IsPositiveAcknowledgement(string value)
        {
            var compact = Compact(value);
            if (compact.Length > 16) return false;
            var tokens = new[] { "好", "好的", "可以", "行", "谢谢", "感谢", "明白", "明白了", "知道了", "收到", "嗯", "哦", "ok", "okay", "解决了" };
            return tokens.Any(x => string.Equals(compact, x, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsNegativeReaction(string value)
        {
            var compact = Compact(value);
            var tokens = new[] { "不是", "不对", "错了", "不行", "不能", "没用", "还是不对", "不支持", "不可以", "搞错", "答非所问" };
            return tokens.Any(x => compact.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string PendingKey(string shopKey, string buyer)
        {
            shopKey = Clean(shopKey);
            return shopKey.Length + ":" + shopKey + Clean(buyer);
        }

        private static string Compact(string value)
        {
            return new string(Clean(value).Where(ch => !char.IsWhiteSpace(ch) && !char.IsPunctuation(ch)).ToArray()).ToLowerInvariant();
        }

        private static string Clean(string value) { return (value ?? string.Empty).Trim(); }
        private static string Truncate(string value, int max)
        {
            value = Clean(value);
            return value.Length <= max ? value : value.Substring(0, max);
        }
        private static DateTime SafeDate(long ticks)
        {
            try { return ticks <= 0 ? DateTime.MinValue : new DateTime(ticks); }
            catch { return DateTime.MinValue; }
        }
        private static double Clamp(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Max(0, Math.Min(1, value)); }
    }
}
