using Bot.Common;
using BotLib;
using BotLib.Db.Sqlite;
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
        public string RecordId { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
    }

    internal static class BotConversationHistoryStore
    {
        public const int RetentionDays = 90;
        public const int MaxTotalRecords = 10000;
        public const int MaxRecordsPerConversation = 200;
        public const int DefaultLoadCount = 100;

        private static readonly object DbSync = new object();
        private static readonly ConcurrentDictionary<string, BotConversationHistoryEntity> Pending =
            new ConcurrentDictionary<string, BotConversationHistoryEntity>(StringComparer.Ordinal);

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
                + "条，单买家最多" + MaxRecordsPerConversation + "条。");
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
                var row = new BotReplySourceStampEntity
                {
                    StampId = BuildSourceStampId(seller, buyer, question, answer),
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
                var id = BuildSourceStampId(seller, buyer, question, answer);
                BotReplySourceStampEntity row;
                lock (DbSync) row = DbHelper.Db.ReadOneRecord<BotReplySourceStampEntity>(x => x.StampId == id);
                return row == null ? string.Empty : (row.AnswerSource ?? string.Empty).Trim();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取Bot回复来源标签失败：" + ex.Message, 10);
                return string.Empty;
            }
        }

        public static List<BotConversationHistoryEntity> LoadRecentBySeller(string seller, int maxCount = 2000)
        {
            Initialize();
            FlushNow();
            EnsureSchema();
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return new List<BotConversationHistoryEntity>();
            var take = Math.Max(1, Math.Min(5000, maxCount <= 0 ? 2000 : maxCount));
            var cutoffTicks = DateTime.Now.AddDays(-RetentionDays).Ticks;
            try
            {
                lock (DbSync)
                {
                    var rows = DbHelper.Db.Select(
                        typeof(BotConversationHistoryEntity),
                        "where Seller = ? and UpdatedAtTicks >= ? order by CreatedAtTicks desc limit " + take,
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

            try
            {
                FlushNow();
                var echoId = BuildBotEchoEntityId(seller, buyer, messageKey, sentAt, answer);
                var candidates = LoadRecent(seller, buyer, 200);
                var answerKey = NormalizeSourceValue(answer);
                var targetTicks = sentAt.Ticks;

                // Re-rendering the same Qianniu echo must resolve to the original
                // record even when that record has already been marked as sent.
                BotReplyEchoLinkEntity existingLink;
                lock (DbSync)
                    existingLink = DbHelper.Db.ReadOneRecord<BotReplyEchoLinkEntity>(
                        x => x.EchoId == echoId);
                var originalId = existingLink != null
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
                                x => x.RecordId == candidateRecordId);
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
                    var manual = DbHelper.Db.ReadOneRecord<BotReplyManualSourceEntity>(
                        x => x.EntityId == match.EntityId);
                    match.AnswerSource = manual != null
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
                return BuildBotEchoEntityId(seller, buyer, messageKey, sentAt, answer);
            }
        }

        public static string BuildBotEchoEntityId(
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
            seller = (seller ?? string.Empty).Trim();
            entityId = (entityId ?? string.Empty).Trim();
            source = (source ?? string.Empty).Trim();
            if (seller.Length == 0 || entityId.Length == 0 || source.Length == 0) return false;
            try
            {
                FlushNow();
                EnsureSchema();
                lock (DbSync)
                {
                    var row = DbHelper.Db.ReadOneRecord<BotConversationHistoryEntity>(x => x.EntityId == entityId);
                    if (row == null || !string.Equals(row.Seller, seller, StringComparison.Ordinal)) return false;
                    if (source.Length > 200) return false;
                    var original = DbHelper.Db.ReadOneRecord<BotReplyManualSourceEntity>(
                        x => x.EntityId == entityId);
                    if (original == null)
                    {
                        original = new BotReplyManualSourceEntity
                        {
                            EntityId = entityId, Seller = seller,
                            OriginalSource = row.AnswerSource ?? string.Empty
                        };
                    }
                    if (!string.Equals(original.Seller, seller, StringComparison.Ordinal)) return false;
                    original.CorrectedSource = source;
                    original.CorrectedAtTicks = DateTime.Now.Ticks;
                    DbHelper.Db.SaveOneRecord(original);
                    row.AnswerSource = source;
                    row.UpdatedAtTicks = DateTime.Now.Ticks;
                    DbHelper.Db.SaveOneRecord(row);
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
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(entityId)) return string.Empty;
            try
            {
                EnsureSchema();
                lock (DbSync)
                {
                    var row = DbHelper.Db.ReadOneRecord<BotReplyManualSourceEntity>(
                        x => x.EntityId == entityId);
                    return row == null || !string.Equals(row.Seller, seller, StringComparison.Ordinal)
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
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(entityId))
                return string.Empty;
            try
            {
                EnsureSchema();
                lock (DbSync)
                {
                    var row = DbHelper.Db.ReadOneRecord<BotReplyManualSourceEntity>(
                        x => x.EntityId == entityId);
                    return row == null || !string.Equals(row.Seller, seller, StringComparison.Ordinal)
                        ? string.Empty : row.OriginalSource ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("读取Bot来源人工更正前证据失败：" + ex.Message, 5);
                return string.Empty;
            }
        }

        private static string BuildSourceStampId(string seller, string buyer, string question, string answer)
        {
            return Sha256((seller ?? string.Empty).Trim() + "\n"
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
            copy.Seller = (copy.Seller ?? string.Empty).Trim();
            copy.Buyer = (copy.Buyer ?? string.Empty).Trim();
            if (copy.EntityId.Length == 0 || copy.Seller.Length == 0 || copy.Buyer.Length == 0) return;

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

        public static List<BotConversationHistoryEntity> LoadRecent(string seller, string buyer, int maxCount = DefaultLoadCount)
        {
            Initialize();
            FlushNow();
            EnsureSchema();

            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0) return new List<BotConversationHistoryEntity>();

            var take = Math.Max(1, Math.Min(MaxRecordsPerConversation, maxCount <= 0 ? DefaultLoadCount : maxCount));
            var cutoffTicks = DateTime.Now.AddDays(-RetentionDays).Ticks;
            try
            {
                lock (DbSync)
                {
                    var rows = DbHelper.Db.Select(
                        typeof(BotConversationHistoryEntity),
                        "where Seller = ? and Buyer = ? and UpdatedAtTicks >= ? order by CreatedAtTicks desc limit " + take,
                        seller,
                        buyer,
                        cutoffTicks);
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
                Log.ErrorWithMaxCount("读取Bot问答历史失败：" + ex.Message, 10);
                return new List<BotConversationHistoryEntity>();
            }
        }

        public static List<BotConversationHistoryEntity> LoadRange(
            string seller,
            string buyer,
            DateTime from,
            DateTime to,
            int maxCount = 120)
        {
            Initialize();
            FlushNow();
            EnsureSchema();

            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0) return new List<BotConversationHistoryEntity>();

            var take = Math.Max(1, Math.Min(MaxRecordsPerConversation, maxCount <= 0 ? 120 : maxCount));
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
                        "where Seller = ? and Buyer = ? and CreatedAtTicks >= ? and CreatedAtTicks <= ? "
                        + "order by CreatedAtTicks asc limit " + take,
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
                    foreach (var conversation in batch
                        .Select(x => new { x.Seller, x.Buyer })
                        .Distinct())
                    {
                        TrimConversationUnsafe(conversation.Seller, conversation.Buyer);
                    }
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
                    Pending[entity.EntityId] = entity;
                }
                throw;
            }
        }

        private static void TrimConversationUnsafe(string seller, string buyer)
        {
            DbHelper.Db.Execute(
                "delete from BotConversationHistoryEntity where Seller = ? and Buyer = ? and EntityId not in "
                + "(select EntityId from BotConversationHistoryEntity where Seller = ? and Buyer = ? "
                + "order by UpdatedAtTicks desc limit " + MaxRecordsPerConversation + ")",
                seller,
                buyer,
                seller,
                buyer);
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
                DbHelper.Db.Execute(
                    "create index if not exists IX_BotConversationHistory_SellerBuyerUpdated "
                    + "on BotConversationHistoryEntity(Seller, Buyer, UpdatedAtTicks)");
                DbHelper.Db.Execute(
                    "create index if not exists IX_BotConversationHistory_Updated "
                    + "on BotConversationHistoryEntity(UpdatedAtTicks)");
                DbHelper.Db.Execute(
                    "create table if not exists BotReplySourceStampEntity ("
                    + "StampId text primary key not null,"
                    + "Seller text not null,"
                    + "Buyer text not null,"
                    + "QuestionKey text,"
                    + "AnswerKey text,"
                    + "AnswerSource text,"
                    + "UpdatedAtTicks integer not null default 0"
                    + ")");
                DbHelper.Db.Execute(
                    "create index if not exists IX_BotReplySourceStamp_Lookup "
                    + "on BotReplySourceStampEntity(Seller, Buyer, UpdatedAtTicks)");
                DbHelper.Db.Execute(
                    "create table if not exists BotReplyManualSourceEntity ("
                    + "EntityId text primary key not null,"
                    + "Seller text not null,"
                    + "OriginalSource text,"
                    + "CorrectedSource text,"
                    + "CorrectedAtTicks integer not null default 0"
                    + ")");
                DbHelper.Db.Execute(
                    "create table if not exists BotReplyEchoLinkEntity ("
                    + "EchoId text primary key not null,"
                    + "RecordId text not null,"
                    + "Seller text not null,"
                    + "Buyer text not null"
                    + ")");
                DbHelper.Db.Execute(
                    "create index if not exists IX_BotReplyEchoLink_Record "
                    + "on BotReplyEchoLinkEntity(RecordId)");
                Volatile.Write(ref _schemaReady, 1);
            }
        }

        private static BotConversationHistoryEntity Clone(BotConversationHistoryEntity source)
        {
            return new BotConversationHistoryEntity
            {
                EntityId = source.EntityId,
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
