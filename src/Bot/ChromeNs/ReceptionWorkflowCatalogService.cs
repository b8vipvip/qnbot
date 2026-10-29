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
            var shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller);
            if (shop == null) throw new InvalidOperationException("店铺身份无法验证，不允许保存流程");
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
            var root = JObject.Parse(json ?? "");
            if (Convert.ToString(root["schema"]) != Schema || root.Value<int?>("catalog_version") != 1)
                throw new InvalidDataException("流程草稿协议不支持");
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
                if (flow.Value<int?>("flow_version") < 1 || flow.Value<int?>("flow_version") > 1000000)
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
                    if (required == null || required.Count > 10)
                        throw new InvalidDataException("缺少必要证据或证据种类过多");
                    foreach (var kind in required)
                        if (kind.Type != JTokenType.String || !AllowedEvidence.Contains(Convert.ToString(kind)))
                            throw new InvalidDataException("证据类型不受支持");
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
            foreach (var id in array)
                if (id.Type != JTokenType.String || !ValidKey(Convert.ToString(id)))
                    throw new InvalidDataException(label + " 存在非法标识");
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
            var data = Validate(json).ToString(Formatting.Indented);
            var path = PathForShop(seller);
            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "ReceptionWorkflowDraft", 3000))
            {
                if (lease == null || !lease.Acquired) throw new IOException("无法获取草稿写锁");
                var folder = Path.GetDirectoryName(path);
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                if (File.Exists(path))
                    File.Copy(path, path + ".backup." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"), false);
                string error;
                if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(path, data, 3, 50, out error))
                    throw new IOException(error);
            }
            return "流程草稿保存成功；未被激活，未赋予消息发送、充值或退款权限。";
        }

        public static string RestoreLatestDraft(string seller)
        {
            var path = PathForShop(seller);
            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "ReceptionWorkflowDraft", 3000))
            {
                if (lease == null || !lease.Acquired) throw new IOException("无法获取恢复写锁");
                var folder = Path.GetDirectoryName(path);
                if (!Directory.Exists(folder)) throw new FileNotFoundException("暂无草稿备份");
                var original = Directory.GetFiles(folder, Path.GetFileName(path) + ".backup.*")
                    .OrderByDescending(x => x, StringComparer.Ordinal).FirstOrDefault();
                if (original == null) throw new FileNotFoundException("暂无草稿备份");
                var validated = Validate(File.ReadAllText(original, Encoding.UTF8)).ToString(Formatting.Indented);
                if (File.Exists(path))
                    File.Copy(path, path + ".backup." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"), false);
                string error;
                if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(path, validated, 3, 50, out error))
                    throw new IOException(error);
            }
            return "已恢复上一份流程草稿备份；仍需单独人工审核和灰度激活。";
        }
    }
}
