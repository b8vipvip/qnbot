using Bot.Common;
using BotLib;
using BotLib.Db.Sqlite;
using Bot.ShopScope;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Bot.ChromeNs
{
    internal sealed class BotConversationHistoryEntity
    {
        [PrimaryKey]
        public string EntityId { get; set; }
        public string ShopKey { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string Question { get; set; }
        public string Answer { get; set; }
        public string AnswerSource { get; set; }
        public string StatusText { get; set; }
        public string StatusKind { get; set; }
        public string StatusDetail { get; set; }
        public bool CanResend { get; set; }
        public long QuestionDetectedAtTicks { get; set; }
        public long AnswerReadyAtTicks { get; set; }
        public long CreatedAtTicks { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    internal sealed class BotReplySourceStampEntity
    {
        [PrimaryKey]
        public string StampId { get; set; }
        public string ShopKey { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string QuestionKey { get; set; }
        public string AnswerKey { get; set; }
        public string AnswerSource { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    internal sealed class BotReplyManualSourceEntity
    {
        [PrimaryKey]
        public string EntityId { get; set; }
        public string ShopKey { get; set; }
        public string Seller { get; set; }
        public string OriginalSource { get; set; }
        public string CorrectedSource { get; set; }
        public long CorrectedAtTicks { get; set; }
    }

    // A single prepared Bot reply can be observed in multiple history refreshes.
    // Bind each native transport echo to exactly one persisted reply record.
    internal sealed class BotReplyEchoLinkEntity
    {
        [PrimaryKey]
        public string EchoId { get; set; }
        public string ShopKey { get; set; }
        public string RecordId { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
    }

    internal static class BotConversationHistoryStore
    {
        public const int RetentionDays = 90;
        public const int MaxTotalRecords = 10000;
        public const int MaxRecentLoadCount = 200;
        public const int DefaultLoadCount = 100;

        private static readonly object DbSync = new object();
        private static readonly ConcurrentDictionary<string, BotConversationHistoryEntity> Pending =
            new ConcurrentDictionary<string, BotConversationHistoryEntity>(StringComparer.Ordinal);
        // Legacy ShopKey assignment is a one-time migration per shop/seller, not
        // a write-on-every-read operation. Only successful attempts are cached.
        private static readonly ConcurrentDictionary<string, bool> CompletedLegacyShopBackfills =
            new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        private static int _initialized;
        private static int _schemaReady;
        private static int _flushScheduled;
        private static int _cleanupScheduled;
        private static int _savedSinceCleanup;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

            try
            {
                if (Application.Current != null)
                {
                    Application.Current.Exit += (s, e) => FlushNow();
                }
            }
            catch
            {
            }

            Task.Run(async () =>
            {
                await Task.Delay(1500);
                CleanupNow();
            });

            Log.Info("Bot问答历史持久化已启用：保留" + RetentionDays
                + "天，全局最多" + MaxTotalRecords
                + "条，界面最近读取上限" + MaxRecentLoadCount + "条；学习复盘使用完整时间范围查询。");
        }

        /// <summary>
        /// Persist the source label before sending so a later/restarted message echo can
        /// still recover the generation route. Keys normalize the transport-only [A] suffix.
        /// </summary>
        public static void SaveAnswerSourceStamp(
            string seller, string buyer, string question, string answer, string source)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            source = (source ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0 || source.Length == 0 || string.IsNullOrWhiteSpace(answer)) return;
            try
            {
                Initialize();
                EnsureSchema();
                var shopKey = ResolveShopKey(seller);
                if (shopKey.Length == 0) return;
                BackfillLegacyShopRows(shopKey, seller);
                var row = new BotReplySourceStampEntity
                {
                    StampId = BuildSourceStampId(shopKey, seller, buyer, question, answer),
                    ShopKey = shopKey,
                    Seller = seller,
                    Buyer = buyer,
                    QuestionKey = NormalizeSourceValue(question),
                    AnswerKey = NormalizeSourceValue(answer),
                    AnswerSource = source,
                    UpdatedAtTicks = DateTime.Now.Ticks
                };
                lock (DbSync) DbHelper.Db.SaveOneRecord(row);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("持久化Bot回复来源标签失败：" + ex.Message, 10);
            }
        }

        public static string ResolveAnswerSourceStamp(string seller, string buyer, string question, string answer)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0 || string.IsNullOrWhiteSpace(answer)) return string.Empty;
            try
            {
                Initialize();
                EnsureSchema();
                var shopKey = ResolveShopKey(seller);
                if (shopKey.Length == 0) return string.Empty;
                BackfillLegacyShopRows(shopKey, seller);
                var id = BuildSourceStampId(shopKey, seller, buyer, question, answer);
                BotReplySourceStampEntity row;
                lock (DbSync)
                {
                    row = DbHelper.Db.ReadOneRecord<BotReplySourceStampEntity>(x => x.StampId == id);
                    if (row == null)
                    {
                        // Existing stamps used the pre-ShopKey primary-key hash.
                        // Resolve them by their migrated, fully scoped natural key.
                        row = (DbHelper.Db.Select(
                            typeof(BotReplySourceStampEntity),
                            "where ShopKey = ? and Seller = ? and Buyer = ? and QuestionKey = ? and AnswerKey = ? "
                            + "order by UpdatedAtTicks desc limit 1",
                            shopKey, seller, buyer,
                            NormalizeSourceValue(question), NormalizeSourceValue(answer))
                            ?? new List<object>())
                            .OfType<BotReplySourceStampEntity>()
                            .FirstOrDefault();
                    }
                }
                return row == null ? string.Empty : (row.AnswerSource ?? string.Empty).Trim();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取Bot回复来源标签失败：" + ex.Message, 10);
                return string.Empty;
            }
        }

        public static List<BotConversationHistoryEntity> LoadRecentBySeller(
            string seller, int maxCount = 2000)
        {
            return LoadRecentBySeller(ResolveShopKey(seller), seller, maxCount);
        }

        public static List<BotConversationHistoryEntity> LoadRecentBySeller(
            string shopKey, string seller, int maxCount = 2000)
        {
            Initialize();
            FlushNow();
            EnsureSchema();
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0)
                return new List<BotConversationHistoryEntity>();
            BackfillLegacyShopRows(shopKey, seller);
            var take = Math.Max(1, Math.Min(5000, maxCount <= 0 ? 2000 : maxCount));
            var cutoffTicks = DateTime.Now.AddDays(-RetentionDays).Ticks;
            try
            {
                lock (DbSync)
                {
                    var rows = DbHelper.Db.Select(
                        typeof(BotConversationHistoryEntity),
                        "where ShopKey = ? and Seller = ? and UpdatedAtTicks >= ? order by CreatedAtTicks desc limit " + take,
                        shopKey,
                        seller,
                        cutoffTicks);
                    return (rows ?? new List<object>())
                        .OfType<BotConversationHistoryEntity>()
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.EntityId))
                        .Where(x => string.Equals(x.StatusKind, "sent", StringComparison.OrdinalIgnoreCase)
                            || x.EntityId.StartsWith("bot-echo-", StringComparison.Ordinal))
                        .OrderByDescending(x => x.CreatedAtTicks)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取店铺Bot回复来源列表失败：" + ex.Message, 10);
                return new List<BotConversationHistoryEntity>();
            }
        }

        /// <summary>
        /// Records every observed Bot transport echo. If a persisted answer card already
        /// represents the same delivered answer, upgrade that row instead of duplicating it.
        /// </summary>
        public static string RecordBotEcho(
            string seller, string buyer, string question, string answer, string source,
            DateTime sentAt, string messageKey)
        {
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            answer = (answer ?? string.Empty).Trim();
            source = (source ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0 || answer.Length == 0) return string.Empty;
            if (sentAt == DateTime.MinValue) sentAt = DateTime.Now;
            var shopKey = ResolveShopKey(seller);
            if (shopKey.Length == 0) return string.Empty;

            try
            {
                FlushNow();
                var echoId = BuildBotEchoEntityId(shopKey, seller, buyer, messageKey, sentAt, answer);
                var candidates = LoadRecent(shopKey, seller, buyer, 200);
                var answerKey = NormalizeSourceValue(answer);
                var targetTicks = sentAt.Ticks;

                // Re-rendering the same Qianniu echo must resolve to the original
                // record even when that record has already been marked as sent.
                BotReplyEchoLinkEntity existingLink;
                var legacyEchoId = BuildLegacyBotEchoEntityId(
                    seller, buyer, messageKey, sentAt, answer);
                lock (DbSync)
                {
                    existingLink = DbHelper.Db.ReadOneRecord<BotReplyEchoLinkEntity>(
                        x => x.EchoId == echoId);
                    if (existingLink == null
                        && !string.Equals(legacyEchoId, echoId, StringComparison.Ordinal))
                    {
                        // Old echo links used a seller/buyer-only hash. Keep the
                        // original record association after safe ShopKey backfill.
                        existingLink = DbHelper.Db.ReadOneRecord<BotReplyEchoLinkEntity>(
                            x => x.EchoId == legacyEchoId);
                    }
                }
                var originalId = existingLink != null
                    && string.Equals(existingLink.ShopKey, shopKey, StringComparison.Ordinal)
                    && string.Equals(existingLink.Seller, seller, StringComparison.Ordinal)
                    && string.Equals(existingLink.Buyer, buyer, StringComparison.Ordinal)
                        ? existingLink.RecordId : echoId;
                var match = candidates.FirstOrDefault(x => x != null
                    && string.Equals(x.EntityId, originalId, StringComparison.Ordinal));

                if (match == null && existingLink == null)
                {
                    // Different message keys with identical text must NOT claim
                    // a previously linked record. Prefer a still-unlinked native
                    // answer card; never merge two independent bot-echo rows.
                    var eligible = candidates
                        .Where(x => x != null
                            && !x.EntityId.StartsWith("bot-echo-", StringComparison.Ordinal)
                            && NormalizeSourceValue(x.Answer) == answerKey)
                        .Select(x => new
                        {
                            Row = x,
                            Time = x.AnswerReadyAtTicks > 0 ? x.AnswerReadyAtTicks : x.CreatedAtTicks
                        })
                        .Where(x => x.Time > 0
                            && Math.Abs((double)(x.Time - targetTicks)) <= TimeSpan.FromMinutes(30).Ticks)
                        .OrderBy(x => Math.Abs((double)(x.Time - targetTicks)));
                    foreach (var candidate in eligible)
                    {
                        var candidateRecordId = candidate.Row.EntityId;
                        BotReplyEchoLinkEntity claimed;
                        lock (DbSync)
                            claimed = DbHelper.Db.ReadOneRecord<BotReplyEchoLinkEntity>(
                                x => x.RecordId == candidateRecordId
                                    && x.ShopKey == shopKey);
                        if (claimed != null) continue;
                        match = candidate.Row;
                        break;
                    }
                }
                if (match != null)
                {
                    match.Question = string.IsNullOrWhiteSpace(match.Question) ? question : match.Question;
                    // Never erase an explicit operator correction on the next
                    // live/history refresh of the same sent message.
                    BotReplyManualSourceEntity manual;
                    lock (DbSync)
                        manual = DbHelper.Db.ReadOneRecord<BotReplyManualSourceEntity>(
                            x => x.EntityId == match.EntityId);
                    match.AnswerSource = manual != null
                        && string.Equals(manual.ShopKey, shopKey, StringComparison.Ordinal)
                        && string.Equals(manual.Seller, seller, StringComparison.Ordinal)
                        ? manual.CorrectedSource : source;
                    match.StatusText = "千牛回显确认已发送";
                    match.StatusKind = "sent";
                    match.StatusDetail = "已观察到Bot消息回显";
                    match.AnswerReadyAtTicks = sentAt.Ticks;
                    match.UpdatedAtTicks = DateTime.Now.Ticks;
                    lock (DbSync)
                        DbHelper.Db.SaveOneRecord(new BotReplyEchoLinkEntity
                        {
                            EchoId = echoId,
                            ShopKey = shopKey,
                            RecordId = match.EntityId,
                            Seller = seller,
                            Buyer = buyer
                        });
                    QueueSave(match);
                    return match.EntityId;
                }

                var id = echoId;
                QueueSave(new BotConversationHistoryEntity
                {
                    EntityId = id,
                    ShopKey = shopKey,
                    Seller = seller,
                    Buyer = buyer,
                    Question = question ?? string.Empty,
                    Answer = answer,
                    AnswerSource = source,
                    StatusText = "千牛回显确认已发送",
                    StatusKind = "sent",
                    StatusDetail = "已观察到Bot消息回显",
                    CanResend = true,
                    QuestionDetectedAtTicks = sentAt.Ticks,
                    AnswerReadyAtTicks = sentAt.Ticks,
                    CreatedAtTicks = sentAt.Ticks,
                    UpdatedAtTicks = DateTime.Now.Ticks
                });
                return id;
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("记录Bot消息来源回显失败：" + ex.Message, 10);
                return BuildBotEchoEntityId(shopKey, seller, buyer, messageKey, sentAt, answer);
            }
        }

        public static string BuildBotEchoEntityId(
            string seller, string buyer, string messageKey, DateTime sentAt, string answer)
        {
            return BuildBotEchoEntityId(ResolveShopKey(seller), seller, buyer, messageKey, sentAt, answer);
        }

        private static string BuildBotEchoEntityId(
            string shopKey, string seller, string buyer, string messageKey, DateTime sentAt, string answer)
        {
            var identity = (shopKey ?? string.Empty).Trim() + "\n"
                + (seller ?? string.Empty).Trim() + "\n"
                + (buyer ?? string.Empty).Trim() + "\n"
                + ((messageKey ?? string.Empty).Trim().Length > 0
                    ? (messageKey ?? string.Empty).Trim()
                    : (sentAt == DateTime.MinValue ? 0 : sentAt.Ticks).ToString())
                + "\n" + NormalizeSourceValue(answer);
            return "bot-echo-" + Sha256(identity);
        }

        private static string BuildLegacyBotEchoEntityId(
            string seller, string buyer, string messageKey, DateTime sentAt, string answer)
        {
            var identity = (seller ?? string.Empty).Trim() + "\n"
                + (buyer ?? string.Empty).Trim() + "\n"
                + ((messageKey ?? string.Empty).Trim().Length > 0
                    ? (messageKey ?? string.Empty).Trim()
                    : (sentAt == DateTime.MinValue ? 0 : sentAt.Ticks).ToString())
                + "\n" + NormalizeSourceValue(answer);
            return "bot-echo-" + Sha256(identity);
        }

        public static bool UpdateAnswerSource(string seller, string entityId, string source)
        {
            return UpdateAnswerSource(ResolveShopKey(seller), seller, entityId, source);
        }

        public static bool UpdateAnswerSource(
            string shopKey, string seller, string entityId, string source)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            entityId = (entityId ?? string.Empty).Trim();
            source = (source ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0
                || entityId.Length == 0 || source.Length == 0 || source.Length > 200)
                return false;
            try
            {
                FlushNow();
                EnsureSchema();
                BackfillLegacyShopRows(shopKey, seller);
                lock (DbSync)
                {
                    var row = DbHelper.Db.ReadOneRecord<BotConversationHistoryEntity>(
                        x => x.EntityId == entityId && x.ShopKey == shopKey);
                    if (row == null || !string.Equals(row.Seller, seller, StringComparison.Ordinal))
                        return false;

                    var original = DbHelper.Db.ReadOneRecord<BotReplyManualSourceEntity>(
                        x => x.EntityId == entityId);
                    if (original != null
                        && (!string.Equals(original.ShopKey, shopKey, StringComparison.Ordinal)
                            || !string.Equals(original.Seller, seller, StringComparison.Ordinal)))
                        return false;
                    if (original == null)
                    {
                        original = new BotReplyManualSourceEntity
                        {
                            EntityId = entityId,
                            ShopKey = shopKey,
                            Seller = seller,
                            OriginalSource = row.AnswerSource ?? string.Empty
                        };
                    }
                    else if (string.IsNullOrWhiteSpace(original.ShopKey))
                    {
                        original.ShopKey = shopKey;
                    }

                    original.CorrectedSource = source;
                    original.CorrectedAtTicks = DateTime.Now.Ticks;
                    row.AnswerSource = source;
                    row.UpdatedAtTicks = DateTime.Now.Ticks;
                    // The correction audit and the visible Bot history must be
                    // one atomic change. Two separate SaveOneRecord calls could
                    // leave the audit changed while the source row remained stale.
                    DbHelper.Db.SaveRecordsInTransaction(new List<object> { original, row });
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("更新Bot回复来源标签失败：" + ex.Message, 10);
                return false;
            }
        }

        public static string GetManualSource(string seller, string entityId)
        {
            return GetManualSource(ResolveShopKey(seller), seller, entityId);
        }

        public static string GetManualSource(string shopKey, string seller, string entityId)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            entityId = (entityId ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0 || entityId.Length == 0)
                return string.Empty;
            try
            {
                EnsureSchema();
                BackfillLegacyShopRows(shopKey, seller);
                lock (DbSync)
                {
                    var row = DbHelper.Db.ReadOneRecord<BotReplyManualSourceEntity>(
                        x => x.EntityId == entityId);
                    return row == null
                        || !string.Equals(row.ShopKey, shopKey, StringComparison.Ordinal)
                        || !string.Equals(row.Seller, seller, StringComparison.Ordinal)
                        ? string.Empty : row.CorrectedSource ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取人工来源更正失败：" + ex.Message, 5);
                return string.Empty;
            }
        }

        public static string GetOriginalSourceBeforeCorrection(string seller, string entityId)
        {
            return GetOriginalSourceBeforeCorrection(ResolveShopKey(seller), seller, entityId);
        }

        public static string GetOriginalSourceBeforeCorrection(
            string shopKey, string seller, string entityId)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            entityId = (entityId ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0 || entityId.Length == 0)
                return string.Empty;
            try
            {
                EnsureSchema();
                BackfillLegacyShopRows(shopKey, seller);
                lock (DbSync)
                {
                    var row = DbHelper.Db.ReadOneRecord<BotReplyManualSourceEntity>(
                        x => x.EntityId == entityId);
                    return row == null
                        || !string.Equals(row.ShopKey, shopKey, StringComparison.Ordinal)
                        || !string.Equals(row.Seller, seller, StringComparison.Ordinal)
                        ? string.Empty : row.OriginalSource ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取Bot来源人工更正前证据失败：" + ex.Message, 5);
                return string.Empty;
            }
        }

        private static string BuildSourceStampId(
            string shopKey, string seller, string buyer, string question, string answer)
        {
            return Sha256((shopKey ?? string.Empty).Trim() + "\n"
                + (seller ?? string.Empty).Trim() + "\n"
                + (buyer ?? string.Empty).Trim() + "\n"
                + NormalizeSourceValue(question) + "\n"
                + NormalizeSourceValue(answer));
        }

        private static string NormalizeSourceValue(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.EndsWith("[A]", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - 3).TrimEnd();
            return System.Text.RegularExpressions.Regex.Replace(value.ToLowerInvariant(), @"\s+", string.Empty);
        }

        private static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        public static void QueueSave(BotConversationHistoryEntity entity)
        {
            if (entity == null) return;
            Initialize();

            var copy = Clone(entity);
            copy.EntityId = (copy.EntityId ?? string.Empty).Trim();
            copy.ShopKey = (copy.ShopKey ?? string.Empty).Trim();
            copy.Seller = (copy.Seller ?? string.Empty).Trim();
            copy.Buyer = (copy.Buyer ?? string.Empty).Trim();
            if (copy.ShopKey.Length == 0) copy.ShopKey = ResolveShopKey(copy.Seller);
            if (copy.EntityId.Length == 0 || copy.ShopKey.Length == 0
                || copy.Seller.Length == 0 || copy.Buyer.Length == 0) return;

            var nowTicks = DateTime.Now.Ticks;
            if (copy.CreatedAtTicks <= 0)
            {
                copy.CreatedAtTicks = copy.QuestionDetectedAtTicks > 0
                    ? copy.QuestionDetectedAtTicks
                    : nowTicks;
            }
            copy.UpdatedAtTicks = nowTicks;
            Pending[copy.EntityId] = copy;
            ScheduleFlush();
        }

        public static List<BotConversationHistoryEntity> LoadRecent(
            string seller, string buyer, int maxCount = DefaultLoadCount)
        {
            return LoadRecent(ResolveShopKey(seller), seller, buyer, maxCount);
        }

        public static List<BotConversationHistoryEntity> LoadRecent(
            string shopKey, string seller, string buyer, int maxCount = DefaultLoadCount)
        {
            List<BotConversationHistoryEntity> records;
            return TryLoadRecent(shopKey, seller, buyer, maxCount, out records)
                ? records : new List<BotConversationHistoryEntity>();
        }

        // Callers making safety decisions must distinguish a genuinely empty history
        // from a failed database read. Returning an empty list on both outcomes can
        // misclassify a delayed Bot echo as a human reply and feed it into learning.
        public static bool TryLoadRecent(
            string shopKey, string seller, string buyer, int maxCount,
            out List<BotConversationHistoryEntity> records)
        {
            records = new List<BotConversationHistoryEntity>();
            try
            {
                Initialize();
                FlushNow();
                EnsureSchema();

                shopKey = (shopKey ?? string.Empty).Trim();
                seller = (seller ?? string.Empty).Trim();
                buyer = (buyer ?? string.Empty).Trim();
                if (shopKey.Length == 0 || seller.Length == 0 || buyer.Length == 0)
                    return false;
                BackfillLegacyShopRows(shopKey, seller);

                var take = Math.Max(1, Math.Min(MaxRecentLoadCount, maxCount <= 0 ? DefaultLoadCount : maxCount));
                var cutoffTicks = DateTime.Now.AddDays(-RetentionDays).Ticks;
                lock (DbSync)
                {
                    var rows = DbHelper.Db.Select(
                        typeof(BotConversationHistoryEntity),
                        "where ShopKey = ? and Seller = ? and Buyer = ? and UpdatedAtTicks >= ? order by CreatedAtTicks desc limit " + take,
                        shopKey,
                        seller,
                        buyer,
                        cutoffTicks);
                    records = (rows ?? new List<object>())
                        .OfType<BotConversationHistoryEntity>()
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.EntityId))
                        .OrderBy(x => x.CreatedAtTicks)
                        .ThenBy(x => x.UpdatedAtTicks)
                        .ToList();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取Bot问答历史失败：" + ex.Message, 10);
                records = new List<BotConversationHistoryEntity>();
                return false;
            }
        }

        public static List<BotConversationHistoryEntity> LoadRange(
            string seller,
            string buyer,
            DateTime from,
            DateTime to,
            int maxCount = 120)
        {
            return LoadRange(ResolveShopKey(seller), seller, buyer, from, to, maxCount);
        }

        public static List<BotConversationHistoryEntity> LoadRange(
            string shopKey,
            string seller,
            string buyer,
            DateTime from,
            DateTime to,
            int maxCount = 120)
        {
            Initialize();
            FlushNow();
            EnsureSchema();

            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0 || buyer.Length == 0)
                return new List<BotConversationHistoryEntity>();
            BackfillLegacyShopRows(shopKey, seller);

            var take = Math.Max(1, Math.Min(MaxRecentLoadCount, maxCount <= 0 ? 120 : maxCount));
            var fromTicks = from == DateTime.MinValue ? DateTime.Now.AddDays(-RetentionDays).Ticks : from.Ticks;
            var toTicks = to == DateTime.MinValue ? DateTime.Now.Ticks : to.Ticks;
            if (toTicks < fromTicks)
            {
                var swap = fromTicks;
                fromTicks = toTicks;
                toTicks = swap;
            }

            try
            {
                lock (DbSync)
                {
                    var rows = DbHelper.Db.Select(
                        typeof(BotConversationHistoryEntity),
                        "where ShopKey = ? and Seller = ? and Buyer = ? and CreatedAtTicks >= ? and CreatedAtTicks <= ? "
                        + "order by CreatedAtTicks asc limit " + take,
                        shopKey,
                        seller,
                        buyer,
                        fromTicks,
                        toTicks);
                    return (rows ?? new List<object>())
                        .OfType<BotConversationHistoryEntity>()
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.EntityId))
                        .OrderBy(x => x.CreatedAtTicks)
                        .ThenBy(x => x.UpdatedAtTicks)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("按接待时间读取Bot问答历史失败：" + ex.Message, 10);
                return new List<BotConversationHistoryEntity>();
            }
        }

        // Learning needs every Bot card in the session window. UI paging remains
        // bounded through LoadRange, but this query intentionally has no 120/200 limit.
        public static List<BotConversationHistoryEntity> LoadRangeComplete(
            string shopKey,
            string seller,
            string buyer,
            DateTime from,
            DateTime to)
        {
            Initialize();
            FlushNow();
            EnsureSchema();

            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0 || buyer.Length == 0)
                return new List<BotConversationHistoryEntity>();
            BackfillLegacyShopRows(shopKey, seller);

            var fromTicks = from == DateTime.MinValue
                ? DateTime.Now.AddDays(-RetentionDays).Ticks : from.Ticks;
            var toTicks = to == DateTime.MinValue ? DateTime.Now.Ticks : to.Ticks;
            if (toTicks < fromTicks)
            {
                var swap = fromTicks;
                fromTicks = toTicks;
                toTicks = swap;
            }

            try
            {
                lock (DbSync)
                {
                    var rows = DbHelper.Db.Select(
                        typeof(BotConversationHistoryEntity),
                        "where ShopKey = ? and Seller = ? and Buyer = ? and CreatedAtTicks >= ? and CreatedAtTicks <= ? "
                        + "order by CreatedAtTicks asc",
                        shopKey,
                        seller,
                        buyer,
                        fromTicks,
                        toTicks);
                    return (rows ?? new List<object>())
                        .OfType<BotConversationHistoryEntity>()
                        .Where(x => x != null && !string.IsNullOrWhiteSpace(x.EntityId))
                        .OrderBy(x => x.CreatedAtTicks)
                        .ThenBy(x => x.UpdatedAtTicks)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取完整接待Bot问答历史失败：" + ex.Message, 10);
                return new List<BotConversationHistoryEntity>();
            }
        }

        public static void FlushNow()
        {
            Initialize();
            FlushPendingCore();
        }

        public static void CleanupNow()
        {
            if (Interlocked.Exchange(ref _cleanupScheduled, 1) != 0) return;
            try
            {
                EnsureSchema();
                FlushPendingCore();
                var cutoffTicks = DateTime.Now.AddDays(-RetentionDays).Ticks;
                int expired;
                int overflow;
                lock (DbSync)
                {
                    expired = DbHelper.Db.Execute(
                        "delete from BotConversationHistoryEntity where UpdatedAtTicks < ?",
                        cutoffTicks);
                    DbHelper.Db.Execute(
                        "delete from BotReplySourceStampEntity where UpdatedAtTicks < ?",
                        cutoffTicks);
                    DbHelper.Db.Execute(
                        "delete from BotReplySourceStampEntity where StampId not in "
                        + "(select StampId from BotReplySourceStampEntity order by UpdatedAtTicks desc limit "
                        + MaxTotalRecords + ")");
                    overflow = DbHelper.Db.Execute(
                        "delete from BotConversationHistoryEntity where EntityId not in "
                        + "(select EntityId from BotConversationHistoryEntity order by UpdatedAtTicks desc limit "
                        + MaxTotalRecords + ")");
                    DbHelper.Db.Execute(
                        "delete from BotReplyEchoLinkEntity where RecordId not in "
                        + "(select EntityId from BotConversationHistoryEntity)");
                }
                if (expired > 0 || overflow > 0)
                {
                    Log.Info("Bot问答历史清理完成：过期=" + expired + "，超限=" + overflow);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("清理Bot问答历史失败：" + ex.Message, 10);
            }
            finally
            {
                Interlocked.Exchange(ref _cleanupScheduled, 0);
            }
        }

        private static void ScheduleFlush()
        {
            if (Interlocked.Exchange(ref _flushScheduled, 1) != 0) return;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(250);
                    FlushPendingCore();
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("写入Bot问答历史失败：" + ex.Message, 10);
                }
                finally
                {
                    Interlocked.Exchange(ref _flushScheduled, 0);
                    if (!Pending.IsEmpty) ScheduleFlush();
                }
            });
        }

        private static void FlushPendingCore()
        {
            var batch = new List<BotConversationHistoryEntity>();
            foreach (var pair in Pending.ToArray())
            {
                BotConversationHistoryEntity removed;
                if (Pending.TryRemove(pair.Key, out removed) && removed != null)
                {
                    batch.Add(removed);
                }
            }
            if (batch.Count == 0) return;

            EnsureSchema();
            try
            {
                lock (DbSync)
                {
                    DbHelper.Db.SaveRecordsInTransaction(batch.Cast<object>().ToList());
                }

                if (Interlocked.Add(ref _savedSinceCleanup, batch.Count) >= 200)
                {
                    Interlocked.Exchange(ref _savedSinceCleanup, 0);
                    Task.Run(() => CleanupNow());
                }
            }
            catch
            {
                foreach (var entity in batch)
                {
                    // A newer status/source update may have arrived during the
                    // failed disk transaction. Never overwrite that newer state
                    // when requeuing the older failed batch.
                    Pending.TryAdd(entity.EntityId, entity);
                }
                throw;
            }
        }

        private static void EnsureSchema()
        {
            if (Volatile.Read(ref _schemaReady) != 0) return;
            lock (DbSync)
            {
                if (_schemaReady != 0) return;

                DbHelper.Db.Execute(
                    "create table if not exists BotConversationHistoryEntity ("
                    + "EntityId text primary key not null,"
                    + "ShopKey text,"
                    + "Seller text not null,"
                    + "Buyer text not null,"
                    + "Question text,"
                    + "Answer text,"
                    + "AnswerSource text,"
                    + "StatusText text,"
                    + "StatusKind text,"
                    + "StatusDetail text,"
                    + "CanResend integer not null default 1,"
                    + "QuestionDetectedAtTicks integer not null default 0,"
                    + "AnswerReadyAtTicks integer not null default 0,"
                    + "CreatedAtTicks integer not null default 0,"
                    + "UpdatedAtTicks integer not null default 0"
                    + ")");
                try
                {
                    DbHelper.Db.Execute("alter table BotConversationHistoryEntity add column ShopKey text");
                }
                catch
                {
                    // Existing installations already have the shop-scope column.
                }
                DbHelper.Db.Execute(
                    "create index if not exists IX_BotConversationHistory_ShopSellerBuyerCreated "
                    + "on BotConversationHistoryEntity(ShopKey, Seller, Buyer, CreatedAtTicks)");
                DbHelper.Db.Execute(
                    "create index if not exists IX_BotConversationHistory_SellerBuyerUpdated "
                    + "on BotConversationHistoryEntity(Seller, Buyer, UpdatedAtTicks)");
                DbHelper.Db.Execute(
                    "create index if not exists IX_BotConversationHistory_Updated "
                    + "on BotConversationHistoryEntity(UpdatedAtTicks)");
                DbHelper.Db.Execute(
                    "create table if not exists BotReplySourceStampEntity ("
                    + "StampId text primary key not null,"
                    + "ShopKey text,"
                    + "Seller text not null,"
                    + "Buyer text not null,"
                    + "QuestionKey text,"
                    + "AnswerKey text,"
                    + "AnswerSource text,"
                    + "UpdatedAtTicks integer not null default 0"
                    + ")");
                try
                {
                    DbHelper.Db.Execute("alter table BotReplySourceStampEntity add column ShopKey text");
                }
                catch
                {
                    // Existing installations already have the shop-scope column.
                }
                DbHelper.Db.Execute(
                    "create index if not exists IX_BotReplySourceStamp_ShopLookup "
                    + "on BotReplySourceStampEntity(ShopKey, Seller, Buyer, UpdatedAtTicks)");
                DbHelper.Db.Execute(
                    "create index if not exists IX_BotReplySourceStamp_Lookup "
                    + "on BotReplySourceStampEntity(Seller, Buyer, UpdatedAtTicks)");
                DbHelper.Db.Execute(
                    "create table if not exists BotReplyManualSourceEntity ("
                    + "EntityId text primary key not null,"
                    + "ShopKey text,"
                    + "Seller text not null,"
                    + "OriginalSource text,"
                    + "CorrectedSource text,"
                    + "CorrectedAtTicks integer not null default 0"
                    + ")");
                try
                {
                    DbHelper.Db.Execute("alter table BotReplyManualSourceEntity add column ShopKey text");
                }
                catch
                {
                    // Existing installations already have the shop-scope column.
                }
                DbHelper.Db.Execute(
                    "create table if not exists BotReplyEchoLinkEntity ("
                    + "EchoId text primary key not null,"
                    + "ShopKey text,"
                    + "RecordId text not null,"
                    + "Seller text not null,"
                    + "Buyer text not null"
                    + ")");
                try
                {
                    DbHelper.Db.Execute("alter table BotReplyEchoLinkEntity add column ShopKey text");
                }
                catch
                {
                    // Existing installations already have the shop-scope column.
                }
                DbHelper.Db.Execute(
                    "create index if not exists IX_BotReplyEchoLink_ShopRecord "
                    + "on BotReplyEchoLinkEntity(ShopKey, RecordId)");
                DbHelper.Db.Execute(
                    "create index if not exists IX_BotReplyEchoLink_Record "
                    + "on BotReplyEchoLinkEntity(RecordId)");
                Volatile.Write(ref _schemaReady, 1);
            }
        }

        private static string ResolveShopKey(string seller)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return string.Empty;
            // An entered ShopSettingsScope comes from the exact QN instance or
            // a persisted ShopKey restoration. Its identity is stronger than the
            // seller's display nickname, which is not a stable authorization key.
            var current = ShopSettingsScope.Current;
            if (current != null) return current.ShopKey;
            try { return ShopContextLocator.ResolveRuntimeBySellerNick(seller).ShopKey; }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("Bot问答历史拒绝无法唯一解析店铺的记录: " + ex.Message, 10);
                return string.Empty;
            }
        }

        // Old Bot history/source rows predate ShopKey. Backfill only when the
        // persisted registry proves that this display nickname belongs to exactly
        // one shop (including shops that are currently offline). Ambiguous legacy
        // rows remain unassigned rather than being guessed into the active shop.
        private static void BackfillLegacyShopRows(string shopKey, string seller)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0) return;
            var backfillKey = shopKey + "\n" + seller;
            if (CompletedLegacyShopBackfills.ContainsKey(backfillKey)) return;
            try
            {
                var resolved = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
                if (resolved == null
                    || !string.Equals(resolved.ShopKey, shopKey, StringComparison.Ordinal))
                    return;

                lock (DbSync)
                {
                    if (CompletedLegacyShopBackfills.ContainsKey(backfillKey)) return;
                    DbHelper.Db.Execute(
                        "update BotConversationHistoryEntity set ShopKey = ? "
                        + "where Seller = ? and (ShopKey is null or ShopKey = '')",
                        shopKey, seller);
                    DbHelper.Db.Execute(
                        "update BotReplySourceStampEntity set ShopKey = ? "
                        + "where Seller = ? and (ShopKey is null or ShopKey = '')",
                        shopKey, seller);
                    DbHelper.Db.Execute(
                        "update BotReplyEchoLinkEntity set ShopKey = ? "
                        + "where Seller = ? and (ShopKey is null or ShopKey = '')",
                        shopKey, seller);
                    DbHelper.Db.Execute(
                        "update BotReplyManualSourceEntity set ShopKey = ? "
                        + "where Seller = ? and (ShopKey is null or ShopKey = '') "
                        + "and EntityId in (select EntityId from BotConversationHistoryEntity "
                        + "where ShopKey = ? and Seller = ?)",
                        shopKey, seller, shopKey, seller);
                    CompletedLegacyShopBackfills.TryAdd(backfillKey, true);
                }
            }
            catch (AmbiguousShopIdentityException)
            {
                // Explicitly keep legacy records unassigned when nickname is ambiguous.
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("迁移旧版Bot历史店铺归属失败: " + ex.Message, 5);
            }
        }

        private static BotConversationHistoryEntity Clone(BotConversationHistoryEntity source)
        {
            return new BotConversationHistoryEntity
            {
                EntityId = source.EntityId,
                ShopKey = source.ShopKey,
                Seller = source.Seller,
                Buyer = source.Buyer,
                Question = source.Question,
                Answer = source.Answer,
                AnswerSource = source.AnswerSource,
                StatusText = source.StatusText,
                StatusKind = source.StatusKind,
                StatusDetail = source.StatusDetail,
                CanResend = source.CanResend,
                QuestionDetectedAtTicks = source.QuestionDetectedAtTicks,
                AnswerReadyAtTicks = source.AnswerReadyAtTicks,
                CreatedAtTicks = source.CreatedAtTicks,
                UpdatedAtTicks = source.UpdatedAtTicks
            };
        }
    }
}
