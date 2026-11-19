using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class StoreRuleScopes
    {
        public const string Text = "text";
        public const string Vision = "vision";
        public const string Both = "both";

        public static string Normalize(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (value == Vision || value == "image" || value == "visual") return Vision;
            if (value == Text || value == "chat") return Text;
            return Both;
        }
    }

    internal sealed class StoreContextRule
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public string Scope { get; set; }
        public int Priority { get; set; }
        public bool Enabled { get; set; }
        public List<string> Triggers { get; set; }
        public string Content { get; set; }

        public StoreContextRule()
        {
            Enabled = true;
            Priority = 50;
            Scope = StoreRuleScopes.Both;
            Triggers = new List<string>();
        }
    }

    internal sealed class StorePromptProfile
    {
        public int SchemaVersion { get; set; }
        public string RawInput { get; set; }

        // 旧版字段保留用于无损迁移。新版生成后该字段为空。
        public string StandardPrompt { get; set; }

        public string CorePrompt { get; set; }
        public List<StoreContextRule> Rules { get; set; }
        public string UpdatedAt { get; set; }

        public StorePromptProfile()
        {
            SchemaVersion = 2;
            Rules = new List<StoreContextRule>();
        }
    }

    internal static class StorePromptProfileService
    {
        private const int CurrentSchemaVersion = 2;
        private const int MaxCoreCharacters = 2500;
        private const int MaxStoredRules = 80;
        private const int MaxRuleCharacters = 2200;
        private const int MaxTextRules = 3;
        private const int MaxVisionRules = 8;
        private const int MaxTextRuleCharacters = 4200;
        private const int MaxVisionRuleCharacters = 6500;
        private const string ProfileFileName = "store-prompt-profile.json";

        private static readonly object Sync = new object();
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);
        private static readonly ConcurrentDictionary<string, StorePromptProfile> Cache =
            new ConcurrentDictionary<string, StorePromptProfile>(StringComparer.OrdinalIgnoreCase);

        internal static event Action<ShopContext> ProfileChanged;

        private sealed class ScoredRule
        {
            public StoreContextRule Rule;
            public double Score;
        }

        public static StorePromptProfile GetProfile()
        {
            var shop = RequireCurrentShop();
            var path = GetPath(shop);
            lock (Sync)
            {
                StorePromptProfile cached;
                if (Cache.TryGetValue(path, out cached)) return Clone(cached);
                cached = LoadInternal(shop, path);
                Cache[path] = cached;
                return Clone(cached);
            }
        }

        public static bool NeedsStructuredMigration(StorePromptProfile profile)
        {
            profile = profile ?? GetProfile();
            return profile.SchemaVersion < CurrentSchemaVersion
                || (string.IsNullOrWhiteSpace(profile.CorePrompt)
                    && !string.IsNullOrWhiteSpace(profile.StandardPrompt));
        }

        public static string GetStandardPrompt()
        {
            var profile = GetProfile();
            if (!string.IsNullOrWhiteSpace(profile.CorePrompt)) return profile.CorePrompt.Trim();
            return (profile.StandardPrompt ?? string.Empty).Trim();
        }

        // 每次AI调用只携带短小的核心规则。旧版配置在重新生成结构化规则前继续完整兼容，避免升级丢失业务约束。
        public static string BuildPromptAddon()
        {
            var profile = GetProfile();
            var core = ResolveCorePrompt(profile);
            if (string.IsNullOrWhiteSpace(core)) return string.Empty;

            var legacy = NeedsStructuredMigration(profile);
            return "\n\n【店铺核心规则与服务边界｜高优先级】\n"
                + core
                + (legacy
                    ? "\n当前仍为旧版整段提示词兼容模式。请到知识库的‘店铺规则中心’重新生成结构化规则，以减少每次请求长度。"
                    : string.Empty)
                + "\n以上内容是本店长期稳定的核心事实和边界。回答时必须遵守；不得自行扩大服务范围、售后保障、适用设备、账号规则或其他承诺。"
                + "如果当前买家问题与这些信息无关，不要生硬复述。\n";
        }

        // 文本回复只加载与当前会话状态最相关的少量场景规则。
        public static string BuildTextRulesAddon(ConversationStateSnapshot state)
        {
            if (state == null) return string.Empty;
            var context = new StringBuilder();
            context.Append(state.CurrentTopic).Append(' ')
                .Append(state.CurrentEntity).Append(' ')
                .Append(state.BuyerGoal).Append(' ')
                .Append(state.PendingQuestion).Append(' ')
                .Append(state.ConversationStage).Append(' ');
            if (state.Entities != null) context.Append(string.Join(" ", state.Entities));
            if (state.ConfirmedFacts != null) context.Append(' ').Append(string.Join(" ", state.ConfirmedFacts));
            return BuildRulesAddon(
                context.ToString(),
                StoreRuleScopes.Text,
                MaxTextRules,
                MaxTextRuleCharacters,
                false);
        }

        // 视觉模型在真正看图前无法知道品牌和界面，因此会携带有限数量的高优先级视觉规则卡；
        // 有买家文字或聊天上下文命中时，相关规则会排在最前面。
        public static string BuildVisionPromptAddon(string context)
        {
            var core = BuildPromptAddon();
            var rules = BuildRulesAddon(
                context,
                StoreRuleScopes.Vision,
                MaxVisionRules,
                MaxVisionRuleCharacters,
                true);
            return core + rules;
        }

        internal static string BuildRulesAddon(
            string context,
            string scope,
            int maxRules,
            int maxCharacters,
            bool includePriorityFallback)
        {
            var profile = GetProfile();
            if (NeedsStructuredMigration(profile) || profile.Rules == null || profile.Rules.Count == 0)
                return string.Empty;

            var selected = SelectRules(profile.Rules, context, scope, maxRules, includePriorityFallback);
            if (selected.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.Append("\n\n【按当前场景动态选取的店铺规则】\n")
                .Append("以下规则由本地规则检索器按当前问题、设备、品牌、账号、界面和会话状态选取。")
                .Append("只应用真正匹配当前场景的规则；不得把其他品牌、设备或系统的规则串用。\n");

            var count = 0;
            foreach (var rule in selected)
            {
                var block = new StringBuilder();
                block.Append("规则").Append(count + 1).Append("：")
                    .Append(CleanOneLine(rule.Title, 160)).Append("\n");
                if (!string.IsNullOrWhiteSpace(rule.Category))
                    block.Append("分类：").Append(CleanOneLine(rule.Category, 80)).Append("\n");
                block.Append("内容：").Append(Clean(rule.Content, MaxRuleCharacters)).Append("\n");

                if (sb.Length + block.Length > maxCharacters && count > 0) break;
                sb.Append(block);
                count++;
            }
            if (count == 0) return string.Empty;
            sb.Append("只能把这些规则作为当前回复的事实和流程边界，不要向买家展示规则标题、分类、内部原因或检索过程。\n");
            return sb.ToString();
        }

        public static void SaveStructured(
            string rawInput,
            string corePrompt,
            IList<StoreContextRule> rules)
        {
            var profile = new StorePromptProfile
            {
                SchemaVersion = CurrentSchemaVersion,
                RawInput = Clean(rawInput, 50000),
                StandardPrompt = string.Empty,
                CorePrompt = Clean(corePrompt, MaxCoreCharacters),
                Rules = NormalizeRules(rules),
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            SaveProfile(profile);
        }

        // 保留旧调用签名，避免旧模块或旧测试在升级期间失效。
        public static void Save(string rawInput, string standardPrompt)
        {
            var profile = new StorePromptProfile
            {
                SchemaVersion = 1,
                RawInput = Clean(rawInput, 50000),
                StandardPrompt = Clean(standardPrompt, 12000),
                CorePrompt = string.Empty,
                Rules = new List<StoreContextRule>(),
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            SaveProfile(profile);
        }

        public static async Task<StorePromptProfile> GenerateStructuredProfileAsync(
            string rawInput,
            CancellationToken token)
        {
            rawInput = Clean(rawInput, 50000);
            if (string.IsNullOrWhiteSpace(rawInput))
                throw new Exception("请先填写店铺介绍、服务范围、设备判断、售后保障等原始资料。");

            var messages = new JArray
            {
                new JObject
                {
                    ["role"] = "system",
                    ["content"] =
                        "你是电商AI客服规则架构师。请把商家提供的原始资料拆成‘短核心规则 + 可按场景检索的规则卡’，不要生成一整段每次都携带的超长提示词。"
                        + "必须严格忠于原始资料，不得补充或猜测价格、库存、时效、售后、链接能力、账号规则或服务范围。"
                        + "只输出一个JSON对象，不要Markdown，不要解释。JSON格式："
                        + "{\"core_prompt\":\"所有场景都必须携带的核心规则，控制在1500个中文字符以内\","
                        + "\"rules\":[{\"id\":\"稳定英文或拼音ID\",\"title\":\"简短规则名\",\"category\":\"设备判断/账号绑定/售后/链接范围/保密边界等\","
                        + "\"scope\":\"text或vision或both\",\"priority\":80,\"triggers\":[\"品牌\",\"设备\",\"软件名\",\"界面文字\",\"买家意图\"],\"content\":\"完整且可执行的场景规则\"}]}。"
                        + "core_prompt只能放店铺定位、所有场景通用的判断原则、绝不能泄露的内部边界、统一回复原则和全局明确禁令；不要把小米、TCL、海信等详细品牌流程全部塞进core_prompt。"
                        + "品牌、设备、软件、账号绑定、视觉界面、退款例外和具体操作步骤必须拆成独立规则卡；每张规则卡只解决一个相对完整的场景。"
                        + "视觉规则的triggers要包含图片中可能出现的稳定文字、界面名称、品牌、系统或版权渠道；文本规则的triggers要包含买家常见说法。"
                        + "海信等覆盖通用规则的明确例外要提高priority，并在content中写清优先级关系。"
                        + "最多生成60条规则；删除重复内容；未知事项写成需要人工确认，不得自行补全。"
                },
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = "请将以下店铺原始资料生成结构化店铺规则配置：\n\n" + rawInput
                }
            };

            var result = await Task.Run(
                () => MyOpenAI.CallStructuredChat(messages, 8000, 0.05, 300, token),
                token);
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.Answer))
            {
                throw new Exception(result == null || string.IsNullOrWhiteSpace(result.Error)
                    ? "AI没有返回有效的结构化规则。"
                    : result.Error);
            }

            var profile = ParseGeneratedProfile(result.Answer, rawInput);
            SaveProfile(profile);
            return Clone(profile);
        }

        // 旧版方法继续返回核心规则，供尚未迁移的调用兼容。
        public static async Task<string> GenerateStandardPromptAsync(
            string rawInput,
            CancellationToken token)
        {
            var profile = await GenerateStructuredProfileAsync(rawInput, token);
            return profile.CorePrompt;
        }

        public static string SerializeRules(IList<StoreContextRule> rules)
        {
            return JsonConvert.SerializeObject(NormalizeRules(rules), Formatting.Indented);
        }

        public static List<StoreContextRule> ParseRulesJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<StoreContextRule>();
            try
            {
                var token = JToken.Parse(json);
                var array = token as JArray;
                if (array == null && token["rules"] is JArray) array = (JArray)token["rules"];
                if (array == null) throw new Exception("场景规则必须是JSON数组。\n示例：[{\"title\":\"海信电视\",...}]");
                return NormalizeRules(array.ToObject<List<StoreContextRule>>());
            }
            catch (JsonException ex)
            {
                throw new Exception("场景规则JSON格式错误：" + ex.Message);
            }
        }

        internal static JObject BuildCloudPayload(StorePromptProfile profile)
        {
            profile = NormalizeProfile(profile);
            return new JObject
            {
                ["schemaVersion"] = profile.SchemaVersion,
                ["rawInput"] = profile.RawInput ?? string.Empty,
                ["standardPrompt"] = profile.StandardPrompt ?? string.Empty,
                ["corePrompt"] = profile.CorePrompt ?? string.Empty,
                ["rules"] = JArray.FromObject(profile.Rules ?? new List<StoreContextRule>())
            };
        }

        internal static void ApplyCloudPayload(JObject payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var profile = new StorePromptProfile
            {
                SchemaVersion = payload.Value<int?>("schemaVersion") ?? CurrentSchemaVersion,
                RawInput = Convert.ToString(payload["rawInput"] ?? string.Empty),
                StandardPrompt = Convert.ToString(payload["standardPrompt"] ?? string.Empty),
                CorePrompt = Convert.ToString(payload["corePrompt"] ?? string.Empty),
                Rules = payload["rules"] == null
                    ? new List<StoreContextRule>()
                    : payload["rules"].ToObject<List<StoreContextRule>>() ?? new List<StoreContextRule>(),
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            SaveProfile(profile);
        }

        internal static string GetCurrentProfilePath()
        {
            return GetPath(RequireCurrentShop());
        }

        private static StorePromptProfile ParseGeneratedProfile(string rawAnswer, string rawInput)
        {
            var json = ExtractJsonObject(rawAnswer);
            JObject obj;
            try
            {
                obj = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                throw new Exception("AI返回内容不是有效JSON：" + ex.Message);
            }

            var core = Convert.ToString(obj["core_prompt"] ?? obj["corePrompt"] ?? obj["CorePrompt"]);
            var rulesToken = obj["rules"] ?? obj["Rules"];
            var rules = new List<StoreContextRule>();
            var array = rulesToken as JArray;
            if (array != null)
            {
                foreach (var token in array.Take(MaxStoredRules))
                {
                    var item = token as JObject;
                    if (item == null) continue;
                    var triggers = new List<string>();
                    var triggerToken = item["triggers"] ?? item["keywords"];
                    var triggerArray = triggerToken as JArray;
                    if (triggerArray != null)
                        triggers.AddRange(triggerArray.Select(x => Convert.ToString(x)));
                    else if (triggerToken != null)
                        triggers.AddRange(SplitTriggers(Convert.ToString(triggerToken)));

                    int priority;
                    if (!int.TryParse(Convert.ToString(item["priority"]), out priority)) priority = 50;
                    rules.Add(new StoreContextRule
                    {
                        Id = Convert.ToString(item["id"]),
                        Title = Convert.ToString(item["title"] ?? item["name"]),
                        Category = Convert.ToString(item["category"]),
                        Scope = Convert.ToString(item["scope"]),
                        Priority = priority,
                        Enabled = item["enabled"] == null || Convert.ToBoolean(item["enabled"]),
                        Triggers = triggers,
                        Content = Convert.ToString(item["content"] ?? item["rule"])
                    });
                }
            }

            core = Clean(core, MaxCoreCharacters);
            rules = NormalizeRules(rules);
            if (core.Length < 20) throw new Exception("AI生成的核心规则过短，请检查原始资料后重试。");
            if (rules.Count == 0) throw new Exception("AI没有拆分出场景规则，请重试或减少原始资料中的歧义。");

            return new StorePromptProfile
            {
                SchemaVersion = CurrentSchemaVersion,
                RawInput = Clean(rawInput, 50000),
                StandardPrompt = string.Empty,
                CorePrompt = core,
                Rules = rules,
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }

        private static List<StoreContextRule> SelectRules(
            IList<StoreContextRule> rules,
            string context,
            string scope,
            int maxRules,
            bool includePriorityFallback)
        {
            scope = StoreRuleScopes.Normalize(scope);
            var scored = (rules ?? new List<StoreContextRule>())
                .Where(x => x != null && x.Enabled && !string.IsNullOrWhiteSpace(x.Content))
                .Where(x => RuleMatchesScope(x, scope))
                .Select(x => new ScoredRule { Rule = x, Score = CalculateRuleScore(x, context) })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Rule.Priority)
                .ToList();

            var selected = scored
                .Where(x => x.Score >= 0.26)
                .Take(Math.Max(1, maxRules))
                .Select(x => x.Rule)
                .ToList();

            if (includePriorityFallback && selected.Count < maxRules)
            {
                foreach (var candidate in scored
                    .OrderByDescending(x => x.Rule.Priority)
                    .ThenByDescending(x => x.Score))
                {
                    if (selected.Any(x => SameRule(x, candidate.Rule))) continue;
                    selected.Add(candidate.Rule);
                    if (selected.Count >= maxRules) break;
                }
            }
            return selected;
        }

        private static double CalculateRuleScore(StoreContextRule rule, string context)
        {
            var compactContext = Compact(context);
            if (compactContext.Length == 0) return Math.Max(0, Math.Min(0.12, rule.Priority / 1000.0));

            var score = Math.Max(0, Math.Min(0.10, rule.Priority / 1000.0));
            foreach (var trigger in rule.Triggers ?? new List<string>())
            {
                var compactTrigger = Compact(trigger);
                if (compactTrigger.Length < 2) continue;
                if (compactContext.Contains(compactTrigger))
                    score += 0.75 + Math.Min(0.35, compactTrigger.Length / 30.0);
                else
                    score += TextSimilarity(compactContext, compactTrigger) * 0.20;
            }

            var title = Compact((rule.Title ?? string.Empty) + " " + (rule.Category ?? string.Empty));
            if (title.Length > 0 && compactContext.Contains(title)) score += 0.70;
            else score += TextSimilarity(compactContext, title) * 0.35;
            return score;
        }

        private static bool RuleMatchesScope(StoreContextRule rule, string requestedScope)
        {
            var scope = StoreRuleScopes.Normalize(rule.Scope);
            return scope == StoreRuleScopes.Both || scope == requestedScope;
        }

        private static bool SameRule(StoreContextRule left, StoreContextRule right)
        {
            if (left == null || right == null) return false;
            if (!string.IsNullOrWhiteSpace(left.Id) && !string.IsNullOrWhiteSpace(right.Id))
                return string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
            return string.Equals(left.Title, right.Title, StringComparison.OrdinalIgnoreCase);
        }

        private static List<StoreContextRule> NormalizeRules(IList<StoreContextRule> source)
        {
            var result = new List<StoreContextRule>();
            var index = 0;
            foreach (var item in (source ?? new List<StoreContextRule>()).Where(x => x != null).Take(MaxStoredRules))
            {
                var content = Clean(item.Content, MaxRuleCharacters);
                if (string.IsNullOrWhiteSpace(content)) continue;
                index++;
                var id = CleanIdentifier(item.Id);
                if (string.IsNullOrWhiteSpace(id)) id = "store-rule-" + index.ToString("00");
                result.Add(new StoreContextRule
                {
                    Id = id,
                    Title = CleanOneLine(string.IsNullOrWhiteSpace(item.Title) ? "场景规则" + index : item.Title, 160),
                    Category = CleanOneLine(item.Category, 80),
                    Scope = StoreRuleScopes.Normalize(item.Scope),
                    Priority = Math.Max(0, Math.Min(100, item.Priority)),
                    Enabled = item.Enabled,
                    Triggers = (item.Triggers ?? new List<string>())
                        .SelectMany(SplitTriggers)
                        .Select(x => CleanOneLine(x, 60))
                        .Where(x => x.Length >= 2)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(20)
                        .ToList(),
                    Content = content
                });
            }
            return result;
        }

        private static IEnumerable<string> SplitTriggers(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim());
        }

        private static string ResolveCorePrompt(StorePromptProfile profile)
        {
            if (profile == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(profile.CorePrompt)) return profile.CorePrompt.Trim();
            return (profile.StandardPrompt ?? string.Empty).Trim();
        }

        private static void SaveProfile(StorePromptProfile profile)
        {
            var shop = RequireCurrentShop();
            profile = NormalizeProfile(profile);
            var path = GetPath(shop);
            var json = JsonConvert.SerializeObject(profile, Formatting.Indented);
            lock (Sync)
            {
                string error;
                if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(path, json, 3, 50, out error))
                    throw new IOException("店铺规则中心原子保存失败：" + error);
                Cache[path] = profile;
            }
            Log.Info("店铺规则中心已保存: shop=" + shop.ShopKey
                + ", schema=" + profile.SchemaVersion
                + ", coreChars=" + ResolveCorePrompt(profile).Length
                + ", rules=" + (profile.Rules == null ? 0 : profile.Rules.Count));
            var changed = ProfileChanged;
            if (changed != null) changed(shop);
        }

        private static StorePromptProfile LoadInternal(ShopContext shop, string path)
        {
            try
            {
                EnsureLegacyProfileMigrated(shop, path);
                if (!File.Exists(path)) return NewProfile();
                var json = File.ReadAllText(path, Encoding.UTF8);
                return NormalizeProfile(JsonConvert.DeserializeObject<StorePromptProfile>(json));
            }
            catch (Exception ex)
            {
                Log.Info("读取本店规则中心失败，使用空配置: shop=" + shop.ShopKey + ", error=" + ex.Message);
                return NewProfile();
            }
        }

        private static void EnsureLegacyProfileMigrated(ShopContext shop, string targetPath)
        {
            if (File.Exists(targetPath)) return;

            // 多店铺显式迁移会先把旧 data 文件放进本店兼容目录；只消费当前 ShopKey 的迁移结果。
            var scopedLegacy = Path.Combine(Paths.GetCompatibilityDataRoot(shop), ProfileFileName);
            if (File.Exists(scopedLegacy))
            {
                CopyLegacyProfile(shop, scopedLegacy, targetPath, "scoped-migration");
                return;
            }

            // 仅单店时允许自动继承历史全局规则。多店时绝不猜测归属。
            IList<ShopProfile> profiles;
            try { profiles = Profiles.GetAll(); }
            catch { profiles = new List<ShopProfile>(); }
            if (profiles.Count != 1 || !string.Equals(profiles[0].ShopKey, shop.ShopKey, StringComparison.Ordinal)) return;

            var globalLegacy = Path.Combine(Paths.LegacyDataRoot, ProfileFileName);
            if (File.Exists(globalLegacy)) CopyLegacyProfile(shop, globalLegacy, targetPath, "single-shop-global");
        }

        private static void CopyLegacyProfile(ShopContext shop, string source, string target, string mode)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var backup = Path.Combine(
                Paths.GetBackupRoot(shop),
                "store-rule-legacy-before-migrate-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".json");
            File.Copy(source, backup, true);
            File.Copy(source, target, false);
            Log.Info("旧店铺规则中心配置已迁移到本店: shop=" + shop.ShopKey
                + ", mode=" + mode + ", backup=" + backup);
        }

        private static StorePromptProfile NormalizeProfile(StorePromptProfile profile)
        {
            profile = profile ?? NewProfile();
            profile.RawInput = profile.RawInput ?? string.Empty;
            profile.StandardPrompt = profile.StandardPrompt ?? string.Empty;
            profile.CorePrompt = profile.CorePrompt ?? string.Empty;
            profile.UpdatedAt = profile.UpdatedAt ?? string.Empty;
            profile.Rules = NormalizeRules(profile.Rules);
            if (profile.SchemaVersion <= 0)
                profile.SchemaVersion = string.IsNullOrWhiteSpace(profile.StandardPrompt) ? CurrentSchemaVersion : 1;
            return profile;
        }

        private static ShopContext RequireCurrentShop()
        {
            var shop = ShopSettingsScope.Current;
            if (shop == null)
                throw new InvalidOperationException("当前没有店铺作用域，无法读取或保存店铺规则中心配置。");
            return shop;
        }

        private static string GetPath(ShopContext shop)
        {
            return Path.Combine(Paths.GetRulesRoot(shop), ProfileFileName);
        }

        private static StorePromptProfile Clone(StorePromptProfile source)
        {
            source = NormalizeProfile(source);
            return new StorePromptProfile
            {
                SchemaVersion = source.SchemaVersion,
                RawInput = source.RawInput,
                StandardPrompt = source.StandardPrompt,
                CorePrompt = source.CorePrompt,
                Rules = source.Rules.Select(x => new StoreContextRule
                {
                    Id = x.Id,
                    Title = x.Title,
                    Category = x.Category,
                    Scope = x.Scope,
                    Priority = x.Priority,
                    Enabled = x.Enabled,
                    Triggers = new List<string>(x.Triggers ?? new List<string>()),
                    Content = x.Content
                }).ToList(),
                UpdatedAt = source.UpdatedAt
            };
        }

        private static StorePromptProfile NewProfile()
        {
            return new StorePromptProfile
            {
                SchemaVersion = CurrentSchemaVersion,
                RawInput = string.Empty,
                StandardPrompt = string.Empty,
                CorePrompt = string.Empty,
                Rules = new List<StoreContextRule>(),
                UpdatedAt = string.Empty
            };
        }

        private static string ExtractJsonObject(string value)
        {
            value = (value ?? string.Empty).Trim();
            var start = value.IndexOf('{');
            var end = value.LastIndexOf('}');
            if (start < 0 || end <= start) return value;
            return value.Substring(start, end - start + 1);
        }

        private static string CleanIdentifier(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            value = Regex.Replace(value, @"[^a-z0-9_-]+", "-").Trim('-');
            return value.Length <= 80 ? value : value.Substring(0, 80).Trim('-');
        }

        private static string CleanOneLine(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max).Trim();
        }

        private static string Clean(string value, int max)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= max ? value : value.Substring(0, max).Trim();
        }

        private static string Compact(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[\s，。！？、；：,.!?:;\-—_()（）\[\]【】]+", string.Empty);
        }

        private static double TextSimilarity(string left, string right)
        {
            var a = Bigrams(Compact(left));
            var b = Bigrams(Compact(right));
            if (a.Count == 0 || b.Count == 0) return 0;
            var common = a.Intersect(b).Count();
            return (2.0 * common) / (a.Count + b.Count);
        }

        private static HashSet<string> Bigrams(string value)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < (value ?? string.Empty).Length; i++)
                result.Add(value.Substring(i, 2));
            return result;
        }
    }
}
