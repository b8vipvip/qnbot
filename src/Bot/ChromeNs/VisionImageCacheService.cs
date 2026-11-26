using Bot.ChatRecord;
using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
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

        private sealed class BrowserImageFetchResponse
        {
            public bool ok { get; set; }
            public string base64 { get; set; }
            public string mime { get; set; }
            public string error { get; set; }
            public string resolvedUrl { get; set; }
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
            public CDPClient BrowserClient;
            public int DownloadAttempts;
            public Task<CacheDownloadResult> DownloadTask;
            public Task<CacheDownloadResult> OcrScheduledDownloadTask;
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

        public static void Prime(
            QNChatMessage message,
            string messageText,
            string shopKey = null,
            CDPClient browserClient = null)
        {
            try
            {
                if (message == null) return;
                // fileId may be an opaque Qianniu media identifier rather than an HTTP URL.
                // Keep the message record even without a URL so the authenticated browser session
                // can resolve the actual image source from its own DOM/runtime.
                var url = VisionImageResolver.ExtractUrl(message);

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
                if (string.IsNullOrWhiteSpace(messageKey) && string.IsNullOrWhiteSpace(url))
                {
                    Log.ErrorWithMaxCount("买家图片缺少可用消息身份和媒体地址，已拒绝建立可能串图的缓存记录。", 20);
                    return;
                }
                var key = BuildKey(shopKey, messageKey, url);
                CacheRecord record;
                var created = false;
                Task<CacheDownloadResult> downloadTaskToPrewarm = null;

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
                            BrowserClient = browserClient,
                            DownloadAttempts = 1,
                            ObservedAt = GetObservedAt(message)
                        };
                        record.DownloadTask = DownloadAndPersistAsync(record, endpoint);
                        created = Records.TryAdd(key, record);
                        if (created) downloadTaskToPrewarm = record.DownloadTask;
                    }
                    else
                    {
                        // A failed first attempt must not permanently poison the message key.
                        // The vision pipeline calls Prime again with the authoritative CDP client;
                        // refresh the media URL/message snapshot before its one bounded retry.
                        if (browserClient != null && !browserClient.IsInvalidated
                            && !browserClient.IsShopIdentityRetired
                            && string.Equals((browserClient.ShopKey ?? string.Empty).Trim(),
                                shopKey, StringComparison.Ordinal))
                            record.BrowserClient = browserClient;

                        var taskPending = record.DownloadTask != null && !record.DownloadTask.IsCompleted;
                        var canRetry = (record.DownloadTask == null
                                || (record.DownloadTask.IsCompleted && !IsTaskSuccessful(record.DownloadTask)))
                            && record.DownloadAttempts < 2;

                        // A newer signed URL/message snapshot can arrive while the first download
                        // is still running. Preserve it now so the bounded retry does not reuse
                        // the stale URL that the in-flight attempt may already have captured.
                        if ((taskPending || canRetry) && !string.IsNullOrWhiteSpace(url))
                            record.Url = url;
                        if ((taskPending || canRetry) && message != null
                            && SameConversation(seller, buyer, record.SellerNick, record.BuyerNick))
                            record.Message = message;

                        if (canRetry)
                        {
                            record.DownloadAttempts++;
                            record.DownloadTask = DownloadAndPersistAsync(record, endpoint);
                            created = true;
                            downloadTaskToPrewarm = record.DownloadTask;
                        }
                        else if (record.Message == null)
                        {
                            record.Message = message;
                        }
                    }
                }

                if (created)
                {
                    StartOcrPrewarm(record, downloadTaskToPrewarm);
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

        private static void StartOcrPrewarm(
            CacheRecord record,
            Task<CacheDownloadResult> downloadTask)
        {
            if (record == null || downloadTask == null) return;
            lock (record)
            {
                if (ReferenceEquals(record.OcrScheduledDownloadTask, downloadTask)) return;
                record.OcrScheduledDownloadTask = downloadTask;
            }

            // This is deliberately detached from the buyer reply generation. A slow text-AI,
            // short-rule, or send path must not postpone OCR after the image bytes are durable.
            Task.Run(async () =>
            {
                var startedAt = DateTime.UtcNow;
                try
                {
                    var downloaded = await downloadTask.ConfigureAwait(false);
                    var downloadMs = Math.Max(0, (long)(DateTime.UtcNow - startedAt).TotalMilliseconds);
                    if (downloaded == null || !downloaded.Success
                        || string.IsNullOrWhiteSpace(downloaded.FilePath)
                        || !File.Exists(downloaded.FilePath))
                    {
                        Log.Info("图片缓存未成功，跳过独立OCR预热: seller=" + record.SellerNick
                            + ", buyer=" + record.BuyerNick
                            + ", messageKey=" + Short(record.MessageKey, 100)
                            + ", reason=" + (downloaded == null ? "empty_download_result" : Short(downloaded.Error, 120)));
                        return;
                    }

                    var shop = ShopContextLocator.ResolveRuntimeByShopKey(record.ShopKey);
                    if (shop == null
                        || !string.Equals((shop.SellerNick ?? string.Empty).Trim(),
                            (record.SellerNick ?? string.Empty).Trim(), StringComparison.Ordinal))
                    {
                        Log.ErrorWithMaxCount("图片缓存后OCR预热无法确认原始ShopKey，已阻止跨店上传: shopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", record.ShopKey), 20);
                        return;
                    }

                    bool configured;
                    using (ShopSettingsScope.Enter(shop, false))
                    {
                        configured = LocalOcrService.IsServerOcrConfigured(shop.SellerNick, record.ShopKey);
                    }
                    if (!configured)
                    {
                        Log.Info("图片缓存后跳过OCR预热：本店未配置服务端OCR: shopRef="
                            + MyWebSocketServer.DiagnosticRef("shop", record.ShopKey)
                            + ", messageKey=" + Short(record.MessageKey, 100));
                        return;
                    }

                    Log.Info("图片缓存完成，独立OCR预热立即开始: seller=" + record.SellerNick
                        + ", buyer=" + record.BuyerNick
                        + ", messageKey=" + Short(record.MessageKey, 100)
                        + ", bytes=" + downloaded.Bytes
                        + ", downloadMs=" + downloadMs
                        + ", shopRef=" + MyWebSocketServer.DiagnosticRef("shop", record.ShopKey));
                    var ocr = await LocalOcrService.TryRecognizeAsync(
                        downloaded.FilePath,
                        record.SellerNick,
                        record.ShopKey,
                        CancellationToken.None).ConfigureAwait(false);
                    Log.Info("图片独立OCR预热结束: seller=" + record.SellerNick
                        + ", buyer=" + record.BuyerNick
                        + ", messageKey=" + Short(record.MessageKey, 100)
                        + ", success=" + (ocr != null && ocr.Success)
                        + ", chars=" + (ocr == null || ocr.Text == null ? 0 : ocr.Text.Length)
                        + ", confidence=" + (ocr == null ? 0d : ocr.Confidence).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                        + ", elapsedMs=" + (ocr == null ? 0 : ocr.ElapsedMs)
                        + ", error=" + (ocr == null ? "empty_ocr_result" : Short(ocr.Error, 160))
                        + ", shopRef=" + MyWebSocketServer.DiagnosticRef("shop", record.ShopKey));
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("图片独立OCR预热异常: seller=" + record.SellerNick
                        + ", buyer=" + record.BuyerNick
                        + ", messageKey=" + Short(record.MessageKey, 100)
                        + ", error=" + Short(ex.Message, 220), 20);
                }
            });
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

                // Use the withdrawal event's own timestamp, not processing time: delayed or
                // replayed notices must never mark an image that arrived after the notice.
                var noticeAt = GetObservedAt(notice);
                var cutoff = noticeAt.AddMinutes(-5);
                var candidate = Records.Values
                    .Where(x => x != null && string.Equals(x.ShopKey, shop.ShopKey, StringComparison.Ordinal))
                    .Where(x => !x.Withdrawn
                        && x.ObservedAt >= cutoff
                        && x.ObservedAt <= noticeAt.AddSeconds(2))
                    .Where(x => SameConversation(seller, buyer, x.SellerNick, x.BuyerNick))
                    .OrderByDescending(x => x.ObservedAt)
                    .ThenBy(x => x.MessageKey, StringComparer.Ordinal)
                    .FirstOrDefault();
                // The in-memory cache is intentionally short-lived. After a Bot restart,
                // recover the same recent image identity from the durable OCR/vision observation
                // store before giving up; otherwise a late withdrawal notice could be lost.
                var messageKey = candidate == null ? string.Empty : (candidate.MessageKey ?? string.Empty);
                if (messageKey.Length == 0)
                    messageKey = VisualKnowledgeLearningService.FindLatestObservationMessageKey(
                        shop.ShopKey, seller, buyer, cutoff, noticeAt.AddSeconds(2));
                if (messageKey.Length == 0)
                {
                    Log.Info("检测到买家撤回图片，但内存缓存和持久化视觉观察均未找到可确认的原图消息: seller="
                        + seller + ", buyer=" + buyer
                        + ", shopRef=" + MyWebSocketServer.DiagnosticRef("shop", shop.ShopKey));
                    return;
                }

                if (candidate != null) candidate.Withdrawn = true;
                try
                {
                    VisualKnowledgeLearningService.MarkObservationWithdrawn(
                        shop.ShopKey, seller, buyer, messageKey);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("同步图片撤回状态到视觉学习候选失败：" + ex.Message, 10);
                }
                Log.Info("已标记买家图片撤回，视觉分析仍将继续: seller=" + seller
                    + ", buyer=" + buyer
                    + ", messageKey=" + Short(messageKey, 100)
                    + ", cacheComplete=" + (candidate != null && IsTaskSuccessful(candidate.DownloadTask)));
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
            string shopKey = null,
            CDPClient browserClient = null)
        {
            if (message == null) return Fail("图片消息为空");
            var url = VisionImageResolver.ExtractUrl(message);

            var seller = GetSellerNick(message, shopKey);
            var shop = ResolveShop(seller, shopKey);
            if (shop == null) return Fail("无法确认图片消息所属ShopKey，已阻止跨店缓存读取");
            shopKey = shop.ShopKey;
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, GetMessageText(message));
            Prime(message, GetMessageText(message), shopKey, browserClient);
            var record = FindRecord(messageKey, url, shopKey, seller, GetBuyerNick(message, seller));
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
            var record = FindRecord(
                messageKey, VisionImageResolver.ExtractUrl(message), shop.ShopKey, seller, buyer);
            // The in-memory cache is deliberately short-lived and is rebuilt after restart.
            // The durable observation tombstone is the source of truth for replayed messages.
            // Keep the message-key guard so a withdrawn earlier image cannot suppress a
            // different later image in the same conversation.
            if (!string.IsNullOrWhiteSpace(messageKey)
                && VisualKnowledgeLearningService.IsObservationWithdrawn(
                    shop.ShopKey, seller, buyer, messageKey))
                return true;
            return record != null && record.Withdrawn;
        }

        internal static bool IsMessageKeyWithdrawn(
            string seller, string buyer, string messageKey, string shopKey)
        {
            if (string.IsNullOrWhiteSpace(messageKey)) return false;
            var shop = ResolveShop(seller, shopKey);
            if (shop == null || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal))
                return true;
            var record = FindRecord(messageKey, string.Empty, shop.ShopKey, seller, buyer);
            return (record != null && record.Withdrawn)
                || VisualKnowledgeLearningService.IsObservationWithdrawn(
                    shop.ShopKey, seller, buyer, messageKey);
        }

        public static async Task<string> TryGetCachedImagePathAsync(
            string shopKey, string seller, string buyer, string messageKey)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            messageKey = (messageKey ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0 || buyer.Length == 0 || messageKey.Length == 0)
                return string.Empty;

            var shop = ResolveShop(seller, shopKey);
            if (shop == null || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal))
                return string.Empty;

            var record = Records.Values.FirstOrDefault(x => x != null
                && string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal)
                && string.Equals(x.MessageKey, messageKey, StringComparison.Ordinal)
                && SameConversation(seller, buyer, x.SellerNick, x.BuyerNick));
            if (record == null || record.Withdrawn || record.DownloadTask == null)
                return string.Empty;

            try
            {
                var cached = await AwaitWithoutCancellingSharedTask(record.DownloadTask, CancellationToken.None)
                    .ConfigureAwait(false);
                return cached != null && cached.Success && !string.IsNullOrWhiteSpace(cached.FilePath)
                    && File.Exists(cached.FilePath) ? cached.FilePath : string.Empty;
            }
            catch (Exception ex)
            {
                Log.Info("从买家消息缓存导入图片知识失败: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>
        /// Non-blocking lookup for the Web mirror. The normal sync loop retries on its next
        /// pass while the shared download task is still running; it never blocks message sync.
        /// </summary>
        public static string TryGetCompletedCachedImagePath(
            string shopKey, string seller, string buyer, string messageKey)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            buyer = (buyer ?? string.Empty).Trim();
            messageKey = (messageKey ?? string.Empty).Trim();
            if (shopKey.Length == 0 || seller.Length == 0 || buyer.Length == 0 || messageKey.Length == 0)
                return string.Empty;

            var shop = ResolveShop(seller, shopKey);
            if (shop == null || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal))
                return string.Empty;

            var record = Records.Values.FirstOrDefault(x => x != null
                && string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal)
                && (string.Equals(x.MessageKey, messageKey, StringComparison.Ordinal)
                    || messageKey.EndsWith("|" + (x.MessageKey ?? string.Empty), StringComparison.Ordinal))
                && SameConversation(seller, buyer, x.SellerNick, x.BuyerNick));
            if (record == null || record.Withdrawn || record.DownloadTask == null
                || !record.DownloadTask.IsCompleted || !IsTaskSuccessful(record.DownloadTask))
                return string.Empty;
            try
            {
                var cached = record.DownloadTask.GetAwaiter().GetResult();
                return cached != null && cached.Success && !string.IsNullOrWhiteSpace(cached.FilePath)
                    && File.Exists(cached.FilePath) ? cached.FilePath : string.Empty;
            }
            catch { return string.Empty; }
        }

        public static bool HasCompleteCache(string messageKey, QNChatMessage message, string shopKey = null)
        {
            var seller = GetSellerNick(message, shopKey);
            var shop = ResolveShop(seller, shopKey);
            if (shop == null) return false;
            var record = FindRecord(
                messageKey, VisionImageResolver.ExtractUrl(message), shop.ShopKey, seller, GetBuyerNick(message, seller));
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

        private static CacheRecord FindRecord(
            string messageKey, string url, string shopKey, string seller, string buyer)
        {
            CacheRecord record;
            var key = BuildKey(shopKey, messageKey, url);
            if (Records.TryGetValue(key, out record))
            {
                return record != null
                    && string.Equals(record.ShopKey, shopKey, StringComparison.Ordinal)
                    && SameConversation(seller, buyer, record.SellerNick, record.BuyerNick)
                    ? record
                    : null;
            }
            if (string.IsNullOrWhiteSpace(url)) return null;
            return Records.Values
                .Where(x => x != null
                    && string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal)
                    && SameConversation(seller, buyer, x.SellerNick, x.BuyerNick)
                    && string.Equals(x.Url, url, StringComparison.Ordinal))
                .OrderByDescending(x => x.ObservedAt)
                .FirstOrDefault();
        }

        private static async Task<CacheDownloadResult> DownloadAndPersistAsync(
            CacheRecord record,
            AiEndpointConfig endpoint)
        {
            var direct = await DownloadAndPersistDirectAsync(record, endpoint).ConfigureAwait(false);
            if (direct != null && direct.Success) return direct;

            var configuredMb = endpoint == null ? DefaultMaxImageSizeMb : endpoint.MaxImageSizeMb;
            var maxBytes = Math.Max(1, Math.Min(20, configuredMb)) * 1024L * 1024L;
            var browser = await DownloadAndPersistFromBrowserAsync(
                record, Math.Min(maxBytes, 8L * 1024L * 1024L), endpoint).ConfigureAwait(false);
            if (browser != null && browser.Success)
            {
                Log.Info("买家图片已通过千牛登录态补全下载并缓存: seller=" + record.SellerNick
                    + ", buyer=" + record.BuyerNick + ", bytes=" + browser.Bytes
                    + ", directFailure=" + Short(direct == null ? string.Empty : direct.Error, 120));
                return browser;
            }

            var failure = "图片获取失败：直连下载="
                + Short(direct == null ? "无结果" : direct.Error, 180)
                + "；千牛登录态下载="
                + Short(browser == null ? "无结果" : browser.Error, 180);
            Log.ErrorWithMaxCount("买家图片未能转存到客户端缓存，服务端OCR不会收到图片二进制: seller="
                + record.SellerNick + ", buyer=" + record.BuyerNick
                + ", shopRef=" + MyWebSocketServer.DiagnosticRef("shop", record.ShopKey)
                + ", messageKey=" + Short(record.MessageKey, 90)
                + ", reason=" + Short(failure, 320), 20);
            return new CacheDownloadResult { Error = failure };
        }

        private static async Task<CacheDownloadResult> DownloadAndPersistFromBrowserAsync(
            CacheRecord record,
            long maxBytes,
            AiEndpointConfig endpoint)
        {
            var result = new CacheDownloadResult();
            var client = record == null ? null : record.BrowserClient;
            if (client == null)
            {
                result.Error = "没有可用的千牛登录浏览器会话";
                return result;
            }
            if (client.IsInvalidated || client.IsShopIdentityRetired
                || !string.Equals((client.ShopKey ?? string.Empty).Trim(),
                    (record.ShopKey ?? string.Empty).Trim(), StringComparison.Ordinal))
            {
                result.Error = "千牛浏览器会话已失效或ShopKey不匹配";
                return result;
            }

            try
            {
                var message = record.Message;
                var original = message == null ? null : message.originalData;
                var args = new
                {
                    url = record.Url ?? string.Empty,
                    fileId = original == null ? string.Empty : (original.fileId ?? string.Empty),
                    messageId = message == null || message.mcode == null ? string.Empty : (message.mcode.messageId ?? string.Empty),
                    clientId = message == null || message.mcode == null ? string.Empty : (message.mcode.clientId ?? string.Empty),
                    wwMsgId = message == null || message.ext == null ? string.Empty : message.ext.ww_msgid.ToString(),
                    ccode = message == null || message.cid == null ? string.Empty : (message.cid.ccode ?? string.Empty),
                    maxBytes = Math.Max(1024L, maxBytes)
                };
                var expression = BuildBrowserImageFetchExpression(args);
                var responseText = await client.EvaluateExpressionAsync(
                    expression, "DownloadBuyerImageWithQianniuSession", 25000).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(responseText))
                {
                    result.Error = "千牛浏览器没有返回图片数据";
                    return result;
                }

                BrowserImageFetchResponse payload;
                try { payload = JsonConvert.DeserializeObject<BrowserImageFetchResponse>(responseText); }
                catch (Exception ex)
                {
                    result.Error = "千牛浏览器图片回执解析失败：" + ex.GetType().Name;
                    return result;
                }
                if (payload == null || !payload.ok || string.IsNullOrWhiteSpace(payload.base64))
                {
                    // A message-history lookup may have recovered the signed image URL even
                    // when page fetch is blocked by cross-origin policy. Retry that exact URL
                    // from the desktop process before declaring the image unavailable.
                    if (payload != null && IsHttpImageUrl(payload.resolvedUrl))
                    {
                        // Use a local URL override instead of mutating the shared cache record.
                        // A concurrent Prime may publish a newer signed URL while this direct
                        // fetch is in flight; restoring a captured old URL would overwrite it.
                        var resolvedDownload = await DownloadAndPersistDirectAsync(
                            record, endpoint, payload.resolvedUrl).ConfigureAwait(false);
                        if (resolvedDownload != null && resolvedDownload.Success)
                        {
                            Log.Info("千牛历史消息补全的签名图片URL已由客户端直连下载成功: seller="
                                + record.SellerNick + ", buyer=" + record.BuyerNick
                                + ", bytes=" + resolvedDownload.Bytes);
                            return resolvedDownload;
                        }
                    }
                    result.Error = payload == null || string.IsNullOrWhiteSpace(payload.error)
                        ? "千牛浏览器未能读取图片原始数据"
                        : Short(payload.error, 180);
                    return result;
                }

                byte[] bytes;
                try { bytes = Convert.FromBase64String(payload.base64); }
                catch
                {
                    result.Error = "千牛浏览器返回的图片编码无效";
                    return result;
                }
                if (bytes == null || bytes.Length < 12 || bytes.LongLength > maxBytes)
                {
                    result.Error = "千牛浏览器返回的图片大小不符合限制";
                    return result;
                }

                var sourceMime = VisionImageResolver.DetectMime(bytes);
                if (string.IsNullOrWhiteSpace(sourceMime))
                {
                    result.Error = "千牛浏览器返回的数据不是有效图片";
                    return result;
                }
                if (!string.IsNullOrWhiteSpace(payload.mime)
                    && payload.mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(payload.mime, sourceMime, StringComparison.OrdinalIgnoreCase))
                {
                    result.Error = "千牛浏览器图片MIME与实际内容不一致";
                    return result;
                }

                byte[] normalizedBytes;
                string detectedMime;
                string normalizeError;
                if (!VisionImageResolver.TryNormalizeForVision(
                    bytes, out normalizedBytes, out detectedMime, out normalizeError))
                {
                    result.Error = "千牛浏览器图片格式无法用于OCR/视觉识别：" + normalizeError;
                    return result;
                }
                if (normalizedBytes.LongLength > maxBytes)
                {
                    result.Error = "图片转换为通用格式后超过大小限制";
                    return result;
                }
                bytes = normalizedBytes;

                Directory.CreateDirectory(CacheDirectory);
                var path = Path.Combine(
                    CacheDirectory,
                    Sha256(record.Key + "|" + record.Url + "|" + record.MessageKey) + ExtensionForMime(detectedMime));
                var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);

                result.Success = true;
                result.FilePath = path;
                result.MimeType = detectedMime;
                result.Bytes = bytes.LongLength;
                Log.Info("买家图片已从当前千牛登录会话读取并缓存: seller=" + record.SellerNick
                    + ", buyer=" + record.BuyerNick + ", bytes=" + result.Bytes
                    + ", messageKey=" + Short(record.MessageKey, 90));
                CleanupIfNeeded();
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "千牛登录态图片读取异常：" + ex.GetType().Name + " " + Short(ex.Message, 160);
                return result;
            }
        }

        private static bool IsHttpImageUrl(string value)
        {
            Uri uri;
            return !string.IsNullOrWhiteSpace(value)
                && Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static string BuildBrowserImageFetchExpression(object args)
        {
            var json = JsonConvert.SerializeObject(args);
            return "(async function(a){"
                + "const max=Number(a.maxBytes)||8388608;"
                + "const tokens=[a.fileId,a.messageId,a.clientId,a.wwMsgId].filter(Boolean).map(String);"
                + "const candidates=[];"
                + "function add(u,img){if(!u)return;u=String(u).trim();"
                + "try{if(!/^(https?:|blob:|data:)/i.test(u)){if(u.indexOf('//')===0)u=location.protocol+u;"
                + "else if(u.charAt(0)==='/')u=new URL(u,location.href).href;else return;}}catch(e){return;}"
                + "if(!candidates.some(x=>x.url===u))candidates.push({url:u,img:img||null});}"
                + "add(a.url,null);"
                + "function matches(m){m=m||{};const orig=m.originBanamaMessage?m.originBanamaMessage:m;"
                + "const mc=orig.mcode||m.mcode||{};const ex=orig.ext||m.ext||{};const od=orig.originalData||m.originalData||{};"
                + "const vals=[mc.messageId,mc.clientId,ex.ww_msgid,od.fileId,od.url].filter(Boolean).map(String);"
                + "return tokens.length>0&&tokens.some(t=>vals.some(v=>v===t||(t.length>=8&&v.length>12&&v.indexOf(t)>=0)));}"
                + "function addMessage(m){if(!matches(m))return;const orig=m&&m.originBanamaMessage?m.originBanamaMessage:m;"
                + "const od=orig&&orig.originalData?orig.originalData:{};add(od.url||od.fileId,null);}"
                + "try{const map=window._db&&window._db.msgDataMap;if(map){Array.from(map.values?map.values():Object.values(map)).forEach(arr=>"
                + "Array.from(arr||[]).forEach(addMessage));}}catch(e){}"
                + "try{if(a.ccode&&window.imsdk&&typeof window.imsdk.invoke==='function'){"
                + "const history=await Promise.race([window.imsdk.invoke('im.singlemsg.GetRemoteHisMsg',{cid:{ccode:a.ccode,type:1},"
                + "count:50,gohistory:1,msgid:'-1',msgtime:'-1'}),new Promise((_,reject)=>setTimeout(()=>reject(new Error('history_timeout')),5000))]);"
                + "const msgs=history&&history.result?history.result.msgs||[]:[];msgs.forEach(addMessage);}}catch(e){}"
                + "try{const imgs=Array.from(document.querySelectorAll('img'));"
                + "for(const img of imgs){const values=[img.currentSrc,img.src,img.getAttribute('data-src'),"
                + "img.getAttribute('data-original'),img.getAttribute('data-url'),img.getAttribute('data-file-id')];"
                + "let node=img;for(let depth=0;node&&depth<4;depth++,node=node.parentElement){"
                + "for(const attr of ['data-message-id','data-msgid','data-mid','data-id','data-file-id'])"
                + "values.push(node.getAttribute&&node.getAttribute(attr));}"
                + "if(tokens.length&&tokens.some(t=>values.some(v=>v&&(String(v)===t||(t.length>=8&&String(v).indexOf(t)>=0)))))add(img.currentSrc||img.src,img);"
                + "}}catch(e){}"
                + "async function fromUrl(u){const controller=new AbortController();const timer=setTimeout(()=>controller.abort(),20000);"
                + "try{const response=await fetch(u,{method:'GET',credentials:'include',"
                + "cache:'no-store',redirect:'follow',signal:controller.signal});"
                + "if(!response.ok)throw new Error('http_'+response.status);"
                + "const blob=await response.blob();if(!blob.size||blob.size>max)throw new Error('image_size');"
                + "const buffer=await blob.arrayBuffer();const bytes=new Uint8Array(buffer);let binary='';"
                + "for(let i=0;i<bytes.length;i+=0x8000)binary+=String.fromCharCode.apply(null,bytes.subarray(i,Math.min(i+0x8000,bytes.length)));"
                + "return {ok:true,base64:btoa(binary),mime:blob.type||''};}finally{clearTimeout(timer);}}"
                + "function fromCanvas(img){try{if(!img||!img.complete||!img.naturalWidth||!img.naturalHeight)return null;"
                + "const canvas=document.createElement('canvas');canvas.width=img.naturalWidth;canvas.height=img.naturalHeight;"
                + "const ctx=canvas.getContext('2d');if(!ctx)return null;ctx.drawImage(img,0,0);"
                + "const data=canvas.toDataURL('image/png');const b=data.substring(data.indexOf(',')+1);"
                + "if(!b||b.length>Math.ceil(max*4/3)+8)return null;return {ok:true,base64:b,mime:'image/png'};"
                + "}catch(e){return null;}}"
                + "let last='no_matching_image_source';let resolvedUrl='';"
                + "for(const item of candidates){resolvedUrl=item.url;try{return await fromUrl(item.url);}catch(e){last=String(e&&e.message||'browser_fetch_failed').slice(0,80);"
                + "const canvas=fromCanvas(item.img);if(canvas)return canvas;}}"
                + "return {ok:false,error:last,resolvedUrl:resolvedUrl};"
                + "})(" + json + ")";
        }

        private static async Task<CacheDownloadResult> DownloadAndPersistDirectAsync(
            CacheRecord record,
            AiEndpointConfig endpoint,
            string urlOverride = null)
        {
            var result = new CacheDownloadResult();
            try
            {
                var sourceUrl = string.IsNullOrWhiteSpace(urlOverride) ? record.Url : urlOverride;
                Uri uri;
                if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out uri)
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
                            var sourceMime = VisionImageResolver.DetectMime(bytes);
                            if (string.IsNullOrWhiteSpace(sourceMime))
                            {
                                result.Error = "图片数据损坏或格式不支持";
                                return result;
                            }

                            var headerMime = response.Content.Headers.ContentType == null
                                ? string.Empty
                                : (response.Content.Headers.ContentType.MediaType ?? string.Empty).ToLowerInvariant();
                            if (!string.IsNullOrWhiteSpace(headerMime)
                                && !string.Equals(headerMime, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(headerMime, sourceMime, StringComparison.OrdinalIgnoreCase))
                            {
                                result.Error = "图片 MIME 与实际内容不一致";
                                return result;
                            }

                            byte[] normalizedBytes;
                            string mime;
                            string normalizeError;
                            if (!VisionImageResolver.TryNormalizeForVision(
                                bytes, out normalizedBytes, out mime, out normalizeError))
                            {
                                result.Error = "图片格式无法用于OCR/视觉识别：" + normalizeError;
                                return result;
                            }
                            if (normalizedBytes.LongLength > maxBytes)
                            {
                                result.Error = "图片转换为通用格式后超过大小限制";
                                return result;
                            }
                            bytes = normalizedBytes;

                            Directory.CreateDirectory(CacheDirectory);
                            var path = Path.Combine(
                                CacheDirectory,
                                Sha256(record.Key + "|" + sourceUrl) + ExtensionForMime(mime));
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

        private static string GetMessageText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            try
            {
                if (message.originalData != null)
                {
                    var text = message.originalData.text ?? string.Empty;
                    if (message.originalData.header != null)
                        text += message.originalData.header.summary ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
            catch { }
            return (message.summary ?? string.Empty).Trim();
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
            if (message == null) return string.Empty;
            var loginNick = message.loginid == null ? string.Empty : (message.loginid.nick ?? string.Empty).Trim();
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length > 0)
            {
                try
                {
                    var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
                    if (shop == null || !DirectOrderIdentityResolver.IsMessageFromShop(message, shop))
                        return string.Empty;
                    return shop.SellerNick.Trim();
                }
                catch { return string.Empty; }
            }

            var current = ShopSettingsScope.Current;
            if (current != null && !string.IsNullOrWhiteSpace(current.SellerNick))
            {
                if (!DirectOrderIdentityResolver.IsMessageFromShop(message, current))
                    return string.Empty;
                return current.SellerNick.Trim();
            }
            if (loginNick.Length > 0)
            {
                var fromIsSeller = DirectOrderIdentityResolver.IdentityEquals(
                    message.fromid == null ? string.Empty : message.fromid.nick, loginNick);
                var toIsSeller = DirectOrderIdentityResolver.IdentityEquals(
                    message.toid == null ? string.Empty : message.toid.nick, loginNick);
                return fromIsSeller == toIsSeller ? string.Empty : loginNick;
            }
            return message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
        }

        private static string GetBuyerNick(QNChatMessage message, string seller)
        {
            if (message == null) return string.Empty;
            var from = message.fromid == null ? string.Empty : (message.fromid.nick ?? string.Empty).Trim();
            var to = message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
            var fromIsSeller = DirectOrderIdentityResolver.IdentityEquals(from, seller);
            var toIsSeller = DirectOrderIdentityResolver.IdentityEquals(to, seller);
            if (fromIsSeller == toIsSeller) return string.Empty;
            return fromIsSeller ? to : from;
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
