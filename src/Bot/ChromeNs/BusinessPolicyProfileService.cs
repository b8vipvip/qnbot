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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bot.ChromeNs
{
    internal static class BusinessPolicyProfileService
    {
        private const string Schema = "qnbot.business-policy";
        private static readonly string LegacySchema = "qianniu" + "-ai-bot.business-policy";
        private const int MaxPolicyRegexLength = 1000;
        private const int MaxPolicyPatternCount = 100;
        private const int MaxHandoffOverrides = 100;
        private static readonly Regex NeverRegex = new Regex("(?!)", RegexOptions.Compiled);
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);
        private static readonly ConcurrentDictionary<string, PolicyState> States =
            new ConcurrentDictionary<string, PolicyState>(StringComparer.OrdinalIgnoreCase);
        private static long _lastStatesSweepUtcTicks;

        private sealed class PolicyState
        {
            public readonly object Sync = new object();
            public JObject Policy = new JObject();
            public Dictionary<string, Regex> Regexes = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
            public DateTime LoadedWriteUtc = DateTime.MinValue;
            public long LoadedLength = -1;
            public DateTime NextCheckUtc = DateTime.MinValue;
            public bool Loaded;
        }

        public static Regex GetRegex(string path)
        {
            var state = EnsureLoaded();
            lock (state.Sync)
            {
                Regex value;
                if (state.Regexes.TryGetValue(path ?? string.Empty, out value)) return value;
                var pattern = ReadString(state.Policy, path);
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    state.Regexes[path ?? string.Empty] = NeverRegex;
                    return NeverRegex;
                }
                try
                {
                    value = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("运行策略正则无效: path=" + Safe(path) + ", error=" + Safe(ex.Message), 10);
                    value = NeverRegex;
                }
                state.Regexes[path ?? string.Empty] = value;
                return value;
            }
        }

        public static string GetString(string path)
        {
            var state = EnsureLoaded();
            lock (state.Sync) return ReadString(state.Policy, path);
        }

        public static string GetJson()
        {
            var state = EnsureLoaded();
            lock (state.Sync) return state.Policy.ToString(Formatting.Indented);
        }

        public static string GetDefaultJson()
        {
            var path = GetDefaultPath();
            if (!File.Exists(path)) throw new FileNotFoundException("安装包缺少默认运行策略文件。", path);
            var json = File.ReadAllText(path, Encoding.UTF8);
            Validate(json);
            return JObject.Parse(json).ToString(Formatting.Indented);
        }

        public static string SaveJson(string json)
        {
            var shop = ShopSettingsScope.Current;
            if (shop == null)
                throw new InvalidOperationException("保存运行策略必须处于已验证的店铺作用域，禁止写入全局默认策略。");

            var normalized = Validate(json).ToString(Formatting.Indented);
            var path = GetUserPath(shop);
            var directory = Path.GetDirectoryName(path);
            var backup = string.Empty;
            using (var lease = CrossProcessAtomicStateFile.Acquire(path, "BusinessPolicyProfile", 3000))
            {
                if (lease == null || !lease.Acquired)
                    throw new IOException("运行策略跨进程写锁未获取，未修改任何配置。");
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                if (File.Exists(path))
                {
                    var backupDirectory = CurrentBackupRoot(directory, shop);
                    Directory.CreateDirectory(backupDirectory);
                    backup = Path.Combine(backupDirectory,
                        "business-policy-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
                        + "-" + Guid.NewGuid().ToString("N") + ".json");
                    File.Copy(path, backup, false);
                }
                string error;
                if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(path, normalized, 3, 50, out error))
                    throw new IOException("运行策略原子保存失败：" + error);
            }
            Invalidate(path);
            EnsureLoaded();
            Log.Info("客户端运行策略JSON已保存: file=" + Path.GetFileName(path)
                + (string.IsNullOrWhiteSpace(backup) ? string.Empty : ", backup=" + Path.GetFileName(backup)));
            return backup;
        }

        public static string RestoreDefault()
        {
            return SaveJson(GetDefaultJson());
        }

        public static string GetUserPath()
        {
            var shop = ShopSettingsScope.Current;
            // Without an explicit shop scope, runtime reads use only the packaged
            // neutral policy. The legacy global file is considered solely while
            // initializing an exact shop-scoped profile.
            return shop == null ? GetDefaultPath() : GetUserPath(shop);
        }

        private static string GetUserPath(ShopContext shop)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            return Path.Combine(Paths.GetRulesRoot(shop), "business-policy.json");
        }

        public static bool TryOverrideHandoff(
            string question,
            AutoReplyRuleDecision decision,
            out string detail)
        {
            detail = string.Empty;
            if (decision == null || !decision.Matched || string.IsNullOrWhiteSpace(decision.HitKeyword)) return false;
            var state = EnsureLoaded();

            JArray rules;
            lock (state.Sync)
            {
                rules = state.Policy["handoffOverrides"] as JArray;
                if (rules == null) return false;
                rules = (JArray)rules.DeepClone();
            }

            foreach (var token in rules)
            {
                var rule = token as JObject;
                if (rule == null) continue;
                var keyword = Convert.ToString(rule["keyword"]);
                if (!Same(keyword, decision.HitKeyword)) continue;

                var text = question ?? string.Empty;
                var strong = CompileOptional(Convert.ToString(rule["strongRiskPattern"]));
                var allowAi = CompileOptional(Convert.ToString(rule["allowAiPattern"]));
                var fixedReply = CompileOptional(Convert.ToString(rule["fixedReplyPattern"]));

                if (strong.IsMatch(text))
                {
                    detail = "客户端JSON强风险语境成立，继续人工确认";
                    return false;
                }
                if (allowAi.IsMatch(text))
                {
                    decision.AllowAutoReply = true;
                    decision.UseAiReply = true;
                    decision.IsOffHours = false;
                    decision.ReplyText = string.Empty;
                    decision.Reason = "命中客户端JSON普通业务例外：" + keyword;
                    detail = "客户端JSON允许继续智能回答";
                    return true;
                }
                if (fixedReply.IsMatch(text))
                {
                    decision.AllowAutoReply = true;
                    decision.UseAiReply = false;
                    decision.IsOffHours = false;
                    decision.ReplyText = Convert.ToString(rule["fixedReply"]).Trim();
                    decision.Reason = "命中客户端JSON固定业务例外：" + keyword;
                    detail = "客户端JSON固定答复例外";
                    return true;
                }
                return false;
            }
            return false;
        }

        private static void CleanupIdleStates(string activePath)
        {
            // PolicyState contains compiled regexes and a parsed JSON tree per ShopKey.
            // Retire inactive profiles after 30 minutes; do a bounded sweep at most
            // every five minutes. Lock + exact key/value removal prevents an old
            // enumeration snapshot from deleting a state that was just refreshed.
            var now = DateTime.UtcNow;
            var nowTicks = now.Ticks;
            var previousTicks = Interlocked.Read(ref _lastStatesSweepUtcTicks);
            if (nowTicks - previousTicks < TimeSpan.FromMinutes(5).Ticks
                || Interlocked.CompareExchange(
                    ref _lastStatesSweepUtcTicks, nowTicks, previousTicks) != previousTicks)
                return;

            var cutoff = now.AddMinutes(-30);
            var exactEntries = (ICollection<KeyValuePair<string, PolicyState>>)States;
            foreach (var pair in States)
            {
                if (string.Equals(pair.Key, activePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                var state = pair.Value;
                lock (state.Sync)
                {
                    if (state.NextCheckUtc == DateTime.MinValue || state.NextCheckUtc >= cutoff)
                        continue;
                    exactEntries.Remove(pair);
                }
            }
        }

        private static PolicyState EnsureLoaded()
        {
            var shop = ShopSettingsScope.Current;
            var userPath = shop == null ? GetDefaultPath() : GetUserPath(shop);
            CleanupIdleStates(userPath);
            while (true)
            {
                var state = States.GetOrAdd(userPath, _ => new PolicyState());
                lock (state.Sync)
                {
                    PolicyState current;
                    if (!States.TryGetValue(userPath, out current) || !ReferenceEquals(current, state))
                        continue;
                    var now = DateTime.UtcNow;
                    if (state.Loaded && now < state.NextCheckUtc) return state;
                    state.NextCheckUtc = now.AddSeconds(2);

                // No ambient shop identity means no merchant policy. Read the
                // packaged neutral defaults and never fall back to the legacy
                // process-global business-policy.json.
                if (shop != null)
                {
                    try
                    {
                        EnsureUserFile(userPath, shop);
                    }
                    catch (Exception ex)
                    {
                        // A competing Bot process may briefly hold the profile mutex.
                        // Do not fail the buyer reply pipeline: retain the last verified
                        // policy, or use the neutral packaged policy on first load, then
                        // retry after the normal short cache interval.
                        Log.ErrorWithMaxCount(
                            "店铺运行策略暂时不可用，使用缓存或中性默认策略并稍后重试: " + Safe(ex.Message), 10);
                        if (state.Loaded) return state;
                        state.Policy = Validate(GetDefaultJson());
                        state.Regexes = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
                        state.LoadedWriteUtc = DateTime.MinValue;
                        state.LoadedLength = -1;
                        state.Loaded = true;
                        return state;
                    }
                }
                var info = new FileInfo(userPath);
                if (state.Loaded && info.Exists
                    && info.LastWriteTimeUtc == state.LoadedWriteUtc
                    && info.Length == state.LoadedLength) return state;

                JObject loaded;
                try
                {
                    loaded = Validate(File.ReadAllText(userPath, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("读取客户端运行策略失败，回退安装包默认策略: " + Safe(ex.Message), 10);
                    loaded = Validate(GetDefaultJson());
                }
                state.Policy = loaded;
                state.Regexes = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
                info.Refresh();
                state.LoadedWriteUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
                state.LoadedLength = info.Exists ? info.Length : -1;
                state.Loaded = true;
                Log.Info("客户端运行策略已加载: version=" + Convert.ToString(state.Policy["version"])
                    + ", file=" + userPath + (shop == null ? ", scope=neutral-default" : ", shop=" + shop.ShopKey));
                    return state;
                }
            }
        }

        private static void EnsureUserFile(string userPath, ShopContext shop)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            using (var lease = CrossProcessAtomicStateFile.Acquire(userPath, "BusinessPolicyProfile", 3000))
            {
                if (lease == null || !lease.Acquired)
                    throw new IOException("运行策略初始化跨进程写锁未获取。");
                // Recheck under the named mutex: another Bot process may have created
                // this ShopKey profile after our initial File.Exists check.
                if (File.Exists(userPath)) return;
                var directory = Path.GetDirectoryName(userPath);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

                var source = GetDefaultPath();
                var legacy = Path.Combine(PathEx.GlobalDataDir, "business-policy.json");
                if (shop != null && CanAutoAdoptLegacy() && File.Exists(legacy)) source = legacy;
                if (!File.Exists(source))
                    throw new FileNotFoundException("安装包缺少默认运行策略文件。", source);
                var initial = File.ReadAllText(source, Encoding.UTF8);
                string error;
                if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(userPath, initial, 3, 50, out error))
                    throw new IOException("运行策略初始化原子保存失败：" + error);
            }
        }

        private static bool CanAutoAdoptLegacy()
        {
            try { return Profiles.GetAll().Count == 1; }
            catch { return false; }
        }

        private static JObject Validate(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new Exception("运行策略JSON不能为空。");
            JObject root;
            try { root = JObject.Parse(json); }
            catch (JsonException ex) { throw new Exception("运行策略JSON格式错误：" + ex.Message); }

            var schema = Convert.ToString(root["schema"]);
            if (!string.IsNullOrWhiteSpace(schema) && !Same(schema, Schema) && !Same(schema, LegacySchema))
                throw new Exception("运行策略schema不匹配：" + schema);
            root["schema"] = Schema;
            if (!(root["patterns"] is JObject)) throw new Exception("运行策略缺少 patterns 对象。");
            if (!(root["stages"] is JObject)) throw new Exception("运行策略缺少 stages 对象。");

            var patterns = (JObject)root["patterns"];
            if (patterns.Count > MaxPolicyPatternCount)
                throw new Exception("patterns 项目数量超过限制。");
            foreach (var property in patterns.Properties())
            {
                if (property.Value == null || property.Value.Type != JTokenType.String)
                    throw new Exception("patterns." + property.Name + " 必须是字符串。");
                var pattern = Convert.ToString(property.Value);
                if (!string.IsNullOrWhiteSpace(pattern))
                    CompileOptional(pattern);
            }
            if (root["handoffOverrides"] != null && !(root["handoffOverrides"] is JArray))
                throw new Exception("handoffOverrides 必须是对象数组。");
            var overrides = root["handoffOverrides"] as JArray;
            if (overrides != null)
            {
                if (overrides.Count > MaxHandoffOverrides)
                    throw new Exception("handoffOverrides 项目数量超过限制。");
                var handoffKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in overrides)
                {
                    var obj = item as JObject;
                    if (obj == null) throw new Exception("handoffOverrides 必须是对象数组。");
                    var keyword = ReadPolicyString(obj, "keyword", 128, true).Trim();
                    if (!handoffKeywords.Add(keyword))
                        throw new Exception("handoffOverrides 不允许重复 keyword。");
                    var strong = ReadPolicyString(obj, "strongRiskPattern", MaxPolicyRegexLength, false);
                    var allowAi = ReadPolicyString(obj, "allowAiPattern", MaxPolicyRegexLength, false);
                    var fixedPattern = ReadPolicyString(obj, "fixedReplyPattern", MaxPolicyRegexLength, false);
                    var fixedReply = ReadPolicyString(obj, "fixedReply", 2000, false);
                    CompileOptional(strong);
                    CompileOptional(allowAi);
                    CompileOptional(fixedPattern);
                    if (!string.IsNullOrWhiteSpace(fixedPattern) && string.IsNullOrWhiteSpace(fixedReply))
                        throw new Exception("handoffOverrides.fixedReplyPattern 非空时必须提供 fixedReply。");
                }
            }
            return root;
        }

        private static string ReadPolicyString(JObject obj, string name, int maxLength, bool required)
        {
            var token = obj == null ? null : obj[name];
            if (token == null)
            {
                if (required) throw new Exception(name + " 必须是字符串。");
                return string.Empty;
            }
            if (token.Type != JTokenType.String)
                throw new Exception(name + " 必须是字符串。");
            var value = Convert.ToString(token) ?? string.Empty;
            if (required && string.IsNullOrWhiteSpace(value))
                throw new Exception(name + " 不能为空。");
            if (value.Length > maxLength)
                throw new Exception(name + " 超过 " + maxLength + " 字符。");
            return value;
        }

        private static Regex CompileOptional(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return NeverRegex;
            if (pattern.Length > MaxPolicyRegexLength)
                throw new Exception("运行策略正则表达式超过 " + MaxPolicyRegexLength + " 字符。");
            try { return new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
            catch (Exception ex) { throw new Exception("正则表达式无效：" + ex.Message); }
        }

        private static string ReadString(JObject root, string path)
        {
            JToken token = root;
            foreach (var part in (path ?? string.Empty).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var obj = token as JObject;
                if (obj == null) return string.Empty;
                token = obj[part];
                if (token == null) return string.Empty;
            }
            return Convert.ToString(token) ?? string.Empty;
        }

        private static string GetDefaultPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "default-business-policy.json");
        }

        private static string CurrentBackupRoot(string fallbackDirectory, ShopContext shop)
        {
            return shop == null ? Path.Combine(fallbackDirectory, "backups") : Paths.GetBackupRoot(shop);
        }

        private static void Invalidate(string path)
        {
            PolicyState state;
            if (!States.TryGetValue(path, out state)) return;
            lock (state.Sync)
            {
                state.Loaded = false;
                state.NextCheckUtc = DateTime.MinValue;
                state.LoadedWriteUtc = DateTime.MinValue;
                state.LoadedLength = -1;
                state.Regexes.Clear();
            }
        }

        private static bool Same(string left, string right)
        {
            return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string Safe(string value)
        {
            value = Regex.Replace((value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(), @"\s+", " ");
            return value.Length <= 240 ? value : value.Substring(0, 240) + "...";
        }
    }
}