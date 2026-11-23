using Bot.ChatRecord;
using Bot.Common;
using BotLib;
using BotLib.Db.Sqlite;
using Bot.ShopScope;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class VisualKnowledgeObservationEntity
    {
        [PrimaryKey] public string EntityId { get; set; }
        public string ShopKey { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public string MessageKey { get; set; }
        public string VisualQuestion { get; set; }
        public string VisualSummary { get; set; }
        public string VisualTags { get; set; }
        public string GeneratedAnswer { get; set; }
        public string Status { get; set; }
        public string LearnedKnowledgeId { get; set; }
        public long ObservedAtTicks { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    internal sealed class VisualKnowledgeEntryEntity
    {
        [PrimaryKey] public string EntityId { get; set; }
        public string ShopKey { get; set; }
        public string Seller { get; set; }
        public string VisualQuestion { get; set; }
        public string VisualSummary { get; set; }
        public string VisualTags { get; set; }
        public string Answer { get; set; }
        public string SourceType { get; set; }
        public int Confirmations { get; set; }
        public bool Enabled { get; set; }
        public long CreatedAtTicks { get; set; }
        public long UpdatedAtTicks { get; set; }
    }

    internal sealed class VisualKnowledgeMatch
    {
        public string KnowledgeId { get; set; }
        public string Answer { get; set; }
        public string VisualQuestion { get; set; }
        public string VisualSummary { get; set; }
        public string VisualTags { get; set; }
        public double Score { get; set; }
    }

    internal static class VisualKnowledgeLearningService
    {
        public const int BuyerQuietMinutes = 5;
        public const int SellerQuietSeconds = 30;
        public const int ObservationRetentionDays = 30;
        public const int MaxObservations = 2000;
        public const int MaxKnowledgeEntriesPerSeller = 500;
        public const double MatchThreshold = 0.74;

        private static readonly object DbSync = new object();
        private static readonly ConcurrentDictionary<string, Timer> Timers =
            new ConcurrentDictionary<string, Timer>(StringComparer.Ordinal);
        private static int _initialized;
        private static int _schemaReady;

        public static void RecordVisionAnalysis(
            string shopKey,
            string seller,
            string buyer,
            QNChatMessage message,
            string messageKey,
            string visualQuestion,
            string visualSummary,
            string visualTags,
            string generatedAnswer)
        {
            shopKey = Clean(shopKey, 200);
            seller = Clean(seller, 160);
            buyer = Clean(buyer, 160);
            visualQuestion = Clean(visualQuestion, 500);
            visualSummary = Redact(Clean(visualSummary, 1600));
            visualTags = NormalizeTags(Redact(visualTags));
            generatedAnswer = Clean(generatedAnswer, 1200);
            if (shopKey.Length == 0 || seller.Length == 0 || buyer.Length == 0 || visualSummary.Length == 0) return;

            EnsureInitialized();
            BackfillLegacyShopRows(shopKey, seller);
            var withdrawn = VisionImageCacheService.IsMessageKeyWithdrawn(
                seller, buyer, messageKey, shopKey);
            var observedAt = GetMessageTime(message);
            if (observedAt == DateTime.MinValue || observedAt > DateTime.Now.AddMinutes(2)) observedAt = DateTime.Now;
            var entity = new VisualKnowledgeObservationEntity
            {
                EntityId = Guid.NewGuid().ToString("N"),
                ShopKey = shopKey,
                Seller = seller,
                Buyer = buyer,
                MessageKey = Clean(messageKey, 500),
                VisualQuestion = visualQuestion,
                VisualSummary = visualSummary,
                VisualTags = visualTags,
                GeneratedAnswer = generatedAnswer,
                Status = withdrawn ? "图片已撤回" : "等待人工接待结束",
                LearnedKnowledgeId = string.Empty,
                ObservedAtTicks = observedAt.Ticks,
                UpdatedAtTicks = DateTime.Now.Ticks
            };

            lock (DbSync)
            {
                if (!string.IsNullOrWhiteSpace(entity.MessageKey))
                {
                    var old = SelectObservations("where ShopKey = ? and Seller = ? and Buyer = ? and MessageKey = ? order by UpdatedAtTicks desc limit 1",
                        shopKey, seller, buyer, entity.MessageKey).FirstOrDefault();
                    if (old != null)
                    {
                        old.VisualQuestion = entity.VisualQuestion;
                        old.VisualSummary = entity.VisualSummary;
                        old.VisualTags = entity.VisualTags;
                        old.GeneratedAnswer = entity.GeneratedAnswer;
                        if (withdrawn) old.Status = "图片已撤回";
                        old.UpdatedAtTicks = DateTime.Now.Ticks;
                        entity = old;
                    }
                }
                DbHelper.Db.SaveRecordsInTransaction(new List<object> { entity });
            }
            if (string.Equals(entity.Status, "等待人工接待结束", StringComparison.Ordinal))
                Schedule(entity.EntityId, TimeSpan.FromMinutes(BuyerQuietMinutes));
            Cleanup();
            Log.Info("已记录视觉知识学习候选: seller=" + seller + ", buyer=" + buyer + ", summary=" + Short(visualSummary, 120));
        }

        internal static bool IsObservationWithdrawn(
            string shopKey, string seller, string buyer, string messageKey)
        {
            shopKey = Clean(shopKey, 200);
            seller = Clean(seller, 160);
            buyer = Clean(buyer, 160);
            messageKey = Clean(messageKey, 500);
            if (shopKey.Length == 0 || seller.Length == 0 || buyer.Length == 0 || messageKey.Length == 0)
                return true;
            try
            {
                EnsureSchema();
                lock (DbSync)
                {
                    var observations = SelectObservations(
                        "where ShopKey = ? and Seller = ? and MessageKey = ? order by UpdatedAtTicks desc",
                        shopKey, seller, messageKey);
                    if (observations.Count == 0) return false;
                    // Message keys are expected to be unique within one shop/seller. If legacy
                    // alias rows exist, inspect all of them: a newer active duplicate must never
                    // hide an older withdrawal tombstone for the same source message.
                    return observations.Any(observation =>
                        !string.Equals(observation.Buyer, buyer, StringComparison.OrdinalIgnoreCase)
                            && !BuyerIdentityAliasService.AreEquivalent(seller, observation.Buyer, buyer)
                        || string.Equals(observation.Status, "图片已撤回", StringComparison.Ordinal));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("检查视觉证据撤回状态失败，按撤回处理：" + ex.Message, 10);
                return true;
            }
        }

        internal static void MarkObservationWithdrawn(
            string shopKey, string seller, string buyer, string messageKey)
        {
            shopKey = Clean(shopKey, 200);
            seller = Clean(seller, 160);
            buyer = Clean(buyer, 160);
            messageKey = Clean(messageKey, 500);
            if (shopKey.Length == 0 || seller.Length == 0 || buyer.Length == 0 || messageKey.Length == 0)
                return;

            EnsureInitialized();
            string observationId = string.Empty;
            lock (DbSync)
            {
                var observation = SelectObservations(
                    "where ShopKey = ? and Seller = ? and Buyer = ? and MessageKey = ? order by UpdatedAtTicks desc limit 1",
                    shopKey, seller, buyer, messageKey).FirstOrDefault();
                if (observation == null)
                {
                    var now = DateTime.Now.Ticks;
                    observation = new VisualKnowledgeObservationEntity
                    {
                        EntityId = Guid.NewGuid().ToString("N"),
                        ShopKey = shopKey,
                        Seller = seller,
                        Buyer = buyer,
                        MessageKey = messageKey,
                        Status = "图片已撤回",
                        LearnedKnowledgeId = string.Empty,
                        ObservedAtTicks = now,
                        UpdatedAtTicks = now
                    };
                }
                else
                {
                    observation.Status = "图片已撤回";
                    observation.UpdatedAtTicks = DateTime.Now.Ticks;
                }
                var updates = new List<object> { observation };
                if (!string.IsNullOrWhiteSpace(observation.LearnedKnowledgeId))
                {
                    var learned = SelectKnowledge(
                        "where EntityId = ? limit 1", observation.LearnedKnowledgeId).FirstOrDefault();
                    if (learned != null && learned.Enabled)
                    {
                        // A withdrawn image must not leave a visual FAQ active after it was
                        // learned from that exact message. Re-learning from later valid evidence
                        // can safely re-enable the entry.
                        learned.Enabled = false;
                        learned.UpdatedAtTicks = DateTime.Now.Ticks;
                        updates.Add(learned);
                    }
                }
                DbHelper.Db.SaveRecordsInTransaction(updates);
                observationId = observation.EntityId;
            }

            Timer timer;
            if (Timers.TryRemove(observationId, out timer))
            {
                try { timer.Dispose(); } catch { }
            }
        }

        internal static bool TrySaveManualVisualKnowledge(
            VisualKnowledgeObservationEntity observation,
            VisualKnowledgeEntryEntity knowledge)
        {
            if (observation == null || knowledge == null
                || string.IsNullOrWhiteSpace(observation.EntityId)
                || string.IsNullOrWhiteSpace(observation.MessageKey))
                return false;
            try
            {
                EnsureInitialized();
                lock (DbSync)
                {
                    var current = SelectObservations(
                        "where EntityId = ? limit 1", observation.EntityId).FirstOrDefault();
                    if (current == null
                        || string.Equals(current.Status, "图片已撤回", StringComparison.Ordinal)
                        || !string.Equals(current.ShopKey, observation.ShopKey, StringComparison.Ordinal)
                        || !string.Equals(current.Seller, observation.Seller, StringComparison.Ordinal)
                        || !string.Equals(current.MessageKey, observation.MessageKey, StringComparison.Ordinal)
                        || (!string.Equals(current.Buyer, observation.Buyer, StringComparison.OrdinalIgnoreCase)
                            && !BuyerIdentityAliasService.AreEquivalent(
                                current.Seller, current.Buyer, observation.Buyer))
                        || !string.Equals(knowledge.ShopKey, current.ShopKey, StringComparison.Ordinal)
                        || !string.Equals(knowledge.Seller, current.Seller, StringComparison.OrdinalIgnoreCase)
                        || VisionImageCacheService.IsMessageKeyWithdrawn(
                            current.Seller, current.Buyer, current.MessageKey, current.ShopKey))
                        return false;

                    var now = DateTime.Now.Ticks;
                    current.Status = "已通过人工即时回复学习";
                    current.LearnedKnowledgeId = knowledge.EntityId;
                    current.UpdatedAtTicks = now;
                    knowledge.Enabled = true;
                    knowledge.UpdatedAtTicks = now;
                    DbHelper.Db.SaveRecordsInTransaction(new List<object> { knowledge, current });
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("人工视觉知识最终撤回复核失败，已阻止写入：" + ex.Message, 10);
                return false;
            }
        }

        public static bool TryFindMatch(
            string shopKey,
            string seller,
            string visualQuestion,
            string visualSummary,
            string visualTags,
            out VisualKnowledgeMatch match)
        {
            match = null;
            shopKey = Clean(shopKey, 200);
            seller = Clean(seller, 160);
            visualQuestion = Clean(visualQuestion, 500);
            visualSummary = Redact(Clean(visualSummary, 1600));
            visualTags = NormalizeTags(Redact(visualTags));
            if (shopKey.Length == 0 || seller.Length == 0 || visualSummary.Length == 0) return false;

            EnsureInitialized();
            BackfillLegacyShopRows(shopKey, seller);
            List<VisualKnowledgeEntryEntity> entries;
            lock (DbSync)
            {
                entries = SelectKnowledge("where ShopKey = ? and Seller = ? and Enabled = 1 order by UpdatedAtTicks desc limit " + MaxKnowledgeEntriesPerSeller, shopKey, seller);
            }

            VisualKnowledgeEntryEntity best = null;
            var bestScore = 0.0;
            foreach (var entry in entries)
            {
                var score = Similarity(visualQuestion, visualSummary, visualTags,
                    entry.VisualQuestion, entry.VisualSummary, entry.VisualTags);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = entry;
                }
            }
            if (best == null || bestScore < MatchThreshold) return false;

            match = new VisualKnowledgeMatch
            {
                KnowledgeId = best.EntityId,
                Answer = best.Answer,
                VisualQuestion = best.VisualQuestion,
                VisualSummary = best.VisualSummary,
                VisualTags = best.VisualTags,
                Score = bestScore
            };
            Log.Info("命中视觉人工知识: shopKey=" + shopKey + ", seller=" + seller + ", knowledgeId=" + best.EntityId
                + ", score=" + bestScore.ToString("0.00", CultureInfo.InvariantCulture));
            return true;
        }

        private static void EnsureInitialized()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            EnsureSchema();
            ResumePending();
        }

        private static void Schedule(string id, TimeSpan due)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            Timer old;
            if (Timers.TryRemove(id, out old)) { try { old.Dispose(); } catch { } }
            var ms = (int)Math.Min(int.MaxValue, Math.Max(1000, due.TotalMilliseconds));
            Timers[id] = new Timer(_ =>
            {
                Timer timer;
                if (Timers.TryRemove(id, out timer)) { try { timer.Dispose(); } catch { } }
                Task.Run(() => FinalizeObservation(id));
            }, null, ms, Timeout.Infinite);
        }

        private static void FinalizeObservation(string id)
        {
            var observation = LoadObservation(id);
            if (observation == null || observation.Status != "等待人工接待结束") return;
            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeByShopKey(observation.ShopKey); }
            catch { Schedule(id, TimeSpan.FromMinutes(1)); return; }
            if (shop == null || !string.Equals(shop.ShopKey, observation.ShopKey, StringComparison.Ordinal)
                || !string.Equals(shop.SellerNick, observation.Seller, StringComparison.Ordinal))
            {
                Schedule(id, TimeSpan.FromMinutes(1));
                return;
            }
            using (ShopSettingsScope.Enter(shop))
            {
            var observedAt = FromTicks(observation.ObservedAtTicks);
            var turns = ConversationContextStore.GetRecentTurns(observation.Seller, observation.Buyer, string.Empty, 24)
                .Where(x => x != null && x.Timestamp != DateTime.MinValue)
                .OrderBy(x => x.Timestamp)
                .ToList();

            var lastBuyer = turns.Where(x => x.Role == "user" && x.Timestamp >= observedAt.AddSeconds(-3))
                .OrderByDescending(x => x.Timestamp).FirstOrDefault();
            if (lastBuyer != null)
            {
                var remaining = TimeSpan.FromMinutes(BuyerQuietMinutes) - (DateTime.Now - lastBuyer.Timestamp);
                if (remaining > TimeSpan.Zero) { Schedule(id, remaining); return; }
            }

            var lastSeller = turns.Where(x => x.Role == "assistant" && x.Timestamp >= observedAt.AddSeconds(-3))
                .OrderByDescending(x => x.Timestamp).FirstOrDefault();
            if (lastSeller != null)
            {
                var remaining = TimeSpan.FromSeconds(SellerQuietSeconds) - (DateTime.Now - lastSeller.Timestamp);
                if (remaining > TimeSpan.Zero) { Schedule(id, remaining); return; }
            }

            var humanReply = turns.Where(x => x.Role == "assistant" && !x.Withdrawn)
                .Where(x => x.Timestamp >= observedAt.AddSeconds(-5))
                .Where(x => !IsBotReply(x.Text) && !string.IsNullOrWhiteSpace(x.Text))
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();
            if (humanReply == null)
            {
                UpdateObservationStatus(observation, "未发现人工确认回复", string.Empty);
                return;
            }

            var answer = StripAi(Clean(humanReply.Text, 1200));
            if (answer.Length == 0 || ContainsHighRisk(answer))
            {
                UpdateObservationStatus(observation, "人工回复不适合自动学习", string.Empty);
                return;
            }

            var knowledge = UpsertKnowledge(observation, answer);
            if (knowledge == null)
            {
                var latest = LoadObservation(id);
                if (latest == null || !string.Equals(latest.Status, "等待人工接待结束", StringComparison.Ordinal))
                    return;
            }
            UpdateObservationStatus(observation,
                knowledge == null ? "视觉知识写入失败" : "已学习",
                knowledge == null ? string.Empty : knowledge.EntityId);
            if (knowledge != null)
            {
                Log.Info("视觉人工知识学习完成: shopKey=" + observation.ShopKey + ", seller=" + observation.Seller + ", buyer=" + observation.Buyer
                    + ", knowledgeId=" + knowledge.EntityId + ", answer=" + Short(answer, 120));
            }
            }
        }

        private static VisualKnowledgeEntryEntity UpsertKnowledge(VisualKnowledgeObservationEntity observation, string answer)
        {
            try
            {
                lock (DbSync)
                {
                    var currentObservation = SelectObservations(
                        "where EntityId = ? limit 1", observation.EntityId).FirstOrDefault();
                    if (currentObservation == null
                        || !string.Equals(currentObservation.Status, "等待人工接待结束", StringComparison.Ordinal))
                        return null;
                    var entries = SelectKnowledge("where ShopKey = ? and Seller = ? and Enabled = 1 order by UpdatedAtTicks desc limit "
                        + MaxKnowledgeEntriesPerSeller, observation.ShopKey, observation.Seller);
                    VisualKnowledgeEntryEntity best = null;
                    var bestScore = 0.0;
                    foreach (var entry in entries)
                    {
                        var score = Similarity(observation.VisualQuestion, observation.VisualSummary, observation.VisualTags,
                            entry.VisualQuestion, entry.VisualSummary, entry.VisualTags);
                        if (score > bestScore) { bestScore = score; best = entry; }
                    }
                    var now = DateTime.Now.Ticks;
                    if (best != null && bestScore >= 0.70)
                    {
                        best.VisualQuestion = PreferLonger(best.VisualQuestion, observation.VisualQuestion);
                        best.VisualSummary = PreferLonger(best.VisualSummary, observation.VisualSummary);
                        best.VisualTags = MergeTags(best.VisualTags, observation.VisualTags);
                        best.Answer = answer;
                        best.SourceType = "视觉人工学习";
                        best.Confirmations = Math.Max(1, best.Confirmations) + 1;
                        best.Enabled = true;
                        best.UpdatedAtTicks = now;
                        currentObservation.Status = "已学习";
                        currentObservation.LearnedKnowledgeId = best.EntityId;
                        currentObservation.UpdatedAtTicks = now;
                        // Persist the knowledge and its exact source observation atomically.
                        // Withdrawal can then either win before this transaction or disable
                        // the linked entry after it; it cannot race between the two writes.
                        DbHelper.Db.SaveRecordsInTransaction(new List<object> { best, currentObservation });
                        return best;
                    }

                    var created = new VisualKnowledgeEntryEntity
                    {
                        EntityId = Guid.NewGuid().ToString("N"),
                        ShopKey = observation.ShopKey,
                        Seller = observation.Seller,
                        VisualQuestion = observation.VisualQuestion,
                        VisualSummary = observation.VisualSummary,
                        VisualTags = observation.VisualTags,
                        Answer = answer,
                        SourceType = "视觉人工学习",
                        Confirmations = 1,
                        Enabled = true,
                        CreatedAtTicks = now,
                        UpdatedAtTicks = now
                    };
                    currentObservation.Status = "已学习";
                    currentObservation.LearnedKnowledgeId = created.EntityId;
                    currentObservation.UpdatedAtTicks = now;
                    DbHelper.Db.SaveRecordsInTransaction(new List<object> { created, currentObservation });
                    DbHelper.Db.Execute("delete from VisualKnowledgeEntryEntity where ShopKey = ? and Seller = ? and EntityId not in "
                        + "(select EntityId from VisualKnowledgeEntryEntity where ShopKey = ? and Seller = ? order by UpdatedAtTicks desc limit "
                        + MaxKnowledgeEntriesPerSeller + ")", observation.ShopKey, observation.Seller, observation.ShopKey, observation.Seller);
                    return created;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("写入视觉人工知识失败：" + ex.Message, 20);
                return null;
            }
        }

        private static void UpdateObservationStatus(VisualKnowledgeObservationEntity item, string status, string knowledgeId)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.EntityId)) return;
            lock (DbSync)
            {
                var current = SelectObservations("where EntityId = ? limit 1", item.EntityId).FirstOrDefault();
                if (current == null
                    || !string.Equals(current.Status, "等待人工接待结束", StringComparison.Ordinal))
                    return;
                current.Status = status;
                current.LearnedKnowledgeId = knowledgeId ?? string.Empty;
                current.UpdatedAtTicks = DateTime.Now.Ticks;
                DbHelper.Db.SaveRecordsInTransaction(new List<object> { current });
            }
        }

        private static VisualKnowledgeObservationEntity LoadObservation(string id)
        {
            EnsureSchema();
            lock (DbSync) return SelectObservations("where EntityId = ? limit 1", id).FirstOrDefault();
        }

        private static List<VisualKnowledgeObservationEntity> SelectObservations(string clause, params object[] args)
        {
            return (DbHelper.Db.Select(typeof(VisualKnowledgeObservationEntity), clause, args) ?? new List<object>())
                .OfType<VisualKnowledgeObservationEntity>().ToList();
        }

        private static List<VisualKnowledgeEntryEntity> SelectKnowledge(string clause, params object[] args)
        {
            return (DbHelper.Db.Select(typeof(VisualKnowledgeEntryEntity), clause, args) ?? new List<object>())
                .OfType<VisualKnowledgeEntryEntity>().ToList();
        }

        private static void ResumePending()
        {
            try
            {
                List<VisualKnowledgeObservationEntity> pending;
                lock (DbSync)
                {
                    pending = SelectObservations("where Status = ? and ObservedAtTicks >= ? order by UpdatedAtTicks desc limit 200",
                        "等待人工接待结束", DateTime.Now.AddDays(-1).Ticks);
                }
                foreach (var item in pending)
                {
                    if (string.IsNullOrWhiteSpace(item.ShopKey))
                    {
                        try
                        {
                            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(item.Seller);
                            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) continue;
                            item.ShopKey = shop.ShopKey;
                            lock (DbSync) DbHelper.Db.SaveRecordsInTransaction(new List<object> { item });
                        }
                        catch (AmbiguousShopIdentityException) { continue; }
                        catch { continue; }
                    }
                    BackfillLegacyShopRows(item.ShopKey, item.Seller);
                    var due = TimeSpan.FromMinutes(BuyerQuietMinutes) - (DateTime.Now - FromTicks(item.ObservedAtTicks));
                    Schedule(item.EntityId, due > TimeSpan.Zero ? due : TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex) { Log.ErrorWithMaxCount("恢复视觉知识学习任务失败：" + ex.Message, 10); }
        }

        private static void EnsureSchema()
        {
            if (Volatile.Read(ref _schemaReady) != 0) return;
            lock (DbSync)
            {
                if (_schemaReady != 0) return;
                DbHelper.Db.Execute("create table if not exists VisualKnowledgeObservationEntity (EntityId text primary key not null,ShopKey text,Seller text,Buyer text,MessageKey text,VisualQuestion text,VisualSummary text,VisualTags text,GeneratedAnswer text,Status text,LearnedKnowledgeId text,ObservedAtTicks integer not null default 0,UpdatedAtTicks integer not null default 0)");
                try { DbHelper.Db.Execute("alter table VisualKnowledgeObservationEntity add column ShopKey text"); } catch { }
                DbHelper.Db.Execute("create index if not exists IX_VisualKnowledgeObservation_ShopSellerBuyer on VisualKnowledgeObservationEntity(ShopKey,Seller,Buyer,ObservedAtTicks)");
                DbHelper.Db.Execute("create table if not exists VisualKnowledgeEntryEntity (EntityId text primary key not null,ShopKey text,Seller text,VisualQuestion text,VisualSummary text,VisualTags text,Answer text,SourceType text,Confirmations integer not null default 0,Enabled integer not null default 1,CreatedAtTicks integer not null default 0,UpdatedAtTicks integer not null default 0)");
                try { DbHelper.Db.Execute("alter table VisualKnowledgeEntryEntity add column ShopKey text"); } catch { }
                DbHelper.Db.Execute("create index if not exists IX_VisualKnowledgeEntry_ShopSeller on VisualKnowledgeEntryEntity(ShopKey,Seller,Enabled,UpdatedAtTicks)");
                Volatile.Write(ref _schemaReady, 1);
            }
        }

        private static void BackfillLegacyShopRows(string shopKey, string seller)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0) return;
            try
            {
                var resolved = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
                if (resolved == null || !string.Equals(resolved.ShopKey, shopKey, StringComparison.Ordinal))
                    return;
                lock (DbSync)
                {
                    DbHelper.Db.Execute(
                        "update VisualKnowledgeObservationEntity set ShopKey = ? where Seller = ? and (ShopKey is null or ShopKey = '')",
                        shopKey, seller);
                    DbHelper.Db.Execute(
                        "update VisualKnowledgeEntryEntity set ShopKey = ? where Seller = ? and (ShopKey is null or ShopKey = '')",
                        shopKey, seller);
                }
            }
            catch (AmbiguousShopIdentityException)
            {
                // Legacy visual knowledge remains unassigned when the seller nickname is shared.
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("迁移旧版视觉知识店铺归属失败: " + ex.Message, 5);
            }
        }

        private static void Cleanup()
        {
            try
            {
                lock (DbSync)
                {
                    DbHelper.Db.Execute("delete from VisualKnowledgeObservationEntity where UpdatedAtTicks < ?", DateTime.Now.AddDays(-ObservationRetentionDays).Ticks);
                    DbHelper.Db.Execute("delete from VisualKnowledgeObservationEntity where EntityId not in (select EntityId from VisualKnowledgeObservationEntity order by UpdatedAtTicks desc limit " + MaxObservations + ")");
                }
            }
            catch (Exception ex) { Log.ErrorWithMaxCount("清理视觉知识学习记录失败：" + ex.Message, 10); }
        }

        private static double Similarity(string q1, string s1, string t1, string q2, string s2, string t2)
        {
            var a = SplitTags(t1);
            var b = SplitTags(t2);
            var common = a.Intersect(b).Count();
            var union = a.Union(b).Count();
            var tagScore = union == 0 ? 0 : (double)common / union;
            var summaryScore = BigramSimilarity(Normalize(s1), Normalize(s2));
            var questionScore = BigramSimilarity(Normalize(q1), Normalize(q2));
            var score = Math.Max(summaryScore * 0.70 + tagScore * 0.30, questionScore * 0.75 + tagScore * 0.25);
            if (a.Count > 0 && b.Count > 0 && common == 0 && score < 0.88) score *= 0.72;
            return Math.Max(0, Math.Min(1, score));
        }

        private static double BigramSimilarity(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a == b) return 1;
            var aa = Bigrams(a); var bb = Bigrams(b);
            if (aa.Count == 0 || bb.Count == 0) return 0;
            return (2.0 * aa.Intersect(bb).Count()) / (aa.Count + bb.Count);
        }

        private static HashSet<string> Bigrams(string value)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < value.Length; i++) set.Add(value.Substring(i, 2));
            return set;
        }

        private static HashSet<string> SplitTags(string value)
        {
            return new HashSet<string>((value ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Normalize).Where(x => x.Length >= 2), StringComparer.Ordinal);
        }

        private static string NormalizeTags(string value) { return string.Join(",", SplitTags(value).Take(20)); }
        private static string MergeTags(string a, string b) { return string.Join(",", SplitTags(a).Union(SplitTags(b)).Take(20)); }
        private static string PreferLonger(string a, string b) { a = a ?? string.Empty; b = b ?? string.Empty; return b.Length > a.Length ? b : a; }
        private static bool IsBotReply(string value) { return (value ?? string.Empty).Trim().EndsWith("[AI]", StringComparison.OrdinalIgnoreCase); }

        private static bool ContainsHighRisk(string value)
        {
            var terms = new[] { "退款", "退货", "赔偿", "投诉", "差评", "举报", "仲裁", "身份证", "银行卡", "验证码", "密码", "订单隐私", "订单号", "手机号", "账号安全", "封号", "解封", "法律", "报警" };
            return terms.Any(x => (value ?? string.Empty).IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static DateTime GetMessageTime(QNChatMessage message)
        {
            if (message == null) return DateTime.MinValue;
            DateTime result;
            if (TryParseTime(message.sendTime, out result)) return result;
            if (TryParseTime(message.sortTimeMicrosecond, out result)) return result;
            return DateTime.MinValue;
        }

        private static bool TryParseTime(string value, out DateTime result)
        {
            result = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            long raw;
            if (long.TryParse(value.Trim(), out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) result = DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    else if (raw > 100000000000L) result = DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    else if (raw > 1000000000L) result = DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                    if (result != DateTime.MinValue) return true;
                }
                catch { }
            }
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto)
                || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                result = dto.LocalDateTime;
                return true;
            }
            return false;
        }

        private static DateTime FromTicks(long ticks) { try { return ticks > 0 ? new DateTime(ticks) : DateTime.MinValue; } catch { return DateTime.MinValue; } }
        private static string StripAi(string value) { value = (value ?? string.Empty).Trim(); while (value.EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 4).TrimEnd(); return value; }
        private static string Normalize(string value) { return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s\p{P}\p{S}]+", string.Empty); }
        private static string Clean(string value, int max) { value = (value ?? string.Empty).Trim(); return max > 0 && value.Length > max ? value.Substring(0, max).Trim() : value; }
        private static string Short(string value, int max) { value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(); return value.Length > max ? value.Substring(0, max) + "..." : value; }
        private static string Redact(string value)
        {
            value = value ?? string.Empty;
            value = Regex.Replace(value, @"(?<!\d)1\d{10}(?!\d)", "[手机号]");
            value = Regex.Replace(value, @"(?<!\d)\d{8,}(?!\d)", "[编号]");
            value = Regex.Replace(value, @"(?i)(验证码|校验码)[：:\s]*\d{4,8}", "$1：[已脱敏]");
            return value;
        }
    }
}
