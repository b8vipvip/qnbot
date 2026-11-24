using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bot.ChromeNs
{
    internal sealed class KugouVisualRule
    {
        public string Id { get; set; }
        public List<List<string>> AllCueGroups { get; set; }
        public List<string> AnyCues { get; set; }
        public List<string> NoneCues { get; set; }
        public List<List<string>> PositiveCueGroups { get; set; }
        public List<string> NonePositiveCues { get; set; }
        public string Reply { get; set; }
        public string Reason { get; set; }
        public string Stage { get; set; }
    }

    internal sealed class KugouReceptionConfig
    {
        public bool Enabled { get; set; }
        public bool EnableAutoGuidance { get; set; }
        public bool EnablePhoneWait { get; set; }
        public bool ManualReviewRequired { get; set; }
        public bool AutomaticRechargeEnabled { get; set; }
        public string GuideUrl { get; set; }
        public string PolicyPrompt { get; set; }
        public string VisualUncertainReply { get; set; }
        public string ScreenshotReplyPattern { get; set; }
        public string PhoneWaitReason { get; set; }
        public List<string> ProductIds { get; set; }
        public List<string> QuarantineKnowledgeIds { get; set; }
        public List<string> QuarantineTextPatterns { get; set; }
        public List<string> BlockedOutgoingPatterns { get; set; }
        public List<string> VisualUncertainPatterns { get; set; }
        public List<string> PhotoInstructionAllCues { get; set; }
        public List<string> PhotoInstructionAnyCues { get; set; }
        public List<string> PhotoInstructionExcludedCues { get; set; }
        public List<string> ScreenshotQuestionCues { get; set; }
        public List<KugouVisualRule> VisualRules { get; set; }

        public KugouReceptionConfig()
        {
            Enabled = false;
            EnableAutoGuidance = false;
            EnablePhoneWait = false;
            ManualReviewRequired = true;
            AutomaticRechargeEnabled = false;
            GuideUrl = string.Empty;
            PolicyPrompt = string.Empty;
            VisualUncertainReply = string.Empty;
            ScreenshotReplyPattern = string.Empty;
            PhoneWaitReason = string.Empty;
            ProductIds = new List<string>();
            QuarantineKnowledgeIds = new List<string>();
            QuarantineTextPatterns = new List<string>();
            BlockedOutgoingPatterns = new List<string>();
            VisualUncertainPatterns = new List<string>();
            PhotoInstructionAllCues = new List<string>();
            PhotoInstructionAnyCues = new List<string>();
            PhotoInstructionExcludedCues = new List<string>();
            ScreenshotQuestionCues = new List<string>();
            VisualRules = new List<KugouVisualRule>();
        }
    }

    // Business facts live in a per-ShopKey profile. The shipped/default profile is deliberately
    // empty and disabled; JSON can never grant recharge or waive mandatory human review.
    internal static class KugouReceptionConfigService
    {
        private const string Schema = "qnbot.shop.reception-rules.v3";
        private const string LegacySchemaV2 = "qnbot.kugou.reception-rules.v2";
        private const string LegacySchemaV1 = "qnbot.kugou.reception-rules.v1";
        private static readonly Regex SafeId = new Regex(@"^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled);

        private static ShopContext ResolveShop(string seller)
        {
            var shop = KugouReceptionPolicy.ResolveShopForSeller(seller);
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey) || !shop.HasStableSellerId)
                throw new InvalidOperationException("店铺身份不稳定或未绑定唯一 ShopKey，禁止读取或修改专属接待配置。");
            return shop;
        }

        private static string PathForShop(ShopContext shop)
        {
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey) || !shop.HasStableSellerId)
                throw new InvalidOperationException("必须使用具有稳定 SellerId 的精确 ShopKey。");
            using (ShopSettingsScope.Enter(shop))
            {
                var folder = Path.GetDirectoryName(BusinessPolicyProfileService.GetUserPath());
                if (string.IsNullOrWhiteSpace(folder))
                    throw new InvalidOperationException("店铺配置目录为空。");
                return Path.Combine(folder, "shop-reception-rules.json");
            }
        }

        private static string LegacyPathForShop(ShopContext shop)
        {
            var current = PathForShop(shop);
            return Path.Combine(Path.GetDirectoryName(current), "kugou-reception-rules.json");
        }

        private static JObject DefaultDocument()
        {
            return new JObject
            {
                ["schema"] = Schema,
                ["version"] = 3,
                ["enabled"] = false,
                ["product_ids"] = new JArray(),
                ["quarantine_knowledge_ids"] = new JArray(),
                ["enable_auto_guidance"] = false,
                ["enable_phone_wait"] = false,
                ["guide_url"] = string.Empty,
                ["policy_prompt"] = string.Empty,
                ["quarantine_text_patterns"] = new JArray(),
                ["blocked_outgoing_patterns"] = new JArray(),
                ["visual_uncertain_patterns"] = new JArray(),
                ["visual_uncertain_reply"] = string.Empty,
                ["visual_rules"] = new JArray(),
                ["photo_instruction_all_cues"] = new JArray(),
                ["photo_instruction_any_cues"] = new JArray(),
                ["photo_instruction_excluded_cues"] = new JArray(),
                ["phone_wait_reason"] = string.Empty,
                ["screenshot_question_cues"] = new JArray(),
                ["screenshot_reply_pattern"] = string.Empty,
                ["manual_review_required"] = true,
                ["automatic_recharge_enabled"] = false,
                ["notes"] = "本配置按 ShopKey 隔离。默认关闭；配置商品ID、审核后的业务事实和规则并经人工核验后才可启用。自动充值始终关闭，业务知识始终需要人工审核。"
            };
        }

        private static JObject MigrateLegacyV1(JObject legacy)
        {
            if (legacy == null || Convert.ToString(legacy["schema"]) != LegacySchemaV1
                || Convert.ToInt32(legacy["version"] ?? 0) != 1)
                throw new InvalidOperationException("不支持的旧版接待配置。");
            if (legacy.Value<bool?>("manual_review_required") != true
                || legacy.Value<bool?>("automatic_recharge_enabled") != false)
                throw new InvalidOperationException("旧配置安全门禁不满足要求，拒绝自动迁移。");

            var migrated = DefaultDocument();
            var oldUrl = Convert.ToString(legacy["tcl_guide_url"]);
            if (IsHttpsUrl(oldUrl)) migrated["guide_url"] = oldUrl.Trim();
            migrated["notes"] = "由旧版配置自动迁移为通用 v3。为避免把旧源码业务事实自动复制到店铺配置，已保持 enabled=false；请人工核对并填写 product_ids、隔离ID、业务提示、视觉规则和教程地址后再启用。";
            return migrated;
        }

        private static JObject MigrateLegacyV2(JObject legacy)
        {
            if (legacy == null || Convert.ToString(legacy["schema"]) != LegacySchemaV2
                || Convert.ToInt32(legacy["version"] ?? 0) != 2)
                throw new InvalidOperationException("不支持的旧版接待配置。");
            if (legacy.Value<bool?>("manual_review_required") != true
                || legacy.Value<bool?>("automatic_recharge_enabled") != false)
                throw new InvalidOperationException("旧配置安全门禁不满足要求，拒绝自动迁移。");

            legacy["schema"] = Schema;
            legacy["version"] = 3;
            legacy["notes"] = (Convert.ToString(legacy["notes"]) ?? string.Empty)
                + " 已迁移为通用 ShopKey 配置格式 v3；所有业务名称、产品标识和规则内容均为店铺自定义数据。";
            return legacy;
        }

        private static bool IsHttpsUrl(string value)
        {
            Uri uri;
            return !string.IsNullOrWhiteSpace(value)
                && value.Length <= 2048
                && Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && !string.IsNullOrWhiteSpace(uri.Host)
                && string.IsNullOrEmpty(uri.UserInfo);
        }

        private static JObject Validate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException("接待规则 JSON 不能为空。");
            JObject obj;
            try { obj = JObject.Parse(raw); }
            catch (JsonException ex) { throw new InvalidOperationException("接待规则 JSON 格式错误：" + ex.Message); }

            var schema = Convert.ToString(obj["schema"]);
            if (string.Equals(schema, LegacySchemaV1, StringComparison.Ordinal))
                obj = MigrateLegacyV1(obj);
            else if (string.Equals(schema, LegacySchemaV2, StringComparison.Ordinal))
                obj = MigrateLegacyV2(obj);
            if (!string.Equals(Convert.ToString(obj["schema"]), Schema, StringComparison.Ordinal))
                throw new InvalidOperationException("接待规则 schema 不匹配。");
            if (Convert.ToInt32(obj["version"] ?? 0) != 3)
                throw new InvalidOperationException("不支持的接待规则版本。");

            RequireBoolean(obj, "enabled");
            RequireBoolean(obj, "enable_auto_guidance");
            RequireBoolean(obj, "enable_phone_wait");
            RequireBoolean(obj, "manual_review_required");
            RequireBoolean(obj, "automatic_recharge_enabled");
            if (obj.Value<bool?>("manual_review_required") != true)
                throw new InvalidOperationException("不能关闭业务知识人工审核。");
            if (obj.Value<bool?>("automatic_recharge_enabled") != false)
                throw new InvalidOperationException("不能通过流程 JSON 授权自动充值。");

            var productIds = ReadStringArray(obj["product_ids"], "product_ids", 100);
            var quarantineIds = ReadStringArray(obj["quarantine_knowledge_ids"], "quarantine_knowledge_ids", 500);
            foreach (var id in productIds.Concat(quarantineIds))
                if (!SafeId.IsMatch(id)) throw new InvalidOperationException("商品ID或知识ID包含非法字符。");

            var guideUrl = ReadStringField(obj, "guide_url", 2048, true);
            if (!string.IsNullOrWhiteSpace(guideUrl) && !IsHttpsUrl(guideUrl))
                throw new InvalidOperationException("教程地址必须是有效 HTTPS URL，且不能包含用户名或密码。");
            var prompt = ReadStringField(obj, "policy_prompt", 12000, true);

            var quarantinePatterns = ReadStringArray(obj["quarantine_text_patterns"], "quarantine_text_patterns", 100);
            var blockedPatterns = ReadStringArray(obj["blocked_outgoing_patterns"], "blocked_outgoing_patterns", 100);
            var uncertainPatterns = ReadStringArray(obj["visual_uncertain_patterns"], "visual_uncertain_patterns", 100);
            foreach (var pattern in quarantinePatterns.Concat(blockedPatterns).Concat(uncertainPatterns))
                CompilePattern(pattern);

            var uncertainReply = ReadStringField(obj, "visual_uncertain_reply", 2000, true);
            if (uncertainPatterns.Count > 0 && string.IsNullOrWhiteSpace(uncertainReply))
                throw new InvalidOperationException("配置 visual_uncertain_patterns 时必须提供 visual_uncertain_reply。");

            var visualRules = obj["visual_rules"] as JArray;
            if (visualRules == null || visualRules.Count > 32)
                throw new InvalidOperationException("visual_rules 必须是最多32条规则的数组。");
            var visualRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in visualRules)
                if (!visualRuleIds.Add(ValidateVisualRule(token, guideUrl)))
                    throw new InvalidOperationException("visual_rules 不允许重复 id。");

            var allCues = ReadStringArray(obj["photo_instruction_all_cues"], "photo_instruction_all_cues", 30);
            var anyCues = ReadStringArray(obj["photo_instruction_any_cues"], "photo_instruction_any_cues", 30);
            var excludedCues = ReadStringArray(obj["photo_instruction_excluded_cues"], "photo_instruction_excluded_cues", 30);
            var screenshotCues = ReadStringArray(obj["screenshot_question_cues"], "screenshot_question_cues", 30);
            var screenshotPattern = ReadStringField(obj, "screenshot_reply_pattern", 1000, true);
            if (!string.IsNullOrWhiteSpace(screenshotPattern)) CompilePattern(screenshotPattern);
            var waitReason = ReadStringField(obj, "phone_wait_reason", 500, true);
            ReadStringField(obj, "notes", 2000, false);

            if (obj.Value<bool>("enabled"))
            {
                if (productIds.Count == 0)
                    throw new InvalidOperationException("启用专属接待前必须配置至少一个 product_ids。");
                if (string.IsNullOrWhiteSpace(prompt))
                    throw new InvalidOperationException("启用专属接待前必须填写经人工审核的 policy_prompt。");
            }
            if (obj.Value<bool>("enable_auto_guidance")
                && (!obj.Value<bool>("enabled") || visualRules.Count == 0))
                throw new InvalidOperationException("自动视觉指导必须先启用店铺配置并至少配置一条视觉规则。");
            if (obj.Value<bool>("enable_phone_wait")
                && (!obj.Value<bool>("enabled") || allCues.Count == 0 || anyCues.Count == 0
                    || string.IsNullOrWhiteSpace(waitReason)))
                throw new InvalidOperationException("启用手机号等待前必须配置店铺专属指令证据条件和等待说明。");

            var allowed = new[]
            {
                "schema", "version", "enabled", "product_ids", "quarantine_knowledge_ids",
                "enable_auto_guidance", "enable_phone_wait", "guide_url", "policy_prompt",
                "quarantine_text_patterns", "blocked_outgoing_patterns",
                "visual_uncertain_patterns", "visual_uncertain_reply", "visual_rules",
                "photo_instruction_all_cues", "photo_instruction_any_cues",
                "photo_instruction_excluded_cues", "phone_wait_reason",
                "screenshot_question_cues", "screenshot_reply_pattern",
                "manual_review_required", "automatic_recharge_enabled", "notes"
            };
            var unknown = obj.Properties().FirstOrDefault(p => !allowed.Contains(p.Name, StringComparer.Ordinal));
            if (unknown != null) throw new InvalidOperationException("不支持的配置字段：" + unknown.Name);
            return obj;
        }

        private static void RequireBoolean(JObject obj, string name)
        {
            if (obj[name] == null || obj[name].Type != JTokenType.Boolean)
                throw new InvalidOperationException(name + " 必须为布尔值。");
        }

        private static string ReadStringField(JObject obj, string name, int maxLength, bool required)
        {
            var token = obj == null ? null : obj[name];
            if (token == null)
            {
                if (required) throw new InvalidOperationException(name + " 必须是字符串。");
                return string.Empty;
            }
            if (token.Type != JTokenType.String)
                throw new InvalidOperationException(name + " 必须是字符串。");
            var value = Convert.ToString(token) ?? string.Empty;
            if (value.Length > maxLength)
                throw new InvalidOperationException(name + " 超过 " + maxLength + " 字符。");
            return value;
        }

        private static List<string> ReadStringArray(JToken token, string name, int max)
        {
            var array = token as JArray;
            if (array == null || array.Count > max)
                throw new InvalidOperationException(name + " 必须是长度不超过 " + max + " 的字符串数组。");
            var values = new List<string>();
            foreach (var item in array)
            {
                if (item == null || item.Type != JTokenType.String)
                    throw new InvalidOperationException(name + " 只能包含字符串。");
                var value = Convert.ToString(item).Trim();
                if (value.Length == 0 || value.Length > 2048)
                    throw new InvalidOperationException(name + " 包含空值或过长项目。");
                if (values.Contains(value, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException(name + " 不允许重复项目。");
                values.Add(value);
            }
            return values;
        }

        private static List<List<string>> ReadCueGroups(JToken token, string name)
        {
            var outer = token as JArray;
            if (outer == null || outer.Count > 20)
                throw new InvalidOperationException(name + " 必须是最多20组的数组。");
            var groups = new List<List<string>>();
            foreach (var group in outer)
            {
                var values = ReadStringArray(group, name + " group", 30);
                if (values.Count == 0) throw new InvalidOperationException(name + " 不允许空条件组。");
                groups.Add(values);
            }
            return groups;
        }

        private static void CompilePattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 1000)
                throw new InvalidOperationException("正则表达式不能为空且不得超过1000字符。");
            try { new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
            catch (Exception ex) { throw new InvalidOperationException("正则表达式无效：" + ex.Message); }
        }

        private static string ValidateVisualRule(JToken token, string guideUrl)
        {
            var rule = token as JObject;
            if (rule == null) throw new InvalidOperationException("visual_rules 项必须是对象。");
            var id = ReadStringField(rule, "id", 80, true);
            var reply = ReadStringField(rule, "reply", 2000, true);
            var reason = ReadStringField(rule, "reason", 120, true);
            var stage = ReadStringField(rule, "stage", 120, true);
            if (!SafeId.IsMatch(id))
                throw new InvalidOperationException("视觉规则 id 只能包含字母、数字、下划线和连字符。");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 80
                || string.IsNullOrWhiteSpace(reply) || reply.Length > 2000
                || string.IsNullOrWhiteSpace(reason) || reason.Length > 120
                || string.IsNullOrWhiteSpace(stage) || stage.Length > 120)
                throw new InvalidOperationException("视觉规则必须包含有效的 id、reply、reason 和 stage。");
            var allGroups = ReadCueGroups(rule["all_cue_groups"], "all_cue_groups");
            var positiveGroups = ReadCueGroups(rule["positive_cue_groups"], "positive_cue_groups");
            var any = ReadStringArray(rule["any_cues"], "any_cues", 30);
            var none = ReadStringArray(rule["none_cues"], "none_cues", 30);
            var nonePositive = ReadStringArray(rule["none_positive_cues"], "none_positive_cues", 30);
            if (allGroups.Count == 0 && positiveGroups.Count == 0 && any.Count == 0)
                throw new InvalidOperationException("视觉规则必须至少包含一种正向匹配条件。");
            if (reply.Contains("{guide_url}") && string.IsNullOrWhiteSpace(guideUrl))
                throw new InvalidOperationException("视觉答复使用 {guide_url} 时必须配置 guide_url。");
            var allowed = new[]
            {
                "id", "all_cue_groups", "positive_cue_groups", "any_cues",
                "none_cues", "none_positive_cues", "reply", "reason", "stage"
            };
            var unknown = rule.Properties().FirstOrDefault(p => !allowed.Contains(p.Name, StringComparer.Ordinal));
            if (unknown != null) throw new InvalidOperationException("视觉规则包含未知字段：" + unknown.Name);
            return id;
        }

        private static KugouReceptionConfig ToConfig(JObject obj)
        {
            var config = new KugouReceptionConfig
            {
                Enabled = obj.Value<bool>("enabled"),
                EnableAutoGuidance = obj.Value<bool>("enable_auto_guidance"),
                EnablePhoneWait = obj.Value<bool>("enable_phone_wait"),
                ManualReviewRequired = obj.Value<bool>("manual_review_required"),
                AutomaticRechargeEnabled = obj.Value<bool>("automatic_recharge_enabled"),
                GuideUrl = Convert.ToString(obj["guide_url"]) ?? string.Empty,
                PolicyPrompt = Convert.ToString(obj["policy_prompt"]) ?? string.Empty,
                VisualUncertainReply = Convert.ToString(obj["visual_uncertain_reply"]) ?? string.Empty,
                ScreenshotReplyPattern = Convert.ToString(obj["screenshot_reply_pattern"]) ?? string.Empty,
                PhoneWaitReason = Convert.ToString(obj["phone_wait_reason"]) ?? string.Empty,
                ProductIds = ReadStringArray(obj["product_ids"], "product_ids", 100),
                QuarantineKnowledgeIds = ReadStringArray(obj["quarantine_knowledge_ids"], "quarantine_knowledge_ids", 500),
                QuarantineTextPatterns = ReadStringArray(obj["quarantine_text_patterns"], "quarantine_text_patterns", 100),
                BlockedOutgoingPatterns = ReadStringArray(obj["blocked_outgoing_patterns"], "blocked_outgoing_patterns", 100),
                VisualUncertainPatterns = ReadStringArray(obj["visual_uncertain_patterns"], "visual_uncertain_patterns", 100),
                PhotoInstructionAllCues = ReadStringArray(obj["photo_instruction_all_cues"], "photo_instruction_all_cues", 30),
                PhotoInstructionAnyCues = ReadStringArray(obj["photo_instruction_any_cues"], "photo_instruction_any_cues", 30),
                PhotoInstructionExcludedCues = ReadStringArray(obj["photo_instruction_excluded_cues"], "photo_instruction_excluded_cues", 30),
                ScreenshotQuestionCues = ReadStringArray(obj["screenshot_question_cues"], "screenshot_question_cues", 30),
                VisualRules = new List<KugouVisualRule>()
            };
            var rules = obj["visual_rules"] as JArray;
            if (rules != null)
            {
                foreach (var token in rules)
                {
                    var rule = (JObject)token;
                    config.VisualRules.Add(new KugouVisualRule
                    {
                        Id = Convert.ToString(rule["id"]),
                        AllCueGroups = ReadCueGroups(rule["all_cue_groups"], "all_cue_groups"),
                        PositiveCueGroups = ReadCueGroups(rule["positive_cue_groups"], "positive_cue_groups"),
                        AnyCues = ReadStringArray(rule["any_cues"], "any_cues", 30),
                        NoneCues = ReadStringArray(rule["none_cues"], "none_cues", 30),
                        NonePositiveCues = ReadStringArray(rule["none_positive_cues"], "none_positive_cues", 30),
                        Reply = Convert.ToString(rule["reply"]),
                        Reason = Convert.ToString(rule["reason"]),
                        Stage = Convert.ToString(rule["stage"])
                    });
                }
            }
            return config;
        }

        private static JObject ReadDocument(ShopContext shop)
        {
            var path = PathForShop(shop);
            var sourcePath = File.Exists(path) ? path : LegacyPathForShop(shop);
            if (!File.Exists(sourcePath)) return DefaultDocument();

            string error;
            var raw = CrossProcessAtomicStateFile.ReadAllTextShared(sourcePath, 2, 30, out error);
            if (!string.IsNullOrWhiteSpace(error))
                throw new IOException("读取店铺接待配置失败：" + error);
            if (string.IsNullOrWhiteSpace(raw)) return DefaultDocument();

            var validated = Validate(raw);
            if (!string.Equals(sourcePath, path, StringComparison.OrdinalIgnoreCase))
            {
                using (var lease = CrossProcessAtomicStateFile.Acquire(path, "ShopReceptionRules", 3000))
                {
                    if (lease == null || !lease.Acquired)
                        throw new IOException("旧版接待配置迁移锁未获取，未修改原配置。");
                    if (!File.Exists(path))
                    {
                        var migrated = validated.ToString(Formatting.Indented);
                        if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(path, migrated, 3, 50, out error))
                            throw new IOException("通用接待配置迁移失败：" + error);
                    }
                    else
                    {
                        var current = CrossProcessAtomicStateFile.ReadAllTextShared(path, 2, 30, out error);
                        if (!string.IsNullOrWhiteSpace(error))
                            throw new IOException("读取已迁移接待配置失败：" + error);
                        if (!string.IsNullOrWhiteSpace(current)) validated = Validate(current);
                    }
                }
            }
            return validated;
        }

        public static string GetJson(string seller)
        {
            var shop = ResolveShop(seller);
            return ReadDocument(shop).ToString(Formatting.Indented);
        }

        // A lightweight revision token lets the policy cache notice profile changes
        // made by another Bot process without reparsing the JSON on every buyer message.
        internal static string GetProfileRevision(ShopContext shop)
        {
            try
            {
                var info = new FileInfo(PathForShop(shop));
                info.Refresh();
                if (!info.Exists)
                {
                    info = new FileInfo(LegacyPathForShop(shop));
                    info.Refresh();
                }
                return info.Exists
                    ? info.LastWriteTimeUtc.Ticks.ToString() + ":" + info.Length.ToString()
                    : "missing";
            }
            catch
            {
                // Empty means "do not trust a cached match"; callers must recompute.
                return string.Empty;
            }
        }

        internal static KugouReceptionConfig GetConfig(ShopContext shop)
        {
            try { return ToConfig(ReadDocument(shop)); }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("店铺接待配置无效或不可用，保持专属流程关闭：" + ex.Message, 10);
                return new KugouReceptionConfig();
            }
        }

        public static KugouReceptionConfig GetConfig(string seller)
        {
            try { return GetConfig(ResolveShop(seller)); }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("店铺接待身份无法唯一解析，保持专属流程关闭：" + ex.Message, 10);
                return new KugouReceptionConfig();
            }
        }

        public static string SaveJson(string seller, string raw)
        {
            var shop = ResolveShop(seller);
            ReadDocument(shop); // migrate an existing legacy profile before the first generic write
            var validated = Validate(raw).ToString(Formatting.Indented);
            var path = PathForShop(shop);
            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "ShopReceptionRules", 3000))
            {
                if (lease == null || !lease.Acquired) throw new IOException("配置锁未获取，未修改任何规则。");
                var directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                if (File.Exists(path))
                {
                    var backup = path + ".backup." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
                        + "-" + Guid.NewGuid().ToString("N");
                    File.Copy(path, backup, false);
                }
                string error;
                if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(path, validated, 3, 50, out error))
                    throw new IOException("流程规则原子保存失败：" + error);
            }
            KugouReceptionPolicy.InvalidateShopCache(shop.ShopKey);
            return "当前 ShopKey 的接待规则已验证并保存；旧版本已备份。未触发任何买家回复或充值。";
        }

        public static string RestoreLatestBackup(string seller)
        {
            var shop = ResolveShop(seller);
            var path = PathForShop(shop);
            string selectedBackup = null;
            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "ShopReceptionRules", 3000))
            {
                if (lease == null || !lease.Acquired) throw new IOException("恢复锁未获取。");
                var directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory)) throw new FileNotFoundException("尚无历史备份。");
                var backups = Directory.GetFiles(directory, Path.GetFileName(path) + ".backup.*")
                    .OrderByDescending(x => x, StringComparer.Ordinal).ToList();
                if (backups.Count == 0) throw new FileNotFoundException("尚无可恢复的流程规则备份。");
                string original = null;
                foreach (var candidate in backups)
                {
                    try
                    {
                        original = Validate(File.ReadAllText(candidate, Encoding.UTF8)).ToString(Formatting.Indented);
                        selectedBackup = candidate;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorWithMaxCount("跳过无效的店铺接待规则备份: file="
                            + Path.GetFileName(candidate) + ", error=" + ex.Message, 10);
                    }
                }
                if (original == null)
                    throw new InvalidDataException("没有找到有效的店铺接待规则备份，当前配置未修改。");
                if (File.Exists(path))
                    File.Copy(path, path + ".backup." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
                        + "-" + Guid.NewGuid().ToString("N"), false);
                string error;
                if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(path, original, 3, 50, out error))
                    throw new IOException("恢复历史备份失败：" + error);
            }
            KugouReceptionPolicy.InvalidateShopCache(shop.ShopKey);
            return "已恢复最近一份有效的当前 ShopKey 接待规则备份：" + Path.GetFileName(selectedBackup);
        }
    }
}