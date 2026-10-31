using Bot.ChromeNs;
using Bot.ShopScope;
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
    internal static class KnowledgeEngineV2Repository
    {
        private sealed class RepositoryState
        {
            public readonly object Sync = new object();
            public SQLiteHelper Db;
            public string Path;
        }

        private static readonly IShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ConcurrentDictionary<string, RepositoryState> States =
            new ConcurrentDictionary<string, RepositoryState>(StringComparer.Ordinal);

        public static List<KnowledgeV2Record> LoadAll(string seller)
        {
            var shop = ResolveShopRequired(seller);
            var state = GetState(shop);
            EnsureMigrated(shop, state);
            lock (state.Sync)
            {
                return state.Db.ReadRecords<KnowledgeV2RecordRow>(null)
                    .Select(FromRow)
                    .Where(x => x != null)
                    .ToList();
            }
        }

        public static void Save(string seller, KnowledgeV2Record record)
        {
            if (record == null) return;
            var shop = ResolveShopRequired(seller);
            var state = GetState(shop);
            EnsureMigrated(shop, state);
            NormalizeForSave(record);
            lock (state.Sync) state.Db.SaveOneRecord(ToRow(record));
            MirrorOneToLegacy(shop, record);
            KnowledgeEngineV2Service.ApplyRecordUpdate(seller, record);
        }

        /// <summary>
        /// Explicit operator Save from the Bot message editor / sandbox. Stores a
        /// production-approved exact question without an AI observation period.
        /// Exact-title duplicates are updated instead of creating competing facts;
        /// vague similarity is never used to overwrite another knowledge record.
        /// </summary>
        public static bool TrySaveReviewedDirectAnswer(string seller, string question,
            string answer, string sourceMessageId, out string result)
        {
            string ignoredId;
            return TrySaveReviewedDirectAnswer(seller, question, answer, sourceMessageId,
                out result, out ignoredId);
        }

        public static bool TrySaveReviewedDirectAnswer(string seller, string question,
            string answer, string sourceMessageId, out string result, out string savedKnowledgeId)
        {
            savedKnowledgeId = string.Empty;
            result = string.Empty;
            question = (question ?? string.Empty).Trim();
            answer = (answer ?? string.Empty).Trim();
            if (answer.EndsWith("[A]", StringComparison.OrdinalIgnoreCase))
                answer = answer.Substring(0, answer.Length - 3).TrimEnd();
            if (question.Length < 5 || question.Length > 500 || answer.Length < 2 || answer.Length > 4000)
            {
                result = "问题或答案长度不合法，未写入知识。";
                return false;
            }
            if (KnowledgeEngineV2Semantics.IsHighRisk(question)
                || KnowledgeEngineV2Semantics.IsHighRisk(answer))
            {
                result = "该内容需要安全流程处理，不能设置为自动直答。";
                return false;
            }
            var parsed = KnowledgeEngineV2Semantics.Parse(question, null);
            if (parsed.ContextDependent || string.IsNullOrWhiteSpace(parsed.Subject))
            {
                result = "问题依赖上下文或缺少独立业务主体，不能建立无条件自动直答。";
                return false;
            }
            try
            {
                var all = LoadAll(seller);
                var exact = all.Where(x => x != null && x.Enabled
                    && !string.Equals(x.Status, "deleted", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(KnowledgeEngineV2Semantics.Compact(x.Title),
                        KnowledgeEngineV2Semantics.Compact(question), StringComparison.Ordinal)).ToList();
                if (exact.Count > 1)
                {
                    result = "存在多条完全同题的启用知识，请先在知识中心消除冲突。";
                    return false;
                }
                var record = exact.Count == 1 ? exact[0] : new KnowledgeV2Record
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Type = "business_fact",
                    Title = question,
                    CreatedAt = DateTime.Now,
                    SourceId = "reviewed-" + Guid.NewGuid().ToString("N")
                };
                if (string.Equals(record.RiskLevel, "high", StringComparison.OrdinalIgnoreCase))
                {
                    result = "原有知识标记为高风险，已阻止覆盖。";
                    return false;
                }
                var old = record.Answer ?? string.Empty;
                record.Answer = answer;
                record.ShortAnswer = answer;
                record.Aliases = (record.Aliases ?? new List<string>())
                    .Concat(new[] { question }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                record.Intent = parsed.Intent;
                record.Subject = parsed.Subject;
                record.Predicate = parsed.Predicate;
                record.Entities = parsed.Entities == null ? new List<string>() : parsed.Entities.ToList();
                if (string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase))
                    record.Type = "business_fact";
                record.SourceType = "manual_reviewed_direct";
                record.RiskLevel = "normal";
                record.Status = "active";
                record.Enabled = true;
                record.Authority = 0.99;
                record.Confidence = 0.99;
                record.LastVerifiedAt = DateTime.Now;
                Save(seller, record); // Durable write + immediate immutable snapshot update.
                var readback = LoadAll(seller).FirstOrDefault(x => x != null && x.Id == record.Id);
                if (readback == null || !string.Equals(readback.Answer, answer, StringComparison.Ordinal))
                {
                    result = "知识写入后验证失败；请核实数据库和云同步。";
                    return false;
                }
                string error;
                KnowledgeEngineV2GovernanceAuditService.TryAppendAction(
                    seller, "manual_direct_answer_add", "knowledge", record.Id,
                    sourceMessageId ?? string.Empty, question,
                    "previous_answer_length=" + old.Length,
                    KnowledgeEngineV2GovernanceAuditService.DescribeRecord(record),
                    "人工确认的直答立即生效，不需影子观察；来源消息=" + (sourceMessageId ?? ""),
                    "success", out error);
                savedKnowledgeId = record.Id;
                result = "直答已保存并立即刷新索引（知识ID：" + record.Id
                    + "）。后续相同完整问题优先使用；安全/订单场景保护仍有效。";
                return true;
            }
            catch (Exception ex)
            {
                result = "人工直答保存失败：" + ex.Message;
                BotLib.Log.ErrorWithMaxCount("Knowledge V2人工直答保存失败: "
                    + ex.GetType().Name, 10);
                return false;
            }
        }

        /// <summary>
        /// A verified V2 source ID is the only authority for editing an answer.
        /// Check the old answer under the same database lock as the write; never
        /// infer a record from semantic similarity or AI-generated titles.
        /// </summary>
        /// <summary>
        /// Approved buyer-message direct answer: creates an exact-question record or
        /// updates one exact record. A trusted supporting KB ID is required, but
        /// never overwritten merely because AI suggested an adjacent question.
        /// The approved record and reversible revision commit in one DB transaction.
        /// </summary>
        public static bool TryApplyBuyerDirect(string seller, string question, string answer,
            string evidenceId, string evidenceAnswer, string actionId,
            out string knowledgeId, out string result)
        {
            knowledgeId = string.Empty;
            result = string.Empty;
            question = (question ?? "").Trim();
            answer = (answer ?? "").Trim();
            if (question.Length < 5 || question.Length > 500 || answer.Length < 2 || answer.Length > 1500
                || KnowledgeEngineV2Semantics.IsHighRisk(question)
                || KnowledgeEngineV2Semantics.IsHighRisk(answer))
            {
                result = "直答长度不合法或涉及高风险，请进入知识中心人工审核。"; return false;
            }
            var parsed = KnowledgeEngineV2Semantics.Parse(question, null);
            if (parsed.ContextDependent || string.IsNullOrWhiteSpace(parsed.Subject))
            {
                result = "缺少独立业务主体，不能创建无条件直答。"; return false;
            }
            var shop = ResolveShopRequired(seller);
            var state = GetState(shop);
            EnsureMigrated(shop, state);
            KnowledgeV2Record applied = null;
            lock (state.Sync)
            {
                var records = state.Db.ReadRecords<KnowledgeV2RecordRow>(null)
                    .Select(FromRow).Where(x => x != null).ToList();
                var evidence = records.FirstOrDefault(x => x.Id == evidenceId);
                if (evidence == null || !evidence.Enabled || evidence.Status != "active"
                    || evidence.Answer != evidenceAnswer || string.IsNullOrWhiteSpace(evidence.Answer)
                    || string.Equals(evidence.RiskLevel, "high", StringComparison.OrdinalIgnoreCase))
                {
                    result = "知识依据已经失效或发生变化，请重新分析。"; return false;
                }
                var exact = records.Where(x => x.Enabled && x.Status == "active"
                    && KnowledgeEngineV2Semantics.Compact(x.Title)
                        == KnowledgeEngineV2Semantics.Compact(question)).ToList();
                if (exact.Count > 1)
                {
                    result = "知识库存在多个同题答案，请先消除冲突。"; return false;
                }
                var previous = exact.Count == 1 ? exact[0] : null;
                if (previous != null && (string.Equals(previous.RiskLevel, "high", StringComparison.OrdinalIgnoreCase)
                    || (previous.Conditions != null && previous.Conditions.Count > 0)
                    || (previous.RequiredContext != null && previous.RequiredContext.Count > 0)
                    || (previous.ProductIds != null && previous.ProductIds.Count > 0)))
                {
                    result = "原知识受场景条件约束，禁止改成无条件直答。"; return false;
                }
                if (previous != null && string.Equals(previous.Answer, answer, StringComparison.Ordinal))
                {
                    result = "直答内容已经相同，无需修改。"; knowledgeId = previous.Id; return false;
                }
                applied = previous == null ? new KnowledgeV2Record
                {
                    Id = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.Now, Type = "business_fact",
                    Title = question, SourceId = actionId
                } : JsonConvert.DeserializeObject<KnowledgeV2Record>(JsonConvert.SerializeObject(previous));
                applied.Title = question;
                applied.Answer = answer;
                applied.ShortAnswer = answer;
                applied.SourceType = "manual_reviewed_direct";
                applied.Status = "active";
                applied.Enabled = true;
                applied.RiskLevel = "normal";
                applied.Authority = 0.99;
                applied.Confidence = 0.99;
                applied.Intent = parsed.Intent;
                applied.Subject = parsed.Subject;
                applied.Predicate = parsed.Predicate;
                applied.Entities = parsed.Entities == null ? new List<string>() : parsed.Entities.ToList();
                applied.Aliases = (applied.Aliases ?? new List<string>())
                    .Concat(new[] { question }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                applied.LastVerifiedAt = DateTime.Now;
                NormalizeForSave(applied);
                var revision = new KnowledgeV2ManualEditRevisionRow
                {
                    Id = Guid.NewGuid().ToString("N"), KnowledgeId = applied.Id,
                    MessageRecordId = (actionId ?? "").Trim(), Action = "buyer_direct_apply",
                    PreviousJson = previous == null ? "" : JsonConvert.SerializeObject(previous),
                    CurrentJson = JsonConvert.SerializeObject(applied),
                    CreatedAtTicks = DateTime.Now.Ticks
                };
                state.Db.SaveRecordsInTransaction(new List<object> { ToRow(applied), revision });
            }
            MirrorOneToLegacy(shop, applied);
            KnowledgeEngineV2Service.ApplyRecordUpdate(seller, applied);
            knowledgeId = applied.Id;
            string audit;
            KnowledgeEngineV2GovernanceAuditService.TryAppendAction(seller, "buyer_direct_apply",
                "knowledge", applied.Id, actionId, question, "verified_knowledge=" + evidenceId,
                KnowledgeEngineV2GovernanceAuditService.DescribeRecord(applied),
                "仅人工确认后应用；旧版本已保存用于回滚。", "success", out audit);
            result = "直答已通过人工确认并更新正式知识库。";
            return true;
        }

        public static bool TryRollbackBuyerDirect(string seller, string knowledgeId,
            string actionId, out string result)
        {
            result = string.Empty;
            var shop = ResolveShopRequired(seller);
            var state = GetState(shop);
            EnsureMigrated(shop, state);
            KnowledgeV2Record restore = null, current = null;
            bool delete = false;
            lock (state.Sync)
            {
                var revisions = state.Db.ReadRecords<KnowledgeV2ManualEditRevisionRow>(null)
                    .Where(x => x.KnowledgeId == knowledgeId)
                    .OrderByDescending(x => x.CreatedAtTicks).ToList();
                var latest = revisions.FirstOrDefault();
                if (latest == null || latest.Action != "buyer_direct_apply"
                    || latest.MessageRecordId != actionId)
                {
                    result = "存在新版本或当前不是可回滚的直答修订。"; return false;
                }
                var row = state.Db.ReadOneRecord<KnowledgeV2RecordRow>(x => x.Id == knowledgeId);
                if (row == null) { result = "知识已删除，拒绝回滚。"; return false; }
                current = FromRow(row);
                var applied = JsonConvert.DeserializeObject<KnowledgeV2Record>(latest.CurrentJson ?? "");
                if (applied == null || current.UpdatedAt.Ticks != applied.UpdatedAt.Ticks
                    || current.Answer != applied.Answer)
                {
                    result = "直答已被其他操作修改，拒绝覆盖。"; return false;
                }
                delete = string.IsNullOrWhiteSpace(latest.PreviousJson);
                if (!delete)
                {
                    restore = JsonConvert.DeserializeObject<KnowledgeV2Record>(latest.PreviousJson);
                    if (restore == null) { result = "原版本快照无效。"; return false; }
                    NormalizeForSave(restore);
                }
                var rollback = new KnowledgeV2ManualEditRevisionRow
                {
                    Id = Guid.NewGuid().ToString("N"), KnowledgeId = knowledgeId,
                    MessageRecordId = actionId, Action = "buyer_direct_rollback",
                    PreviousJson = JsonConvert.SerializeObject(current),
                    CurrentJson = delete ? "" : JsonConvert.SerializeObject(restore),
                    CreatedAtTicks = DateTime.Now.Ticks
                };
                if (delete)
                {
                    state.Db.SaveOneRecord(rollback);
                    state.Db.Delete(row);
                }
                else state.Db.SaveRecordsInTransaction(new List<object> { ToRow(restore), rollback });
            }
            if (delete)
            {
                MirrorDeleteToLegacy(shop, knowledgeId);
                KnowledgeEngineV2Service.ApplyRecordDelete(seller, knowledgeId);
            }
            else
            {
                MirrorOneToLegacy(shop, restore);
                KnowledgeEngineV2Service.ApplyRecordUpdate(seller, restore);
            }
            string audit;
            KnowledgeEngineV2GovernanceAuditService.TryAppendAction(seller, "buyer_direct_rollback",
                "knowledge", knowledgeId, actionId, current.Title,
                KnowledgeEngineV2GovernanceAuditService.DescribeRecord(current),
                delete ? "new_direct_answer_deleted" : KnowledgeEngineV2GovernanceAuditService.DescribeRecord(restore),
                "人工确认回滚，先检查修订版本一致性。", "success", out audit);
            result = "已回滚直答并刷新知识库索引。";
            return true;
        }

        public static bool TryCorrectBotAnswer(string seller, string knowledgeId,
            string observedAnswer, string correctedAnswer, string messageRecordId, out string result)
        {
            result = string.Empty;
            knowledgeId = (knowledgeId ?? string.Empty).Trim();
            correctedAnswer = (correctedAnswer ?? string.Empty).Trim();
            observedAnswer = (observedAnswer ?? string.Empty).Trim();
            if (knowledgeId.Length == 0 || correctedAnswer.Length == 0 || observedAnswer.Length == 0)
            {
                result = "知识ID、原始答案或修改答案为空，未修改知识。";
                return false;
            }
            if (correctedAnswer.EndsWith("[A]", StringComparison.OrdinalIgnoreCase))
                correctedAnswer = correctedAnswer.Substring(0, correctedAnswer.Length - 3).TrimEnd();
            if (correctedAnswer.Length == 0)
            {
                result = "修改答案为空，未修改知识。";
                return false;
            }
            var shop = ResolveShopRequired(seller);
            var state = GetState(shop);
            EnsureMigrated(shop, state);
            KnowledgeV2Record corrected = null;
            KnowledgeV2Record before = null;
            lock (state.Sync)
            {
                var id = knowledgeId;
                var row = state.Db.ReadOneRecord<KnowledgeV2RecordRow>(x => x.Id == id);
                if (row == null)
                {
                    result = "原始Knowledge V2记录已不存在，拒绝按相似文本修改其他知识。";
                    return false;
                }
                before = FromRow(row);
                if (!before.Enabled || string.Equals(before.Status, "disabled", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(before.Status, "candidate", StringComparison.OrdinalIgnoreCase))
                {
                    result = "原始知识不是已启用的生产知识，请在知识中心手工审核。";
                    return false;
                }
                if (!string.Equals((before.Answer ?? string.Empty).Trim(), observedAnswer, StringComparison.Ordinal))
                {
                    result = "知识答案已被其他操作修改，或消息与来源记录不匹配；未覆盖现有内容。请在知识中心刷新后编辑。";
                    return false;
                }
                if (string.Equals(before.Answer.Trim(), correctedAnswer, StringComparison.Ordinal))
                {
                    result = "新答案与原知识相同，无需再次修改。";
                    return true;
                }
                if (string.Equals(before.RiskLevel, "high", StringComparison.OrdinalIgnoreCase)
                    || KnowledgeEngineV2Semantics.IsHighRisk(correctedAnswer))
                {
                    result = "原知识或修改内容涉及高风险操作，请前往知识中心按审核流程修改。";
                    return false;
                }
                corrected = JsonConvert.DeserializeObject<KnowledgeV2Record>(
                    JsonConvert.SerializeObject(before));
                corrected.Answer = correctedAnswer;
                if (string.IsNullOrWhiteSpace(corrected.ShortAnswer)
                    || string.Equals(corrected.ShortAnswer.Trim(), before.Answer.Trim(), StringComparison.Ordinal))
                    corrected.ShortAnswer = correctedAnswer;
                corrected.SourceType = "manual_reviewed_direct";
                corrected.Authority = Math.Max(corrected.Authority, 0.98);
                corrected.Confidence = Math.Max(corrected.Confidence, 0.99);
                corrected.CorrectionCount++;
                corrected.LastVerifiedAt = DateTime.Now;
                NormalizeForSave(corrected);
                var revision = new KnowledgeV2ManualEditRevisionRow
                {
                    Id = Guid.NewGuid().ToString("N"),
                    KnowledgeId = knowledgeId,
                    MessageRecordId = (messageRecordId ?? string.Empty).Trim(),
                    Action = "apply",
                    PreviousJson = JsonConvert.SerializeObject(before),
                    CurrentJson = JsonConvert.SerializeObject(corrected),
                    CreatedAtTicks = DateTime.Now.Ticks
                };
                state.Db.SaveRecordsInTransaction(new List<object> { ToRow(corrected), revision });
            }
            MirrorOneToLegacy(shop, corrected);
            KnowledgeEngineV2Service.ApplyRecordUpdate(seller, corrected);
            string auditError;
            KnowledgeEngineV2GovernanceAuditService.TryAppendAction(
                seller, "manual_bot_answer_correction", "knowledge", knowledgeId,
                (messageRecordId ?? string.Empty).Trim(), corrected.Title,
                KnowledgeEngineV2GovernanceAuditService.DescribeRecord(before),
                KnowledgeEngineV2GovernanceAuditService.DescribeRecord(corrected),
                "人工确认的Bot答案按实际Knowledge V2记录ID精确更新；原快照已保存，可回滚。",
                "success", out auditError);
            result = "已精确更新Knowledge V2知识并刷新检索索引；历史答案已留存，可从知识中心回滚。";
            return true;
        }

        public static bool TryRollbackLastBotCorrection(string seller, string knowledgeId, out string result)
        {
            result = string.Empty;
            knowledgeId = (knowledgeId ?? string.Empty).Trim();
            if (knowledgeId.Length == 0) { result = "请选择要回滚的知识。"; return false; }
            var shop = ResolveShopRequired(seller);
            var state = GetState(shop);
            EnsureMigrated(shop, state);
            KnowledgeV2Record restored = null;
            KnowledgeV2Record before = null;
            lock (state.Sync)
            {
                var id = knowledgeId;
                var revisions = state.Db.ReadRecords<KnowledgeV2ManualEditRevisionRow>(null)
                    .Where(x => x != null && x.KnowledgeId == id)
                    .OrderByDescending(x => x.CreatedAtTicks).ToList();
                var latest = revisions.FirstOrDefault();
                if (latest == null || latest.Action != "apply")
                {
                    result = "没有可回滚的最近一次Bot人工纠错。";
                    return false;
                }
                var currentRow = state.Db.ReadOneRecord<KnowledgeV2RecordRow>(x => x.Id == id);
                if (currentRow == null) { result = "目标知识已删除，无法回滚。"; return false; }
                before = FromRow(currentRow);
                var applied = JsonConvert.DeserializeObject<KnowledgeV2Record>(latest.CurrentJson ?? "");
                restored = JsonConvert.DeserializeObject<KnowledgeV2Record>(latest.PreviousJson ?? "");
                if (applied == null || restored == null
                    || !string.Equals(before.Answer, applied.Answer, StringComparison.Ordinal)
                    || before.UpdatedAt.Ticks != applied.UpdatedAt.Ticks)
                {
                    result = "知识在纠错后又发生变化，禁止覆盖新的编辑。请人工处理冲突。";
                    return false;
                }
                NormalizeForSave(restored);
                var rollback = new KnowledgeV2ManualEditRevisionRow
                {
                    Id = Guid.NewGuid().ToString("N"),
                    KnowledgeId = knowledgeId,
                    MessageRecordId = latest.MessageRecordId,
                    Action = "rollback",
                    PreviousJson = JsonConvert.SerializeObject(before),
                    CurrentJson = JsonConvert.SerializeObject(restored),
                    CreatedAtTicks = DateTime.Now.Ticks
                };
                state.Db.SaveRecordsInTransaction(new List<object> { ToRow(restored), rollback });
            }
            MirrorOneToLegacy(shop, restored);
            KnowledgeEngineV2Service.ApplyRecordUpdate(seller, restored);
            string auditError;
            KnowledgeEngineV2GovernanceAuditService.TryAppendAction(
                seller, "rollback_revision", "knowledge", knowledgeId,
                string.Empty, restored.Title,
                KnowledgeEngineV2GovernanceAuditService.DescribeRecord(before),
                KnowledgeEngineV2GovernanceAuditService.DescribeRecord(restored),
                "回滚最后一条已验证Bot人工纠错；新版本发生变化时拒绝回滚。", "success", out auditError);
            result = "已回滚最近的Bot人工纠错并刷新检索索引。";
            return true;
        }

        public static bool Delete(string seller, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            var shop = ResolveShopRequired(seller);
            var state = GetState(shop);
            EnsureMigrated(shop, state);
            lock (state.Sync)
            {
                var row = state.Db.ReadOneRecord<KnowledgeV2RecordRow>(x => x.Id == id);
                if (row == null) return false;
                state.Db.Delete(row);
            }
            MirrorDeleteToLegacy(shop, id);
            KnowledgeEngineV2Service.ApplyRecordDelete(seller, id);
            return true;
        }

        public static void ReplaceAll(string seller, IEnumerable<KnowledgeV2Record> records)
        {
            var shop = ResolveShopRequired(seller);
            var state = GetState(shop);
            EnsureMigrated(shop, state);
            var normalized = (records ?? Enumerable.Empty<KnowledgeV2Record>())
                .Where(x => x != null)
                .Select(CloneAndNormalize)
                .ToList();
            lock (state.Sync)
            {
                state.Db.ClearTable(new List<Type> { typeof(KnowledgeV2RecordRow) });
                if (normalized.Count > 0)
                    state.Db.SaveRecordsInTransaction(normalized.Select(x => (object)ToRow(x)).ToList());
                state.Db.SaveOneRecord(new KnowledgeV2MetaRow
                {
                    Key = "schema_version",
                    Value = KnowledgeEngineV2Constants.SchemaVersion.ToString()
                });
            }
            MirrorAllToLegacy(shop, normalized);
            KnowledgeEngineV2Service.ReplaceSnapshot(seller, normalized);
        }

        public static string GetDatabasePath(string seller)
        {
            var shop = ResolveShopRequired(seller);
            return GetState(shop).Path;
        }

        public static void ResetFromLegacy(string seller)
        {
            var shop = ResolveShopRequired(seller);
            var state = GetState(shop);
            lock (state.Sync)
                state.Db.ClearTable(new List<Type> { typeof(KnowledgeV2RecordRow), typeof(KnowledgeV2MetaRow) });
            EnsureMigrated(shop, state);
            var records = LoadAll(seller);
            KnowledgeEngineV2Service.ReplaceSnapshot(seller, records);
        }

        private static RepositoryState GetState(ShopContext shop)
        {
            return States.GetOrAdd(shop.ShopKey, _ =>
            {
                var root = Paths.GetKnowledgeRoot(shop);
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                var path = Path.Combine(root, "knowledge-center-v2.db");
                return new RepositoryState
                {
                    Path = path,
                    Db = new SQLiteHelper(path, new List<Type>
                    {
                        typeof(KnowledgeV2RecordRow),
                        typeof(KnowledgeV2MetaRow),
                        typeof(KnowledgeV2ManualEditRevisionRow)
                    })
                };
            });
        }

        private static void EnsureMigrated(ShopContext shop, RepositoryState state)
        {
            lock (state.Sync)
            {
                var schema = state.Db.ReadOneRecord<KnowledgeV2MetaRow>(x => x.Key == "schema_version");
                var count = state.Db.ReadRecords<KnowledgeV2RecordRow>(null).Count;
                if (schema != null && schema.Value == KnowledgeEngineV2Constants.SchemaVersion.ToString()) return;

                if (count == 0)
                {
                    List<KnowledgeBaseEntry> legacy;
                    using (ShopSettingsScope.Enter(shop))
                        legacy = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
                    var migrated = new List<KnowledgeV2RecordRow>();
                    foreach (var entry in legacy.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Answer)))
                    {
                        KnowledgePolicyProfile profile = null;
                        try
                        {
                            using (ShopSettingsScope.Enter(shop)) profile = KnowledgePolicyProfileService.GetProfile(entry);
                        }
                        catch { }
                        var record = KnowledgeEngineV2Semantics.FromLegacy(entry, profile);
                        if (record != null) migrated.Add(ToRow(record));
                    }
                    if (migrated.Count > 0)
                        state.Db.SaveRecordsInTransaction(migrated.Select(x => (object)x).ToList());
                    Log.Info("Knowledge Center V2完成旧知识迁移: shop=" + shop.ShopKey + ", records=" + migrated.Count);
                }

                state.Db.SaveOneRecord(new KnowledgeV2MetaRow
                {
                    Key = "schema_version",
                    Value = KnowledgeEngineV2Constants.SchemaVersion.ToString()
                });
            }
        }

        private static KnowledgeV2Record CloneAndNormalize(KnowledgeV2Record source)
        {
            var clone = JsonConvert.DeserializeObject<KnowledgeV2Record>(JsonConvert.SerializeObject(source)) ?? new KnowledgeV2Record();
            // Replicating an existing cloud record is not a new edit. The normal
            // Save() path updates timestamps, but cloud replay must preserve them
            // or every 10-second poll causes a spurious revision/upload loop.
            var updatedAt = clone.UpdatedAt;
            var verifiedAt = clone.LastVerifiedAt;
            NormalizeForSave(clone);
            if (updatedAt != DateTime.MinValue) clone.UpdatedAt = updatedAt;
            if (verifiedAt != DateTime.MinValue) clone.LastVerifiedAt = verifiedAt;
            return clone;
        }

        private static void NormalizeForSave(KnowledgeV2Record record)
        {
            if (string.IsNullOrWhiteSpace(record.Id)) record.Id = Guid.NewGuid().ToString("N");
            record.Type = KnowledgeEngineV2Semantics.NormalizeType(record.Type);
            record.Intent = KnowledgeEngineV2Semantics.NormalizeIntent(record.Intent);
            record.Predicate = KnowledgeEngineV2Semantics.NormalizePredicate(record.Predicate);
            record.RiskLevel = string.IsNullOrWhiteSpace(record.RiskLevel) ? "normal" : record.RiskLevel.Trim().ToLowerInvariant();
            record.Status = string.IsNullOrWhiteSpace(record.Status) ? "active" : record.Status.Trim().ToLowerInvariant();
            record.Title = (record.Title ?? string.Empty).Trim();
            record.Subject = (record.Subject ?? string.Empty).Trim();
            record.Answer = (record.Answer ?? string.Empty).Trim();
            record.ShortAnswer = (record.ShortAnswer ?? string.Empty).Trim();
            record.Entities = NormalizeList(record.Entities, 24);
            record.Aliases = NormalizeList(record.Aliases, 48);
            record.Conditions = NormalizeList(record.Conditions, 20);
            record.Exclusions = NormalizeList(record.Exclusions, 20);
            record.RequiredContext = NormalizeList(record.RequiredContext, 20);
            record.ProductIds = NormalizeList(record.ProductIds, 20);
            record.Authority = Clamp(record.Authority <= 0 ? 0.90 : record.Authority);
            record.Confidence = Clamp(record.Confidence <= 0 ? 0.80 : record.Confidence);
            if (record.CreatedAt == DateTime.MinValue) record.CreatedAt = DateTime.Now;
            record.UpdatedAt = DateTime.Now;
            if (record.LastVerifiedAt == DateTime.MinValue) record.LastVerifiedAt = record.UpdatedAt;
        }

        private static List<string> NormalizeList(IEnumerable<string> values, int max)
        {
            return (values ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToList();
        }

        private static KnowledgeV2RecordRow ToRow(KnowledgeV2Record record)
        {
            return new KnowledgeV2RecordRow
            {
                Id = record.Id,
                Type = record.Type,
                Title = record.Title,
                Intent = record.Intent,
                Subject = record.Subject,
                Predicate = record.Predicate,
                EntitiesJson = JsonConvert.SerializeObject(record.Entities ?? new List<string>()),
                AliasesJson = JsonConvert.SerializeObject(record.Aliases ?? new List<string>()),
                Answer = record.Answer,
                ShortAnswer = record.ShortAnswer,
                ConditionsJson = JsonConvert.SerializeObject(record.Conditions ?? new List<string>()),
                ExclusionsJson = JsonConvert.SerializeObject(record.Exclusions ?? new List<string>()),
                RequiredContextJson = JsonConvert.SerializeObject(record.RequiredContext ?? new List<string>()),
                ProductIdsJson = JsonConvert.SerializeObject(record.ProductIds ?? new List<string>()),
                RiskLevel = record.RiskLevel,
                SourceType = record.SourceType,
                SourceId = record.SourceId,
                Authority = record.Authority,
                Confidence = record.Confidence,
                UseCount = record.UseCount,
                AcceptedCount = record.AcceptedCount,
                CorrectionCount = record.CorrectionCount,
                WithdrawCount = record.WithdrawCount,
                Enabled = record.Enabled,
                Status = record.Status,
                CreatedAtTicks = record.CreatedAt.Ticks,
                UpdatedAtTicks = record.UpdatedAt.Ticks,
                LastVerifiedAtTicks = record.LastVerifiedAt.Ticks
            };
        }

        private static KnowledgeV2Record FromRow(KnowledgeV2RecordRow row)
        {
            if (row == null) return null;
            return new KnowledgeV2Record
            {
                Id = row.Id,
                Type = row.Type,
                Title = row.Title,
                Intent = row.Intent,
                Subject = row.Subject,
                Predicate = row.Predicate,
                Entities = ParseList(row.EntitiesJson),
                Aliases = ParseList(row.AliasesJson),
                Answer = row.Answer,
                ShortAnswer = row.ShortAnswer,
                Conditions = ParseList(row.ConditionsJson),
                Exclusions = ParseList(row.ExclusionsJson),
                RequiredContext = ParseList(row.RequiredContextJson),
                ProductIds = ParseList(row.ProductIdsJson),
                RiskLevel = row.RiskLevel,
                SourceType = row.SourceType,
                SourceId = row.SourceId,
                Authority = row.Authority,
                Confidence = row.Confidence,
                UseCount = row.UseCount,
                AcceptedCount = row.AcceptedCount,
                CorrectionCount = row.CorrectionCount,
                WithdrawCount = row.WithdrawCount,
                Enabled = row.Enabled,
                Status = row.Status,
                CreatedAt = SafeDate(row.CreatedAtTicks),
                UpdatedAt = SafeDate(row.UpdatedAtTicks),
                LastVerifiedAt = SafeDate(row.LastVerifiedAtTicks)
            };
        }

        private static DateTime SafeDate(long ticks)
        {
            try { return ticks <= 0 ? DateTime.MinValue : new DateTime(ticks); }
            catch { return DateTime.MinValue; }
        }

        private static List<string> ParseList(string json)
        {
            try { return JsonConvert.DeserializeObject<List<string>>(json ?? "[]") ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        private static void MirrorOneToLegacy(ShopContext shop, KnowledgeV2Record record)
        {
            if (shop == null || record == null) return;
            try
            {
                using (ShopSettingsScope.Enter(shop))
                {
                    var list = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
                    var item = list.FirstOrDefault(x => x != null && string.Equals(x.Id, record.Id, StringComparison.Ordinal));
                    if (item == null)
                    {
                        item = new KnowledgeBaseEntry();
                        list.Add(item);
                    }
                    ApplyToLegacy(record, item);
                    BotFeatureStore.SaveKnowledgeBase(list);
                }
            }
            catch (Exception ex) { Log.ErrorWithMaxCount("V2知识镜像旧知识库失败: " + ex.Message, 10); }
        }

        private static void MirrorDeleteToLegacy(ShopContext shop, string id)
        {
            if (shop == null || string.IsNullOrWhiteSpace(id)) return;
            try
            {
                using (ShopSettingsScope.Enter(shop))
                {
                    var list = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
                    list.RemoveAll(x => x != null && string.Equals(x.Id, id, StringComparison.Ordinal));
                    BotFeatureStore.SaveKnowledgeBase(list);
                }
            }
            catch (Exception ex) { Log.ErrorWithMaxCount("V2知识删除镜像旧知识库失败: " + ex.Message, 10); }
        }

        private static void MirrorAllToLegacy(ShopContext shop, List<KnowledgeV2Record> records)
        {
            if (shop == null) return;
            try
            {
                var list = new List<KnowledgeBaseEntry>();
                foreach (var record in records ?? new List<KnowledgeV2Record>())
                {
                    if (record == null) continue;
                    var item = new KnowledgeBaseEntry();
                    ApplyToLegacy(record, item);
                    list.Add(item);
                }
                using (ShopSettingsScope.Enter(shop)) BotFeatureStore.SaveKnowledgeBase(list);
            }
            catch (Exception ex) { Log.ErrorWithMaxCount("V2完整知识镜像旧知识库失败: " + ex.Message, 10); }
        }

        private static void ApplyToLegacy(KnowledgeV2Record record, KnowledgeBaseEntry item)
        {
            item.Id = record.Id;
            item.Enabled = record.Enabled
                && !string.Equals(record.Status, "candidate", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(record.Type, "learning_candidate", StringComparison.OrdinalIgnoreCase);
            item.Category = DisplayType(record.Type);
            item.Title = record.Title ?? string.Empty;
            item.Answer = record.Answer ?? string.Empty;
            item.Keywords = string.Join(",", (record.Entities ?? new List<string>())
                .Concat(record.Aliases ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24));
            item.SourceType = string.IsNullOrWhiteSpace(record.SourceType) ? "KnowledgeCenterV2" : record.SourceType;
            item.UpdatedAt = (record.UpdatedAt == DateTime.MinValue ? DateTime.Now : record.UpdatedAt).ToString("yyyy-MM-dd HH:mm:ss");
        }

        private static string DisplayType(string type)
        {
            switch ((type ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "procedure": return "操作流程";
                case "presale": return "售前规则";
                case "order_rule": return "订单规则";
                case "after_sale": return "售后规则";
                case "safety_rule": return "安全边界";
                case "fixed_reply": return "固定话术";
                case "product_knowledge": return "商品知识";
                case "learning_candidate": return "学习候选";
                case "temporary": return "临时知识";
                default: return "业务事实";
            }
        }

        private static ShopContext ResolveShopRequired(string seller)
        {
            var current = ShopSettingsScope.Current;
            if (current != null) return current;
            var shop = ShopContextLocator.ResolveBySellerNick((seller ?? string.Empty).Trim());
            if (shop == null) throw new InvalidOperationException("Knowledge Center V2无法确定当前店铺身份。");
            return shop;
        }

        private static double Clamp(double value)
        {
            return Math.Max(0, Math.Min(1, value));
        }
    }
}
