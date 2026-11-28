using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib.Db.Sqlite;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
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
        public long CloudUtcTicks { get; set; }
    }

    internal sealed class KnowledgeV2FeedbackCursorRow
    {
        [PrimaryKey]
        public string Id { get; set; }
        public string Epoch { get; set; }
        public long Cursor { get; set; }
    }

    internal sealed class KnowledgeV2FeedbackAckRow
    {
        [PrimaryKey]
        public string Id { get; set; }
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

        public static void ObserveBuyerMessage(string seller, string buyer, string text, DateTime messageTime)
        {
            PendingReply pending;
            if (!TryGetPending(seller, buyer, out pending) || !IsAfterSend(pending, messageTime)) return;
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

        public static void ObserveSellerMessage(string seller, string buyer, string text, DateTime messageTime)
        {
            PendingReply pending;
            if (!TryGetPending(seller, buyer, out pending) || !IsAfterSend(pending, messageTime)) return;
            text = Clean(text);
            if (text.Length == 0) return;

            // An unmarked late echo is not proof of human approval either.
            if (text.EndsWith("[A]", StringComparison.OrdinalIgnoreCase)
                || text.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)) return;
            var age = messageTime - pending.SentAt;
            if (age > TimeSpan.FromMinutes(3))
            {
                RemovePending(pending);
                return;
            }

            var similarity = KnowledgeEngineV2Semantics.TextSimilarity(pending.Answer, text);
            // Identical seller text may be a delayed Bot echo at any age.
            // Only buyer acknowledgements can establish acceptance here.
            if (similarity >= 0.92 && KnowledgeEngineV2Service.AnswerFactsCompatible(pending.Answer, text)) return;

            bool negative;
            lock (pending.Sync) negative = pending.NegativeBuyerReaction || pending.WithdrawalRecorded;
            if (negative && (similarity < 0.78 || !KnowledgeEngineV2Service.AnswerFactsCompatible(pending.Answer, text)))
            {
                if (!RemovePending(pending)) return;
                AppendEvent(pending.Seller, pending.Buyer, pending.KnowledgeId, "correction",
                    pending.Question, pending.Answer, "manual_reply:" + Truncate(text, 220));
            }
        }

        public static void ObserveWithdrawal(string seller, string buyer, string evidence, DateTime messageTime)
        {
            PendingReply pending;
            if (!TryGetPending(seller, buyer, out pending) || !IsAfterSend(pending, messageTime)) return;
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
                CreatedAtTicks = DateTime.Now.Ticks,
                CloudUtcTicks = DateTime.UtcNow.Ticks
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
                        state.Db.Execute("delete from KnowledgeV2FeedbackAckRow where CreatedAtTicks < ?", cutoff);
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
                    Db = new SQLiteHelper(path, new List<Type> { typeof(KnowledgeV2FeedbackEventRow), typeof(KnowledgeV2FeedbackCursorRow), typeof(KnowledgeV2FeedbackAckRow) })
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

        private static bool IsAfterSend(PendingReply pending, DateTime messageTime)
        {
            return pending != null && messageTime != DateTime.MinValue
                && messageTime >= pending.SentAt
                && messageTime <= DateTime.Now.AddSeconds(5)
                && messageTime - pending.SentAt <= TimeSpan.FromMinutes(3);
        }

        private static string FeedbackAckKey(string scope, string epoch, string id)
        {
            return scope.Length + ":" + scope + ":" + epoch + ":" + id;
        }

        private static long FeedbackUtcTicks(KnowledgeV2FeedbackEventRow row)
        {
            return row.CloudUtcTicks > 0 ? row.CloudUtcTicks
                : DateTime.SpecifyKind(new DateTime(row.CreatedAtTicks), DateTimeKind.Local).ToUniversalTime().Ticks;
        }

        internal static JObject BuildCloudFeedbackRequest(string seller, string scope)
        {
            var state = GetState(ResolveShopRequired(seller));
            lock (state.Sync)
            {
                var cursor = state.Db.ReadOneRecord<KnowledgeV2FeedbackCursorRow>(x => x.Id == scope);
                var epoch = cursor == null ? string.Empty : cursor.Epoch ?? string.Empty;
                var acks = new HashSet<string>(state.Db.ReadRecords<KnowledgeV2FeedbackAckRow>(null).Select(x => x.Id), StringComparer.Ordinal);
                var cutoff = DateTime.Now.AddDays(-180).Ticks;
                var local = state.Db.ReadRecords<KnowledgeV2FeedbackEventRow>(null);
                var outgoing = local
                    .Where(x => x != null && x.CreatedAtTicks >= cutoff
                        && !acks.Contains(FeedbackAckKey(scope, epoch, x.Id)))
                    .OrderBy(x => x.CreatedAtTicks).ThenBy(x => x.Id, StringComparer.Ordinal).Take(200)
                    .Select(x => new JObject
                    {
                        ["Id"] = x.Id, ["KnowledgeId"] = x.KnowledgeId,
                        ["EventType"] = x.EventType, ["UtcTicks"] = FeedbackUtcTicks(x)
                    }).ToList();
                var utcCutoff = DateTime.UtcNow.AddDays(-180).Ticks;
                var retained = local.Where(x => x != null && FeedbackUtcTicks(x) >= utcCutoff)
                    .OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
                var canonical = retained.Select(x => string.Join("\n", new[]
                {
                    x.Id, Convert.ToBase64String(Encoding.UTF8.GetBytes(x.KnowledgeId ?? string.Empty)),
                    x.EventType, FeedbackUtcTicks(x).ToString(System.Globalization.CultureInfo.InvariantCulture)
                }));
                string digest;
                using (var sha = SHA256.Create())
                    digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", canonical))))
                        .Replace("-", string.Empty).ToLowerInvariant();
                return new JObject { ["epoch"] = epoch, ["cursor"] = cursor == null ? 0L : cursor.Cursor,
                    ["events"] = new JArray(outgoing), ["local_event_count"] = retained.Count,
                    ["local_event_digest"] = digest };
            }
        }

        internal static bool ApplyCloudFeedbackResponse(string seller, string scope, JObject request, JObject response)
        {
            var shop = ResolveShopRequired(seller);
            var epoch = (string)response["epoch"];
            var cursor = response["cursor"];
            var page = response["events"] as JArray;
            var acks = response["ack_ids"] as JArray;
            var hasMore = response["has_more"];
            Guid epochId;
            if (!Guid.TryParseExact(epoch, "N", out epochId) || epochId == Guid.Empty
                || cursor == null || cursor.Type != JTokenType.Integer || cursor.Value<long>() < 0
                || page == null || page.Count > 200 || acks == null || hasMore == null || hasMore.Type != JTokenType.Boolean
                || (epoch == (string)request["epoch"] && cursor.Value<long>() < request["cursor"].Value<long>()))
                throw new InvalidDataException("反馈同步响应无效，未更新游标。");
            var submitted = ((JArray)request["events"]).Select(x => (string)x["Id"]).ToList();
            if (acks.Any(x => x.Type != JTokenType.String)
                || acks.Count != submitted.Count || !new HashSet<string>(submitted, StringComparer.Ordinal).SetEquals(acks.Values<string>()))
                throw new InvalidDataException("反馈同步确认未覆盖完整提交批次。");
            var incoming = new List<KnowledgeV2FeedbackEventRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var types = new HashSet<string>(new[] { "sent", "send_failed", "accepted", "correction", "withdrawal", "buyer_negative" }, StringComparer.Ordinal);
            foreach (var item in page)
            {
                var id = (string)item["Id"];
                var kid = (string)item["KnowledgeId"];
                var type = (string)item["EventType"];
                var ticks = item["UtcTicks"];
                Guid eventId;
                if (!Guid.TryParseExact(id, "N", out eventId) || eventId == Guid.Empty || !seen.Add(id)
                    || string.IsNullOrWhiteSpace(kid) || kid.Length > 200 || !types.Contains(type)
                    || ticks == null || ticks.Type != JTokenType.Integer || ticks.Value<long>() <= 0
                    || ticks.Value<long>() > DateTime.UtcNow.AddMinutes(5).Ticks)
                    throw new InvalidDataException("反馈同步事件无效。");
                var utc = ticks.Value<long>();
                incoming.Add(new KnowledgeV2FeedbackEventRow
                {
                    Id = id, KnowledgeId = kid, EventType = type, Seller = shop.SellerNick,
                    Buyer = string.Empty, Question = string.Empty, Answer = string.Empty,
                    Evidence = "cloud_synced_statistic", CloudUtcTicks = utc,
                    CreatedAtTicks = new DateTime(utc, DateTimeKind.Utc).ToLocalTime().Ticks
                });
            }
            var state = GetState(shop);
            lock (state.Sync)
            {
                var prior = state.Db.ReadOneRecord<KnowledgeV2FeedbackCursorRow>(x => x.Id == scope);
                if ((prior == null ? string.Empty : prior.Epoch ?? string.Empty) != (string)request["epoch"]
                    || (prior == null ? 0L : prior.Cursor) != request["cursor"].Value<long>())
                    throw new InvalidOperationException("反馈同步游标已变化，拒绝迟到响应。");
                var existing = state.Db.ReadRecords<KnowledgeV2FeedbackEventRow>(null).ToDictionary(x => x.Id, StringComparer.Ordinal);
                var writes = new List<object>();
                foreach (var row in incoming)
                {
                    KnowledgeV2FeedbackEventRow old;
                    if (existing.TryGetValue(row.Id, out old))
                    {
                        if (old.KnowledgeId != row.KnowledgeId || old.EventType != row.EventType || FeedbackUtcTicks(old) != row.CloudUtcTicks)
                            throw new InvalidDataException("云端反馈与本地同ID事件冲突。");
                    }
                    else writes.Add(row);
                    writes.Add(new KnowledgeV2FeedbackAckRow { Id = FeedbackAckKey(scope, epoch, row.Id), CreatedAtTicks = row.CreatedAtTicks });
                }
                foreach (var id in submitted.Where(x => !seen.Contains(x)))
                    writes.Add(new KnowledgeV2FeedbackAckRow { Id = FeedbackAckKey(scope, epoch, id), CreatedAtTicks = DateTime.Now.Ticks });
                writes.Add(new KnowledgeV2FeedbackCursorRow { Id = scope, Epoch = epoch, Cursor = cursor.Value<long>() });
                // Event rows, acknowledgement and cursor are durable in the SAME SQLite transaction.
                state.Db.SaveRecordsInTransaction(writes);
                AggregateCache ignored;
                AggregateCaches.TryRemove(shop.ShopKey, out ignored);
            }
            return hasMore.Value<bool>() || submitted.Count == 200;
        }

        internal static List<KnowledgeV2FeedbackEventRow> ExportEvents(string seller)
        {
            var state = GetState(ResolveShopRequired(seller));
            lock (state.Sync)
                return state.Db.ReadRecords<KnowledgeV2FeedbackEventRow>(null)
                    .Where(x => x != null).OrderBy(x => x.CreatedAtTicks).ToList();
        }

        internal static void ValidateBackupEvents(IEnumerable<KnowledgeV2FeedbackEventRow> events, IEnumerable<KnowledgeV2Record> records)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var knowledgeIds = new HashSet<string>((records ?? Enumerable.Empty<KnowledgeV2Record>())
                .Where(x => x != null).Select(x => x.Id), StringComparer.Ordinal);
            var types = new HashSet<string>(new[] { "sent", "send_failed", "accepted", "correction", "withdrawal", "buyer_negative" }, StringComparer.Ordinal);
            foreach (var row in events ?? Enumerable.Empty<KnowledgeV2FeedbackEventRow>())
            {
                Guid eventId;
                if (row == null || !Guid.TryParseExact(row.Id, "N", out eventId) || eventId == Guid.Empty
                    || !ids.Add(row.Id) || !knowledgeIds.Contains(row.KnowledgeId)
                    || !types.Contains(row.EventType) || row.CreatedAtTicks <= 0
                    || row.CreatedAtTicks > DateTime.Now.AddMinutes(5).Ticks
                    || (row.Question ?? string.Empty).Length > 800 || (row.Answer ?? string.Empty).Length > 1400
                    || (row.Evidence ?? string.Empty).Length > 500)
                    throw new InvalidDataException("知识包反馈事件无效、重复或没有对应知识。");
            }
        }

        internal static int MergeBackupEvents(string seller, string sourceShopKey, IEnumerable<KnowledgeV2FeedbackEventRow> events)
        {
            var shop = ResolveShopRequired(seller);
            if (!string.Equals(shop.ShopKey, sourceShopKey, StringComparison.Ordinal))
                throw new InvalidOperationException("反馈备份仅允许恢复到原店铺。");
            var records = KnowledgeEngineV2Repository.LoadAll(seller);
            var incoming = (events ?? Enumerable.Empty<KnowledgeV2FeedbackEventRow>()).ToList();
            ValidateBackupEvents(incoming, records);
            if (incoming.Any(x => !DirectOrderIdentityResolver.IdentityEquals(x.Seller, shop.SellerNick)
                && !DirectOrderIdentityResolver.IdentityEquals(x.Seller, shop.DisplayName)))
                throw new InvalidDataException("反馈事件卖家身份不匹配。");
            var state = GetState(shop);
            lock (state.Sync)
            {
                var ids = new HashSet<string>(state.Db.ReadRecords<KnowledgeV2FeedbackEventRow>(null)
                    .Where(x => x != null).Select(x => x.Id), StringComparer.Ordinal);
                var cutoff = DateTime.Now.AddDays(-180).Ticks;
                var additions = incoming.Where(x => x.CreatedAtTicks >= cutoff && ids.Add(x.Id)).Cast<object>().ToList();
                if (additions.Count > 0)
                {
                    state.Db.SaveRecordsInTransaction(additions);
                    AggregateCache ignored;
                    AggregateCaches.TryRemove(shop.ShopKey, out ignored);
                }
                return additions.Count;
            }
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
            var tokens = new[] { "谢谢", "感谢", "明白了", "解决了", "已解决", "问题解决了" };
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
