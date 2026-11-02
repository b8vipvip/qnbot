using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    public sealed class LocalOcrResult
    {
        public bool Success { get; set; }
        public string Text { get; set; }
        public double Confidence { get; set; }
        public long ElapsedMs { get; set; }
        public bool CacheHit { get; set; }
        public string Engine { get; set; }
        public string Error { get; set; }
        public string ImageSha256 { get; set; }
    }

    internal sealed class RemoteOcrResponse
    {
        public bool ok { get; set; }
        public string text { get; set; }
        public double confidence { get; set; }
        public long elapsedMs { get; set; }
        public string engine { get; set; }
        public string imageSha256 { get; set; }
    }

    internal sealed class LocalOcrCacheEnvelope
    {
        public string ImageSha256 { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string Text { get; set; }
        public double Confidence { get; set; }
        public string Engine { get; set; }
    }

    internal sealed class ServerOcrEndpoint
    {
        public string BaseUrl { get; set; }
        public string ApiKey { get; set; }
        public string Source { get; set; }
    }

    /// <summary>
    /// OCR facade retained for compatibility with the existing image-decision pipeline.
    /// Inference runs on the authenticated server control plane. Windows only hashes,
    /// caches and uploads the already-resolved image. Failures remain soft and fall
    /// through to the normal vision-provider pipeline.
    /// </summary>
    public static class LocalOcrService
    {
        private const int DefaultTimeoutMs = 22000; // Server 16s inference + admission/network margin.
        private const int MaxEvidenceChars = 6000;
        private const long MaxImageBytes = 8L * 1024L * 1024L;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);
        private static readonly HttpClient Http = CreateHttpClient();

        public static Task<LocalOcrResult> TryRecognizeAsync(
            string imagePath,
            CancellationToken cancellationToken,
            int timeoutMs = DefaultTimeoutMs)
        {
            return TryRecognizeAsync(imagePath, null, cancellationToken, timeoutMs);
        }

        public static async Task<LocalOcrResult> TryRecognizeAsync(
            string imagePath,
            string sellerNick,
            CancellationToken cancellationToken,
            int timeoutMs = DefaultTimeoutMs)
        {
            var startedAt = DateTime.UtcNow;
            imagePath = (imagePath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return Failure("图片文件不存在", startedAt);
            }

            long imageLength;
            try
            {
                imageLength = new FileInfo(imagePath).Length;
            }
            catch (Exception ex)
            {
                return Failure("读取图片信息失败: " + ex.Message, startedAt);
            }
            if (imageLength <= 0 || imageLength > MaxImageBytes)
            {
                return Failure("图片大小不符合服务端OCR限制", startedAt);
            }

            string sha256;
            try
            {
                sha256 = ComputeSha256(imagePath);
            }
            catch (Exception ex)
            {
                return Failure("计算图片哈希失败: " + ex.Message, startedAt);
            }

            var cached = TryReadCache(sha256);
            if (cached != null)
            {
                return new LocalOcrResult
                {
                    Success = true,
                    Text = Limit(cached.Text),
                    Confidence = cached.Confidence,
                    ElapsedMs = Math.Max(0, (long)(DateTime.UtcNow - startedAt).TotalMilliseconds),
                    CacheHit = true,
                    Engine = cached.Engine,
                    Error = string.Empty,
                    ImageSha256 = sha256
                };
            }

            var endpoint = ResolveControlPlaneEndpoint(sellerNick);
            if (endpoint == null)
            {
                return Failure("未找到当前店铺可用的服务端控制面连接或令牌", startedAt, sha256);
            }

            try
            {
                var safeTimeout = Math.Max(1500, Math.Min(30000, timeoutMs));
                byte[] imageBytes;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(safeTimeout);
                    imageBytes = await ReadAllBytesAsync(imagePath, timeout.Token).ConfigureAwait(false);
                    using (var request = new HttpRequestMessage(HttpMethod.Post, NormalizeOcrUrl(endpoint.BaseUrl)))
                    using (var content = new ByteArrayContent(imageBytes))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                        request.Headers.TryAddWithoutValidation("Accept", "application/json");
                        request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot/server-ocr");
                        request.Headers.TryAddWithoutValidation("X-Image-Sha256", sha256);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        request.Content = content;

                        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false))
                        {
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!response.IsSuccessStatusCode)
                            {
                                return Failure("服务端OCR HTTP " + (int)response.StatusCode + " " + LimitError(body), startedAt, sha256);
                            }

                            RemoteOcrResponse remote;
                            try
                            {
                                remote = JsonConvert.DeserializeObject<RemoteOcrResponse>(body);
                            }
                            catch (Exception ex)
                            {
                                return Failure("服务端OCR响应解析失败: " + ex.Message, startedAt, sha256);
                            }
                            if (remote == null || !remote.ok)
                            {
                                return Failure("服务端OCR返回无效结果", startedAt, sha256);
                            }
                            if (!string.IsNullOrWhiteSpace(remote.imageSha256)
                                && !string.Equals(remote.imageSha256, sha256, StringComparison.OrdinalIgnoreCase))
                            {
                                return Failure("服务端OCR图片哈希回执不一致", startedAt, sha256);
                            }

                            var result = new LocalOcrResult
                            {
                                Success = true,
                                Text = Limit(remote.text),
                                Confidence = Clamp(remote.confidence),
                                ElapsedMs = remote.elapsedMs > 0
                                    ? remote.elapsedMs
                                    : Math.Max(0, (long)(DateTime.UtcNow - startedAt).TotalMilliseconds),
                                CacheHit = false,
                                Engine = string.IsNullOrWhiteSpace(remote.engine) ? "RapidOCR/ONNXRuntime" : remote.engine.Trim(),
                                Error = string.Empty,
                                ImageSha256 = sha256
                            };
                            TryWriteCache(result);
                            Log.Info("服务端OCR完成: sha256=" + ShortHash(sha256)
                                + ", chars=" + (result.Text == null ? 0 : result.Text.Length)
                                + ", confidence=" + result.Confidence.ToString("0.000", CultureInfo.InvariantCulture)
                                + ", elapsedMs=" + result.ElapsedMs
                                + ", cacheHit=false, endpointSource=" + endpoint.Source);
                            return result;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return Failure(cancellationToken.IsCancellationRequested ? "服务端OCR已取消" : "服务端OCR超时", startedAt, sha256);
            }
            catch (Exception ex)
            {
                return Failure("服务端OCR异常: " + ex.Message, startedAt, sha256);
            }
        }

        // OCR-to-text-AI fallback may reuse successful OCR evidence but must
        // NEVER restart a failed/504 inference immediately (which would queue
        // behind a still-running ONNX worker and double load).
        internal static LocalOcrResult TryGetCachedRecognition(string imagePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                    return null;
                var hash = ComputeSha256(imagePath);
                var cached = TryReadCache(hash);
                if (cached == null) return null;
                return new LocalOcrResult
                {
                    Success = true,
                    Text = Limit(cached.Text),
                    Confidence = cached.Confidence,
                    CacheHit = true,
                    ElapsedMs = 0,
                    Engine = cached.Engine,
                    ImageSha256 = hash,
                    Error = string.Empty
                };
            }
            catch { return null; }
        }

        public static string BuildPromptEvidence(LocalOcrResult result)
        {
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.Text)) return string.Empty;
            return "\n\n[服务端OCR预识别，仅作辅助证据，可能存在错字；请以图片本身为准]\n"
                + Limit(result.Text)
                + "\n[OCR置信度=" + Clamp(result.Confidence).ToString("0.000", CultureInfo.InvariantCulture)
                + ", 引擎=" + (result.Engine ?? "server") + "]";
        }

        private static ServerOcrEndpoint ResolveControlPlaneEndpoint(string sellerNick)
        {
            // OCR is a control-plane capability, not an AI provider. Reuse the same
            // per-shop connection/token that Web sync, rules and update channels use.
            // This avoids requiring users to create a fake "服务端控制面" AI endpoint.
            try
            {
                var shop = ShopSettingsScope.Current;
                if (shop == null && !string.IsNullOrWhiteSpace(sellerNick))
                {
                    shop = ShopContextLocator.ResolveRuntimeBySellerNick(sellerNick.Trim());
                }
                if (shop != null)
                {
                    var connection = new ShopControlPlaneConnectionStore(shop, new ShopScopedPathProvider());
                    string token;
                    string error;
                    var serverUrl = connection.GetServerUrl();
                    if (!string.IsNullOrWhiteSpace(serverUrl)
                        && connection.TryGetToken(out token, out error)
                        && !string.IsNullOrWhiteSpace(token))
                    {
                        return new ServerOcrEndpoint
                        {
                            BaseUrl = serverUrl,
                            ApiKey = token.Trim(),
                            Source = "shop-control-plane"
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("服务端OCR读取店铺控制面连接失败，尝试兼容AI端点: " + LimitError(ex.Message));
            }

            // Backward compatibility for installations that previously configured a
            // dedicated control-plane entry in the AI endpoint list.
            var endpoints = AiEndpointStore.GetEnabledEndpoints();
            if (endpoints == null || endpoints.Count == 0) return null;
            var endpoint = endpoints.FirstOrDefault(x => x != null
                && x.Type == "服务端控制面"
                && !string.IsNullOrWhiteSpace(x.BaseUrl)
                && !string.IsNullOrWhiteSpace(x.ApiKey));
            if (endpoint == null) return null;
            return new ServerOcrEndpoint
            {
                BaseUrl = endpoint.BaseUrl,
                ApiKey = endpoint.ApiKey,
                Source = "legacy-ai-endpoint"
            };
        }

        private static string NormalizeOcrUrl(string baseUrl)
        {
            var value = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            var suffixes = new[] { "/chat/completions", "/responses", "/embeddings", "/api/runtime/v1/ocr" };
            foreach (var suffix in suffixes)
            {
                if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring(0, value.Length - suffix.Length).TrimEnd('/');
                    break;
                }
            }
            if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 3).TrimEnd('/');
            }
            return value + "/api/runtime/v1/ocr";
        }

        private static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            {
                if (stream.Length > MaxImageBytes) throw new IOException("图片超过OCR上传限制");
                var buffer = new byte[stream.Length];
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken).ConfigureAwait(false);
                    if (read <= 0) throw new EndOfStreamException("图片读取不完整");
                    offset += read;
                }
                return buffer;
            }
        }

        private static LocalOcrCacheEnvelope TryReadCache(string sha256)
        {
            try
            {
                var path = GetCachePath(sha256);
                if (!File.Exists(path)) return null;
                var envelope = JsonConvert.DeserializeObject<LocalOcrCacheEnvelope>(File.ReadAllText(path, Encoding.UTF8));
                if (envelope == null || !string.Equals(envelope.ImageSha256, sha256, StringComparison.OrdinalIgnoreCase)) return null;
                if (envelope.CreatedAtUtc == default(DateTime) || DateTime.UtcNow - envelope.CreatedAtUtc > CacheTtl)
                {
                    try { File.Delete(path); } catch { }
                    return null;
                }
                Log.Info("OCR缓存命中: sha256=" + ShortHash(sha256));
                return envelope;
            }
            catch
            {
                return null;
            }
        }

        private static void TryWriteCache(LocalOcrResult result)
        {
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.ImageSha256)) return;
            try
            {
                var path = GetCachePath(result.ImageSha256);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var envelope = new LocalOcrCacheEnvelope
                {
                    ImageSha256 = result.ImageSha256,
                    CreatedAtUtc = DateTime.UtcNow,
                    Text = Limit(result.Text),
                    Confidence = Clamp(result.Confidence),
                    Engine = result.Engine
                };
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(envelope), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch
            {
            }
        }

        private static string GetCachePath(string sha256)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "ocr-cache");
            return Path.Combine(root, sha256 + ".json");
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static string Limit(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length <= MaxEvidenceChars) return value;
            return value.Substring(0, MaxEvidenceChars) + "…";
        }

        private static string LimitError(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= 500 ? value : value.Substring(0, 500) + "…";
        }

        private static double Clamp(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0d;
            return Math.Max(0d, Math.Min(1d, value));
        }

        private static string ShortHash(string sha256)
        {
            if (string.IsNullOrWhiteSpace(sha256)) return string.Empty;
            return sha256.Length <= 12 ? sha256 : sha256.Substring(0, 12);
        }

        private static LocalOcrResult Failure(string error, DateTime startedAt, string sha256 = null)
        {
            Log.Info("服务端OCR跳过/失败: " + LimitError(error));
            return new LocalOcrResult
            {
                Success = false,
                Text = string.Empty,
                Confidence = 0d,
                ElapsedMs = Math.Max(0, (long)(DateTime.UtcNow - startedAt).TotalMilliseconds),
                CacheHit = false,
                Engine = "RapidOCR/ONNXRuntime",
                Error = error ?? string.Empty,
                ImageSha256 = sha256 ?? string.Empty
            };
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient();
            http.Timeout = Timeout.InfiniteTimeSpan;
            return http;
        }
    }
}
