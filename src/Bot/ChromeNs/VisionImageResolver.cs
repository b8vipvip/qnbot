using Bot.ChatRecord;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

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
            if (bytes[0] == 0x42 && bytes[1] == 0x4D) return "image/bmp";

            // HEIF/HEIC is an ISO-BMFF image container. Identify only known still-image
            // brands; arbitrary MP4/video files must never enter the image decoder.
            if (bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
            {
                var brand = Encoding.ASCII.GetString(bytes, 8, 4).ToLowerInvariant();
                if (brand == "heic" || brand == "heix" || brand == "hevc" || brand == "hevx"
                    || brand == "heim" || brand == "heis" || brand == "hevm" || brand == "hevs")
                    return "image/heic";
                if (brand == "mif1" || brand == "msf1") return "image/heif";
            }
            return string.Empty;
        }

        internal static bool IsSupportedVisionMime(string mime)
        {
            return string.Equals(mime, "image/jpeg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mime, "image/png", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mime, "image/webp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mime, "image/gif", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalize decodable BMP/HEIF inputs to PNG before they enter the vision/OCR
        /// pipeline. The downstream API deliberately accepts only JPEG/PNG/WEBP/GIF.
        /// HEIC decoding depends on an installed Windows WIC codec; failure is explicit.
        /// </summary>
        internal static bool TryNormalizeForVision(
            byte[] sourceBytes,
            out byte[] normalizedBytes,
            out string normalizedMime,
            out string error)
        {
            normalizedBytes = null;
            normalizedMime = DetectMime(sourceBytes);
            error = string.Empty;
            if (sourceBytes == null || sourceBytes.Length < 12)
            {
                error = "图片数据不完整";
                return false;
            }

            if (IsSupportedVisionMime(normalizedMime))
            {
                normalizedBytes = sourceBytes;
                return true;
            }
            if (normalizedMime != "image/bmp" && normalizedMime != "image/heic" && normalizedMime != "image/heif")
            {
                error = "图片格式不受支持";
                return false;
            }

            try
            {
                // Reject oversized BMP dimensions from the header before asking WIC to
                // allocate decoded pixels. The byte-size limit alone does not prevent a
                // tiny malformed BMP header from declaring an enormous raster.
                if (normalizedMime == "image/bmp")
                {
                    string dimensionError;
                    if (!HasSafeBmpDimensions(sourceBytes, out dimensionError))
                    {
                        error = dimensionError;
                        return false;
                    }
                }

                using (var input = new MemoryStream(sourceBytes, false))
                {
                    // OnDemand exposes frame metadata first; the pixel limit below is
                    // checked before the PNG encoder triggers the actual decode.
                    var decoder = BitmapDecoder.Create(
                        input,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnDemand);
                    if (decoder == null || decoder.Frames == null || decoder.Frames.Count == 0)
                    {
                        error = "图片解码器没有返回有效图像";
                        return false;
                    }

                    var frame = decoder.Frames[0];
                    if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0
                        || (long)frame.PixelWidth * frame.PixelHeight > 25000000L)
                    {
                        error = "图片分辨率无效或超过 2500 万像素限制";
                        return false;
                    }

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(frame));
                    using (var output = new MemoryStream())
                    {
                        encoder.Save(output);
                        normalizedBytes = output.ToArray();
                    }
                }

                if (normalizedBytes == null || normalizedBytes.Length < 12)
                {
                    normalizedBytes = null;
                    error = "图片转换为 PNG 后数据不完整";
                    return false;
                }

                normalizedMime = DetectMime(normalizedBytes);
                if (!string.Equals(normalizedMime, "image/png", StringComparison.Ordinal))
                {
                    normalizedBytes = null;
                    error = "图片转换结果不是有效 PNG";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                normalizedBytes = null;
                error = normalizedMime == "image/heic" || normalizedMime == "image/heif"
                    ? "当前 Windows 未安装可用的 HEIC/HEIF 解码组件，请先转换为 JPG 或 PNG。"
                    : "BMP 图片转换为 PNG 失败：" + ex.GetType().Name;
                return false;
            }
        }

        private static bool HasSafeBmpDimensions(byte[] bytes, out string error)
        {
            error = string.Empty;
            if (bytes == null || bytes.Length < 26)
            {
                error = "BMP 文件头不完整";
                return false;
            }

            try
            {
                var dibHeaderSize = BitConverter.ToUInt32(bytes, 14);
                long width;
                long height;
                if (dibHeaderSize >= 40)
                {
                    width = BitConverter.ToInt32(bytes, 18);
                    height = Math.Abs((long)BitConverter.ToInt32(bytes, 22));
                }
                else if (dibHeaderSize == 12)
                {
                    width = BitConverter.ToUInt16(bytes, 18);
                    height = BitConverter.ToUInt16(bytes, 20);
                }
                else
                {
                    error = "BMP 图像头格式不受支持";
                    return false;
                }

                if (width <= 0 || height <= 0 || width * height > 25000000L)
                {
                    error = "BMP 分辨率无效或超过 2500 万像素限制";
                    return false;
                }
                return true;
            }
            catch
            {
                error = "BMP 图像尺寸字段无效";
                return false;
            }
        }

        private static VisionImageResult Fail(string error)
        {
            return new VisionImageResult { Success = false, Error = error, CacheComplete = false };
        }
    }
}
