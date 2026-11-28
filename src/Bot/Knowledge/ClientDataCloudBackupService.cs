using Bot.ShopScope;
using BotLib;
using BotLib.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bot.Knowledge
{
    internal static class ClientDataCloudBackupService
    {
        private const string Magic = "QABK2";
        private static readonly string LegacyBackupSchema = "qianniu" + "-ai-bot.shop-data-backup";
        private const long MaxDataBytes = 48L * 1024 * 1024;
        private const long MaxSingleFileBytes = 32L * 1024 * 1024;

        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        internal static async Task<JObject> GetStatusAsync(ShopContext shop)
        {
            string serverUrl;
            string token;
            GetConnection(shop, out serverUrl, out token);
            using (var http = CreateHttp(token, shop))
            using (var response = await http.GetAsync(
                serverUrl.TrimEnd('/') + "/api/runtime/v1/client-data-backup/status"))
            {
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body, 300));
                return JObject.Parse(body);
            }
        }

        internal static async Task<JObject> UploadAsync(ShopContext shop, Action<string> status)
        {
            string serverUrl;
            string token;
            GetConnection(shop, out serverUrl, out token);
            status("正在整理本店业务数据...");
            BackupBuildResult build = null;
            try
            {
                build = BuildEncryptedBackup(shop, token);
                status("正在上传本店加密备份 " + FormatBytes(build.EncryptedBytes.Length) + "...");
                using (var http = CreateHttp(token, shop))
                using (var request = new HttpRequestMessage(
                    HttpMethod.Put,
                    serverUrl.TrimEnd('/') + "/api/runtime/v1/client-data-backup"))
                {
                    request.Content = new ByteArrayContent(build.EncryptedBytes);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey);
                    request.Headers.TryAddWithoutValidation("X-Backup-Sha256", Sha256(build.EncryptedBytes));
                    request.Headers.TryAddWithoutValidation("X-Backup-Created-At", build.CreatedAt);
                    request.Headers.TryAddWithoutValidation("X-Backup-Device", Safe(Environment.MachineName, 100));
                    request.Headers.TryAddWithoutValidation("X-Backup-App-Version", AppVersion());
                    request.Headers.TryAddWithoutValidation("X-Backup-File-Count", build.FileCount.ToString());
                    request.Headers.TryAddWithoutValidation("X-Backup-Data-Bytes", build.DataBytes.ToString());
                    using (var response = await http.SendAsync(request))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body, 300));
                        using (ShopSettingsScope.Enter(shop))
                            Log.Info("本店业务数据云备份已上传: shop=" + shop.ShopKey
                                + ", files=" + build.FileCount + ", dataBytes=" + build.DataBytes
                                + ", encryptedBytes=" + build.EncryptedBytes.Length);
                        return JObject.Parse(body);
                    }
                }
            }
            finally
            {
                if (build != null) build.Dispose();
            }
        }

        internal static async Task<RestoreResult> DownloadAndRestoreAsync(ShopContext shop, Action<string> status)
        {
            string serverUrl;
            string token;
            GetConnection(shop, out serverUrl, out token);
            status("正在下载本店云端加密备份...");
            byte[] encrypted;
            using (var http = CreateHttp(token, shop))
            using (var response = await http.GetAsync(
                serverUrl.TrimEnd('/') + "/api/runtime/v1/client-data-backup"))
            {
                var body = await response.Content.ReadAsByteArrayAsync();
                if (!response.IsSuccessStatusCode)
                    throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(Encoding.UTF8.GetString(body), 300));
                encrypted = body;
            }

            byte[] zipBytes = null;
            try
            {
                status("正在使用本店 Bot 令牌和 ShopKey 解密并校验...");
                zipBytes = Decrypt(encrypted, token, shop.ShopKey);
                status("正在生成本店回滚备份...");
                var rollback = CreateRollbackBackup(shop, token);
                status("正在恢复本店业务数据...");
                var restored = RestoreZip(shop, zipBytes);
                restored.RollbackPath = rollback;
                using (ShopSettingsScope.Enter(shop))
                    Log.Info("本店云端业务数据已恢复: shop=" + shop.ShopKey
                        + ", settings=" + restored.ParamCount + ", files=" + restored.FileCount
                        + ", rollback=" + rollback);
                return restored;
            }
            finally
            {
                if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
                if (zipBytes != null) Array.Clear(zipBytes, 0, zipBytes.Length);
            }
        }

        internal static string GetDataFolder(ShopContext shop)
        {
            return Paths.GetShopRoot(shop);
        }

        private static BackupBuildResult BuildEncryptedBackup(ShopContext shop, string token)
        {
            var createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            int fileCount;
            long dataBytes;
            var plain = BuildPlainBackup(shop, createdAt, out fileCount, out dataBytes);
            try
            {
                return new BackupBuildResult
                {
                    EncryptedBytes = Encrypt(plain, token, shop.ShopKey),
                    FileCount = fileCount,
                    DataBytes = dataBytes,
                    CreatedAt = createdAt
                };
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        private const string PortableExportSchema = "qnbot.shop-data-export";
        private const int PortableExportVersion = 1;
        private const int MaxPortableFiles = 2000;
        private const long MaxPortableSingleFileBytes = 128L * 1024 * 1024;
        private const long MaxPortableDataBytes = 256L * 1024 * 1024;
        private const long MaxPortableArchiveBytes = 320L * 1024 * 1024;

        internal static PortableDataExportResult ExportPortable(ShopContext shop, string destinationPath)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("请选择全店信息导出位置。", nameof(destinationPath));

            var createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            int fileCount;
            long dataBytes;
            var settings = ExportPortableSettings(shop, true);
            var archive = BuildPlainBackup(shop, createdAt, out fileCount, out dataBytes, true);
            try
            {
                if (archive.Length > MaxPortableArchiveBytes)
                    throw new InvalidDataException("全店信息导出包超过320MB限制，请先清理不需要的本店数据。");
                var skippedCount = ReadPortableSkippedCount(archive);
                var fullPath = Path.GetFullPath(destinationPath);
                var directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrWhiteSpace(directory))
                    throw new InvalidOperationException("导出目录无效。");
                Directory.CreateDirectory(directory);
                using (var output = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    output.Write(archive, 0, archive.Length);

                return new PortableDataExportResult
                {
                    FileCount = fileCount,
                    DataBytes = dataBytes,
                    SettingCount = settings.Count,
                    SkippedCount = skippedCount,
                    ArchiveBytes = archive.Length
                };
            }
            finally { Array.Clear(archive, 0, archive.Length); }
        }

        internal static RestoreResult ImportPortable(ShopContext shop, string sourcePath)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("请选择有效的全店信息备份文件。", sourcePath);

            var info = new FileInfo(sourcePath);
            if (info.Length <= 0 || info.Length > MaxPortableArchiveBytes)
                throw new InvalidDataException("全店信息备份文件为空或超过320MB限制。");

            var archive = File.ReadAllBytes(sourcePath);
            PortableArchivePlan plan = null;
            try
            {
                // Validate every entry and the target ShopKey before touching local data.
                plan = ReadPortableArchive(shop, archive);
                PreserveLocalSensitiveSettings(shop, plan);
                var rollbackPath = CreateProtectedPortableRollback(shop);
                try
                {
                    var result = ApplyPortableArchive(shop, plan);
                    result.RollbackPath = rollbackPath;
                    return result;
                }
                catch (Exception importError)
                {
                    try
                    {
                        RestoreProtectedPortableRollback(shop, rollbackPath);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new AggregateException(
                            "全店信息导入失败，且自动回滚也失败。请保留回滚文件：" + rollbackPath,
                            importError,
                            rollbackError);
                    }
                    throw new InvalidOperationException(
                        "全店信息导入失败，已自动恢复导入前的本地配置：" + importError.Message,
                        importError);
                }
            }
            finally
            {
                Array.Clear(archive, 0, archive.Length);
                if (plan != null)
                    foreach (var file in plan.Files)
                        if (file.Content != null) Array.Clear(file.Content, 0, file.Content.Length);
            }
        }

        internal static string ResetLocalConfiguration(ShopContext shop)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            var rollbackPath = CreateProtectedPortableRollback(shop);
            try
            {
                ClearLocalConfigurationFiles(shop);
                new ShopScopedSettingsStore(shop, Paths)
                    .ReplaceValues(new Dictionary<string, string>(StringComparer.Ordinal));
                return rollbackPath;
            }
            catch (Exception resetError)
            {
                try { RestoreProtectedPortableRollback(shop, rollbackPath); }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "本地配置重置失败，自动回滚也失败。请保留回滚文件：" + rollbackPath,
                        resetError,
                        rollbackError);
                }
                throw new InvalidOperationException(
                    "本地配置重置失败，已恢复原配置：" + resetError.Message,
                    resetError);
            }
        }

        internal static void RestorePortableRollback(ShopContext shop, string rollbackPath)
        {
            RestoreProtectedPortableRollback(shop, rollbackPath);
        }

        private static byte[] BuildPlainBackup(
            ShopContext shop,
            string createdAt,
            out int fileCount,
            out long dataBytes,
            bool portableExport = false,
            bool localRollback = false)
        {
            var largeArchive = portableExport || localRollback;
            var skipped = new List<string>();
            var settings = localRollback
                ? ExportLocalRollbackSettings(shop)
                : ExportPortableSettings(shop, portableExport);
            var maxSingleFileBytes = largeArchive ? MaxPortableSingleFileBytes : MaxSingleFileBytes;
            var maxDataBytes = largeArchive ? MaxPortableDataBytes : MaxDataBytes;
            fileCount = 0;
            dataBytes = 0;
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true, Encoding.UTF8))
                {
                    WriteTextEntry(zip, "settings.json", JsonConvert.SerializeObject(settings, Formatting.Indented));
                    foreach (var source in (largeArchive ? EnumerateShopFilesStrict(shop) : EnumerateShopFiles(shop)))
                    {
                        var relative = RelativeShopPath(shop, source);
                        if (portableExport ? ShouldExcludePortable(relative) : localRollback ? ShouldExcludeLocalRollback(relative) : ShouldExclude(relative)) continue;
                        if (largeArchive && fileCount >= MaxPortableFiles)
                        {
                            skipped.Add(relative + "（全店信息文件数量超过2000）");
                            continue;
                        }
                        long length;
                        try { length = new FileInfo(source).Length; }
                        catch { skipped.Add(relative + "（无法读取大小）"); continue; }
                        if (length > maxSingleFileBytes)
                        {
                            skipped.Add(relative + "（单文件超过" + (largeArchive ? "128MB" : "32MB") + "）");
                            continue;
                        }
                        if (dataBytes + length > maxDataBytes)
                        {
                            skipped.Add(relative + "（总数据超过" + (largeArchive ? "256MB" : "48MB") + "）");
                            continue;
                        }
                        try
                        {
                            if (portableExport
                                && string.Equals(Path.GetExtension(relative), ".json", StringComparison.OrdinalIgnoreCase))
                            {
                                var original = File.ReadAllBytes(source);
                                var sanitized = SanitizePortableJsonFile(original);
                                try
                                {
                                    if (sanitized.Length > maxSingleFileBytes)
                                    {
                                        skipped.Add(relative + "（清理敏感字段后单文件超过" + (portableExport ? "128MB" : "32MB") + "）");
                                        continue;
                                    }
                                    if (dataBytes + sanitized.Length > maxDataBytes)
                                    {
                                        skipped.Add(relative + "（清理敏感字段后总数据超过" + (portableExport ? "256MB" : "48MB") + "）");
                                        continue;
                                    }
                                    var entry = zip.CreateEntry("files/" + relative.Replace('\\', '/'), CompressionLevel.Optimal);
                                    using (var output = entry.Open())
                                        output.Write(sanitized, 0, sanitized.Length);
                                    dataBytes += sanitized.Length;
                                }
                                finally { Array.Clear(sanitized, 0, sanitized.Length); }
                            }
                            else
                            {
                                var entry = zip.CreateEntry("files/" + relative.Replace('\\', '/'), CompressionLevel.Optimal);
                                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                                    FileShare.ReadWrite | FileShare.Delete))
                                using (var output = entry.Open()) input.CopyTo(output);
                                dataBytes += length;
                            }
                            fileCount++;
                        }
                        catch (Exception ex) { skipped.Add(relative + "（" + Safe(ex.Message, 80) + "）"); }
                    }
                    var manifest = new JObject
                    {
                        ["schema"] = portableExport ? PortableExportSchema : "qnbot.shop-data-backup",
                        ["version"] = portableExport ? PortableExportVersion : 2,
                        ["shopKey"] = shop.ShopKey,
                        ["platform"] = shop.Platform,
                        ["sellerId"] = shop.SellerId,
                        ["createdAt"] = createdAt,
                        ["deviceName"] = Environment.MachineName,
                        ["appVersion"] = AppVersion(),
                        ["fileCount"] = fileCount,
                        ["dataBytes"] = dataBytes,
                        ["settingCount"] = settings.Count,
                        ["skippedFileCount"] = skipped.Count,
                        ["excluded"] = portableExport
                            ? new JArray(
                                "本店 Bot 客户端令牌及其他 Token/API Key/Secret/Password/Credential",
                                "DPAPI settings.json（改为逻辑设置导出）",
                                "profile.json 和全局 shops.json",
                                "logs/backup/cache 与临时文件",
                                "params.db、云同步 revision/hash、远程暂停和已处理命令")
                            : new JArray(
                                "本店 Bot 客户端令牌",
                                "DPAPI settings.json（改为逻辑设置导出）",
                                "profile.json 和全局 shops.json",
                                "logs/backup/cache 与临时文件",
                                "云同步 revision/hash、远程暂停和已处理命令"),
                        ["skippedFiles"] = JArray.FromObject(skipped.Take(200).ToList())
                    };
                    WriteTextEntry(zip, "manifest.json", manifest.ToString(Formatting.Indented));
                }
                return memory.ToArray();
            }
        }

        private static Dictionary<string, string> ExportPortableSettings(ShopContext shop)
        {
            return ExportPortableSettings(shop, false);
        }

        private static Dictionary<string, string> ExportPortableSettings(ShopContext shop, bool stripSecrets)
        {
            var source = new ShopScopedSettingsStore(shop, Paths).ExportValues();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in source)
            {
                if (IsTransientSetting(pair.Key)) continue;
                if (stripSecrets && IsSensitiveSettingName(pair.Key)) continue;
                result[pair.Key] = stripSecrets
                    ? StripSensitiveJsonProperties(pair.Value)
                    : pair.Value ?? string.Empty;
            }
            return result;
        }

        private static Dictionary<string, string> ExportLocalRollbackSettings(ShopContext shop)
        {
            var source = new ShopScopedSettingsStore(shop, Paths).ExportValues();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in source)
            {
                if (IsTransientSetting(pair.Key) || IsBotClientTokenSettingName(pair.Key)) continue;
                result[pair.Key] = pair.Value ?? string.Empty;
            }
            return result;
        }

        private static string StripSensitiveJsonProperties(string value)
        {
            value = value ?? string.Empty;
            if (value.Length == 0) return value;
            try
            {
                var token = JToken.Parse(value);
                RemoveSensitiveJsonProperties(token);
                return token.ToString(Formatting.None);
            }
            catch
            {
                var trimmed = value.TrimStart();
                return trimmed.StartsWith("{", StringComparison.Ordinal)
                    || trimmed.StartsWith("[", StringComparison.Ordinal)
                    ? string.Empty
                    : value;
            }
        }

        private static void RemoveSensitiveJsonProperties(JToken token)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                foreach (var property in obj.Properties().ToList())
                {
                    if (IsSensitiveSettingName(property.Name)) property.Remove();
                    else RemoveSensitiveJsonProperties(property.Value);
                }
                return;
            }

            var array = token as JArray;
            if (array != null)
                foreach (var item in array) RemoveSensitiveJsonProperties(item);
        }

        private static bool IsSensitiveSettingName(string value)
        {
            var compact = new string((value ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
            return compact.Contains("token")
                || compact.Contains("apikey")
                || compact.Contains("secret")
                || compact.Contains("password")
                || compact.Contains("credential")
                || compact.Contains("authorization")
                || compact.Contains("accesskey")
                || compact.Contains("privatekey")
                || compact.Contains("licensekey")
                || compact.Contains("cookie")
                || compact.Contains("bearer")
                || compact.Contains("jwt");
        }
        private static bool IsBotClientTokenSettingName(string value)
        {
            var compact = new string((value ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
            return (compact.Contains("controlplane") && compact.Contains("token"))
                || compact == "botclienttoken"
                || compact == "botservicetoken"
                || compact == "bottoken";
        }

        private static void PreserveLocalSensitiveSettings(ShopContext shop, PortableArchivePlan plan)
        {
            var current = new ShopScopedSettingsStore(shop, Paths).ExportValues();
            foreach (var pair in current)
            {
                if (IsTransientSetting(pair.Key) || IsBotClientTokenSettingName(pair.Key)) continue;
                if (IsSensitiveSettingName(pair.Key))
                {
                    if (!plan.Settings.ContainsKey(pair.Key))
                        plan.Settings[pair.Key] = pair.Value ?? string.Empty;
                    continue;
                }

                string imported;
                if (plan.Settings.TryGetValue(pair.Key, out imported))
                    plan.Settings[pair.Key] = MergeSensitiveJsonProperties(imported, pair.Value);
            }
        }

        private static string MergeSensitiveJsonProperties(string imported, string existing)
        {
            if (string.IsNullOrWhiteSpace(imported) || string.IsNullOrWhiteSpace(existing))
                return imported ?? string.Empty;
            try
            {
                var incoming = JToken.Parse(imported);
                var current = JToken.Parse(existing);
                MergeSensitiveJsonProperties(incoming, current);
                return incoming.ToString(Formatting.None);
            }
            catch
            {
                return imported;
            }
        }

        private static void MergeSensitiveJsonProperties(JToken incoming, JToken current)
        {
            var incomingObject = incoming as JObject;
            var currentObject = current as JObject;
            if (incomingObject != null && currentObject != null)
            {
                foreach (var currentProperty in currentObject.Properties())
                {
                    var incomingProperty = incomingObject.Properties().FirstOrDefault(
                        x => string.Equals(x.Name, currentProperty.Name, StringComparison.OrdinalIgnoreCase));
                    if (IsSensitiveSettingName(currentProperty.Name))
                    {
                        if (!IsBotClientTokenSettingName(currentProperty.Name) && incomingProperty == null)
                            incomingObject.Add(currentProperty.Name, currentProperty.Value.DeepClone());
                    }
                    else if (incomingProperty != null)
                    {
                        MergeSensitiveJsonProperties(incomingProperty.Value, currentProperty.Value);
                    }
                }
                return;
            }

            var incomingArray = incoming as JArray;
            var currentArray = current as JArray;
            if (incomingArray == null || currentArray == null) return;
            foreach (var incomingItem in incomingArray.OfType<JObject>())
            {
                var currentItem = FindSensitiveMergeMatch(incomingItem, currentArray);
                if (currentItem != null)
                    MergeSensitiveJsonProperties(incomingItem, currentItem);
            }
        }

        private static JObject FindSensitiveMergeMatch(JObject incoming, JArray current)
        {
            var identityNames = new[] { "id", "key", "guid", "name", "baseurl" };
            foreach (var identityName in identityNames)
            {
                var incomingIdentity = incoming.Properties().FirstOrDefault(
                    x => string.Equals(x.Name, identityName, StringComparison.OrdinalIgnoreCase));
                if (incomingIdentity == null || string.IsNullOrWhiteSpace(incomingIdentity.Value.ToString()))
                    continue;
                foreach (var candidate in current.OfType<JObject>())
                {
                    var currentIdentity = candidate.Properties().FirstOrDefault(
                        x => string.Equals(x.Name, identityName, StringComparison.OrdinalIgnoreCase));
                    if (currentIdentity != null
                        && string.Equals(currentIdentity.Value.ToString(), incomingIdentity.Value.ToString(), StringComparison.Ordinal))
                        return candidate;
                }
            }
            return null;
        }


        private static bool ShouldExcludePortable(string relative)
        {
            if (ShouldExclude(relative)) return true;
            var normalized = (relative ?? string.Empty).Replace('/', '\\');
            var parts = normalized.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(IsSensitiveSettingName)) return true;
            var name = Path.GetFileName(normalized);
            var lower = (name ?? string.Empty).ToLowerInvariant();
            var extension = Path.GetExtension(relative);
            return lower == "params.db"
                || lower.StartsWith("params.db-", StringComparison.Ordinal)
                || IsSensitiveSettingName(Path.GetFileNameWithoutExtension(name))
                || lower.StartsWith(".env", StringComparison.Ordinal)
                || string.Equals(extension, ".qbr", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".qnbot", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".pem", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".key", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".pfx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".p12", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jks", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSafePortableRelativePath(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return false;
            var normalized = relative.Replace('/', '\\');
            if (Path.IsPathRooted(normalized)
                || normalized.StartsWith("\\", StringComparison.Ordinal)
                || normalized.IndexOf(':') >= 0)
                return false;

            var parts = normalized.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            foreach (var part in parts)
            {
                if (part == "." || part == ".."
                    || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return false;
            }
            return true;
        }

        private static PortableArchivePlan ReadPortableArchive(ShopContext shop, byte[] archive)
        {
            var plan = new PortableArchivePlan
            {
                Settings = new Dictionary<string, string>(StringComparer.Ordinal),
                Files = new List<PortableArchiveFile>()
            };
            try
            {
                using (var stream = new MemoryStream(archive, false))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, false, Encoding.UTF8))
                {
                    if (zip.Entries.Count > MaxPortableFiles + 2)
                        throw new InvalidDataException("全店信息备份包含过多文件。");

                    var manifestEntry = zip.GetEntry("manifest.json");
                    var settingsEntry = zip.GetEntry("settings.json");
                    if (manifestEntry == null || settingsEntry == null)
                        throw new InvalidDataException("全店信息备份缺少 manifest.json 或 settings.json。");
                    if (manifestEntry.Length > 1024 * 1024 || settingsEntry.Length > 8L * 1024 * 1024)
                        throw new InvalidDataException("全店信息备份的清单或设置数据超过大小限制。");

                    var manifest = JObject.Parse(ReadTextEntry(manifestEntry));
                    if (!string.Equals(Convert.ToString(manifest["schema"]), PortableExportSchema, StringComparison.Ordinal)
                        || (manifest.Value<int?>("version") ?? 0) != PortableExportVersion)
                        throw new InvalidDataException("文件格式不是受支持的 QnBot 全店信息导出包。");
                    if (!string.Equals(Convert.ToString(manifest["shopKey"]), shop.ShopKey, StringComparison.Ordinal))
                        throw new InvalidDataException("备份 ShopKey 与当前店铺不匹配，已阻止跨店导入。");
                    if (!string.Equals(Convert.ToString(manifest["platform"]), shop.Platform ?? string.Empty, StringComparison.Ordinal)
                        || !string.Equals(Convert.ToString(manifest["sellerId"]), shop.SellerId ?? string.Empty, StringComparison.Ordinal))
                        throw new InvalidDataException("备份中的店铺身份与当前店铺不一致，已阻止导入。");

                    var rawSettings = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                        ReadTextEntry(settingsEntry)) ?? new Dictionary<string, string>();
                    foreach (var pair in rawSettings)
                    {
                        if (IsTransientSetting(pair.Key) || IsSensitiveSettingName(pair.Key)) continue;
                        plan.Settings[pair.Key] = StripSensitiveJsonProperties(pair.Value);
                    }

                    var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    long totalBytes = 0;
                    foreach (var entry in zip.Entries)
                    {
                        if (!entry.FullName.StartsWith("files/", StringComparison.Ordinal)) continue;
                        var relative = entry.FullName.Substring("files/".Length);
                        if (string.IsNullOrWhiteSpace(relative) || relative.EndsWith("/", StringComparison.Ordinal)) continue;
                        if (!IsSafePortableRelativePath(relative))
                            throw new InvalidDataException("备份中包含不安全路径：" + relative);
                        if (ShouldExcludePortable(relative))
                            throw new InvalidDataException("备份中包含不允许导入的令牌、敏感文件或运行缓存：" + relative);
                        if (!seenPaths.Add(relative))
                            throw new InvalidDataException("备份中包含重复文件路径：" + relative);
                        if (entry.Length > MaxPortableSingleFileBytes || totalBytes + entry.Length > MaxPortableDataBytes)
                            throw new InvalidDataException("备份文件超过单文件或总数据大小限制。");

                        var content = ReadPortableEntry(entry);
                        if (string.Equals(Path.GetExtension(relative), ".json", StringComparison.OrdinalIgnoreCase))
                            content = SanitizePortableJsonFile(content);
                        if (content.Length > MaxPortableSingleFileBytes
                            || totalBytes + content.Length > MaxPortableDataBytes)
                        {
                            Array.Clear(content, 0, content.Length);
                            throw new InvalidDataException("清理敏感字段后的备份文件超过大小限制。");
                        }
                        totalBytes += content.Length;
                        plan.Files.Add(new PortableArchiveFile { RelativePath = relative, Content = content });
                    }

                    plan.CreatedAt = Convert.ToString(manifest["createdAt"] ?? string.Empty);
                    plan.SourceDevice = Convert.ToString(manifest["deviceName"] ?? string.Empty);
                }
                return plan;
            }
            catch
            {
                foreach (var file in plan.Files)
                    if (file.Content != null) Array.Clear(file.Content, 0, file.Content.Length);
                throw;
            }
        }

        private static int ReadPortableSkippedCount(byte[] archive)
        {
            using (var stream = new MemoryStream(archive, false))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, false, Encoding.UTF8))
            {
                var manifestEntry = zip.GetEntry("manifest.json");
                if (manifestEntry == null) return 0;
                var manifest = JObject.Parse(ReadTextEntry(manifestEntry));
                var declared = manifest.Value<int?>("skippedFileCount");
                if (declared.HasValue) return declared.Value;
                var skipped = manifest["skippedFiles"] as JArray;
                return skipped == null ? 0 : skipped.Count;
            }
        }

        private static byte[] ReadPortableEntry(ZipArchiveEntry entry)
        {
            using (var input = entry.Open())
            using (var output = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                long total = 0;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaxPortableSingleFileBytes)
                        throw new InvalidDataException("备份中包含超过128MB的文件。");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        private static byte[] SanitizePortableJsonFile(byte[] content)
        {
            try
            {
                var json = Encoding.UTF8.GetString(content);
                if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
                var token = JToken.Parse(json);
                RemoveSensitiveJsonProperties(token);
                var sanitized = Encoding.UTF8.GetBytes(token.ToString(Formatting.Indented));
                Array.Clear(content, 0, content.Length);
                return sanitized;
            }
            catch
            {
                if (content != null) Array.Clear(content, 0, content.Length);
                throw new InvalidDataException(
                    "无法验证 JSON 文件中的敏感字段，已阻止导出或导入该文件。");
            }
        }

        private static RestoreResult ApplyPortableArchive(ShopContext shop, PortableArchivePlan plan)
        {
            ClearPortableFiles(shop);
            new ShopScopedSettingsStore(shop, Paths).ReplaceValues(plan.Settings);

            var result = new RestoreResult
            {
                ParamCount = plan.Settings.Count,
                CreatedAt = plan.CreatedAt,
                SourceDevice = plan.SourceDevice
            };
            var root = EnsureTrailing(Path.GetFullPath(Paths.GetShopRoot(shop)));
            foreach (var file in plan.Files)
            {
                var target = Path.GetFullPath(Path.Combine(root, file.RelativePath));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("备份中包含不安全路径：" + file.RelativePath);
                var directory = Path.GetDirectoryName(target);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    output.Write(file.Content, 0, file.Content.Length);
                result.FileCount++;
            }
            return result;
        }

        private static void ClearPortableFiles(ShopContext shop)
        {
            foreach (var source in EnumerateShopFilesStrict(shop))
            {
                var relative = RelativeShopPath(shop, source);
                if (ShouldExcludePortable(relative)) continue;
                File.Delete(source);
            }
        }

        private static void ClearLocalConfigurationFiles(ShopContext shop)
        {
            foreach (var source in EnumerateShopFilesStrict(shop))
            {
                var relative = RelativeShopPath(shop, source);
                if (ShouldExcludeLocalRollback(relative)) continue;
                File.Delete(source);
            }
        }

        private static bool ShouldExcludeLocalRollback(string relative)
        {
            if (ShouldExclude(relative)) return true;
            var name = Path.GetFileName((relative ?? string.Empty).Replace('/', '\\'));
            var compact = new string((name ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
            return compact.Contains("controlplanetoken")
                || compact == "botclienttoken"
                || compact == "botservicetoken"
                || compact == "bottoken";
        }

        private static string CreateProtectedPortableRollback(ShopContext shop)
        {
            int fileCount;
            long dataBytes;
            var plain = BuildPlainBackup(
                shop, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), out fileCount, out dataBytes,
                portableExport: false, localRollback: true);
            byte[] protectedBytes = null;
            try
            {
                protectedBytes = ProtectedData.Protect(
                    plain,
                    Encoding.UTF8.GetBytes("qnbot|portable-local-rollback|" + shop.ShopKey),
                    DataProtectionScope.CurrentUser);
                var path = Path.Combine(
                    Paths.GetBackupRoot(shop),
                    "portable-config-before-change-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff")
                        + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".qbr");
                File.WriteAllBytes(path, protectedBytes);
                return path;
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
                if (protectedBytes != null) Array.Clear(protectedBytes, 0, protectedBytes.Length);
            }
        }

        private static void RestoreProtectedPortableRollback(ShopContext shop, string rollbackPath)
        {
            if (string.IsNullOrWhiteSpace(rollbackPath) || !File.Exists(rollbackPath))
                throw new FileNotFoundException("本地配置回滚包不存在。", rollbackPath);
            var protectedBytes = File.ReadAllBytes(rollbackPath);
            byte[] plain = null;
            try
            {
                plain = ProtectedData.Unprotect(
                    protectedBytes,
                    Encoding.UTF8.GetBytes("qnbot|portable-local-rollback|" + shop.ShopKey),
                    DataProtectionScope.CurrentUser);
                ClearLocalConfigurationFiles(shop);
                new ShopScopedSettingsStore(shop, Paths)
                    .ReplaceValues(new Dictionary<string, string>(StringComparer.Ordinal));
                RestoreZip(shop, plain);
            }
            finally
            {
                Array.Clear(protectedBytes, 0, protectedBytes.Length);
                if (plain != null) Array.Clear(plain, 0, plain.Length);
            }
        }

        private sealed class PortableArchivePlan
        {
            public Dictionary<string, string> Settings { get; set; }
            public List<PortableArchiveFile> Files { get; set; }
            public string CreatedAt { get; set; }
            public string SourceDevice { get; set; }
        }

        private sealed class PortableArchiveFile
        {
            public string RelativePath { get; set; }
            public byte[] Content { get; set; }
        }

        private static bool IsTransientSetting(string key)
        {
            key = key ?? string.Empty;
            return key.IndexOf("Revision", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("LastHash", StringComparison.OrdinalIgnoreCase) >= 0
                || key.IndexOf("ProcessedCommand", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(key, "BotWebRemotePause", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> EnumerateShopFiles(ShopContext shop)
        {
            var root = Paths.GetShopRoot(shop);
            if (!Directory.Exists(root)) return Enumerable.Empty<string>();
            try { return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList(); }
            catch { return Enumerable.Empty<string>(); }
        }

        private static IEnumerable<string> EnumerateShopFilesStrict(ShopContext shop)
        {
            var root = Paths.GetShopRoot(shop);
            if (!Directory.Exists(root)) return Enumerable.Empty<string>();
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        }

        private static string RelativeShopPath(ShopContext shop, string fullPath)
        {
            var root = EnsureTrailing(Path.GetFullPath(Paths.GetShopRoot(shop)));
            var full = Path.GetFullPath(fullPath);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).TrimStart('\\', '/')
                : Path.GetFileName(full);
        }

        private static bool ShouldExclude(string relative)
        {
            relative = (relative ?? string.Empty).Replace('/', '\\').TrimStart('\\');
            var parts = relative.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return true;
            if (new[] { "logs", "backup", "cache" }.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) return true;
            var name = Path.GetFileName(relative);
            if (string.Equals(relative, "profile.json", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(relative, "config\\settings.json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relative, "config\\control-plane-token.json", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("settings.json.", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("control-plane-token.json.", StringComparison.OrdinalIgnoreCase)) return true;
            var extension = Path.GetExtension(relative);
            return string.Equals(extension, ".log", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bak", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".dmp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".trace", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".etl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".qab", StringComparison.OrdinalIgnoreCase);
        }

        private static string CreateRollbackBackup(ShopContext shop, string token)
        {
            int fileCount;
            long dataBytes;
            var plain = BuildPlainBackup(shop, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), out fileCount, out dataBytes);
            byte[] encrypted = null;
            try
            {
                encrypted = Encrypt(plain, token, shop.ShopKey);
                var path = Path.Combine(Paths.GetBackupRoot(shop),
                    "before-cloud-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".qab");
                File.WriteAllBytes(path, encrypted);
                return path;
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
                if (encrypted != null) Array.Clear(encrypted, 0, encrypted.Length);
            }
        }

        private static RestoreResult RestoreZip(ShopContext shop, byte[] zipBytes)
        {
            var result = new RestoreResult();
            using (var stream = new MemoryStream(zipBytes, false))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, false, Encoding.UTF8))
            {
                var manifestEntry = zip.GetEntry("manifest.json");
                if (manifestEntry == null) throw new Exception("备份缺少 manifest.json");
                var manifest = JObject.Parse(ReadTextEntry(manifestEntry));
                var backupSchema = Convert.ToString(manifest["schema"]);
                if (!string.Equals(backupSchema, "qnbot.shop-data-backup", StringComparison.Ordinal)
                    && !string.Equals(backupSchema, LegacyBackupSchema, StringComparison.Ordinal))
                    throw new Exception("云端文件不是店铺隔离版千牛 Bot 数据备份");
                if (!string.Equals(Convert.ToString(manifest["shopKey"]), shop.ShopKey, StringComparison.Ordinal))
                    throw new Exception("云备份 ShopKey 与当前店铺不匹配，已阻止跨店恢复");

                var settingsEntry = zip.GetEntry("settings.json");
                if (settingsEntry == null) throw new Exception("备份缺少 settings.json");
                var settings = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                    ReadTextEntry(settingsEntry)) ?? new Dictionary<string, string>();
                var current = new ShopScopedSettingsStore(shop, Paths).ExportValues();
                foreach (var pair in settings) current[pair.Key] = pair.Value ?? string.Empty;
                new ShopScopedSettingsStore(shop, Paths).ReplaceValues(current);
                result.ParamCount = settings.Count;

                var root = EnsureTrailing(Path.GetFullPath(Paths.GetShopRoot(shop)));
                foreach (var entry in zip.Entries.Where(x => x.FullName.StartsWith("files/", StringComparison.Ordinal)))
                {
                    var relative = entry.FullName.Substring("files/".Length).Replace('/', '\\');
                    if (string.IsNullOrWhiteSpace(relative) || ShouldExclude(relative)) continue;
                    var target = Path.GetFullPath(Path.Combine(root, relative));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("备份中包含不安全路径：" + relative);
                    var directory = Path.GetDirectoryName(target);
                    if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                    using (var input = entry.Open())
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                    result.FileCount++;
                }
                result.CreatedAt = Convert.ToString(manifest["createdAt"] ?? string.Empty);
                result.SourceDevice = Convert.ToString(manifest["deviceName"] ?? string.Empty);
            }
            return result;
        }

        private static byte[] Encrypt(byte[] plain, string token, string shopKey)
        {
            var salt = RandomBytes(16);
            var iv = RandomBytes(16);
            byte[] encryptionKey = null;
            byte[] macKey = null;
            byte[] cipher = null;
            byte[] prefix = null;
            byte[] mac = null;
            try
            {
                DeriveKeys(token, shopKey, salt, out encryptionKey, out macKey);
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = encryptionKey;
                    aes.IV = iv;
                    using (var encryptor = aes.CreateEncryptor())
                        cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
                }
                prefix = Encoding.ASCII.GetBytes(Magic).Concat(salt).Concat(iv).Concat(cipher).ToArray();
                using (var hmac = new HMACSHA256(macKey)) mac = hmac.ComputeHash(prefix);
                return prefix.Concat(mac).ToArray();
            }
            finally
            {
                Clear(salt); Clear(iv); Clear(encryptionKey); Clear(macKey); Clear(cipher); Clear(prefix); Clear(mac);
            }
        }

        private static byte[] Decrypt(byte[] encrypted, string token, string shopKey)
        {
            if (encrypted == null || encrypted.Length < Magic.Length + 16 + 16 + 32 + 1)
                throw new Exception("云备份文件不完整");
            if (!string.Equals(Encoding.ASCII.GetString(encrypted, 0, Magic.Length), Magic, StringComparison.Ordinal))
                throw new Exception("云备份文件版本不受支持");

            var salt = encrypted.Skip(Magic.Length).Take(16).ToArray();
            var iv = encrypted.Skip(Magic.Length + 16).Take(16).ToArray();
            var cipherLength = encrypted.Length - Magic.Length - 16 - 16 - 32;
            var cipher = encrypted.Skip(Magic.Length + 32).Take(cipherLength).ToArray();
            var suppliedMac = encrypted.Skip(encrypted.Length - 32).Take(32).ToArray();
            var signed = encrypted.Take(encrypted.Length - 32).ToArray();
            byte[] encryptionKey = null;
            byte[] macKey = null;
            byte[] expectedMac = null;
            try
            {
                DeriveKeys(token, shopKey, salt, out encryptionKey, out macKey);
                using (var hmac = new HMACSHA256(macKey)) expectedMac = hmac.ComputeHash(signed);
                if (!ConstantTimeEquals(suppliedMac, expectedMac))
                    throw new Exception("解密失败：本店 Bot 令牌/ShopKey 不一致或云备份已损坏");
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = encryptionKey;
                    aes.IV = iv;
                    using (var decryptor = aes.CreateDecryptor())
                        return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
                }
            }
            catch (CryptographicException)
            {
                throw new Exception("解密失败：本店 Bot 令牌/ShopKey 不一致或云备份已损坏");
            }
            finally
            {
                Clear(salt); Clear(iv); Clear(cipher); Clear(suppliedMac); Clear(signed);
                Clear(encryptionKey); Clear(macKey); Clear(expectedMac);
            }
        }

        private static void DeriveKeys(string token, string shopKey, byte[] salt, out byte[] encryptionKey, out byte[] macKey)
        {
            using (var derive = new Rfc2898DeriveBytes(
                (token ?? string.Empty) + "|qianniu-shop-backup|" + (shopKey ?? string.Empty),
                salt,
                120000))
            {
                var material = derive.GetBytes(64);
                try
                {
                    encryptionKey = material.Take(32).ToArray();
                    macKey = material.Skip(32).Take(32).ToArray();
                }
                finally { Clear(material); }
            }
        }

        private static bool ConstantTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            var different = 0;
            for (var i = 0; i < left.Length; i++) different |= left[i] ^ right[i];
            return different == 0;
        }

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return bytes;
        }

        private static void Clear(byte[] value)
        {
            if (value != null) Array.Clear(value, 0, value.Length);
        }

        private static void WriteTextEntry(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(text ?? string.Empty);
        }

        private static string ReadTextEntry(ZipArchiveEntry entry)
        {
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, true)) return reader.ReadToEnd();
        }

        private static HttpClient CreateHttp(string token, ShopContext shop)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var http = new HttpClient(new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "qianniu-bot-shop-data-backup/2.0");
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey);
            return http;
        }

        private static void GetConnection(ShopContext shop, out string serverUrl, out string token)
        {
            if (shop == null) throw new Exception("当前没有店铺作用域。");
            var connection = new ShopControlPlaneConnectionStore(shop, Paths);
            serverUrl = connection.GetServerUrl();
            string error;
            if (!connection.TryGetToken(out token, out error))
                throw new Exception(string.IsNullOrWhiteSpace(error) ? "请先配置本店 Bot 客户端令牌。" : error);
            if (string.IsNullOrWhiteSpace(serverUrl)) throw new Exception("请先配置统一 API 服务地址。");
            if (string.IsNullOrWhiteSpace(token)) throw new Exception("请先配置本店 Bot 客户端令牌。");
        }

        internal static void RestartApplication()
        {
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            var arguments = "/c ping 127.0.0.1 -n 3 >nul & start \"\" \"" + exe.Replace("\"", "\"\"") + "\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            Application.Current.Shutdown();
        }

        private static string AppVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "unknown" : version.ToString();
        }

        private static string Sha256(byte[] value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(value ?? new byte[0]))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024d / 1024d).ToString("0.0") + " MB";
            return (bytes / 1024d / 1024d / 1024d).ToString("0.00") + " GB";
        }

        private static string EnsureTrailing(string path)
        {
            return path.EndsWith("\\") ? path : path + "\\";
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private sealed class BackupBuildResult : IDisposable
        {
            public byte[] EncryptedBytes { get; set; }
            public int FileCount { get; set; }
            public long DataBytes { get; set; }
            public string CreatedAt { get; set; }
            public void Dispose()
            {
                if (EncryptedBytes != null) Array.Clear(EncryptedBytes, 0, EncryptedBytes.Length);
            }
        }
    }

    internal sealed class RestoreResult
    {
        public int ParamCount { get; set; }
        public int FileCount { get; set; }
        public string CreatedAt { get; set; }
        public string SourceDevice { get; set; }
        public string RollbackPath { get; set; }
    }

    internal sealed class PortableDataExportResult
    {
        public int FileCount { get; set; }
        public long DataBytes { get; set; }
        public int SettingCount { get; set; }
        public int SkippedCount { get; set; }
        public long ArchiveBytes { get; set; }
    }

    internal sealed class ClientDataBackupWindow : Window
    {
        private readonly ShopContext _shop;
        private readonly TextBlock _cloudStatus;
        private readonly TextBlock _operationStatus;
        private readonly Button _upload;
        private readonly Button _restore;
        private readonly Button _refresh;
        private bool _busy;

        public ClientDataBackupWindow(ShopContext shop)
        {
            _shop = shop ?? throw new ArgumentNullException(nameof(shop));
            Title = "本店云备份与更换电脑";
            Width = 680;
            Height = 530;
            MinWidth = 620;
            MinHeight = 470;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new DockPanel { Margin = new Thickness(20) };
            Content = root;
            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            AddButton(footer, "打开本店数据目录", 132, (s, e) =>
            {
                var folder = ClientDataCloudBackupService.GetDataFolder(_shop);
                Directory.CreateDirectory(folder);
                PathEx.OpenFolder(folder);
            });
            AddButton(footer, "关闭", 82, (s, e) => Close());

            var content = new StackPanel();
            root.Children.Add(content);
            content.Children.Add(new TextBlock
            {
                Text = "本店云备份与更换电脑",
                FontSize = 21,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            content.Children.Add(new TextBlock
            {
                Text = "当前店铺：" + (_shop.DisplayName ?? _shop.ShopKey) + "\nShopKey：" + _shop.ShopKey
                    + "\n\n只备份本店知识库、规则、策略、AI设置和业务状态。不会包含其他店铺、令牌、日志、缓存或店铺身份文件。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                LineHeight = 22,
                Margin = new Thickness(0, 0, 0, 14)
            });
            content.Children.Add(new Border
            {
                BorderBrush = Brushes.LightSteelBlue,
                BorderThickness = new Thickness(1),
                Background = Brushes.AliceBlue,
                Padding = new Thickness(12),
                Child = new TextBlock
                {
                    Text = "备份使用本店 Bot 令牌和 ShopKey 派生密钥加密。新电脑必须登录同一店铺并配置相同令牌；跨 ShopKey 恢复会被拒绝。",
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 21
                }
            });
            content.Children.Add(new TextBlock
            {
                Text = "本店云端备份状态",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 18, 0, 6)
            });
            _cloudStatus = new TextBlock
            {
                Text = "正在读取...",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 12)
            };
            content.Children.Add(_cloudStatus);
            var actions = new WrapPanel();
            content.Children.Add(actions);
            _upload = AddButton(actions, "上传本店数据", 140, async (s, e) => await UploadAsync());
            _restore = AddButton(actions, "恢复本店云备份", 150, async (s, e) => await RestoreAsync());
            _refresh = AddButton(actions, "刷新状态", 96, async (s, e) => await RefreshAsync());
            _operationStatus = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.SteelBlue,
                Margin = new Thickness(0, 16, 0, 0)
            };
            content.Children.Add(_operationStatus);
            Loaded += async (s, e) => await RunScopedAsync(RefreshAsync);
        }

        private async Task RunScopedAsync(Func<Task> action)
        {
            using (ShopSettingsScope.Enter(_shop)) await action();
        }

        private async Task RefreshAsync()
        {
            if (_busy) return;
            SetBusy(true, "正在读取本店云备份状态...");
            try
            {
                var state = await ClientDataCloudBackupService.GetStatusAsync(_shop);
                var exists = state.Value<bool?>("exists") == true;
                if (!exists)
                {
                    _cloudStatus.Text = "本店令牌尚无云端备份。请先在旧电脑上传本店数据。";
                    _cloudStatus.Foreground = Brushes.DarkOrange;
                    _restore.IsEnabled = false;
                }
                else
                {
                    _cloudStatus.Text = "版本 v" + (state.Value<int?>("revision") ?? 0)
                        + " · 上传时间 " + Convert.ToString(state["updated_at"] ?? state["created_at"] ?? "未知")
                        + "\n来源电脑：" + Convert.ToString(state["device_name"] ?? "未知")
                        + " · 程序版本：" + Convert.ToString(state["app_version"] ?? "未知")
                        + "\n加密包：" + ClientDataCloudBackupService.FormatBytes(state.Value<long?>("size_bytes") ?? 0)
                        + " · 本店文件：" + (state.Value<int?>("file_count") ?? 0)
                        + " · 原始数据：" + ClientDataCloudBackupService.FormatBytes(state.Value<long?>("data_bytes") ?? 0);
                    _cloudStatus.Foreground = Brushes.SeaGreen;
                    _restore.IsEnabled = true;
                }
                _operationStatus.Text = "";
            }
            catch (Exception ex)
            {
                _cloudStatus.Text = "读取失败：" + ex.Message;
                _cloudStatus.Foreground = Brushes.IndianRed;
                _operationStatus.Text = "";
            }
            finally { SetBusy(false, null); }
        }

        private async Task UploadAsync()
        {
            if (_busy) return;
            if (MessageBox.Show(
                "将使用本店令牌加密并覆盖本店现有云备份。不会包含其他店铺或令牌本身。是否继续？",
                "上传本店数据", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            SetBusy(true, "正在准备上传...");
            try
            {
                using (ShopSettingsScope.Enter(_shop))
                {
                    var result = await ClientDataCloudBackupService.UploadAsync(_shop, SetOperation);
                    MessageBox.Show(
                        "本店云备份上传成功。\n版本：v" + (result.Value<int?>("revision") ?? 0)
                        + "\n加密包大小：" + ClientDataCloudBackupService.FormatBytes(result.Value<long?>("size_bytes") ?? 0),
                        "本店云备份完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("上传本店云备份失败：" + ex.Message, "云备份", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false, null);
                await RunScopedAsync(RefreshAsync);
            }
        }

        private async Task RestoreAsync()
        {
            if (_busy) return;
            if (MessageBox.Show(
                "将用云端数据覆盖当前 ShopKey 的业务数据，并先生成本店加密回滚包。其他店铺不会修改。恢复后程序自动重启。是否继续？",
                "恢复本店云备份", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SetBusy(true, "正在下载本店云备份...");
            try
            {
                using (ShopSettingsScope.Enter(_shop))
                {
                    var result = await ClientDataCloudBackupService.DownloadAndRestoreAsync(_shop, SetOperation);
                    MessageBox.Show(
                        "本店恢复成功。\n来源电脑：" + (string.IsNullOrWhiteSpace(result.SourceDevice) ? "未知" : result.SourceDevice)
                        + "\n备份时间：" + (string.IsNullOrWhiteSpace(result.CreatedAt) ? "未知" : result.CreatedAt)
                        + "\n恢复设置：" + result.ParamCount + "\n恢复文件：" + result.FileCount
                        + "\n本店回滚包：" + result.RollbackPath + "\n\n程序现在将自动重启。",
                        "本店换机恢复完成", MessageBoxButton.OK, MessageBoxImage.Information);
                    ClientDataCloudBackupService.RestartApplication();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("恢复本店云备份失败：" + ex.Message, "换机恢复", MessageBoxButton.OK, MessageBoxImage.Error);
                SetBusy(false, null);
            }
        }

        private void SetOperation(string text)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _operationStatus.Text = text ?? string.Empty;
                _operationStatus.Foreground = Brushes.SteelBlue;
            }));
        }

        private void SetBusy(bool busy, string text)
        {
            _busy = busy;
            _upload.IsEnabled = !busy;
            _refresh.IsEnabled = !busy;
            if (busy) _restore.IsEnabled = false;
            if (!string.IsNullOrWhiteSpace(text)) SetOperation(text);
        }

        private static Button AddButton(Panel panel, string text, double width, RoutedEventHandler click)
        {
            var button = new Button
            {
                Content = text,
                Width = width,
                Height = 32,
                Margin = new Thickness(0, 0, 10, 8)
            };
            button.Click += click;
            panel.Children.Add(button);
            return button;
        }
    }
}