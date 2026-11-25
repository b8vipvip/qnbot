using Bot.ChromeNs;
using Bot.ShopScope;
using BotLib;
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

namespace Bot
{
    public partial class App
    {
        private readonly object _visualImageKnowledgeCloudSyncBootstrap =
            Knowledge.VisualImageKnowledgeCloudSyncService.InitializeForApp();
    }
}

namespace Bot.Knowledge
{
    /// <summary>
    /// Mirrors the shop-scoped Windows image knowledge library to the Bot Web cloud.
    /// Cloud revisions carry metadata and image bytes are transferred separately with SHA-256 checks.
    /// </summary>
    internal static class VisualImageKnowledgeCloudSyncService
    {
        private const string SettingsScope = "image-knowledge-cloud";
        private const string RevisionKey = "ImageKnowledgeCloudRevision";
        private const string FingerprintKey = "ImageKnowledgeCloudFingerprint";

        private sealed class State
        {
            public int Revision;
            public string Fingerprint = string.Empty;
            public int Syncing;
        }

        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);
        private static readonly ConcurrentDictionary<string, State> States =
            new ConcurrentDictionary<string, State>(StringComparer.Ordinal);
        private static Timer _timer;
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                _timer = new Timer(_ => QueueAll(), null, 12000, 15000);
                Log.Info("图片知识云同步已启动：Windows 与 Web 共用店铺隔离的图片知识记录和原图。");
            }
            return new object();
        }

        private static void QueueAll()
        {
            IList<ShopProfile> profiles;
            try { profiles = Profiles.GetAll(); }
            catch { return; }
            foreach (var profile in profiles)
            {
                if (profile != null) Queue(profile.ToContext());
            }
        }

        private static void Queue(ShopContext shop)
        {
            if (shop == null || !KnowledgeCloudSyncService.IsEnabledForShop(shop)
                || !ShopTokenBindingService.ShouldAttemptSync(shop)) return;
            var state = States.GetOrAdd(shop.ShopKey, _ => LoadState(shop));
            if (Interlocked.Exchange(ref state.Syncing, 1) != 0) return;
            Task.Run(async () =>
            {
                try
                {
                    await SyncOnceAsync(shop, state);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("图片知识云同步失败：" + Safe(ex.Message, 260), 20);
                }
                finally
                {
                    Interlocked.Exchange(ref state.Syncing, 0);
                }
            });
        }

        private static async Task SyncOnceAsync(ShopContext shop, State state)
        {
            using (ShopSettingsScope.Enter(shop))
            {
                var connection = new ShopControlPlaneConnectionStore(shop, Paths);
                var serverUrl = connection.GetServerUrl();
                string token;
                string tokenError;
                if (!connection.TryGetToken(out token, out tokenError)
                    || string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token))
                    return;

                var local = VisualImageKnowledgeService.LoadAllForCloudSync(shop.ShopKey, shop.DisplayName);
                var localFingerprint = Fingerprint(local);
                var payload = new JObject { ["revision"] = state.Revision };
                if (state.Revision == 0
                    || !string.Equals(localFingerprint, state.Fingerprint, StringComparison.Ordinal))
                    payload["records"] = new JArray(local.Select(ToCloudRecord));

                var responseBody = await SendJsonAsync(
                    shop, serverUrl, token, "/api/runtime/v1/bot-web/image-knowledge-sync", payload);
                var root = JObject.Parse(responseBody);
                var cloudRevision = root.Value<int?>("revision") ?? state.Revision;
                var cloudRecords = (root["records"] as JArray ?? new JArray())
                    .OfType<JObject>().ToList();

                if (cloudRevision < state.Revision)
                {
                    // A shop rebind can reset the server's token-scoped revision namespace.
                    // Re-negotiate from zero without deleting the shop's local image knowledge.
                    PersistState(shop, state, 0, string.Empty);
                    Log.Info("图片知识检测到云端 revision 重置，保留本地记录并重新协商: shopRef="
                        + MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey));
                    return;
                }

                if (cloudRevision > state.Revision)
                {
                    var latestLocal = VisualImageKnowledgeService.LoadAllForCloudSync(shop.ShopKey, shop.DisplayName);
                    var latestFingerprint = Fingerprint(latestLocal);
                    if (!string.Equals(latestFingerprint, localFingerprint, StringComparison.Ordinal))
                    {
                        Log.Info("图片知识云端更新期间本地记录发生变化，本轮不覆盖，下一轮重试: shopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey));
                        return;
                    }

                    if (latestLocal.Count > 0)
                        BackupLocalSnapshot(shop, latestLocal, cloudRevision);
                    await ApplyCloudRecordsAsync(shop, latestLocal, cloudRecords, serverUrl, token);
                }

                // Refresh the credential after unauthorized recovery before binary transfers.
                if (connection.TryGetToken(out token, out tokenError) && !string.IsNullOrWhiteSpace(token))
                    await UploadMissingImagesAsync(shop, serverUrl, token, root["missing_uploads"] as JArray);

                var cloudFingerprint = Fingerprint(cloudRecords.Select(FromCloudRecord).Where(x => x != null).ToList());
                PersistState(shop, state, cloudRevision, cloudFingerprint);
            }
        }

        private static async Task ApplyCloudRecordsAsync(
            ShopContext shop,
            IList<VisualImageKnowledgeEntry> local,
            IList<JObject> cloudRecords,
            string serverUrl,
            string token)
        {
            var byId = (local ?? new List<VisualImageKnowledgeEntry>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.EntityId))
                .GroupBy(x => x.EntityId, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

            foreach (var record in cloudRecords ?? new List<JObject>())
            {
                var id = Convert.ToString(record["Id"] ?? string.Empty);
                if (string.IsNullOrWhiteSpace(id)) continue;
                VisualImageKnowledgeEntry localEntry;
                byId.TryGetValue(id, out localEntry);
                var cloud = FromCloudRecord(record);
                if (cloud == null) continue;
                cloud.EntityId = id;
                var cloudFingerprint = FingerprintRecord(cloud);
                if (localEntry != null)
                {
                    var localFingerprint = FingerprintRecord(localEntry);
                    var baseline = localEntry.CloudContentFingerprint ?? string.Empty;
                    var localDirty = baseline.Length == 0
                        || !string.Equals(localFingerprint, baseline, StringComparison.Ordinal);
                    var cloudDirty = baseline.Length == 0
                        || !string.Equals(cloudFingerprint, baseline, StringComparison.Ordinal);

                    if (string.Equals(localFingerprint, cloudFingerprint, StringComparison.Ordinal))
                    {
                        VisualImageKnowledgeService.SetCloudContentFingerprint(
                            shop.ShopKey, shop.DisplayName, id, cloudFingerprint);
                        continue;
                    }

                    if (localDirty)
                    {
                        // Preserve unsynced Windows edits on conflicts. The new cloud
                        // baseline is stored so the next poll can safely push the local winner.
                        if (cloudDirty)
                            Log.ErrorWithMaxCount("图片知识发生 Windows/Web 并发修改，保留本地版本并在下一轮同步：" + id, 10);
                        VisualImageKnowledgeService.SetCloudContentFingerprint(
                            shop.ShopKey, shop.DisplayName, id, cloudFingerprint);
                        continue;
                    }

                    if (!cloudDirty) continue;
                }

                var deleted = record.Value<bool?>("Deleted").GetValueOrDefault();
                if (deleted)
                {
                    if (localEntry != null && !localEntry.Deleted)
                        VisualImageKnowledgeService.Delete(shop.ShopKey, shop.DisplayName, id);
                    VisualImageKnowledgeService.SetCloudContentFingerprint(
                        shop.ShopKey, shop.DisplayName, id, cloudFingerprint);
                    continue;
                }

                var imageAvailable = record.Value<bool?>("ImageAvailable").GetValueOrDefault();
                string sourcePath = localEntry != null
                    && string.Equals(localEntry.ImageSha256, cloud.ImageSha256, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(localEntry.ImagePath)
                    && File.Exists(localEntry.ImagePath)
                    ? localEntry.ImagePath
                    : (imageAvailable
                        ? await DownloadImageAsync(shop, serverUrl, token, id, cloud.ImageSha256, cloud.ImageName)
                        : string.Empty);

                var temporary = !string.Equals(sourcePath, localEntry == null ? string.Empty : localEntry.ImagePath,
                    StringComparison.OrdinalIgnoreCase);
                try
                {
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) continue;
                    VisualImageKnowledgeService.Save(shop.ShopKey, shop.DisplayName, cloud, sourcePath);
                    VisualImageKnowledgeService.SetCloudContentFingerprint(
                        shop.ShopKey, shop.DisplayName, id, cloudFingerprint);
                }
                finally
                {
                    if (temporary)
                    {
                        try { if (File.Exists(sourcePath)) File.Delete(sourcePath); } catch { }
                    }
                }
            }
        }

        private static async Task UploadMissingImagesAsync(
            ShopContext shop, string serverUrl, string token, JArray missingUploads)
        {
            if (missingUploads == null || missingUploads.Count == 0) return;
            var local = VisualImageKnowledgeService.LoadAllForCloudSync(shop.ShopKey, shop.DisplayName)
                .Where(x => x != null && !x.Deleted)
                .ToDictionary(x => x.EntityId, x => x, StringComparer.Ordinal);
            foreach (var missing in missingUploads.OfType<JObject>().Take(50))
            {
                var id = Convert.ToString(missing["id"] ?? string.Empty);
                VisualImageKnowledgeEntry item;
                if (string.IsNullOrWhiteSpace(id) || !local.TryGetValue(id, out item)
                    || string.IsNullOrWhiteSpace(item.ImagePath) || !File.Exists(item.ImagePath))
                    continue;
                var bytes = File.ReadAllBytes(item.ImagePath);
                if (bytes.Length <= 0 || bytes.Length > VisualImageKnowledgeService.MaxImageBytes) continue;
                var sha = HashBytes(bytes);
                if (!string.Equals(sha, item.ImageSha256, StringComparison.OrdinalIgnoreCase)) continue;

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
                using (var http = new HttpClient(handler))
                {
                    http.Timeout = TimeSpan.FromSeconds(45);
                    using (var response = await ShopRuntimeCredentialAuthority.SendWithUnauthorizedRecoveryAsync(
                        shop, token, async requestToken =>
                        {
                            using (var request = new HttpRequestMessage(
                                HttpMethod.Post,
                                serverUrl.TrimEnd('/') + "/api/runtime/v1/bot-web/image-knowledge/"
                                    + Uri.EscapeDataString(id) + "/image"))
                            using (var content = new ByteArrayContent(bytes))
                            {
                                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
                                request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey);
                                request.Headers.TryAddWithoutValidation("X-Image-Sha256", sha);
                                content.Headers.ContentType = new MediaTypeHeaderValue(MimeForPath(item.ImagePath));
                                request.Content = content;
                                return await http.SendAsync(request).ConfigureAwait(false);
                            }
                        }))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            var body = await response.Content.ReadAsStringAsync();
                            Log.Info("图片知识原图上传失败，保留本地记录并等待重试: HTTP "
                                + (int)response.StatusCode + " " + Safe(body, 180));
                        }
                    }
                }
            }
        }

        private static async Task<string> DownloadImageAsync(
            ShopContext shop, string serverUrl, string token, string id, string sha, string imageName)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
            using (var http = new HttpClient(handler))
            {
                http.Timeout = TimeSpan.FromSeconds(45);
                using (var response = await ShopRuntimeCredentialAuthority.SendWithUnauthorizedRecoveryAsync(
                    shop, token, async requestToken =>
                    {
                        using (var request = new HttpRequestMessage(
                            HttpMethod.Get,
                            serverUrl.TrimEnd('/') + "/api/runtime/v1/bot-web/image-knowledge/"
                                + Uri.EscapeDataString(id) + "/image"))
                        {
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
                            request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey);
                            return await http.SendAsync(request).ConfigureAwait(false);
                        }
                    }))
                {
                    if (!response.IsSuccessStatusCode) return string.Empty;
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    if (bytes.Length <= 0 || bytes.Length > VisualImageKnowledgeService.MaxImageBytes
                        || !string.Equals(HashBytes(bytes), sha, StringComparison.OrdinalIgnoreCase))
                        return string.Empty;
                    var extension = ExtensionForMime(response.Content.Headers.ContentType == null
                        ? string.Empty : response.Content.Headers.ContentType.MediaType);
                    if (extension.Length == 0) extension = Path.GetExtension(imageName ?? string.Empty);
                    if (extension.Length == 0) extension = ".png";
                    var path = Path.Combine(Path.GetTempPath(),
                        "qianniu-image-knowledge-" + Guid.NewGuid().ToString("N") + extension);
                    File.WriteAllBytes(path, bytes);
                    return path;
                }
            }
        }

        private static async Task<string> SendJsonAsync(
            ShopContext shop, string serverUrl, string token, string path, JObject payload)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
            using (var http = new HttpClient(handler))
            {
                http.Timeout = TimeSpan.FromSeconds(45);
                using (var response = await ShopRuntimeCredentialAuthority.SendWithUnauthorizedRecoveryAsync(
                    shop, token, async requestToken =>
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Post, serverUrl.TrimEnd('/') + path))
                        {
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
                            request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey);
                            request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-image-knowledge-cloud/1.0");
                            request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                            return await http.SendAsync(request).ConfigureAwait(false);
                        }
                    }))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("图片知识云同步 HTTP " + (int)response.StatusCode + " " + Safe(body, 220));
                    return body;
                }
            }
        }

        private static State LoadState(ShopContext shop)
        {
            using (ShopSettingsScope.Enter(shop))
            {
                var store = new ShopScopedSettingsStore(shop, Paths);
                string revisionText;
                int revision;
                if (!store.TryGetString(RevisionKey, out revisionText) || !int.TryParse(revisionText, out revision))
                    revision = 0;
                string fingerprint;
                store.TryGetString(FingerprintKey, out fingerprint);
                return new State { Revision = Math.Max(0, revision), Fingerprint = fingerprint ?? string.Empty };
            }
        }

        private static void PersistState(ShopContext shop, State state, int revision, string fingerprint)
        {
            using (ShopSettingsScope.Enter(shop))
            {
                var store = new ShopScopedSettingsStore(shop, Paths);
                store.MergeValues(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { RevisionKey, Math.Max(0, revision).ToString() },
                    { FingerprintKey, fingerprint ?? string.Empty }
                }, true);
                string savedRevision;
                string savedFingerprint;
                if (!store.TryGetString(RevisionKey, out savedRevision)
                    || !store.TryGetString(FingerprintKey, out savedFingerprint)
                    || savedRevision != Math.Max(0, revision).ToString()
                    || savedFingerprint != (fingerprint ?? string.Empty))
                    throw new InvalidOperationException("图片知识云同步检查点写入失败");
            }
            state.Revision = Math.Max(0, revision);
            state.Fingerprint = fingerprint ?? string.Empty;
        }

        private static JObject ToCloudRecord(VisualImageKnowledgeEntry item)
        {
            return new JObject
            {
                ["Id"] = item.EntityId ?? string.Empty,
                ["ImageName"] = item.ImageName ?? string.Empty,
                ["ImageSha256"] = item.ImageSha256 ?? string.Empty,
                ["ImageMimeType"] = MimeForPath(item.ImagePath),
                ["OcrText"] = item.OcrText ?? string.Empty,
                ["Description"] = item.Description ?? string.Empty,
                ["Enabled"] = item.Enabled,
                ["Deleted"] = item.Deleted,
                ["CreatedAt"] = item.CreatedAtTicks.ToString(),
                ["UpdatedAt"] = item.UpdatedAtTicks.ToString()
            };
        }

        private static VisualImageKnowledgeEntry FromCloudRecord(JObject item)
        {
            if (item == null) return null;
            return new VisualImageKnowledgeEntry
            {
                EntityId = Convert.ToString(item["Id"] ?? string.Empty),
                ImageName = Convert.ToString(item["ImageName"] ?? string.Empty),
                ImageSha256 = Convert.ToString(item["ImageSha256"] ?? string.Empty),
                OcrText = Convert.ToString(item["OcrText"] ?? string.Empty),
                Description = Convert.ToString(item["Description"] ?? string.Empty),
                Enabled = item.Value<bool?>("Enabled").GetValueOrDefault(true),
                Deleted = item.Value<bool?>("Deleted").GetValueOrDefault(false)
            };
        }

        private static string Fingerprint(IEnumerable<VisualImageKnowledgeEntry> entries)
        {
            var canonical = (entries ?? Enumerable.Empty<VisualImageKnowledgeEntry>())
                .Where(x => x != null)
                .OrderBy(x => x.EntityId ?? string.Empty, StringComparer.Ordinal)
                .Select(x => string.Join("|", new[]
                {
                    x.EntityId ?? string.Empty,
                    x.ImageName ?? string.Empty,
                    x.ImageSha256 ?? string.Empty,
                    x.OcrText ?? string.Empty,
                    x.Description ?? string.Empty,
                    x.Enabled ? "1" : "0",
                    x.Deleted ? "1" : "0"
                }));
            return HashBytes(Encoding.UTF8.GetBytes(string.Join("\n", canonical)));
        }

        private static string FingerprintRecord(VisualImageKnowledgeEntry item)
        {
            if (item == null) return string.Empty;
            var canonical = string.Join("|", new[]
            {
                item.EntityId ?? string.Empty,
                item.ImageName ?? string.Empty,
                item.ImageSha256 ?? string.Empty,
                item.OcrText ?? string.Empty,
                item.Description ?? string.Empty,
                item.Enabled ? "1" : "0",
                item.Deleted ? "1" : "0"
            });
            return HashBytes(Encoding.UTF8.GetBytes(canonical));
        }

        private static bool SameContent(VisualImageKnowledgeEntry local, VisualImageKnowledgeEntry cloud)
        {
            return local != null && cloud != null
                && string.Equals(local.EntityId, cloud.EntityId, StringComparison.Ordinal)
                && string.Equals(local.ImageSha256, cloud.ImageSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(local.ImageName ?? string.Empty, cloud.ImageName ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(local.OcrText ?? string.Empty, cloud.OcrText ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(local.Description ?? string.Empty, cloud.Description ?? string.Empty, StringComparison.Ordinal)
                && local.Enabled == cloud.Enabled && !local.Deleted && !cloud.Deleted;
        }

        private static void BackupLocalSnapshot(ShopContext shop, IList<VisualImageKnowledgeEntry> entries, int cloudRevision)
        {
            try
            {
                var root = Paths.GetKnowledgeRoot(shop);
                var directory = Path.Combine(root, "visual-images", "cloud-sync-backups");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
                    + "-cloud-r" + cloudRevision + ".json");
                File.WriteAllText(path, JsonConvert.SerializeObject(entries, Formatting.Indented), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("图片知识云同步本地备份失败：" + Safe(ex.Message, 200), 5);
            }
        }

        private static string MimeForPath(string path)
        {
            var extension = (Path.GetExtension(path ?? string.Empty) ?? string.Empty).ToLowerInvariant();
            if (extension == ".jpg" || extension == ".jpeg") return "image/jpeg";
            if (extension == ".png") return "image/png";
            if (extension == ".webp") return "image/webp";
            if (extension == ".gif") return "image/gif";
            return "image/png";
        }

        private static string ExtensionForMime(string mime)
        {
            switch ((mime ?? string.Empty).ToLowerInvariant())
            {
                case "image/jpeg": return ".jpg";
                case "image/png": return ".png";
                case "image/webp": return ".webp";
                case "image/gif": return ".gif";
                default: return string.Empty;
            }
        }

        private static string HashBytes(byte[] value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(value ?? new byte[0]))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
