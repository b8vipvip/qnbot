using Bot.ChatRecord;
using Bot.ShopScope;
using BotLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class VisionCachedImageReference
    {
        public string ShopKey { get; set; }
        public string SellerNick { get; set; }
        public string BuyerNick { get; set; }
        public string MessageKey { get; set; }
        public QNChatMessage Message { get; set; }
        public DateTime ObservedAt { get; set; }
        public bool Withdrawn { get; set; }
        public bool CacheComplete { get; set; }
    }

    internal static class VisionImageCacheService
    {
        private sealed class CacheDownloadResult
        {
            public bool Success;
            public string FilePath;
            public string MimeType;
            public long Bytes;
            public string Error;
        }

        private sealed class CacheRecord
        {
            public string Key;
            public string ShopKey;
            public string SellerNick;
            public string BuyerNick;
            public string MessageKey;
            public string Url;
            public QNChatMessage Message;
            public DateTime ObservedAt;
            public volatile bool Withdrawn;
            public Task<CacheDownloadResult> DownloadTask;
        }

        public const int CacheRetentionHours = 24;
        public const int RecentReferenceWindowSeconds = 90;
        private const int MaxCacheFiles = 300;
        private const long MaxCacheBytes = 512L * 1024L * 1024L;
        private const int MaxCacheRecords = 512;
        private const int MaxConcurrentDownloads = 2;
        private const int RecentMessageReferenceRetentionMinutes = 6;
        private const int DefaultMaxImageSizeMb = 5;
        private const int DefaultTimeoutSeconds = 45;

        private static readonly string[] AllowedMimeTypes =
        {
            "image/jpeg", "image/png", "image/webp", "image/gif"
        };

        private static readonly ConcurrentDictionary<string, CacheRecord> Records =
            new ConcurrentDictionary<string, CacheRecord>(StringComparer.Ordinal);
        internal static int CachedRecordCount { get { return Records.Count; } }
        private static readonly object RecordAdmissionSync = new object();
        private static readonly object CleanupSync = new object();
        private static readonly SemaphoreSlim DownloadSlots =
            new SemaphoreSlim(MaxConcurrentDownloads, MaxConcurrentDownloads);
        private static Timer _cleanupTimer;
        private static int _cleanupTimerStarted;
        private static DateTime _lastCleanup = DateTime.MinValue;

        internal static string CacheDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "QianniuAiBot",
                    "data",
                    "vision-cache");
            }
        }

        public static void Prime(QNChatMessage message, string messageText, string shopKey = null)
        {
            try
            {
                if (message == null) return;
                var url = VisionImageResolver.ExtractUrl(message);
                if (string.IsNullOrWhiteSpace(url)) return;

                var seller = GetSellerNick(message, shopKey);
                var buyer = GetBuyerNick(message, seller);
                var shop = ResolveShop(seller, shopKey);
                if (shop == null)
                {
                    Log.ErrorWithMaxCount("买家图片缓存无法确认唯一ShopKey，已阻止跨店缓存: seller=" + seller, 20);
                    return;
                }
                shopKey = shop.ShopKey;
                AiEndpointConfig endpoint;
                using (ShopSettingsScope.Enter(shop))
                {
                    var endpoints = AiEndpointStore.GetVisionEnabledEndpoints();
                    endpoint = endpoints == null ? null : endpoints.FirstOrDefault();
                }

                EnsureCleanupTimer();
                var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);
                var key = BuildKey(shopKey, messageKey, url);
                CacheRecord record;
                var created = false;

                // A locked admission path prevents ConcurrentDictionary value factories from
                // starting duplicate downloads under contention and keeps retained records bounded.
                lock (RecordAdmissionSync)
                {
                    if (!Records.TryGetValue(key, out record))
                    {
                        TrimCompletedRecordsForAdmission(DateTime.Now);
                        if (Records.Count >= MaxCacheRecords)
                        {
                            Log.ErrorWithMaxCount(
                                "买家图片缓存达到记录上限，暂缓建立新图片缓存任务: records="
                                + Records.Count + ", limit=" + MaxCacheRecords,
                                10);
                            return;
                        }

                        record = new CacheRecord
                        {
                            Key = key,
                            ShopKey = shopKey,
                            SellerNick = seller,
                            BuyerNick = buyer,
                            MessageKey = messageKey,
                            Url = url,
                            Message = message,
                            ObservedAt = GetObservedAt(message)
                        };
                        record.DownloadTask = DownloadAndPersistAsync(record, endpoint);
                        created = Records.TryAdd(key, record);
                    }
                }

                if (created)
                {
                    Log.Info("买家图片本地缓存已启动: seller=" + seller
                        + ", buyer=" + buyer
                        + ", messageKey=" + Short(messageKey, 100));
                }
                CleanupIfNeeded();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("启动买家图片本地缓存失败：" + ex.Message, 20);
            }
        }

        private static void EnsureCleanupTimer()
        {
            if (Interlocked.CompareExchange(ref _cleanupTimerStarted, 1, 0) != 0) return;
            _cleanupTimer = new Timer(
                _ => CleanupIfNeeded(),
                null,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1));
        }

        private static void TrimCompletedRecordsForAdmission(DateTime now)
        {
            var recentCutoff = now.AddMinutes(-RecentMessageReferenceRetentionMinutes);
            var exactEntries = (ICollection<KeyValuePair<string, CacheRecord>>)Records;
            var candidates = Records
                .Where(x => x.Value == null
                    || (x.Value.ObservedAt < recentCutoff
                        && x.Value.DownloadTask != null
                        && x.Value.DownloadTask.IsCompleted))
                .OrderBy(x => x.Value == null ? DateTime.MinValue : x.Value.ObservedAt)
                .ToArray();

            foreach (var pair in candidates)
            {
                if (Records.Count < MaxCacheRecords) break;
                exactEntries.Remove(pair);
            }
        }

        public static void MarkLatestBuyerImageWithdrawn(QNChatMessage notice, string messageText, string shopKey = null)
        {
            try
            {
                var seller = GetSellerNick(notice, shopKey);
                var buyer = GetBuyerNick(notice, seller);
                var shop = ResolveShop(seller, shopKey);
                if (shop == null || string.IsNullOrWhiteSpace(buyer))
                {
                    Log.ErrorWithMaxCount("买家撤回图片缺少唯一ShopKey/会话身份，未跨店修改缓存状态。", 20);
                    return;
                }

                var cutoff = DateTime.Now.AddMinutes(-5);
                var candidate = Records.Values
                    .Where(x => x != null && string.Equals(x.ShopKey, shop.ShopKey, StringComparison.Ordinal))
                    .Where(x => !x.Withdrawn && x.ObservedAt >= cutoff)
                    .Where(x => SameConversation(seller, buyer, x.SellerNick, x.BuyerNick))
                    .OrderByDescending(x => x.ObservedAt)
                    .FirstOrDefault();
                if (candidate == null)
                {
                    Log.Info("检测到买家撤回图片，但未找到近期图片缓存记录: seller=" + seller + ", buyer=" + buyer);
                    return;
                }

                candidate.Withdrawn = true;
                Log.Info("已标记买家图片撤回，视觉分析仍将继续: seller=" + seller
                    + ", buyer=" + buyer
                    + ", messageKey=" + Short(candidate.MessageKey, 100)
                    + ", cacheComplete=" + IsTaskSuccessful(candidate.DownloadTask));
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("记录买家图片撤回失败：" + ex.Message, 20);
            }
        }

        public static async Task<VisionImageResult> ResolveAsync(
            QNChatMessage message,
            AiEndpointConfig endpoint,
            CancellationToken cancellationToken,
            string shopKey = null)
        {
            if (message == null) return Fail("图片消息为空");
            var url = VisionImageResolver.ExtractUrl(message);
            if (string.IsNullOrWhiteSpace(url)) return Fail("图片 URL 不存在");

            var seller = GetSellerNick(message, shopKey);
            var shop = ResolveShop(seller, shopKey);
            if (shop == null) return Fail("无法确认图片消息所属ShopKey，已阻止跨店缓存读取");
            shopKey = shop.ShopKey;
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, string.Empty);
            var record = FindRecord(messageKey, url, shopKey);
            if (record == null)
            {
                Prime(message, string.Empty, shopKey);
                record = FindRecord(messageKey, url, shopKey);
            }
            if (record == null) return Fail("图片本地缓存任务未建立");

            CacheDownloadResult cached;
            try
            {
                cached = await AwaitWithoutCancellingSharedTask(record.DownloadTask, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail("图片本地缓存失败：" + ex.Message);
            }

            if (cached == null || !cached.Success || string.IsNullOrWhiteSpace(cached.FilePath))
            {
                return Fail(cached == null || string.IsNullOrWhiteSpace(cached.Error)
                    ? "图片未能完整缓存到本地"
                    : cached.Error);
            }
            if (!File.Exists(cached.FilePath)) return Fail("图片本地缓存文件不存在");

            byte[] bytes;
            try { bytes = File.ReadAllBytes(cached.FilePath); }
            catch (Exception ex) { return Fail("读取图片本地缓存失败：" + ex.Message); }
            if (bytes == null || bytes.Length < 12) return Fail("图片本地缓存数据不完整");

            var detectedMime = VisionImageResolver.DetectMime(bytes);
            if (string.IsNullOrWhiteSpace(detectedMime)
                || !AllowedMimeTypes.Contains(detectedMime, StringComparer.OrdinalIgnoreCase))
            {
                return Fail("图片本地缓存格式不受支持");
            }

            return new VisionImageResult
            {
                Success = true,
                ImageUrl = "data:" + detectedMime + ";base64," + Convert.ToBase64String(bytes),
                MimeType = detectedMime,
                Bytes = bytes.LongLength,
                LocalCachePath = cached.FilePath,
                FromLocalCache = true,
                CacheComplete = true
            };
        }

        public static bool IsWithdrawn(string seller, string buyer, string messageKey, QNChatMessage message, string shopKey = null)
        {
            var shop = ResolveShop(seller, shopKey);
            if (shop == null) return true;
            var record = FindRecord(messageKey, VisionImageResolver.ExtractUrl(message), shop.ShopKey);
            if (record != null) return record.Withdrawn;
            return Records.Values
                .Where(x => x != null && x.Withdrawn
                    && string.Equals(x.ShopKey, shop.ShopKey, StringComparison.Ordinal))
                .Where(x => SameConversation(seller, buyer, x.SellerNick, x.BuyerNick))
                .OrderByDescending(x => x.ObservedAt)
                .Take(1)
                .Any();
        }

        public static bool HasCompleteCache(string messageKey, QNChatMessage message, string shopKey = null)
        {
            var seller = GetSellerNick(message, shopKey);
            var shop = ResolveShop(seller, shopKey);
            if (shop == null) return false;
            var record = FindRecord(messageKey, VisionImageResolver.ExtractUrl(message), shop.ShopKey);
            return record != null && IsTaskSuccessful(record.DownloadTask);
        }

        public static bool TryGetRecentReference(
            string seller,
            string buyer,
            TimeSpan window,
            out VisionCachedImageReference reference,
            string shopKey = null)
        {
            reference = null;
            var shop = ResolveShop(seller, shopKey);
            if (shop == null) return false;
            var cutoff = DateTime.Now - window;
            var record = Records.Values
                .Where(x => x != null && x.Message != null && x.ObservedAt >= cutoff
                    && string.Equals(x.ShopKey, shop.ShopKey, StringComparison.Ordinal))
                .Where(x => SameConversation(seller, buyer, x.SellerNick, x.BuyerNick))
                .OrderByDescending(x => x.ObservedAt)
                .FirstOrDefault();
            if (record == null) return false;

            reference = new VisionCachedImageReference
            {
                ShopKey = record.ShopKey,
                SellerNick = record.SellerNick,
                BuyerNick = record.BuyerNick,
                MessageKey = record.MessageKey,
                Message = record.Message,
                ObservedAt = record.ObservedAt,
                Withdrawn = record.Withdrawn,
                CacheComplete = IsTaskSuccessful(record.DownloadTask)
            };
            return true;
        }

        private static CacheRecord FindRecord(string messageKey, string url, string shopKey)
        {
            CacheRecord record;
            var key = BuildKey(shopKey, messageKey, url);
            if (Records.TryGetValue(key, out record)) return record;
            if (string.IsNullOrWhiteSpace(url)) return null;
            return Records.Values
                .Where(x => x != null
                    && string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal)
                    && string.Equals(x.Url, url, StringComparison.Ordinal))
                .OrderByDescending(x => x.ObservedAt)
                .FirstOrDefault();
        }

        private static async Task<CacheDownloadResult> DownloadAndPersistAsync(
            CacheRecord record,
            AiEndpointConfig endpoint)
        {
            var result = new CacheDownloadResult();
            try
            {
                Uri uri;
                if (!Uri.TryCreate(record.Url, UriKind.Absolute, out uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    result.Error = "图片 URL 协议不受支持";
                    return result;
                }

                var configuredMb = endpoint == null ? DefaultMaxImageSizeMb : endpoint.MaxImageSizeMb;
                var configuredTimeout = endpoint == null ? DefaultTimeoutSeconds : endpoint.VisionTimeoutSeconds;
                var maxBytes = Math.Max(1, Math.Min(20, configuredMb)) * 1024L * 1024L;
                using (var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(Math.Max(10, Math.Min(180, configuredTimeout)))))
                {
                    // Image downloads are a native-memory and LOH pressure source. Keep only
                    // two downloads active; waiting requests remain bounded by MaxCacheRecords.
                    await DownloadSlots.WaitAsync(timeout.Token);
                    try
                    {
                        using (var http = new HttpClient())
                        using (var response = await http.GetAsync(
                            uri,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeout.Token))
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                result.Error = "图片下载失败：HTTP " + (int)response.StatusCode;
                                return result;
                            }
                            if (response.Content.Headers.ContentLength.HasValue
                                && response.Content.Headers.ContentLength.Value > maxBytes)
                            {
                                result.Error = "图片超过大小限制";
                                return result;
                            }

                            var bytes = await ReadWithLimitAsync(response.Content, maxBytes, timeout.Token);
                            if (bytes == null)
                            {
                                result.Error = "图片超过大小限制或下载不完整";
                                return result;
                            }
                            var mime = VisionImageResolver.DetectMime(bytes);
                            if (string.IsNullOrWhiteSpace(mime)
                                || !AllowedMimeTypes.Contains(mime, StringComparer.OrdinalIgnoreCase))
                            {
                                result.Error = "图片数据损坏或格式不支持";
                                return result;
                            }

                            var headerMime = response.Content.Headers.ContentType == null
                                ? string.Empty
                                : (response.Content.Headers.ContentType.MediaType ?? string.Empty).ToLowerInvariant();
                            if (!string.IsNullOrWhiteSpace(headerMime)
                                && !AllowedMimeTypes.Contains(headerMime, StringComparer.OrdinalIgnoreCase))
                            {
                                result.Error = "图片 MIME 类型不支持";
                                return result;
                            }
                            if (!string.IsNullOrWhiteSpace(headerMime)
                                && !string.Equals(headerMime, mime, StringComparison.OrdinalIgnoreCase))
                            {
                                result.Error = "图片 MIME 与实际内容不一致";
                                return result;
                            }

                            Directory.CreateDirectory(CacheDirectory);
                            var path = Path.Combine(
                                CacheDirectory,
                                Sha256(record.Key + "|" + record.Url) + ExtensionForMime(mime));
                            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                            File.WriteAllBytes(temp, bytes);
                            if (File.Exists(path)) File.Delete(path);
                            File.Move(temp, path);

                            result.Success = true;
                            result.FilePath = path;
                            result.MimeType = mime;
                            result.Bytes = bytes.LongLength;
                            Log.Info("买家图片已完整缓存到本地: seller=" + record.SellerNick
                                + ", buyer=" + record.BuyerNick
                                + ", bytes=" + result.Bytes
                                + ", path=" + path);
                            CleanupIfNeeded();
                            return result;
                        }
                    }
                    finally
                    {
                        DownloadSlots.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result.Error = "图片本地缓存超时";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "图片本地缓存异常：" + ex.Message;
                return result;
            }
        }

        private static async Task<byte[]> ReadWithLimitAsync(HttpContent content, long limit, CancellationToken token)
        {
            using (var input = await content.ReadAsStreamAsync())
            using (var output = new MemoryStream())
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, 0, buffer.Length, token);
                    if (read <= 0) break;
                    if (output.Length + read > limit) return null;
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        private static async Task<T> AwaitWithoutCancellingSharedTask<T>(Task<T> task, CancellationToken token)
        {
            if (task == null) return default(T);
            if (!token.CanBeCanceled) return await task;
            var cancellation = Task.Delay(Timeout.Infinite, token);
            var completed = await Task.WhenAny(task, cancellation);
            if (completed != task) throw new OperationCanceledException(token);
            return await task;
        }

        private static bool IsTaskSuccessful(Task<CacheDownloadResult> task)
        {
            if (task == null || !task.IsCompleted || task.IsCanceled || task.IsFaulted) return false;
            try
            {
                var result = task.Result;
                return result != null && result.Success && File.Exists(result.FilePath);
            }
            catch { return false; }
        }

        private static void CleanupIfNeeded()
        {
            if (DateTime.Now - _lastCleanup < TimeSpan.FromMinutes(10)) return;
            lock (CleanupSync)
            {
                if (DateTime.Now - _lastCleanup < TimeSpan.FromMinutes(10)) return;
                _lastCleanup = DateTime.Now;
                try
                {
                    Directory.CreateDirectory(CacheDirectory);
                    var files = new DirectoryInfo(CacheDirectory)
                        .GetFiles()
                        .Where(x => x != null && !x.Name.Contains(".tmp-"))
                        .OrderByDescending(x => x.LastWriteTimeUtc)
                        .ToList();
                    var cutoff = DateTime.UtcNow.AddHours(-CacheRetentionHours);
                    long keptBytes = 0;
                    var keptCount = 0;
                    foreach (var file in files)
                    {
                        var keep = file.LastWriteTimeUtc >= cutoff
                            && keptCount < MaxCacheFiles
                            && keptBytes + file.Length <= MaxCacheBytes;
                        if (keep)
                        {
                            keptCount++;
                            keptBytes += file.Length;
                        }
                        else
                        {
                            try { file.Delete(); } catch { }
                        }
                    }

                    var now = DateTime.Now;
                    var messageCutoff = now.AddMinutes(-RecentMessageReferenceRetentionMinutes);
                    var exactEntries = (ICollection<KeyValuePair<string, CacheRecord>>)Records;
                    foreach (var pair in Records.ToArray())
                    {
                        var record = pair.Value;
                        if (record == null)
                        {
                            exactEntries.Remove(pair);
                            continue;
                        }

                        // Follow-up image context is only consumed within the five-minute
                        // manual-learning window. Release the original message object after
                        // a one-minute safety margin; keep the small withdrawal/cache metadata.
                        if (record.Message != null && record.ObservedAt < messageCutoff)
                            Interlocked.Exchange(ref record.Message, null);
                    }

                    var recordCutoff = now.AddHours(-CacheRetentionHours);
                    foreach (var pair in Records.ToArray())
                    {
                        if (pair.Value == null || pair.Value.ObservedAt < recordCutoff)
                            exactEntries.Remove(pair);
                    }

                    TrimCompletedRecordsForAdmission(now);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("清理买家图片本地缓存失败：" + ex.Message, 10);
                }
            }
        }

        private static string BuildKey(string shopKey, string messageKey, string url)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0) return string.Empty;
            if (!string.IsNullOrWhiteSpace(messageKey))
                return shopKey + "|message:" + messageKey.Trim();
            return shopKey + "|url:" + Sha256(url ?? string.Empty);
        }

        private static ShopContext ResolveShop(string seller, string shopKey)
        {
            seller = (seller ?? string.Empty).Trim();
            shopKey = (shopKey ?? string.Empty).Trim();
            if (seller.Length == 0) return null;
            try
            {
                var current = ShopSettingsScope.Current;
                ShopContext shop = shopKey.Length > 0
                    ? ShopContextLocator.ResolveRuntimeByShopKey(shopKey)
                    : (current != null
                        ? ShopContextLocator.ResolveCurrentOrBySellerNick(seller)
                        : ShopContextLocator.ResolveRuntimeBySellerNick(seller));
                if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                    || !string.Equals((shop.SellerNick ?? string.Empty).Trim(), seller, StringComparison.Ordinal)
                    || (shopKey.Length > 0 && !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal))
                    || (current != null && !string.Equals(current.ShopKey, shop.ShopKey, StringComparison.Ordinal)))
                    return null;
                return shop;
            }
            catch { return null; }
        }

        private static string Sha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                return string.Concat(bytes.Select(x => x.ToString("x2")));
            }
        }

        private static string ExtensionForMime(string mime)
        {
            if (string.Equals(mime, "image/png", StringComparison.OrdinalIgnoreCase)) return ".png";
            if (string.Equals(mime, "image/webp", StringComparison.OrdinalIgnoreCase)) return ".webp";
            if (string.Equals(mime, "image/gif", StringComparison.OrdinalIgnoreCase)) return ".gif";
            return ".jpg";
        }

        private static DateTime GetObservedAt(QNChatMessage message)
        {
            if (message == null) return DateTime.Now;
            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(message.sendTime, out parsed)) return parsed.LocalDateTime;
            long raw;
            if (long.TryParse(message.sendTime, out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) return DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    if (raw > 100000000000L) return DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    if (raw > 1000000000L) return DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                }
                catch { }
            }
            return DateTime.Now;
        }

        private static string GetSellerNick(QNChatMessage message, string shopKey = null)
        {
            if (message != null && message.loginid != null && !string.IsNullOrWhiteSpace(message.loginid.nick))
                return message.loginid.nick.Trim();

            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length > 0)
            {
                try
                {
                    var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
                    if (shop != null) return (shop.SellerNick ?? string.Empty).Trim();
                }
                catch { return string.Empty; }
            }

            var current = ShopSettingsScope.Current;
            if (current != null && !string.IsNullOrWhiteSpace(current.SellerNick))
                return current.SellerNick.Trim();
            return message == null || message.toid == null
                ? string.Empty
                : (message.toid.nick ?? string.Empty).Trim();
        }

        private static string GetBuyerNick(QNChatMessage message, string seller)
        {
            if (message == null) return string.Empty;
            var from = message.fromid == null ? string.Empty : (message.fromid.nick ?? string.Empty).Trim();
            var to = message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(from) && !string.Equals(from, seller, StringComparison.Ordinal)) return from;
            if (!string.IsNullOrWhiteSpace(to) && !string.Equals(to, seller, StringComparison.Ordinal)) return to;
            return string.Empty;
        }

        private static bool SameConversation(
            string seller,
            string buyer,
            string otherSeller,
            string otherBuyer)
        {
            if (!string.Equals((seller ?? string.Empty).Trim(), (otherSeller ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var left = (buyer ?? string.Empty).Trim();
            var right = (otherBuyer ?? string.Empty).Trim();
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
            try { return BuyerIdentityAliasService.AreEquivalent(seller, left, right); }
            catch { return false; }
        }

        private static VisionImageResult Fail(string error)
        {
            return new VisionImageResult { Success = false, Error = error, CacheComplete = false };
        }

        private static string Short(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
