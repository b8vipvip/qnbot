using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib;
using BotLib.Db.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    public partial class App
    {
        private readonly object _storeRuleCloudSyncBootstrap =
            Knowledge.StoreRuleCloudSyncService.InitializeForApp();
    }
}

namespace Bot.Knowledge
{
    internal static class StoreRuleCloudSyncService
    {
        private const string Scope = "shop-cloud";
        private const string RevisionKey = "StoreRuleCloudRevision";
        private const string LastHashKey = "StoreRuleCloudLastHash";

        private sealed class ShopSyncState
        {
            public int Syncing;
        }

        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);
        private static readonly ConcurrentDictionary<string, ShopSyncState> States =
            new ConcurrentDictionary<string, ShopSyncState>(StringComparer.Ordinal);
        private static Timer _timer;
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                StorePromptProfileService.ProfileChanged += OnProfileChanged;
                _timer = new Timer(_ => QueueAll(), null, 7000, 8000);
                Log.Info("店铺规则云同步服务已启动：每个 ShopKey 使用独立 revision/hash 和本店令牌。");
            }
            return new object();
        }

        internal static async Task SyncNowAsync(ShopContext shop)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            if (!KnowledgeCloudSyncService.IsEnabledForShop(shop)) return;
            var state = GetState(shop);
            var acquired = false;
            for (var i = 0; i < 80; i++)
            {
                if (Interlocked.Exchange(ref state.Syncing, 1) == 0)
                {
                    acquired = true;
                    break;
                }
                await Task.Delay(125);
            }
            if (!acquired) throw new InvalidOperationException("本店店铺规则正在执行其他同步，请稍后重试。");
            try { await SyncOnceAsync(shop); }
            finally { Interlocked.Exchange(ref state.Syncing, 0); }
        }

        private static void OnProfileChanged(ShopContext shop)
        {
            if (shop != null && KnowledgeCloudSyncService.IsEnabledForShop(shop)) QueueSync(shop);
        }

        private static void QueueAll()
        {
            IList<ShopProfile> profiles;
            try { profiles = Profiles.GetAll(); }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("店铺规则云同步读取店铺列表失败：" + ex.Message, 10);
                return;
            }
            foreach (var profile in profiles ?? new List<ShopProfile>())
            {
                if (profile == null) continue;
                try
                {
                    var shop = profile.ToContext();
                    if (KnowledgeCloudSyncService.IsEnabledForShop(shop)) QueueSync(shop);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("店铺规则云同步跳过异常店铺并继续：" + ex.Message, 10);
                }
            }
        }

        private static void QueueSync(ShopContext shop)
        {
            if (shop == null || !KnowledgeCloudSyncService.IsEnabledForShop(shop)
                || !ShopTokenBindingService.ShouldAttemptSync(shop)) return;
            var state = GetState(shop);
            if (Interlocked.Exchange(ref state.Syncing, 1) != 0) return;
            Task.Run(async () =>
            {
                try { await SyncOnceAsync(shop); }
                catch (Exception ex)
                {
                    using (ShopSettingsScope.Enter(shop))
                        Log.ErrorWithMaxCount(
                            "本店店铺规则云同步失败：" + Safe(ex.Message, 300),
                            20);
                }
                finally { Interlocked.Exchange(ref state.Syncing, 0); }
            });
        }

        private static async Task SyncOnceAsync(ShopContext shop)
        {
            using (ShopSettingsScope.Enter(shop))
            {
                var connection = new ShopControlPlaneConnectionStore(shop, Paths);
                var serverUrl = connection.GetServerUrl();
                string token;
                string tokenError;
                if (!connection.TryGetToken(out token, out tokenError)
                    || string.IsNullOrWhiteSpace(serverUrl)
                    || string.IsNullOrWhiteSpace(token))
                {
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(tokenError)
                            ? "请先在“店铺绑定”中绑定本店 Bot 令牌。"
                            : tokenError);
                }

                var localProfile = StorePromptProfileService.BuildCloudPayload(
                    StorePromptProfileService.GetProfile());
                var localHash = Hash(localProfile);
                var lastHash = PersistentParams.GetParam2Key(LastHashKey, Scope, string.Empty);
                int revision;
                if (!int.TryParse(PersistentParams.GetParam2Key(RevisionKey, Scope, "0"), out revision)) revision = 0;

                var payload = new JObject
                {
                    ["enabled"] = true,
                    ["revision"] = revision,
                    ["content_hash"] = localHash
                };
                if (revision == 0 || !string.Equals(localHash, lastHash, StringComparison.OrdinalIgnoreCase))
                    payload["profile"] = localProfile;

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
                using (var http = new HttpClient(handler))
                {
                    http.Timeout = TimeSpan.FromSeconds(30);
                    using (var response = await ShopRuntimeCredentialAuthority.SendWithUnauthorizedRecoveryAsync(
                        shop,
                        token,
                        async requestToken =>
                        {
                            using (var request = new HttpRequestMessage(
                                HttpMethod.Post,
                                serverUrl.TrimEnd('/') + "/api/runtime/v1/bot-web/store-rule-sync"))
                            {
                                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
                                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                                request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-store-rule-cloud/1.0");
                                request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey);
                                request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                                return await http.SendAsync(request).ConfigureAwait(false);
                            }
                        }))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body, 240));

                        var root = JObject.Parse(body);
                        var cloudRevision = root.Value<int?>("revision") ?? revision;
                        var cloudHash = Convert.ToString(root["content_hash"] ?? string.Empty);
                        var cloudProfile = root["profile"] as JObject;
                        if (cloudProfile != null
                            && !string.IsNullOrWhiteSpace(cloudHash)
                            && !string.Equals(cloudHash, localHash, StringComparison.OrdinalIgnoreCase))
                        {
                            var backup = Backup(shop, localProfile);
                            StorePromptProfileService.ApplyCloudPayload(cloudProfile);
                            PersistentParams.TrySaveParam2Key(LastHashKey, Scope, cloudHash);
                            PersistentParams.TrySaveParam2Key(RevisionKey, Scope, cloudRevision.ToString());
                            Log.Info("本店店铺规则云同步已应用云端版本: shop=" + shop.ShopKey
                                + ", revision=" + cloudRevision
                                + ", backup=" + Path.GetFileName(backup));
                            return;
                        }

                        PersistentParams.TrySaveParam2Key(
                            LastHashKey,
                            Scope,
                            string.IsNullOrWhiteSpace(cloudHash) ? localHash : cloudHash);
                        PersistentParams.TrySaveParam2Key(RevisionKey, Scope, cloudRevision.ToString());
                        Log.Info("本店店铺规则云同步正常: shop=" + shop.ShopKey
                            + ", revision=" + cloudRevision);
                    }
                }
            }
        }

        private static string Backup(ShopContext shop, JObject profile)
        {
            var directory = Paths.GetBackupRoot(shop);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                "store-rule-cloud-before-apply-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".json");
            File.WriteAllText(path, (profile ?? new JObject()).ToString(Formatting.Indented), new UTF8Encoding(false));
            return path;
        }

        private static ShopSyncState GetState(ShopContext shop)
        {
            return States.GetOrAdd(shop.ShopKey, _ => new ShopSyncState());
        }

        private static string Hash(JObject value)
        {
            var json = (value ?? new JObject()).ToString(Formatting.None);
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
