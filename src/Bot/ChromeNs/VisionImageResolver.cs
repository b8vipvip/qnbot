using Bot.ChatRecord;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class VisionImageResult
    {
        public bool Success { get; set; }
        public string ImageUrl { get; set; }
        public string MimeType { get; set; }
        public long Bytes { get; set; }
        public string Error { get; set; }
        public string LocalCachePath { get; set; }
        public bool FromLocalCache { get; set; }
        public bool CacheComplete { get; set; }
    }

    internal sealed class VisionImageResolver
    {
        private static readonly string[] AllowedMimeTypes = { "image/jpeg", "image/png", "image/webp", "image/gif" };

        public async Task<VisionImageResult> ResolveAsync(
            QNChatMessage message,
            AiEndpointConfig endpoint,
            CancellationToken cancellationToken,
            string shopKey = null,
            CDPClient browserClient = null)
        {
            // Images are cached as soon as the incoming message is observed. Vision always reads
            // the completed local copy so a buyer withdrawing the remote message cannot interrupt
            // an analysis that has already started.
            var cached = await VisionImageCacheService.ResolveAsync(message, endpoint, cancellationToken, shopKey, browserClient);
            if (cached != null && cached.Success) return cached;

            // ResolveAsync has already primed the message with the canonical message text and
            // authenticated browser session. A second identical resolution is a bounded retry;
            // never rebuild the cache key with an empty message string.
            cached = await VisionImageCacheService.ResolveAsync(message, endpoint, cancellationToken, shopKey, browserClient);
            return cached ?? Fail("图片未能完整缓存到本地");
        }

        internal static string ExtractUrl(QNChatMessage message)
        {
            if (message == null || message.originalData == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(message.originalData.url)) return message.originalData.url.Trim();
            return string.IsNullOrWhiteSpace(message.originalData.fileId) ? string.Empty : message.originalData.fileId.Trim();
        }

        internal static bool LooksLikeImage(byte[] bytes, string mime)
        {
            var detected = DetectMime(bytes);
            return !string.IsNullOrWhiteSpace(detected)
                && string.Equals(detected, mime, StringComparison.OrdinalIgnoreCase);
        }

        internal static string DetectMime(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12) return string.Empty;
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return "image/jpeg";
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return "image/gif";
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return "image/webp";
            return string.Empty;
        }

        private static VisionImageResult Fail(string error)
        {
            return new VisionImageResult { Success = false, Error = error, CacheComplete = false };
        }
    }
}
