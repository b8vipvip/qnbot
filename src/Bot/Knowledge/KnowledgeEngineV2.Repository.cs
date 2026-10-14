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
                        typeof(KnowledgeV2MetaRow)
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
