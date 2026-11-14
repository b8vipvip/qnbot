using Bot.ChromeNs;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ShopScope
{
    internal sealed class ShopApiDiagnosticReport
    {
        public bool Success { get; set; }
        public string Summary { get; set; }
        public string Details { get; set; }
    }

    internal static class ShopApiDiagnosticsService
    {
        private const int RequestTimeoutSeconds = 105;

        public static async Task<ShopApiDiagnosticReport> TestConnectionAsync(
            ShopContext shop,
            string serverUrl,
            string token,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ValidateInputs(shop, serverUrl, token);
            var baseUrl = ShopControlPlaneConnectionStore.NormalizeUrl(serverUrl);
            var watch = Stopwatch.StartNew();
            try
            {
                using (var http = CreateClient(shop, token))
                using (var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    baseUrl + "/api/runtime/v1/config"))
                using (var response = await http.SendAsync(request, cancellationToken))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    watch.Stop();
                    if (!response.IsSuccessStatusCode)
                    {
                        return Failure(
                            "API连接失败",
                            "阶段1/3 网络连接：已到达服务端\n"
                            + "阶段2/3 Token/ShopKey 鉴权：失败\n"
                            + "HTTP：" + (int)response.StatusCode + " " + response.ReasonPhrase + "\n"
                            + "服务端：" + baseUrl + "\n"
                            + "ShopKey：" + shop.ShopKey + "\n"
                            + "耗时：" + watch.ElapsedMilliseconds + " ms\n"
                            + "响应：" + Safe(body, 1000));
                    }

                    var root = ParseObject(body);
                    var clientName = Convert.ToString(root["client"] ?? string.Empty);
                    var textRoute = Convert.ToString(root["text_route"] ?? string.Empty);
                    var providers = root["providers"] as JArray;
                    var providerCount = providers == null ? 0 : providers.Count;
                    return Success(
                        "API连接正常",
                        "阶段1/3 网络连接：通过\n"
                        + "阶段2/3 Token/ShopKey 鉴权：通过\n"
                        + "阶段3/3 Control Plane 配置读取：通过\n"
                        + "服务端：" + baseUrl + "\n"
                        + "客户端：" + (string.IsNullOrWhiteSpace(clientName) ? "已鉴权" : clientName) + "\n"
                        + "ShopKey：" + shop.ShopKey + "\n"
                        + "文本路由：" + (string.IsNullOrWhiteSpace(textRoute) ? "text-default" : textRoute) + "\n"
                        + "启用供应商：" + providerCount + "\n"
                        + "总耗时：" + watch.ElapsedMilliseconds + " ms");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                watch.Stop();
                return Failure(
                    "API连接失败",
                    "阶段1/3 网络连接：失败\n"
                    + "服务端：" + baseUrl + "\n"
                    + "ShopKey：" + shop.ShopKey + "\n"
                    + "耗时：" + watch.ElapsedMilliseconds + " ms\n"
                    + "错误：" + Safe(ex.Message, 1200));
            }
        }

        public static async Task<ShopApiDiagnosticReport> TestAnswerChainAsync(
            ShopContext shop,
            string seller,
            string serverUrl,
            string token,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ValidateInputs(shop, serverUrl, token);
            seller = (seller ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(seller))
                throw new InvalidOperationException("当前店铺没有可用于真实发送测试的客服身份。" );

            var baseUrl = ShopControlPlaneConnectionStore.NormalizeUrl(serverUrl);
            var overall = Stopwatch.StartNew();

            var connection = await TestConnectionAsync(
                shop,
                baseUrl,
                token,
                cancellationToken);
            if (!connection.Success)
            {
                return Failure(
                    "AI回答链路失败：API连接未通过",
                    connection.Details);
            }

            var prompt =
                "这是千牛Bot设置中的诊断请求，不是买家问题，不要调用工具或外部接口。"
                + "请只用一句简短中文回复：AI回答链路正常，真实发送测试成功。";
            var payload = new JObject
            {
                ["model"] = "text-default",
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = prompt
                    }
                },
                ["max_tokens"] = 96,
                ["temperature"] = 0.1,
                ["timeout_seconds"] = 90
            };

            var aiWatch = Stopwatch.StartNew();
            try
            {
                using (var http = CreateClient(shop, token))
                using (var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    baseUrl + "/v1/chat/completions"))
                {
                    request.Content = new StringContent(
                        payload.ToString(Newtonsoft.Json.Formatting.None),
                        Encoding.UTF8,
                        "application/json");
                    using (var response = await http.SendAsync(request, cancellationToken))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        aiWatch.Stop();
                        if (!response.IsSuccessStatusCode)
                        {
                            return await ContinueAfterAiFailureAsync(
                                shop,
                                seller,
                                "阶段1/6 API网络：通过\n"
                                + "阶段2/6 Token/ShopKey：通过\n"
                                + "阶段3/6 Control Plane 路由：已进入\n"
                                + "阶段4/6 上游供应商/模型调用：失败\n"
                                + "阶段5/6 AI回复文本解析：未执行\n"
                                + "HTTP：" + (int)response.StatusCode + " " + response.ReasonPhrase + "\n"
                                + "AI阶段耗时：" + aiWatch.ElapsedMilliseconds + " ms\n"
                                + "响应：" + Safe(body, 1800),
                                overall,
                                cancellationToken);
                        }

                        var root = ParseObject(body);
                        var choices = root["choices"] as JArray;
                        var first = choices != null && choices.Count > 0 ? choices[0] as JObject : null;
                        var message = first == null ? null : first["message"] as JObject;
                        var answer = Convert.ToString(message == null ? null : message["content"]);
                        if (string.IsNullOrWhiteSpace(answer))
                        {
                            return await ContinueAfterAiFailureAsync(
                                shop,
                                seller,
                                "阶段1/6 API网络：通过\n"
                                + "阶段2/6 Token/ShopKey：通过\n"
                                + "阶段3/6 Control Plane 路由：通过\n"
                                + "阶段4/6 上游供应商/模型调用：通过\n"
                                + "阶段5/6 AI回复文本解析：失败\n"
                                + "服务端已返回 HTTP 2xx，但 choices[0].message.content 为空。\n"
                                + "响应：" + Safe(body, 1800),
                                overall,
                                cancellationToken);
                        }

                        var routing = root["qianniu_routing"] as JObject;
                        var provider = Convert.ToString(routing == null ? null : routing["provider"]);
                        var protocol = Convert.ToString(routing == null ? null : routing["protocol"]);
                        var model = Convert.ToString(root["model"] ?? string.Empty);
                        var latency = Convert.ToString(routing == null ? null : routing["latency_ms"]);
                        var fallback = Convert.ToString(routing == null ? null : routing["fallback_attempts"]);

                        var sendResult = await SendDiagnosticAnswerAsync(
                            shop,
                            seller,
                            answer.Trim(),
                            cancellationToken);
                        overall.Stop();

                        var common =
                            "阶段1/6 API网络：通过\n"
                            + "阶段2/6 Token/ShopKey：通过\n"
                            + "阶段3/6 Control Plane 路由：通过\n"
                            + "阶段4/6 上游供应商/模型/协议：通过\n"
                            + "阶段5/6 AI回复文本解析：通过\n"
                            + "供应商：" + Empty(provider, "未返回") + "\n"
                            + "模型：" + Empty(model, "未返回") + "\n"
                            + "协议：" + Empty(protocol, "未返回") + "\n"
                            + "上游耗时：" + Empty(latency, "-") + " ms\n"
                            + "回退次数：" + Empty(fallback, "0") + "\n"
                            + "链路总耗时：" + overall.ElapsedMilliseconds + " ms\n\n"
                            + "AI实际回复：\n" + answer.Trim() + "\n\n"
                            + sendResult.Details;

                        return sendResult.Success
                            ? Success("AI回答 + 千牛真实发送链路正常", common)
                            : Failure("AI回答正常，但千牛真实发送失败", common);
                    }
                }
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                aiWatch.Stop();
                return await ContinueAfterAiFailureAsync(
                    shop,
                    seller,
                    "阶段1/6 API网络：通过\n"
                    + "阶段2/6 Token/ShopKey：通过\n"
                    + "阶段3/6 Control Plane 路由：已进入\n"
                    + "阶段4/6 上游供应商/模型调用：超时\n"
                    + "阶段5/6 AI回复文本解析：未执行\n"
                    + "AI阶段耗时：" + aiWatch.ElapsedMilliseconds + " ms\n"
                    + "错误：" + Safe(ex.Message, 1600),
                    overall,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Only an explicit caller/user cancellation is allowed to stop the diagnostic.
                throw;
            }
            catch (Exception ex)
            {
                aiWatch.Stop();
                return await ContinueAfterAiFailureAsync(
                    shop,
                    seller,
                    "阶段1/6 API网络：通过\n"
                    + "阶段2/6 Token/ShopKey：通过\n"
                    + "阶段3/6 Control Plane 路由：已进入\n"
                    + "阶段4/6 上游供应商/模型调用：异常\n"
                    + "阶段5/6 AI回复文本解析：未执行\n"
                    + "AI阶段耗时：" + aiWatch.ElapsedMilliseconds + " ms\n"
                    + "错误：" + Safe(ex.Message, 1600),
                    overall,
                    cancellationToken);
            }
        }

        private static async Task<ShopApiDiagnosticReport> ContinueAfterAiFailureAsync(
            ShopContext shop,
            string seller,
            string aiFailureDetails,
            Stopwatch overall,
            CancellationToken cancellationToken)
        {
            const string sendOnlyProbe = "AI阶段异常，本条仅用于独立验证千牛真实发送链路。";
            var sendResult = await SendDiagnosticAnswerAsync(
                shop,
                seller,
                sendOnlyProbe,
                cancellationToken);
            if (overall.IsRunning) overall.Stop();

            var details =
                (aiFailureDetails ?? string.Empty).TrimEnd()
                + "\n\nAI阶段没有产出可用文本；诊断测试未中断。"
                + "已改用本地固定测试文本继续阶段6，不会把上游错误正文发送给买家。\n"
                + sendResult.Details
                + "\n链路总耗时：" + overall.ElapsedMilliseconds + " ms";

            return Failure(
                sendResult.Success
                    ? "AI回答链路失败，但千牛真实发送链路已独立验证通过"
                    : "AI回答链路失败，千牛真实发送链路也失败",
                details);
        }

        private static async Task<ShopApiDiagnosticReport> SendDiagnosticAnswerAsync(
            ShopContext shop,
            string seller,
            string answer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QN qn = null;
            try
            {
                qn = ShopContextLocator.FindConnectedQnByShopKey(shop.ShopKey);
            }
            catch (Exception ex)
            {
                return Failure(
                    "千牛真实发送失败",
                    "阶段6/6 千牛真实发送：失败\n无法按ShopKey定位客服实例：" + Safe(ex.Message, 800));
            }

            if (qn == null || qn.Seller == null || qn.CDP == null)
            {
                return Failure(
                    "千牛真实发送失败",
                    "阶段6/6 千牛真实发送：失败\n当前ShopKey没有可用的在线千牛CDP会话，请先保持对应店铺接待窗口在线。" );
            }

            try
            {
                var resolvedShopKey = ShopIdentityResolver.Resolve(qn.Seller).ShopKey;
                if (!string.Equals(resolvedShopKey, shop.ShopKey, StringComparison.Ordinal)
                    || !string.Equals((qn.Seller.Nick ?? string.Empty).Trim(),
                        (seller ?? string.Empty).Trim(), StringComparison.Ordinal))
                {
                    return Failure(
                        "千牛真实发送失败",
                        "阶段6/6 千牛真实发送：失败\n客服卖家或ShopKey与测试目标不一致，已阻止跨店测试发送。" );
                }
            }
            catch (Exception ex)
            {
                return Failure(
                    "千牛真实发送失败",
                    "阶段6/6 千牛真实发送：失败\n无法验证客服ShopKey：" + Safe(ex.Message, 800));
            }

            string buyer = string.Empty;
            try
            {
                var current = await qn.GetCurrentConversationID();
                buyer = current == null || current.Result == null
                    ? string.Empty
                    : (current.Result.Nick ?? string.Empty).Trim();
            }
            catch (Exception ex)
            {
                return Failure(
                    "千牛真实发送失败",
                    "阶段6/6 千牛真实发送：失败\n读取当前买家会话失败：" + Safe(ex.Message, 900));
            }

            if (string.IsNullOrWhiteSpace(buyer))
            {
                return Failure(
                    "千牛真实发送失败",
                    "阶段6/6 千牛真实发送：失败\n当前没有可确认的买家会话。请先在千牛中打开一个测试买家，再点击测试。" );
            }

            var safeAnswer = answer ?? string.Empty;
            if (safeAnswer.Length > 420) safeAnswer = safeAnswer.Substring(0, 420);
            var sendText = "【Bot链路测试，可手动撤回】" + safeAnswer;
            try
            {
                bool sent;
                using (ShopSettingsScope.Enter(shop))
                {
                    KnowledgeLearningService.AllowNextManualSend(seller, buyer, sendText);
                    sent = await qn.SendTextWithRetryAsync(buyer, sendText, 1);
                }

                if (!sent)
                {
                    var reason = qn.Rpa == null ? "发送链路返回失败" : qn.Rpa.GetSendFailureReason();
                    return Failure(
                        "千牛真实发送失败",
                        "阶段6/6 千牛真实发送：失败\n"
                        + "客服：" + seller + "\n"
                        + "当前买家：" + buyer + "\n"
                        + "失败阶段：" + Safe(reason, 1200) + "\n"
                        + "测试文本：" + Safe(sendText, 600));
                }

                return Success(
                    "千牛真实发送正常",
                    "阶段6/6 千牛真实发送：通过\n"
                    + "客服：" + seller + "\n"
                    + "当前买家：" + buyer + "\n"
                    + "发送确认：生产 SendTextWithRetryAsync 已确认成功\n"
                    + "测试文本：" + Safe(sendText, 600) + "\n"
                    + "提示：这是一条真实买家消息，可在千牛中手动撤回。" );
            }
            catch (Exception ex)
            {
                return Failure(
                    "千牛真实发送失败",
                    "阶段6/6 千牛真实发送：失败\n"
                    + "客服：" + seller + "\n"
                    + "当前买家：" + buyer + "\n"
                    + "异常：" + Safe(ex.Message, 1200));
            }
        }

        private static HttpClient CreateClient(ShopContext shop, string token)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = WebRequest.DefaultWebProxy
            };
            var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
            };
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token.Trim());
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "qianniu-bot-shop-diagnostics/1.0");
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey);
            return http;
        }

        private static void ValidateInputs(ShopContext shop, string serverUrl, string token)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            if (string.IsNullOrWhiteSpace(serverUrl))
                throw new InvalidOperationException("程序没有配置 Bot 服务端地址。" );
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("本店尚未保存 Bot 客户端令牌，请先保存令牌后再测试。" );
        }

        private static JObject ParseObject(string value)
        {
            try
            {
                return JObject.Parse(value ?? string.Empty);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "服务端返回的不是有效 JSON：" + Safe(ex.Message, 300));
            }
        }

        private static ShopApiDiagnosticReport Success(string summary, string details)
        {
            return new ShopApiDiagnosticReport
            {
                Success = true,
                Summary = summary,
                Details = details
            };
        }

        private static ShopApiDiagnosticReport Failure(string summary, string details)
        {
            return new ShopApiDiagnosticReport
            {
                Success = false,
                Summary = summary,
                Details = details
            };
        }

        private static string Empty(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
