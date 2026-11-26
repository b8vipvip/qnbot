using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// One-time compatibility import for legacy server-side handoff rules. Each ShopKey uses its
    /// own client token and marker; a failure for one shop cannot block or overwrite another shop.
    /// </summary>
    internal static class HandoffPolicyLegacyMigrationService
    {
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);
        private static int _started;

        public static void StartOnce()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            Task.Run((Func<Task>)TryMigrateAllAsync);
        }

        private static async Task TryMigrateAllAsync()
        {
            IList<ShopProfile> profiles;
            try { profiles = Profiles.GetAll(); }
            catch { return; }
            foreach (var profile in profiles.Where(x => x != null))
            {
                try
                {
                    var shop = profile.ToContext();
                    if (shop == null || File.Exists(GetMarkerPath(shop))) continue;
                    await TryMigrateAsync(shop);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("旧版转人工策略迁移跳过异常店铺并继续：" + ex.Message, 10);
                }
            }
        }

        private static async Task TryMigrateAsync(ShopContext shop)
        {
            using (ShopSettingsScope.Enter(shop))
            {
                try
                {
                    var connection = new ShopControlPlaneConnectionStore(shop, Paths);
                    var serverUrl = connection.GetServerUrl();
                    string token;
                    string tokenError;
                    if (!connection.TryGetToken(out token, out tokenError)
                        || string.IsNullOrWhiteSpace(serverUrl)
                        || string.IsNullOrWhiteSpace(token))
                    {
                        Log.Info("本店旧服务端转人工策略暂未迁移：本店客户端令牌尚未配置。shop=" + shop.ShopKey);
                        return;
                    }

                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
                    using (var http = new HttpClient(handler))
                    using (var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        serverUrl.TrimEnd('/') + "/api/runtime/v1/handoff/rules"))
                    {
                        http.Timeout = TimeSpan.FromSeconds(10);
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        request.Headers.TryAddWithoutValidation("Accept", "application/json");
                        request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-handoff-policy-migration/2.0");
                        request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey);

                        using (var response = await http.SendAsync(request).ConfigureAwait(false))
                        {
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!response.IsSuccessStatusCode)
                                throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body));
                            var root = JObject.Parse(body);
                            var array = root["rules"] as JArray;
                            if (array == null) throw new Exception("服务端迁移响应缺少 rules 数组");
                            var rules = array.ToObject<List<RemoteHandoffRule>>() ?? new List<RemoteHandoffRule>();
                            HandoffRuleRemoteConfigService.SaveRules(rules);
                            WriteMarker(shop, Convert.ToString(root["revision"]), rules.Count, serverUrl);
                            Log.Info("本店旧服务端AI转人工策略已一次性迁移: shop=" + shop.ShopKey
                                + ", rules=" + rules.Count);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("本店旧服务端AI转人工策略迁移失败，下次启动继续尝试: shop="
                        + shop.ShopKey + ", error=" + Safe(ex.Message), 10);
                }
            }
        }

        private static void WriteMarker(ShopContext shop, string revision, int count, string serverUrl)
        {
            var path = GetMarkerPath(shop);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var payload = new JObject
            {
                ["schema"] = "qnbot.handoff-policy-server-migration",
                ["shopKey"] = shop.ShopKey,
                ["migratedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["revision"] = revision ?? string.Empty,
                ["ruleCount"] = count,
                ["source"] = serverUrl ?? string.Empty
            }.ToString();
            var temp = path + ".tmp";
            File.WriteAllText(temp, payload, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static string GetMarkerPath(ShopContext shop)
        {
            return Path.Combine(Paths.GetStateRoot(shop), "handoff-policy-server-migration.json");
        }

        private static string Safe(string value)
        {
            value = Regex.Replace(
                (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(),
                @"\s+", " ");
            return value.Length <= 300 ? value : value.Substring(0, 300) + "...";
        }
    }
}