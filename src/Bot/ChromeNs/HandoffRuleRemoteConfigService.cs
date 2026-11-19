using Bot.Options;
using Bot.ShopScope;
using BotLib;
using BotLib.Extensions;
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

namespace Bot.ChromeNs
{
    internal sealed class RemoteHandoffRule
    {
        [JsonProperty("id")]
        public int Id { get; set; }
        [JsonProperty("enabled")]
        public bool Enabled { get; set; }
        [JsonProperty("rule_type")]
        public string RuleType { get; set; }
        [JsonProperty("keyword")]
        public string Keyword { get; set; }
        [JsonProperty("match_mode")]
        public string MatchMode { get; set; }
        [JsonProperty("risk_terms")]
        public string RiskTerms { get; set; }
        [JsonProperty("exceptions")]
        public string Exceptions { get; set; }
        [JsonProperty("safe_reply")]
        public string SafeReply { get; set; }
        [JsonProperty("note")]
        public string Note { get; set; }
        [JsonProperty("sort_order")]
        public int SortOrder { get; set; }
        [JsonIgnore]
        public bool IsSelected { get; set; }

        public RemoteHandoffRule()
        {
            Enabled = true;
            RuleType = "confirm";
            MatchMode = "contains";
            Keyword = string.Empty;
            RiskTerms = string.Empty;
            Exceptions = string.Empty;
            SafeReply = string.Empty;
            Note = string.Empty;
        }
    }

    internal static class HandoffRuleRemoteConfigService
    {
        private const string Schema = "qnbot.handoff-policy";
        private static readonly string LegacySchema = "qianniu" + "-ai-bot.handoff-policy";
        private const int CurrentVersion = 1;
        private const string DefaultAccountSafeReply =
            "可以的，月卡可以给朋友或其他账号充值，您再拍对应月卡即可；下单后按页面提示提供需要充值的账号。";

