using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Merchant-independent versioned workflow drafts. No active workflow is created
    /// by saving draft JSON. Activation requires a separately authenticated approval
    /// and trusted external order/evidence tools, not yet implemented here.
    /// </summary>
    internal static class ReceptionWorkflowCatalogService
    {
        private const string Schema = "qnbot.reception-workflow-drafts.v1";
        private static readonly HashSet<string> AllowedActions =
            new HashSet<string>(new[] {
                "WAIT", "ASK_EVIDENCE", "HANDOFF", "READ_ONLY_TOOL", "REPLY_REVIEWED"
            }, StringComparer.Ordinal);
        private static readonly HashSet<string> AllowedEvidence =
            new HashSet<string>(new[] {
                "order_snapshot", "buyer_message", "image_message",
                "vision_observation", "manual_correction", "verified_account_snapshot"
            }, StringComparer.Ordinal);

        private static string PathForShop(string seller)
        {
            var shop = KugouReceptionPolicy.ResolveShopForSeller(seller);
            if (shop == null
                || string.IsNullOrWhiteSpace(shop.ShopKey)
                || !shop.HasStableSellerId)
                throw new InvalidOperationException("店铺身份不稳定或未绑定唯一ShopKey，不允许保存流程草稿");
            using (ShopSettingsScope.Enter(shop))
            {
                var dir = Path.GetDirectoryName(BusinessPolicyProfileService.GetUserPath());
                if (string.IsNullOrWhiteSpace(dir)) throw new InvalidOperationException("店铺隔离目录缺失");
                return Path.Combine(dir, "reception-workflow-drafts.json");
            }
        }

        private static JObject Default()
        {
            return new JObject {
                ["schema"] = Schema,
                ["catalog_version"] = 1,
                ["draft_revision"] = 0,
                ["flows"] = new JArray {
                    new JObject {
                        ["flow_id"] = "example-flow",
                        ["flow_version"] = 1,
                        ["status"] = "draft",
                        ["product_ids"] = new JArray(),
                        ["sku_ids"] = new JArray(),
                        ["steps"] = new JArray {
                            new JObject {
                                ["state"] = "awaiting_evidence",
                                ["required_evidence"] = new JArray("image_message"),
                                ["action"] = "ASK_EVIDENCE",
                                ["next_state"] = "manual_review",
                                ["timeout_minutes"] = 30
                            },
                            new JObject {
                                ["state"] = "manual_review",
                                ["required_evidence"] = new JArray("manual_correction"),
                                ["action"] = "HANDOFF",
                                ["next_state"] = "manual_review",
                                ["timeout_minutes"] = 0
                            }
                        }
                    }
                }
            };
        }

        private static JObject Validate(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 1024 * 1024)
                throw new InvalidDataException("流程草稿为空或超过1MiB安全上限");
            var root = JObject.Parse(json);
            if (Convert.ToString(root["schema"]) != Schema || root.Value<int?>("catalog_version") != 1)
                throw new InvalidDataException("流程草稿协议不支持");
            long draftRevision = 0;
            var revisionToken = root["draft_revision"];
            if (revisionToken != null)
            {
                if (revisionToken.Type != JTokenType.Integer
                    || !long.TryParse(revisionToken.ToString(), out draftRevision)
                    || draftRevision < 0)
                    throw new InvalidDataException("流程草稿修订版本无效");
            }
            var flows = root["flows"] as JArray;
            if (flows == null || flows.Count > 50) throw new InvalidDataException("流程数量超出安全范围");
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
                if (Convert.ToString(flow["status"]) != "draft")
                    throw new InvalidDataException("保存草稿不能启用/发布业务流程");
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
                        throw new InvalidDataException("包含未经审批的高风险流程动作");
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
                }
                foreach (var step in steps.Cast<JObject>())
                {
                    var next = Convert.ToString(step["next_state"]);
                    if (!stages.Contains(next))
                        throw new InvalidDataException("下一状态在当前流程中不存在：" + next);
                }
            }
            // Do not accept unknown executable fragments, URLs, secrets or arbitrary
            // payloads as if they were authoritative workflow settings.
            var canonical = new JObject {
                ["schema"] = Schema,
                ["catalog_version"] = 1,
                ["draft_revision"] = draftRevision,
                ["flows"] = new JArray(flows.Select(f => {
                    var flow = (JObject)f;
                    return new JObject {
                        ["flow_id"] = Convert.ToString(flow["flow_id"]),
                        ["flow_version"] = flow.Value<int>("flow_version"),
                        ["status"] = "draft",
                        ["product_ids"] = flow["product_ids"].DeepClone(),
                        ["sku_ids"] = flow["sku_ids"].DeepClone(),
                        ["steps"] = new JArray(((JArray)flow["steps"]).Cast<JObject>().Select(s => new JObject {
                            ["state"] = Convert.ToString(s["state"]),
                            ["required_evidence"] = s["required_evidence"].DeepClone(),
                            ["action"] = Convert.ToString(s["action"]),
                            ["next_state"] = Convert.ToString(s["next_state"]),
                            ["timeout_minutes"] = s.Value<int>("timeout_minutes")
                        }))
                    };
                }))
            };
            return canonical;
        }

        private static bool ValidKey(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.Length > 80) return false;
            return s.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.');
        }

        private static void CheckKeys(JArray array, string label)
        {
            if (array == null || array.Count > 100) throw new InvalidDataException(label + " 列表无效");
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in array)
            {
                if (id.Type != JTokenType.String || !ValidKey(Convert.ToString(id)))
                    throw new InvalidDataException(label + " 存在非法标识");
                if (!unique.Add(Convert.ToString(id)))
                    throw new InvalidDataException(label + " 不允许重复标识");
            }
        }

        private static long ReadCurrentRevision(string path)
        {
            if (!File.Exists(path)) return 0;
            string error;
            var contents = CrossProcessAtomicStateFile.ReadAllTextShared(path, 3, 30, out error);
            if (!string.IsNullOrWhiteSpace(error)) throw new IOException(error);
            if (string.IsNullOrWhiteSpace(contents))
                throw new InvalidDataException("当前流程草稿文件为空；拒绝覆盖未知版本");
            return Validate(contents).Value<long>("draft_revision");
        }

        private static string CreateBackupPath(string path)
        {
            // Seven fractional timestamp digits preserve chronological lexical ordering
            // even when multiple edits are saved within the same millisecond.
            return path + ".backup." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfffffff")
                + "-" + Guid.NewGuid().ToString("N");
        }

        public static string GetDraftJson(string seller)
        {
            var path = PathForShop(seller);
            string error;
            var contents = CrossProcessAtomicStateFile.ReadAllTextShared(path, 2, 30, out error);
            if (!string.IsNullOrWhiteSpace(error)) throw new IOException(error);
            return (string.IsNullOrWhiteSpace(contents) ? Default() : Validate(contents))
                .ToString(Formatting.Indented);
        }

        public static string SaveDraft(string seller, string json)
        {
            var data = Validate(json);
            var expectedRevision = data.Value<long>("draft_revision");
            var path = PathForShop(seller);
            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "ReceptionWorkflowDraft", 3000))
            {
                if (lease == null || !lease.Acquired) throw new IOException("无法获取草稿写锁");
                var currentRevision = ReadCurrentRevision(path);
                if (expectedRevision != currentRevision)
                    throw new InvalidOperationException("流程草稿已被其他窗口更新；本次修改未覆盖任何数据，请重新读取最新草稿后再编辑");
                if (currentRevision == long.MaxValue)
                    throw new InvalidOperationException("流程草稿修订版本已达到上限，拒绝继续覆盖");
                var folder = Path.GetDirectoryName(path);
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                if (File.Exists(path))
                    File.Copy(path, CreateBackupPath(path), false);
                data["draft_revision"] = currentRevision + 1;
                string error;
                if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(
                    path, data.ToString(Formatting.Indented), 3, 50, out error))
                    throw new IOException(error);
            }
            return "流程草稿保存成功；已递增修订版本，仍未激活或赋予消息发送、充值、退款权限。";
        }

        public static string RestoreLatestDraft(string seller)
        {
            var path = PathForShop(seller);
            string selectedBackup = null;
            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "ReceptionWorkflowDraft", 3000))
            {
                if (lease == null || !lease.Acquired) throw new IOException("无法获取恢复写锁");
                var currentRevision = ReadCurrentRevision(path);
                if (currentRevision == long.MaxValue)
                    throw new InvalidOperationException("流程草稿修订版本已达到上限，拒绝恢复覆盖");
                var folder = Path.GetDirectoryName(path);
                if (!Directory.Exists(folder)) throw new FileNotFoundException("暂无草稿备份");
                var backups = Directory.GetFiles(folder, Path.GetFileName(path) + ".backup.*")
                    .OrderByDescending(x => x, StringComparer.Ordinal).ToList();
                if (backups.Count == 0) throw new FileNotFoundException("暂无草稿备份");
                JObject restored = null;
                foreach (var candidate in backups)
                {
                    try
                    {
                        restored = Validate(File.ReadAllText(candidate, Encoding.UTF8));
                        selectedBackup = candidate;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorWithMaxCount("跳过无效的流程草稿备份: file="
                            + Path.GetFileName(candidate) + ", error=" + ex.Message, 10);
                    }
                }
                if (restored == null)
                    throw new InvalidDataException("没有找到有效的流程草稿备份，当前草稿未修改。");
                if (File.Exists(path))
                    File.Copy(path, CreateBackupPath(path), false);
                // Restoring old content must still advance the revision. Otherwise a
                // stale editor holding the restored backup's revision could overwrite it.
                restored["draft_revision"] = currentRevision + 1;
                string error;
                if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(
                    path, restored.ToString(Formatting.Indented), 3, 50, out error))
                    throw new IOException(error);
            }
            return "已恢复最近一份有效的流程草稿备份：" + Path.GetFileName(selectedBackup)
                + "；修订版本已递增，仍需单独人工审核和灰度激活。";
        }
    }
}
