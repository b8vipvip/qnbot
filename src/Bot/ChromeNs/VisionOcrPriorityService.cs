using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    /// <summary>
    /// Reads the server-owned image understanding priority. Failures deliberately fall back to
    /// OCR-first so older/unreachable control planes preserve the historical behavior.
    /// </summary>
    internal static class VisionOcrPriorityService
    {
        public const string OcrFirst = "ocr_first";
        public const string AiFirst = "ai_first";
        private const int PriorityTimeoutMs = 2500;
        private static readonly HttpClient Http = CreateHttpClient();

        private sealed class RemotePriorityResponse
        {
            public string vision_priority { get; set; }
        }

        private sealed class ControlPlaneEndpoint
        {
            public string BaseUrl { get; set; }
            public string ApiKey { get; set; }
            public string Source { get; set; }
        }

        public static bool IsAiFirst(string priority)
        {
            return string.Equals((priority ?? string.Empty).Trim(), AiFirst, StringComparison.OrdinalIgnoreCase);
        }

        public static Task<string> ResolveAsync(string sellerNick, CancellationToken cancellationToken)
        {
            return ResolveAsync(sellerNick, null, cancellationToken);
        }

        public static async Task<string> ResolveAsync(
            string sellerNick,
            string shopKey,
            CancellationToken cancellationToken)
        {
            var endpoint = ResolveControlPlaneEndpoint(sellerNick, shopKey);
            if (endpoint == null) return OcrFirst;

            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(PriorityTimeoutMs);
                    using (var request = new HttpRequestMessage(HttpMethod.Get, NormalizePriorityUrl(endpoint.BaseUrl)))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                        request.Headers.TryAddWithoutValidation("Accept", "application/json");
                        request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot/ocr-vision-priority");
                        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                Log.Info("读取服务端视觉理解优先级失败，保持OCR优先: HTTP " + (int)response.StatusCode);
                                return OcrFirst;
                            }
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var remote = JsonConvert.DeserializeObject<RemotePriorityResponse>(body);
                            var priority = remote == null ? string.Empty : (remote.vision_priority ?? string.Empty).Trim().ToLowerInvariant();
                            if (priority != OcrFirst && priority != AiFirst)
                            {
                                Log.Info("服务端视觉理解优先级无效，保持OCR优先: value=" + Limit(priority));
                                return OcrFirst;
                            }
                            return priority;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log.Info("读取服务端视觉理解优先级超时/取消，保持OCR优先");
                return OcrFirst;
            }
            catch (Exception ex)
            {
                Log.Info("读取服务端视觉理解优先级异常，保持OCR优先: " + Limit(ex.Message));
                return OcrFirst;
            }
        }

        private static ControlPlaneEndpoint ResolveControlPlaneEndpoint(string sellerNick, string shopKey)
        {
            sellerNick = (sellerNick ?? string.Empty).Trim();
            shopKey = (shopKey ?? string.Empty).Trim();
            try
            {
                var current = ShopSettingsScope.Current;
                ShopContext shop = null;
                if (shopKey.Length > 0)
                {
                    shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
                    // The supplied ShopKey is the authoritative identity. Resolve and enter
                    // that exact scope even when another shop is the ambient AsyncLocal context.
                    if (shop == null
                        || !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal))
                        return null;
                }
                else if (current != null)
                {
                    shop = current;
                }
                else if (sellerNick.Length > 0)
                {
                    shop = ShopContextLocator.ResolveRuntimeBySellerNick(sellerNick);
                }

                if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                    || (shopKey.Length == 0 && sellerNick.Length > 0
                        && !string.Equals((shop.SellerNick ?? string.Empty).Trim(), sellerNick, StringComparison.Ordinal))
                    || (shopKey.Length > 0
                        && !string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)))
                    return null;

                using (ShopSettingsScope.Enter(shop, false))
                {
                    try
                    {
                        var connection = new ShopControlPlaneConnectionStore(shop, new ShopScopedPathProvider());
                        string token;
                        string error;
                        var serverUrl = connection.GetServerUrl();
                        if (!string.IsNullOrWhiteSpace(serverUrl)
                            && connection.TryGetToken(out token, out error)
                            && !string.IsNullOrWhiteSpace(token))
                        {
                            return new ControlPlaneEndpoint
                            {
                                BaseUrl = serverUrl,
                                ApiKey = token.Trim(),
                                Source = "shop-control-plane"
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Info("读取视觉优先级控制面连接失败，尝试店铺隔离的兼容端点: " + Limit(ex.Message));
                    }

                    var endpoints = AiEndpointStore.GetEnabledEndpoints();
                    if (endpoints == null || endpoints.Count == 0) return null;
                    var endpoint = endpoints.FirstOrDefault(x => x != null
                        && x.Type == "服务端控制面"
                        && !string.IsNullOrWhiteSpace(x.BaseUrl)
                        && !string.IsNullOrWhiteSpace(x.ApiKey));
                    if (endpoint == null) return null;
                    return new ControlPlaneEndpoint
                    {
                        BaseUrl = endpoint.BaseUrl,
                        ApiKey = endpoint.ApiKey,
                        Source = "legacy-ai-endpoint"
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Info("读取视觉优先级控制面连接失败，已fail-closed: " + Limit(ex.Message));
                return null;
            }
        }

        private static string NormalizePriorityUrl(string baseUrl)
        {
            var value = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            var suffixes = new[]
            {
                "/chat/completions",
                "/responses",
                "/embeddings",
                "/api/runtime/v1/ocr",
                "/api/runtime/v1/ocr/vision-priority"
            };
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
            return value + "/api/runtime/v1/ocr/vision-priority";
        }

        private static string Limit(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= 300 ? value : value.Substring(0, 300) + "…";
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient();
            http.Timeout = Timeout.InfiniteTimeSpan;
            return http;
        }
    }
}
