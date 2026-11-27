using Bot.ShopScope;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class WeComAppBridgeClient
    {
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);
        private static int _started;

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            Task.Run(PollLoopAsync);
            Log.Info("企业微信应用人工回复桥接轮询已启动：按 ShopKey 分别领取和回报人工回复。" );
        }

        public static bool IsConfigured()
        {
            var shop = ShopSettingsScope.Current;
            if (shop == null) return false;
            string url;
            string token;
            return TryReadConnection(shop, out url, out token);
        }

        public static async Task<string> SendNotificationAsync(
            string seller,
            string buyer,
            string question,
            AutoReplyRuleDecision decision,
            bool test)
        {
            var shop = ShopSettingsScope.Current;
            if (shop == null) return "当前没有店铺作用域";
            string serverUrl;
            string token;
            if (!TryReadConnection(shop, out serverUrl, out token)) return "未配置本店统一API令牌";

            var rawReason = decision == null
                ? "测试企业微信应用消息双向链路"
                : (decision.Reason ?? string.Empty);
            var payload = new JObject
            {
                ["shop_key"] = shop.ShopKey,
                ["seller"] = seller ?? string.Empty,
                ["buyer"] = buyer ?? string.Empty,
                ["question"] = SafePayload(question, 6000),
                // Control-plane schema limits reason to 500 characters. Keep margin for future
                // server-side normalization and never ship an oversized AI/upstream error blob.
                ["reason"] = SafePayload(rawReason, 480),
                ["is_off_hours"] = decision != null && decision.IsOffHours,
                ["test"] = test
            };
            try
            {
                var result = await SendJsonAsync(
                    shop,
                    HttpMethod.Post,
                    serverUrl + "/api/runtime/v1/handoff/notify",
                    token,
                    payload);
                if (!result.Success) return result.Error;
                var json = JObject.Parse(result.Body);
                var ticket = Convert.ToString(json["ticket_id"]);
                return string.IsNullOrWhiteSpace(ticket) ? "成功" : "成功，工单=" + ticket;
            }
            catch (Exception ex)
            {
                return "失败：" + Safe(ex.Message);
            }
        }

        private static async Task PollLoopAsync()
        {
            while (true)
            {
                try
                {
                    var shops = SnapshotOnlineShops();
                    foreach (var shop in shops)
                    {
                        string url;
                        string token;
                        if (!TryReadConnection(shop, out url, out token)) continue;
                        await PollOnceAsync(shop, url, token);
                    }
                    await Task.Delay(shops.Count == 0 ? 10000 : 3000);
                }
                catch (Exception ex)
                {
                    Log.Info("企业微信应用人工回复轮询异常：" + Safe(ex.Message));
                    await Task.Delay(5000);
                }
            }
        }

        private static IList<ShopContext> SnapshotOnlineShops()
        {
            var result = new Dictionary<string, ShopContext>(StringComparer.Ordinal);
            try
            {
                var qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray();
                foreach (var qn in qns)
                {
                    if (qn == null || qn.Seller == null) continue;
                    try
                    {
                        var shop = Profiles.GetOrCreate(ShopIdentityResolver.Resolve(qn.Seller)).ToContext();
                        result[shop.ShopKey] = shop;
                    }
                    catch { }
                }
            }
            catch { }
            return result.Values.ToList();
        }

        private static async Task PollOnceAsync(ShopContext shop, string serverUrl, string token)
        {
            var next = await SendJsonAsync(
                shop,
                HttpMethod.Get,
                serverUrl + "/api/runtime/v1/handoff/replies/next",
                token,
                null);
            if (next.StatusCode == HttpStatusCode.NoContent) return;
            if (!next.Success)
            {
                if (next.StatusCode != HttpStatusCode.NotFound)
                {
                    using (ShopSettingsScope.Enter(shop))
                        Log.Info("领取本店企业微信人工回复失败：" + next.Error);
                }
                return;
            }

            var json = JObject.Parse(next.Body);
            var commandId = Convert.ToInt32(json["id"]);
            var ticketId = Convert.ToString(json["ticket_id"]);
            var seller = Convert.ToString(json["seller"]);
            var buyer = Convert.ToString(json["buyer"]);
            var question = Convert.ToString(json["question"]);
            var reply = Convert.ToString(json["reply_text"]);
            var claimToken = Convert.ToString(json["claim_token"]);
            if (commandId <= 0 || string.IsNullOrWhiteSpace(claimToken)) return;

            var qn = FindQn(shop, seller);
            if (qn == null || qn.CDP == null)
            {
                using (ShopSettingsScope.Enter(shop))
                    Log.Info("本店企业微信人工回复等待对应千牛客服上线。ticket=" + ticketId + ", seller=" + seller);
                return;
            }

            var success = false;
            var error = string.Empty;
            using (ShopSettingsScope.Enter(shop))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(buyer) || string.IsNullOrWhiteSpace(reply))
                        throw new Exception("人工回复任务缺少买家或回复内容");
                    KnowledgeLearningService.AllowNextManualSend(seller, buyer, reply);
                    success = await qn.SendTextWithRetryAsync(buyer, reply, 1);
                    if (!success)
                    {
                        error = "无法确认目标买家会话或千牛发送未完成";
                    }
                    else
                    {
                        ReplyDeduplicationService.RememberDelivered(seller, buyer, reply, shop.ShopKey);
                        KnowledgeLearningService.RegisterAnswerSource(
                            seller, buyer, question, reply, "人工回复-企业微信应用");
                        Log.Info("本店企业微信人工回复已发送；后续由 Knowledge V2 完整会话学习统一处理。shop="
                            + shop.ShopKey + ", ticket=" + ticketId + ", seller=" + seller + ", buyer=" + buyer);
                    }
                }
                catch (Exception ex)
                {
                    error = Safe(ex.Message);
                    Log.Info("本店企业微信人工回复发送失败。ticket=" + ticketId + ", error=" + error);
                }
            }
            await CompleteAsync(shop, serverUrl, token, commandId, claimToken, success, error);
        }

        private static QN FindQn(ShopContext shop, string seller)
        {
            try
            {
                var qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray();
                foreach (var qn in qns)
                {
                    if (qn == null || qn.Seller == null) continue;
                    if (!string.Equals((qn.Seller.Nick ?? string.Empty).Trim(),
                        (seller ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (string.Equals(ShopIdentityResolver.Resolve(qn.Seller).ShopKey,
                            shop.ShopKey, StringComparison.Ordinal)) return qn;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static async Task CompleteAsync(
            ShopContext shop,
            string serverUrl,
            string token,
            int commandId,
            string claimToken,
            bool success,
            string error)
        {
            var payload = new JObject
            {
                ["shop_key"] = shop.ShopKey,
                ["claim_token"] = claimToken,
                ["success"] = success,
                ["error"] = SafePayload(error, 480)
            };
            var result = await SendJsonAsync(
                shop,
                HttpMethod.Post,
                serverUrl + "/api/runtime/v1/handoff/replies/" + commandId + "/complete",
                token,
                payload);
            if (!result.Success)
            {
                using (ShopSettingsScope.Enter(shop))
                    Log.Info("回报本店企业微信人工回复结果失败：" + result.Error);
            }
        }

        private static async Task<HttpResult> SendJsonAsync(
            ShopContext shop,
            HttpMethod method,
            string url,
            string token,
            JObject payload)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
            using (var http = new HttpClient(handler))
            using (var request = new HttpRequestMessage(method, url))
            {
                http.Timeout = TimeSpan.FromSeconds(25);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-wecom-bridge/2.0");
                request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey);
                if (payload != null)
                    request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    return new HttpResult
                    {
                        Success = response.IsSuccessStatusCode,
                        StatusCode = response.StatusCode,
                        Body = body,
                        Error = response.IsSuccessStatusCode
                            ? string.Empty
                            : "HTTP " + (int)response.StatusCode + " " + Safe(body)
                    };
                }
            }
        }

        private static bool TryReadConnection(ShopContext shop, out string serverUrl, out string token)
        {
            serverUrl = string.Empty;
            token = string.Empty;
            if (shop == null) return false;
            var connection = new ShopControlPlaneConnectionStore(shop, Paths);
            serverUrl = connection.GetServerUrl();
            string error;
            if (!connection.TryGetToken(out token, out error)) return false;
            return !string.IsNullOrWhiteSpace(serverUrl) && !string.IsNullOrWhiteSpace(token);
        }

        private static string SafePayload(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            if (max < 1) return string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static string Safe(string value)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= 300 ? value : value.Substring(0, 300) + "...";
        }

        private sealed class HttpResult
        {
            public bool Success;
            public HttpStatusCode StatusCode;
            public string Body;
            public string Error;
        }
    }
}