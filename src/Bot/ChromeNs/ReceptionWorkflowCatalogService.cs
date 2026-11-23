using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Shop-scoped, versioned reception workflow catalog.
    /// Validated workflows are published immediately by the agent; no administrator
    /// approval queue or draft-only activation gate exists. Runtime side effects still
    /// belong to the canonical reply/order services and their independent safety checks.
    /// </summary>
    internal static class ReceptionWorkflowCatalogService
    {
        private const string Schema = "qnbot.reception-workflows.v2";
        private const string LegacySchema = "qnbot.reception-workflow-drafts.v1";
        private const string CatalogFileName = "reception-workflow-catalog.json";
        private const string LegacyFileName = "reception-workflow-drafts.json";
        private const string HistoryDirectoryName = "reception-workflow-history";

        private static readonly HashSet<string> AllowedActions =
            new HashSet<string>(new[] {
                "WAIT", "ASK_EVIDENCE", "HANDOFF", "READ_ONLY_TOOL", "REPLY", "REPLY_REVIEWED"
            }, StringComparer.Ordinal);

        private static readonly HashSet<string> AllowedEvidence =
            new HashSet<string>(new[] {
                "order_snapshot", "buyer_message", "image_message",
                "vision_observation", "manual_correction", "verified_account_snapshot"
            }, StringComparer.Ordinal);

        private static ShopContext ResolveShopContext(string seller, string shopKey)
        {
            seller = (seller ?? string.Empty).Trim();
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0)
                throw new InvalidOperationException("ShopKey为空，拒绝读取或发布接待流程");

            // The caller supplies the exact shop identity captured by the Knowledge Center
            // or message event. Never resolve a multi-shop seller nickname as the primary key.
            var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
            var resolvedSeller = (shop == null ? string.Empty : shop.SellerNick ?? string.Empty).Trim();
            if (shop == null
                || string.IsNullOrWhiteSpace(shop.ShopKey)
                || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)
                || !shop.HasStableSellerId
                || (seller.Length > 0 && resolvedSeller.Length > 0
                    && !string.Equals(resolvedSeller, seller, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Seller/ShopKey 不匹配或店铺身份不稳定，拒绝读取或发布接待流程");
            return shop;
        }

        private static string ShopDirectoryForShop(ShopContext shop)
        {
            if (shop == null
                || string.IsNullOrWhiteSpace(shop.ShopKey)
                || !shop.HasStableSellerId)
                throw new InvalidOperationException("店铺身份不稳定或未绑定唯一ShopKey，拒绝读取或发布接待流程");
            using (ShopSettingsScope.Enter(shop))
            {
                var dir = Path.GetDirectoryName(BusinessPolicyProfileService.GetUserPath());
                if (string.IsNullOrWhiteSpace(dir))
                    throw new InvalidOperationException("店铺隔离目录缺失");
                return dir;
            }
        }

        private static string PathForShop(ShopContext shop)
        {
            return Path.Combine(ShopDirectoryForShop(shop), CatalogFileName);
        }

        private static string LegacyPathForShop(ShopContext shop)
        {
            return Path.Combine(ShopDirectoryForShop(shop), LegacyFileName);
        }

        private static string HistoryDirectoryForShop(ShopContext shop)
        {
            return Path.Combine(ShopDirectoryForShop(shop), HistoryDirectoryName);
        }

        private static JObject Default()
        {
            return new JObject {
                ["schema"] = Schema,
                ["catalog_version"] = 2,
                ["catalog_revision"] = 0,
                ["published_revision"] = 0,
                ["published_at_utc"] = string.Empty,
                ["publication_mode"] = "autonomous",
                ["rollback_from_revision"] = 0,
                ["flows"] = new JArray()
            };
        }

        private static long ReadRevision(JObject root, string property)
        {
            var token = root[property];
            if (token == null) return 0;
            long value;
            if (token.Type != JTokenType.Integer
                || !long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                || value < 0)
                throw new InvalidDataException("接待流程目录修订版本无效：" + property);
            return value;
        }

        private static JObject Validate(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 1024 * 1024)
                throw new InvalidDataException("接待流程为空或超过1MiB安全上限");

            var root = JObject.Parse(json);
            var schema = Convert.ToString(root["schema"]);
            var isLegacy = string.Equals(schema, LegacySchema, StringComparison.Ordinal);
            if (!isLegacy
                && (!string.Equals(schema, Schema, StringComparison.Ordinal)
                    || root.Value<int?>("catalog_version") != 2))
                throw new InvalidDataException("接待流程目录协议不支持");
            if (isLegacy && root.Value<int?>("catalog_version") != 1)
                throw new InvalidDataException("旧版接待流程草稿协议无效");

            var revision = isLegacy
                ? ReadRevision(root, "draft_revision")
                : ReadRevision(root, "catalog_revision");

            var flows = root["flows"] as JArray;
            if (flows == null || flows.Count > 50)
                throw new InvalidDataException("流程数量超出安全范围");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in flows)
            {
                var flow = item as JObject;
                if (flow == null) throw new InvalidDataException("流程必须是对象");
                var id = Convert.ToString(flow["flow_id"]);
                if (!ValidKey(id) || !ids.Add(id))
                    throw new InvalidDataException("流程ID无效或重复");

                var flowVersion = flow.Value<int?>("flow_version");
                if (!flowVersion.HasValue || flowVersion.Value < 1 || flowVersion.Value > 1000000)
                    throw new InvalidDataException("流程版本无效");

                var status = Convert.ToString(flow["status"]);
                if (status != "draft" && status != "active" && status != "published")
                    throw new InvalidDataException("流程状态无效；仅允许经过校验的流程进入自动发布");

                CheckKeys(flow["product_ids"] as JArray, "product_ids");
                CheckKeys(flow["sku_ids"] as JArray, "sku_ids");

                var steps = flow["steps"] as JArray;
                if (steps == null || steps.Count < 1 || steps.Count > 30)
                    throw new InvalidDataException("流程步骤数量无效");

                var stages = new HashSet<string>(StringComparer.Ordinal);
                foreach (var token in steps)
                {
                    var step = token as JObject;
                    if (step == null) throw new InvalidDataException("流程步骤必须是对象");

                    var stage = Convert.ToString(step["state"]);
                    if (!ValidKey(stage) || !stages.Add(stage))
                        throw new InvalidDataException("步骤状态缺失或重复");

                    if (!AllowedActions.Contains(Convert.ToString(step["action"])))
                        throw new InvalidDataException("流程包含不支持的动作；付款、充值、退款、账号修改和实际履约动作不允许由流程目录授权");

                    var timeout = step.Value<int?>("timeout_minutes");
                    if (!timeout.HasValue || timeout < 0 || timeout > 10080)
                        throw new InvalidDataException("步骤超时超出范围");

                    var required = step["required_evidence"] as JArray;
                    if (required == null || required.Count < 1 || required.Count > 10)
                        throw new InvalidDataException("每个流程步骤必须声明1到10种必要证据");

                    var evidenceKinds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var kind in required)
                    {
                        if (kind == null || kind.Type != JTokenType.String
                            || !AllowedEvidence.Contains(Convert.ToString(kind)))
                            throw new InvalidDataException("证据类型不受支持");
                        if (!evidenceKinds.Add(Convert.ToString(kind)))
                            throw new InvalidDataException("同一步骤不允许重复声明相同证据类型");
                    }

                    if (!ValidKey(Convert.ToString(step["next_state"])))
                        throw new InvalidDataException("下一状态无效");

                    var triggerKeywords = step["trigger_keywords"] as JArray;
                    if (triggerKeywords != null)
                    {
                        if (triggerKeywords.Count > 8)
                            throw new InvalidDataException("触发关键词数量超出范围");
                        var uniqueKeywords = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var keyword in triggerKeywords)
                        {
                            var value = Convert.ToString(keyword);
                            if (keyword == null || keyword.Type != JTokenType.String
                                || string.IsNullOrWhiteSpace(value) || value.Length > 40
                                || !uniqueKeywords.Add(value))
                                throw new InvalidDataException("触发关键词无效或重复");
                        }
                    }

                    var replyTemplate = Convert.ToString(step["reply_template"]);
                    if (replyTemplate != null && replyTemplate.Length > 1200)
                        throw new InvalidDataException("流程回复模板超过1200字符");
                }

                foreach (var step in steps.Cast<JObject>())
                {
                    var next = Convert.ToString(step["next_state"]);
                    if (!stages.Contains(next))
                        throw new InvalidDataException("下一状态在当前流程中不存在：" + next);
                }
                var initialState = Convert.ToString(flow["initial_state"]);
                if (!string.IsNullOrWhiteSpace(initialState) && !stages.Contains(initialState))
                    throw new InvalidDataException("流程初始状态未在步骤中定义");

                var sourceEvidence = flow["source_evidence"] as JArray;
                if (sourceEvidence != null)
                {
                    if (sourceEvidence.Count > 12)
                        throw new InvalidDataException("流程来源证据数量超出范围");
                    foreach (var source in sourceEvidence.OfType<JObject>())
                    {
                        var kind = Convert.ToString(source["kind"]);
                        var digest = Convert.ToString(source["digest"]);
                        if (!AllowedEvidence.Contains(kind)
                            || string.IsNullOrWhiteSpace(digest) || digest.Length != 64
                            || !digest.All(Uri.IsHexDigit))
                            throw new InvalidDataException("流程来源证据引用无效");
                    }
                    if (sourceEvidence.Any(x => !(x is JObject)))
                        throw new InvalidDataException("流程来源证据必须是对象");
                }

                var confidenceToken = flow["confidence"];
                if (confidenceToken != null)
                {
                    var confidence = flow.Value<double?>("confidence");
                    if (!confidence.HasValue || double.IsNaN(confidence.Value)
                        || double.IsInfinity(confidence.Value) || confidence.Value < 0
                        || confidence.Value > 1)
                        throw new InvalidDataException("流程置信度无效");
                }
            }

            var canonical = new JObject {
                ["schema"] = Schema,
                ["catalog_version"] = 2,
                ["catalog_revision"] = revision,
                ["published_revision"] = isLegacy
                    ? revision
                    : ReadRevision(root, "published_revision"),
                ["published_at_utc"] = SafeMetadata(root["published_at_utc"], 64),
                ["publication_mode"] = SafeMetadata(root["publication_mode"], 40),
                ["rollback_from_revision"] = isLegacy ? 0 : ReadRevision(root, "rollback_from_revision"),
                ["flows"] = new JArray(flows.Select(token => {
                    var flow = (JObject)token;
                    return new JObject {
                        ["flow_id"] = Convert.ToString(flow["flow_id"]),
                        ["flow_version"] = flow.Value<int>("flow_version"),
                        ["status"] = "active",
                        ["initial_state"] = SafeMetadata(flow["initial_state"], 80).Length > 0
                            ? SafeMetadata(flow["initial_state"], 80)
                            : Convert.ToString(((JObject)((JArray)flow["steps"])[0])["state"]),
                        ["product_ids"] = flow["product_ids"].DeepClone(),
                        ["sku_ids"] = flow["sku_ids"].DeepClone(),
                        ["confidence"] = flow.Value<double?>("confidence") ?? 0,
                        ["source_evidence"] = flow["source_evidence"] == null
                            ? new JArray() : flow["source_evidence"].DeepClone(),
                        ["updated_at_utc"] = SafeMetadata(flow["updated_at_utc"], 64),
                        ["steps"] = new JArray(((JArray)flow["steps"]).Cast<JObject>().Select(step => new JObject {
                            ["state"] = Convert.ToString(step["state"]),
                            ["trigger_keywords"] = step["trigger_keywords"] == null
                                ? new JArray() : step["trigger_keywords"].DeepClone(),
                            ["required_evidence"] = step["required_evidence"].DeepClone(),
                            ["action"] = Convert.ToString(step["action"]),
                            ["reply_template"] = SafeMetadata(step["reply_template"], 1200),
                            ["terminal"] = step.Value<bool?>("terminal") ?? false,
                            ["next_state"] = Convert.ToString(step["next_state"]),
                            ["timeout_minutes"] = step.Value<int>("timeout_minutes")
                        }))
                    };
                }))
            };
            return canonical;
        }

        private static string SafeMetadata(JToken token, int maxLength)
        {
            var value = token == null ? string.Empty : Convert.ToString(token);
            if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) return string.Empty;
            return value;
        }

        private static bool ValidKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 80) return false;
            return value.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.');
        }

        private static void CheckKeys(JArray array, string label)
        {
            if (array == null || array.Count > 100)
                throw new InvalidDataException(label + " 列表无效");
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in array)
            {
                if (id == null || id.Type != JTokenType.String || !ValidKey(Convert.ToString(id)))
                    throw new InvalidDataException(label + " 存在非法标识");
                if (!unique.Add(Convert.ToString(id)))
                    throw new InvalidDataException(label + " 不允许重复标识");
            }
        }

        private static bool IsMissing(string path)
        {
            try
            {
                File.GetAttributes(path);
                return false;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception ex)
            {
                throw new IOException("无法确认接待流程文件状态；拒绝覆盖或回退：" + ex.Message, ex);
            }
        }

        private static JObject ReadCatalog(string path)
        {
            if (IsMissing(path)) return Default();
            string error;
            var contents = CrossProcessAtomicStateFile.ReadAllTextShared(path, 3, 30, out error);
            if (!string.IsNullOrWhiteSpace(error)) throw new IOException(error);
            if (string.IsNullOrWhiteSpace(contents))
                throw new InvalidDataException("已发布流程目录为空；拒绝将未知状态当作空目录");
            return Validate(contents);
        }

        private static string HistoryPath(string directory, long revision)
        {
            return Path.Combine(directory, "revision-" + revision.ToString("D10", CultureInfo.InvariantCulture) + ".json");
        }

        private static void SaveHistorySnapshot(ShopContext shop, JObject catalog)
        {
            var revision = catalog.Value<long>("catalog_revision");
            if (revision <= 0) return;

            var directory = HistoryDirectoryForShop(shop);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var path = HistoryPath(directory, revision);
            if (!IsMissing(path)) return;

            string error;
            if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(
                path, catalog.ToString(Formatting.Indented), 3, 40, out error))
                throw new IOException("保存接待流程历史版本失败：" + error);
        }

        private static void WriteCatalog(string path, JObject catalog)
        {
            var directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            string error;
            if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(
                path, catalog.ToString(Formatting.Indented), 3, 50, out error))
                throw new IOException(error);
        }

        private static void BumpFlowVersions(JObject candidate, JObject current)
        {
            var currentFlows = current["flows"] as JArray;
            var currentVersions = new Dictionary<string, int>(StringComparer.Ordinal);
            if (currentFlows != null)
            {
                foreach (var item in currentFlows.OfType<JObject>())
                {
                    var id = Convert.ToString(item["flow_id"]);
                    if (ValidKey(id)) currentVersions[id] = item.Value<int>("flow_version");
                }
            }

            var candidateFlows = candidate["flows"] as JArray;
            if (candidateFlows == null) return;
            foreach (var item in candidateFlows.OfType<JObject>())
            {
                var id = Convert.ToString(item["flow_id"]);
                int previous;
                if (!currentVersions.TryGetValue(id, out previous)) continue;
                if (previous >= 1000000)
                    throw new InvalidOperationException("流程版本已达到上限，拒绝发布覆盖");
                var minimumNext = previous + 1;
                if (item.Value<int>("flow_version") < minimumNext)
                    item["flow_version"] = minimumNext;
            }
        }

        private static void EnsureLegacyDraftMigrated(ShopContext shop)
        {
            var path = PathForShop(shop);
            if (!IsMissing(path)) return;

            var legacyPath = LegacyPathForShop(shop);
            if (IsMissing(legacyPath)) return;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "ReceptionWorkflowPublish", 3000))
            {
                if (lease == null || !lease.Acquired)
                    throw new IOException("无法取得接待流程迁移写锁");
                if (!IsMissing(path)) return;

                string error;
                var legacyJson = CrossProcessAtomicStateFile.ReadAllTextShared(legacyPath, 3, 30, out error);
                if (!string.IsNullOrWhiteSpace(error)) throw new IOException(error);
                var migrated = Validate(legacyJson);
                var oldRevision = migrated.Value<long>("catalog_revision");
                if (oldRevision == long.MaxValue)
                    throw new InvalidOperationException("旧版流程修订版本已达到上限，无法安全迁移");
                var newRevision = Math.Max(1, oldRevision + 1);
                migrated["catalog_revision"] = newRevision;
                migrated["published_revision"] = newRevision;
                migrated["published_at_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                migrated["publication_mode"] = "autonomous-migration";
                migrated["rollback_from_revision"] = 0;
                WriteCatalog(path, migrated);
            }
        }

        public static string GetActiveJson(string seller, string shopKey)
        {
            var shop = ResolveShopContext(seller, shopKey);
            EnsureLegacyDraftMigrated(shop);
            return ReadCatalog(PathForShop(shop)).ToString(Formatting.Indented);
        }

        public static string GetActiveFlowJson(string seller, string flowId, string shopKey)
        {
            if (!ValidKey(flowId)) return string.Empty;
            var catalog = JObject.Parse(GetActiveJson(seller, shopKey));
            var flows = catalog["flows"] as JArray;
            var flow = flows == null
                ? null
                : flows.OfType<JObject>().FirstOrDefault(x =>
                    string.Equals(Convert.ToString(x["flow_id"]), flowId, StringComparison.Ordinal));
            return flow == null ? string.Empty : flow.ToString(Formatting.Indented);
        }

        /// <summary>
        /// Validates and publishes in one transaction. No administrator identity,
        /// approval token, manual activation or gray-rollout state is required.
        /// </summary>
        public static string Publish(string seller, string json, string shopKey)
        {
            var candidate = Validate(json);
            var expectedRevision = candidate.Value<long>("catalog_revision");
            var shop = ResolveShopContext(seller, shopKey);
            EnsureLegacyDraftMigrated(shop);

            var path = PathForShop(shop);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "ReceptionWorkflowPublish", 3000))
            {
                if (lease == null || !lease.Acquired)
                    throw new IOException("无法取得接待流程发布写锁");

                var current = ReadCatalog(path);
                var currentRevision = current.Value<long>("catalog_revision");
                if (expectedRevision != currentRevision)
                    throw new InvalidOperationException("已发布流程已被其他窗口或 Agent 更新；本次发布未覆盖任何数据，请重新读取最新版本");
                if (currentRevision == long.MaxValue)
                    throw new InvalidOperationException("接待流程目录修订版本已达到上限，拒绝发布");

                SaveHistorySnapshot(shop, current);
                BumpFlowVersions(candidate, current);
                var nextRevision = currentRevision + 1;
                candidate["catalog_revision"] = nextRevision;
                candidate["published_revision"] = nextRevision;
                candidate["published_at_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                candidate["publication_mode"] = "autonomous";
                candidate["rollback_from_revision"] = 0;
                WriteCatalog(path, candidate);
            }

            ReceptionWorkflowRuntimeService.InvalidateShop(shopKey);
            return "接待流程已通过结构校验并自动发布；新版本已立即生效，无需管理员审批。";
        }

        /// <summary>
        /// Rolls back to the previous valid published snapshot as a new monotonic version.
        /// The rollback itself is immediate and does not require administrator approval.
        /// </summary>
        public static string RollbackToPreviousPublished(string seller, string shopKey)
        {
            return RollbackToPreviousPublished(seller, shopKey, null);
        }

        internal static string RollbackToPreviousPublished(
            string seller, string shopKey, long? expectedCurrentRevision)
        {
            var shop = ResolveShopContext(seller, shopKey);
            EnsureLegacyDraftMigrated(shop);
            var path = PathForShop(shop);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            long restoredRevision = 0;

            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "ReceptionWorkflowPublish", 3000))
            {
                if (lease == null || !lease.Acquired)
                    throw new IOException("无法取得接待流程回滚写锁");

                var current = ReadCatalog(path);
                var currentRevision = current.Value<long>("catalog_revision");
                if (expectedCurrentRevision.HasValue
                    && currentRevision != expectedCurrentRevision.Value)
                    throw new InvalidOperationException("自动回滚CAS失败：当前目录已被其他发布更新，未回滚新版本");
                if (currentRevision <= 0)
                    throw new InvalidOperationException("当前没有已发布版本可供回滚");
                if (currentRevision == long.MaxValue)
                    throw new InvalidOperationException("接待流程目录修订版本已达到上限，拒绝回滚");

                var historyDirectory = HistoryDirectoryForShop(shop);
                if (!Directory.Exists(historyDirectory))
                    throw new FileNotFoundException("没有可回滚的历史发布版本");

                var backups = Directory.GetFiles(historyDirectory, "revision-*.json")
                    .Select(file => new {
                        Path = file,
                        Name = Path.GetFileNameWithoutExtension(file)
                    })
                    .Select(item => {
                        long revision;
                        return new {
                            item.Path,
                            Revision = long.TryParse(
                                item.Name.Substring("revision-".Length),
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out revision) ? revision : -1
                        };
                    })
                    .Where(item => item.Revision >= 0 && item.Revision < currentRevision)
                    .OrderByDescending(item => item.Revision)
                    .ToList();

                JObject restored = null;
                long targetRevision = 0;
                foreach (var backup in backups)
                {
                    try
                    {
                        var contents = File.ReadAllText(backup.Path, Encoding.UTF8);
                        var candidate = Validate(contents);
                        if (candidate.Value<long>("catalog_revision") != backup.Revision) continue;
                        restored = candidate;
                        targetRevision = backup.Revision;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorWithMaxCount("跳过无效的接待流程历史版本: revision="
                            + backup.Revision + ", error=" + ex.Message, 10);
                    }
                }

                if (restored == null)
                    throw new InvalidDataException("没有找到有效的上一发布版本；当前生效版本未修改");

                SaveHistorySnapshot(shop, current);
                BumpFlowVersions(restored, current);
                restoredRevision = currentRevision + 1;
                restored["catalog_revision"] = restoredRevision;
                restored["published_revision"] = restoredRevision;
                restored["published_at_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                restored["publication_mode"] = "autonomous-rollback";
                restored["rollback_from_revision"] = targetRevision;
                WriteCatalog(path, restored);
            }

            ReceptionWorkflowRuntimeService.InvalidateShop(shopKey);
            return "已自动回滚到上一有效发布版本的内容，并以新修订版本 "
                + restoredRevision.ToString(CultureInfo.InvariantCulture) + " 立即生效。";
        }
    }
}
