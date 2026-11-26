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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bot
{
    public partial class App
    {
        private readonly object _knowledgeCloudSyncBootstrap =
            Knowledge.KnowledgeCloudSyncService.InitializeForApp();
    }
}

namespace Bot.Knowledge
{
    internal static class KnowledgeCloudSyncService
    {
        private const string Scope = "shop-cloud";
        private const string EnabledKey = "KnowledgeCloudSyncEnabled";
        private const string RevisionKey = "KnowledgeCloudRevision";
        private const string LastHashKey = "KnowledgeCloudLastHash";

        private sealed class ShopSyncState
        {
            public readonly object UiSync = new object();
            public readonly List<WeakReference> StatusBlocks = new List<WeakReference>();
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
                EventManager.RegisterClassHandler(
                    typeof(KnowledgeManagerControl),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnKnowledgeManagerLoaded),
                    true);
                KnowledgeLearningService.KnowledgeBaseChanged += OnKnowledgeBaseChanged;
                _timer = new Timer(_ => QueueAll(), null, 5000, 8000);
                Log.Info("知识库云同步服务已启动：每个 ShopKey 使用独立令牌、revision、hash 和备份目录。" );
            }
            return new object();
        }

        internal static bool IsEnabled
        {
            get
            {
                var shop = ShopSettingsScope.Current;
                return shop != null && IsEnabledForShop(shop);
            }
        }

        internal static void SetEnabled(bool enabled)
        {
            var shop = ShopSettingsScope.Current;
            if (shop == null)
            {
                Log.Error("无法修改知识库云同步：当前没有店铺作用域。" );
                return;
            }
            SetEnabledForShop(shop, enabled, true);
        }

        internal static bool IsEnabledForShop(ShopContext shop)
        {
            if (shop == null) return false;
            using (ShopSettingsScope.Enter(shop))
            {
                return string.Equals(
                    PersistentParams.GetParam2Key(EnabledKey, Scope, "false"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static void SetEnabledForShop(ShopContext shop, bool enabled, bool queueSync)
        {
            if (shop == null) return;
            using (ShopSettingsScope.Enter(shop))
            {
                PersistentParams.TrySaveParam2Key(EnabledKey, Scope, enabled ? "true" : "false");
            }
            UpdateStatus(shop,
                enabled ? "本店云同步已启用，正在连接服务端..." : "本店云同步已关闭",
                enabled ? Brushes.SteelBlue : Brushes.Gray);
            if (enabled && queueSync) QueueSync(shop);
        }

        /// <summary>
        /// Explicit user-triggered sync used by the shop binding page. This waits for a
        /// currently-running background pass instead of starting two writers for one ShopKey.
        /// </summary>
        internal static async Task SyncNowAsync(ShopContext shop)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            SetEnabledForShop(shop, true, false);
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
            if (!acquired) throw new InvalidOperationException("本店知识库正在执行其他同步，请稍后重试。" );

            try
            {
                UpdateStatus(shop, "正在立即同步本店云端知识库...", Brushes.SteelBlue);
                await SyncOnceAsync(shop);
            }
            catch (Exception ex)
            {
                UpdateStatus(shop, "本店云同步失败：" + Safe(ex.Message, 90), Brushes.IndianRed);
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref state.Syncing, 0);
            }
        }

        private static void OnKnowledgeManagerLoaded(object sender, RoutedEventArgs e)
        {
            var control = sender as KnowledgeManagerControl;
            if (control == null) return;
            var window = Window.GetWindow(control);
            var shop = ShopSettingsScope.Current ?? ShopScopedUiBridge.Get(window);
            if (shop == null) return;
            var marker = "knowledge-cloud-sync-attached:" + shop.ShopKey;
            if (string.Equals(control.Tag as string, marker, StringComparison.Ordinal)) return;
            var root = control.Content as DockPanel;
            if (root == null) return;
            var top = root.Children.OfType<WrapPanel>().FirstOrDefault();
            if (top == null) return;

            var enabled = IsEnabledForShop(shop);
            var check = new CheckBox
            {
                Content = "启用本店知识库云同步",
                IsChecked = enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 3, 8, 6),
                ToolTip = "仅同步当前 ShopKey 的知识库；写入云端版本前自动备份本店知识。"
            };
            var status = new TextBlock
            {
                Text = enabled ? "等待本店云端同步" : "仅保存在本店本机目录",
                Foreground = enabled ? Brushes.SteelBlue : Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 6)
            };
            check.Click += (s, args) =>
            {
                using (ShopSettingsScope.Enter(shop)) SetEnabled(check.IsChecked == true);
            };
            top.Children.Add(check);
            top.Children.Add(status);
            var state = GetState(shop);
            lock (state.UiSync)
            {
                state.StatusBlocks.Add(new WeakReference(status));
                CleanupStatusBlocks(state);
            }
            control.Tag = marker;
            if (enabled) QueueSync(shop);
        }

        private static void OnKnowledgeBaseChanged(object sender, EventArgs e)
        {
            var shop = ShopSettingsScope.Current;
            if (shop != null && IsEnabledForShop(shop)) QueueSync(shop);
        }

        private static void QueueAll()
        {
            IList<ShopProfile> profiles;
            try { profiles = Profiles.GetAll(); }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("知识库云同步读取店铺列表失败：" + ex.Message, 10);
                return;
            }
            foreach (var profile in profiles ?? new List<ShopProfile>())
            {
                if (profile == null) continue;
                try
                {
                    var shop = profile.ToContext();
                    if (IsEnabledForShop(shop)) QueueSync(shop);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("知识库云同步跳过异常店铺并继续：" + ex.Message, 10);
                }
            }
        }

        private static void QueueSync(ShopContext shop)
        {
            if (shop == null || !IsEnabledForShop(shop)
                || !ShopTokenBindingService.ShouldAttemptSync(shop)) return;
            var state = GetState(shop);
            if (Interlocked.Exchange(ref state.Syncing, 1) != 0) return;
            Task.Run(async () =>
            {
                try { await SyncOnceAsync(shop); }
                catch (Exception ex)
                {
                    UpdateStatus(shop, "本店云同步失败：" + Safe(ex.Message, 90), Brushes.IndianRed);
                    using (ShopSettingsScope.Enter(shop))
                        Log.ErrorWithMaxCount("本店知识库云同步失败：" + Safe(ex.Message, 300), 20);
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
                    UpdateStatus(shop,
                        string.IsNullOrWhiteSpace(tokenError)
                            ? "等待配置统一 API 服务和本店客户端令牌"
                            : Safe(tokenError, 90),
                        Brushes.DarkOrange);
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(tokenError)
                            ? "请先在“店铺绑定”中填写本店服务端地址并绑定 Bot 令牌。"
                            : tokenError);
                }

                var local = BotFeatureStore.GetKnowledgeBase() ?? new List<KnowledgeBaseEntry>();
                var localJson = JsonConvert.SerializeObject(local, Formatting.None);
                // The Python control plane hashes JSON with sort_keys=True. Canonicalize every
                // object key here as well so identical knowledge does not look changed every
                // eight seconds and repeatedly rewrite 700+ items/backups after a restart.
                var localHash = Hash(localJson);
                var lastHash = PersistentParams.GetParam2Key(LastHashKey, Scope, string.Empty);
                int revision;
                if (!int.TryParse(PersistentParams.GetParam2Key(RevisionKey, Scope, "0"), out revision)) revision = 0;

                var payload = new JObject
                {
                    ["enabled"] = true,
                    ["shop_key"] = shop.ShopKey,
                    ["revision"] = revision,
                    ["content_hash"] = localHash
                };
                if (revision == 0 || !string.Equals(localHash, lastHash, StringComparison.OrdinalIgnoreCase))
                    payload["items"] = JArray.Parse(localJson);

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
                                serverUrl.TrimEnd('/') + "/api/runtime/v1/bot-web/knowledge-sync"))
                            {
                                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
                                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                                request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-knowledge-cloud/2.0");
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
                        var items = root["items"] as JArray;
                        if (items != null && !string.IsNullOrWhiteSpace(cloudHash)
                            && !string.Equals(cloudHash, localHash, StringComparison.OrdinalIgnoreCase))
                        {
                            var cloudJson = items.ToString(Formatting.None);
                            var canonicalCloudHash = Hash(cloudJson);
                            if (string.Equals(canonicalCloudHash, localHash, StringComparison.OrdinalIgnoreCase))
                            {
                                // Compatibility bridge for the old non-canonical Windows hash:
                                // data is already identical, so adopt the server hash/revision
                                // without rewriting hundreds of records or creating another backup.
                                PersistentParams.TrySaveParam2Key(LastHashKey, Scope, cloudHash);
                                PersistentParams.TrySaveParam2Key(RevisionKey, Scope, cloudRevision.ToString());
                                UpdateStatus(shop, "本店云同步正常 · " + local.Count + " 条 · v" + cloudRevision, Brushes.SeaGreen);
                                Log.Info("本店知识库云同步哈希已收敛，无需重复覆盖本地: shop="
                                    + shop.ShopKey + ", revision=" + cloudRevision + ", count=" + local.Count);
                                return;
                            }

                            var cloud = items.ToObject<List<KnowledgeBaseEntry>>() ?? new List<KnowledgeBaseEntry>();
                            var backup = Backup(shop, localJson);
                            BotFeatureStore.SaveKnowledgeBase(cloud);
                            PersistentParams.TrySaveParam2Key(LastHashKey, Scope, cloudHash);
                            PersistentParams.TrySaveParam2Key(RevisionKey, Scope, cloudRevision.ToString());
                            UpdateStatus(shop, "已应用本店云端知识：" + cloud.Count + " 条", Brushes.SeaGreen);
                            Log.Info("本店知识库云同步已应用云端版本: shop=" + shop.ShopKey
                                + ", revision=" + cloudRevision + ", count=" + cloud.Count
                                + ", backup=" + Path.GetFileName(backup));
                            return;
                        }

                        PersistentParams.TrySaveParam2Key(LastHashKey, Scope,
                            string.IsNullOrWhiteSpace(cloudHash) ? localHash : cloudHash);
                        PersistentParams.TrySaveParam2Key(RevisionKey, Scope, cloudRevision.ToString());
                        UpdateStatus(shop, "本店云同步正常 · " + local.Count + " 条 · v" + cloudRevision, Brushes.SeaGreen);
                    }
                }
            }
        }

        private static string Backup(ShopContext shop, string json)
        {
            var directory = Paths.GetBackupRoot(shop);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                "knowledge-cloud-before-apply-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".json");
            File.WriteAllText(path, json ?? "[]", new UTF8Encoding(false));
            return path;
        }

        private static ShopSyncState GetState(ShopContext shop)
        {
            return States.GetOrAdd(shop.ShopKey, _ => new ShopSyncState());
        }

        private static string Hash(string value)
        {
            var canonical = CanonicalizeJson(value);
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string CanonicalizeJson(string value)
        {
            try
            {
                return CanonicalizeToken(JToken.Parse(value ?? "null")).ToString(Formatting.None);
            }
            catch
            {
                return value ?? string.Empty;
            }
        }

        private static JToken CanonicalizeToken(JToken token)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                var sorted = new JObject();
                foreach (var property in obj.Properties().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    sorted.Add(property.Name, CanonicalizeToken(property.Value));
                }
                return sorted;
            }

            var array = token as JArray;
            if (array != null)
            {
                var sortedItems = new JArray();
                foreach (var item in array)
                    sortedItems.Add(CanonicalizeToken(item));
                return sortedItems;
            }

            return token == null ? JValue.CreateNull() : token.DeepClone();
        }

        private static void UpdateStatus(ShopContext shop, string text, Brush brush)
        {
            var state = GetState(shop);
            lock (state.UiSync)
            {
                CleanupStatusBlocks(state);
                foreach (var weak in state.StatusBlocks.ToArray())
                {
                    var block = weak.Target as TextBlock;
                    if (block == null) continue;
                    try
                    {
                        block.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            block.Text = text;
                            block.Foreground = brush;
                        }));
                    }
                    catch { }
                }
            }
        }

        private static void CleanupStatusBlocks(ShopSyncState state)
        {
            state.StatusBlocks.RemoveAll(x => x == null || !x.IsAlive || !(x.Target is TextBlock));
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