        private sealed class RuleState
        {
            public readonly object Sync = new object();
            public List<RemoteHandoffRule> Rules = new List<RemoteHandoffRule>();
            public DateTime LastWriteUtc = DateTime.MinValue;
            public long LastLength = -1;
            public bool Loaded;
        }

        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);
        private static readonly ConcurrentDictionary<string, RuleState> States =
            new ConcurrentDictionary<string, RuleState>(StringComparer.OrdinalIgnoreCase);
        private static int _initialized;

        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
            HandoffPolicyUiBridge.Initialize();
            BulkListManagementUi.Initialize();
            Log.Info("本机AI转人工通知策略已启动：策略按 ShopKey 独立保存，不再访问服务端规则接口。");
        }

        public static string GetPath()
        {
            var shop = ShopSettingsScope.Current;
            if (shop != null) return Path.Combine(Paths.GetRulesRoot(shop), "handoff-policy.json");
            return Path.Combine(PathEx.GlobalDataDir, "handoff-policy.json");
        }

        public static List<RemoteHandoffRule> GetRules()
        {
            EnsureInitialized();
            var state = GetState(true);
            lock (state.Sync) return state.Rules.Select(Clone).ToList();
        }

        public static string ExportJson(IEnumerable<RemoteHandoffRule> rules)
        {
            var normalized = NormalizeRules(rules);
            return new JObject
            {
                ["schema"] = Schema,
                ["version"] = CurrentVersion,
                ["exportedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["shopKey"] = ShopSettingsScope.Current == null ? string.Empty : ShopSettingsScope.Current.ShopKey,
                ["rules"] = JArray.FromObject(normalized)
            }.ToString(Formatting.Indented);
        }

        public static List<RemoteHandoffRule> ParseImport(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new Exception("转人工策略JSON不能为空。");
            var token = JToken.Parse(json);
            var array = token as JArray;
            var root = token as JObject;
            if (array == null && root != null)
            {
                var schema = Convert.ToString(root["schema"]);
                if (!string.IsNullOrWhiteSpace(schema)
                    && !string.Equals(schema, Schema, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(schema, LegacySchema, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("文件类型不匹配：" + schema);
                array = root["rules"] as JArray ?? root["Rules"] as JArray;
            }
            if (array == null) throw new Exception("文件中没有 rules 规则数组。");
            return NormalizeRules(array.ToObject<List<RemoteHandoffRule>>());
        }

        public static void SaveRules(IEnumerable<RemoteHandoffRule> rules)
        {
            EnsureInitialized();
            var normalized = NormalizeRules(rules);
            var path = GetPath();
            var directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            BackupCurrentFile(path, "handoff-policy-before-save");
            AtomicWrite(path, ExportJson(normalized));
            var state = States.GetOrAdd(path, _ => new RuleState());
            lock (state.Sync)
            {
                state.Rules = normalized.Select(Clone).ToList();
                RefreshFileStamp(path, state);
                state.Loaded = true;
            }
            SyncKeywordsToLocalConfig(normalized);
            Log.Info("本店AI转人工通知策略已保存: enabled=" + normalized.Count(x => x.Enabled)
                + ", total=" + normalized.Count + ", file=" + path);
        }

        public static List<RemoteHandoffRule> ResetDefaults()
        {
            SaveRules(DefaultRules());
            return GetRules();
        }

        public static bool TryApplySafeAutoReply(
            string question,
            AutoReplyRuleDecision decision,
            out string detail)
        {
            detail = string.Empty;
            if (decision == null || !decision.Matched || string.IsNullOrWhiteSpace(decision.HitKeyword)) return false;
            var snapshot = GetRules();
            var rule = snapshot
                .Where(x => x != null && x.Enabled)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Id)
                .FirstOrDefault(x => Same(x.Keyword, decision.HitKeyword));
            if (rule == null) return false;

            string riskHit;
            string exceptionHit;
            var hasRisk = ContainsAny(question, rule.RiskTerms, out riskHit);
            var hasException = ContainsAny(question, rule.Exceptions, out exceptionHit);
            var contextual = string.Equals(rule.MatchMode, "sensitive_context", StringComparison.OrdinalIgnoreCase);
            if (contextual && hasRisk)
            {
                UpdateReason(decision, rule);
                detail = "本店策略敏感语境成立：" + riskHit;
                return false;
            }
            if (hasException)
            {
                ApplySafeReply(decision, rule.SafeReply, rule.Keyword, exceptionHit);
                detail = "本店策略安全例外：" + exceptionHit;
                return true;
            }
            UpdateReason(decision, rule);
            detail = contextual ? "本店策略敏感语境未充分确认，保持人工确认" : "本店策略包含规则命中";
            return false;
        }

        private static void EnsureInitialized()
        {
            if (_initialized == 0) Initialize();
        }

        private static RuleState GetState(bool synchronizeKeywords)
        {
            var path = GetPath();
            var state = States.GetOrAdd(path, _ => new RuleState());
            lock (state.Sync)
            {
                var info = new FileInfo(path);
                if (state.Loaded && info.Exists
                    && info.LastWriteTimeUtc == state.LastWriteUtc
                    && info.Length == state.LastLength) return state;
                try
                {
                    EnsurePolicyFile(path);
                    state.Rules = ParseImport(File.ReadAllText(path, Encoding.UTF8));
                    RefreshFileStamp(path, state);
                    state.Loaded = true;
                    if (synchronizeKeywords) SyncKeywordsToLocalConfig(state.Rules);
                }
                catch (Exception ex)
                {
                    state.Rules = DefaultRules();
                    state.Loaded = true;
                    if (synchronizeKeywords) SyncKeywordsToLocalConfig(state.Rules);
                    Log.ErrorWithMaxCount("读取本店AI转人工通知策略失败，已使用默认规则：" + Safe(ex.Message), 10);
                }
                return state;
            }
        }

        private static void EnsurePolicyFile(string path)
        {
            if (File.Exists(path)) return;
            var directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var legacy = Path.Combine(PathEx.GlobalDataDir, "handoff-policy.json");
            if (ShopSettingsScope.Current != null && CanAutoAdoptLegacy() && File.Exists(legacy))
            {
                AtomicWrite(path, File.ReadAllText(legacy, Encoding.UTF8));
                return;
            }
            AtomicWrite(path, ExportJson(DefaultRules()));
        }

        private static bool CanAutoAdoptLegacy()
        {
            try { return Profiles.GetAll().Count == 1; }
            catch { return false; }
        }

        private static List<RemoteHandoffRule> DefaultRules()
        {
            var result = new List<RemoteHandoffRule>();
            var manual = new[]
            {
                "退款", "退货", "投诉", "差评", "赔偿", "发票", "税票",
                "订单隐私", "身份证", "银行卡", "法律", "维权", "平台介入"
            };
            var confirm = new[]
            {
                "手机号", "地址", "隐私", "密码", "验证码", "转账", "补偿", "客服主管"
            };
            var order = 10;
            foreach (var keyword in manual)
            {
                result.Add(new RemoteHandoffRule
                {
                    Enabled = true,
                    RuleType = "manual",
                    Keyword = keyword,
                    MatchMode = "contains",
                    Note = "命中后转人工，不自动回答具体结论。",
                    SortOrder = order
                });
                order += 10;
            }
            foreach (var keyword in confirm)
            {
                result.Add(new RemoteHandoffRule
                {
                    Enabled = true,
                    RuleType = "confirm",
                    Keyword = keyword,
                    MatchMode = "contains",
                    Note = "命中后仅由人工确认。",
                    SortOrder = order
                });
                order += 10;
            }
            result.Add(new RemoteHandoffRule
            {
                Enabled = true,
                RuleType = "confirm",
                Keyword = "账号",
                MatchMode = "sensitive_context",
                RiskTerms = "密码|验证码|找回|被盗|盗号|冻结|解冻|封禁|实名|身份证|银行卡|泄露|安全|申诉|换绑|修改绑定",
                Exceptions = "电视端能登自己账号|电视能登录自己账号|大屏能绑定我的账号|另一个账号|其他账号|别的账号|朋友账号|好友账号|给朋友|给别人|帮朋友|帮别人|再拍|再买|购买|充值|充到|月卡",
                SafeReply = DefaultAccountSafeReply,
                Note = "账号安全问题转人工；本人设备登录能力和为其他账号购买属于正常业务。",
                SortOrder = order
            });
            return NormalizeRules(result);
        }

        private static List<RemoteHandoffRule> NormalizeRules(IEnumerable<RemoteHandoffRule> rules)
        {
            var output = new List<RemoteHandoffRule>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nextId = 1;
            foreach (var source in (rules ?? Enumerable.Empty<RemoteHandoffRule>()).Where(x => x != null).Take(300))
            {
                var keyword = Clean(source.Keyword, 120);
                if (keyword.Length == 0 || !seen.Add(keyword)) continue;
                var item = Clone(source);
                item.Id = source.Id > 0 ? source.Id : nextId;
                nextId = Math.Max(nextId, item.Id + 1);
                item.Enabled = source.Enabled;
                item.RuleType = string.Equals(source.RuleType, "manual", StringComparison.OrdinalIgnoreCase) ? "manual" : "confirm";
                item.MatchMode = string.Equals(source.MatchMode, "sensitive_context", StringComparison.OrdinalIgnoreCase)
                    ? "sensitive_context" : "contains";
                item.Keyword = keyword;
                item.RiskTerms = CleanTerms(source.RiskTerms, 3000);
                item.Exceptions = CleanTerms(source.Exceptions, 3000);
                item.SafeReply = Clean(source.SafeReply, 1200);
                item.Note = Clean(source.Note, 1000);
                item.SortOrder = source.SortOrder < 0 ? 0 : Math.Min(100000, source.SortOrder);
                item.IsSelected = false;
                output.Add(item);
            }
            return output.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToList();
        }

        private static void SyncKeywordsToLocalConfig(IEnumerable<RemoteHandoffRule> rules)
        {
            try
            {
                var enabled = (rules ?? Enumerable.Empty<RemoteHandoffRule>())
                    .Where(x => x != null && x.Enabled && !string.IsNullOrWhiteSpace(x.Keyword)).ToList();
                var manual = string.Join(",", enabled
                    .Where(x => string.Equals(x.RuleType, "manual", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.SortOrder).Select(x => x.Keyword.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
                var confirm = string.Join(",", enabled
                    .Where(x => string.Equals(x.RuleType, "confirm", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.SortOrder).Select(x => x.Keyword.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
                var cfg = BotFeatureStore.GetAutoReplyRules();
                if (cfg == null) return;
                if (string.Equals(cfg.ManualKeywords ?? string.Empty, manual, StringComparison.Ordinal)
                    && string.Equals(cfg.NoAutoReplyKeywords ?? string.Empty, confirm, StringComparison.Ordinal)) return;
                cfg.ManualKeywords = manual;
                cfg.NoAutoReplyKeywords = confirm;
                BotFeatureStore.SaveAutoReplyRules(cfg);
                Log.Info("本店AI转人工通知关键词已同步：强制=" + Split(manual).Count()
                    + "，人工确认=" + Split(confirm).Count());
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("同步本店AI转人工通知关键词失败：" + Safe(ex.Message), 10);
            }
        }

        private static void ApplySafeReply(AutoReplyRuleDecision decision, string reply, string keyword, string exceptionHit)
        {
            decision.AllowAutoReply = true;
            decision.UseAiReply = false;
            decision.IsOffHours = false;
            decision.ReplyText = string.IsNullOrWhiteSpace(reply) ? DefaultAccountSafeReply : reply.Trim();
            decision.Reason = "命中可自动回答的转人工规则例外：" + keyword
                + (string.IsNullOrWhiteSpace(exceptionHit) ? string.Empty : "（" + exceptionHit + "）");
        }

        private static void UpdateReason(AutoReplyRuleDecision decision, RemoteHandoffRule rule)
        {
            decision.HitKeyword = string.IsNullOrWhiteSpace(rule.Keyword) ? decision.HitKeyword : rule.Keyword.Trim();
            decision.Reason = string.Equals(rule.RuleType, "manual", StringComparison.OrdinalIgnoreCase)
                ? "命中强制转人工关键词：" + decision.HitKeyword
                : "命中仅人工确认关键词：" + decision.HitKeyword;
        }

        private static bool ContainsAny(string text, string values, out string hit)
        {
            text = text ?? string.Empty;
            foreach (var value in Split(values))
            {
                if (text.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0) continue;
                hit = value;
                return true;
            }
            hit = string.Empty;
            return false;
        }

        private static IEnumerable<string> Split(string values)
        {
            return (values ?? string.Empty)
                .Split(new[] { '|', ',', '，', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string CleanTerms(string value, int max) { return Clean(string.Join("|", Split(value)), max); }
        private static bool Same(string left, string right)
        {
            return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static RemoteHandoffRule Clone(RemoteHandoffRule rule)
        {
            if (rule == null) return null;
            return new RemoteHandoffRule
            {
                Id = rule.Id,
                Enabled = rule.Enabled,
                RuleType = rule.RuleType ?? string.Empty,
                Keyword = rule.Keyword ?? string.Empty,
                MatchMode = rule.MatchMode ?? string.Empty,
                RiskTerms = rule.RiskTerms ?? string.Empty,
                Exceptions = rule.Exceptions ?? string.Empty,
                SafeReply = rule.SafeReply ?? string.Empty,
                Note = rule.Note ?? string.Empty,
                SortOrder = rule.SortOrder,
                IsSelected = rule.IsSelected
            };
        }

        private static void BackupCurrentFile(string source, string prefix)
        {
            if (!File.Exists(source)) return;
            var shop = ShopSettingsScope.Current;
            var directory = shop == null
                ? Path.Combine(PathEx.GlobalDataDir, "backups")
                : Paths.GetBackupRoot(shop);
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory,
                prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".json");
            File.Copy(source, target, true);
        }

        private static void AtomicWrite(string path, string content)
        {
            string error;
            if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(
                path, content, 3, 50, out error, path + ".bak"))
                throw new IOException("配置原子保存失败：" + error);
        }

        private static void RefreshFileStamp(string path, RuleState state)
        {
            var info = new FileInfo(path);
            info.Refresh();
            state.LastWriteUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            state.LastLength = info.Exists ? info.Length : -1;
        }

        private static string Clean(string value, int max)
        {
            value = Regex.Replace((value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(), @"\s+", " ");
            return value.Length <= max ? value : value.Substring(0, max).Trim();
        }

        private static string Safe(string value) { return Clean(value, 300); }
    }
}