using Bot.ShopScope;
using Bot.Options;
using Bot.ChatRecord;
using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    /// <summary>
    /// 在普通消息安全分类前启动充值进度查询桥接。
    /// 只在服务端明确启用、买家明确询问进度、且同一会话中存在客服发送的“兑换码：...”时接管。
    /// </summary>
    public partial class App
    {
        private static readonly object RechargeStatusAutoQueryBootstrap =
            ChromeNs.RechargeStatusAutoQueryService.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class RechargeStatusAutoQueryService
    {
        private sealed class GateEntry
        {
            public DateTime Until;
            public string Note;
        }

        private static readonly ConditionalWeakTable<QN, object> Attached =
            new ConditionalWeakTable<QN, object>();
        private static readonly object AttachSync = new object();
        private static readonly ConcurrentDictionary<string, GateEntry> HandledMessages =
            new ConcurrentDictionary<string, GateEntry>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> HumanNotifyDedup =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly HttpClient Http = CreateHttpClient();

        private static readonly Regex ProgressIntentRegex = new Regex(
            "充值进度|充值状态|查询进度|查询状态|充好了吗|充好没有|充值好了吗|充值了吗|什么时候到账|多久到账|多久能到|多久充值好|还要多久|怎么还没到|怎么还没好|还没到账|还没有到账|是否到账|进度怎么样|现在什么状态|处理好了吗",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RedeemCodeRegex = new Regex(
            @"(?:会员)?兑换码\s*[:：]\s*([A-Za-z0-9_-]{6,64})(?![A-Za-z0-9_-])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static Timer _attachTimer;
        private static int _initialized;
        private static volatile bool _serverEnabled;
        private static DateTime _lastConfigSuccess = DateTime.MinValue;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                _attachTimer = new Timer(_ => Attach(), null, 0, 100);
                Task.Run(PollConfigLoopAsync);
                Log.Info("充值结果自动查询服务已启动：等待服务端开关与同会话兑换码证据。");
            }
            return new object();
        }

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            return new HttpClient(new HttpClientHandler
            {
                UseProxy = true,
                Proxy = WebRequest.DefaultWebProxy
            })
            {
                Timeout = TimeSpan.FromSeconds(25)
            };
        }

        private static void Attach()
        {
            try
            {
                Cleanup();
                foreach (var qn in QN.GetRuntimeSafetySnapshot())
                {
                    if (qn == null) continue;
                    lock (AttachSync)
                    {
                        object marker;
                        if (Attached.TryGetValue(qn, out marker)) continue;
                        qn.EvRecieveNewMessage += OnReceiveNewMessage;
                        Attached.Add(qn, new object());
                    }
                    Log.Info("充值结果自动查询已绑定: seller="
                        + (qn.Seller == null ? string.Empty : qn.Seller.Nick));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("绑定充值结果自动查询失败：" + Safe(ex.Message), 10);
            }
        }

        private static void OnReceiveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            if (!_serverEnabled) return;
            var qn = sender as QN;
            var raw = e == null ? string.Empty : (e.Message ?? string.Empty);
            if (qn == null || raw.Length < 8) return;

            try
            {
                var chat = JsonConvert.DeserializeObject<ChatResponse>(raw);
                var messages = chat == null || chat.result == null
                    ? new List<QNChatMessage>()
                    : chat.result.Where(x => x != null).ToList();
                foreach (var message in messages)
                {
                    TryClaimAndQuery(qn, message);
                }
            }
            catch (Exception ex)
            {
                Log.Info("充值查询消息解析失败: length=" + raw.Length + ", error=" + Safe(ex.Message));
            }
        }

        private static void TryClaimAndQuery(QN qn, QNChatMessage message)
        {
            if (qn == null || message == null || qn.Seller == null) return;
            var seller = (qn.Seller.Nick ?? string.Empty).Trim();
            var from = message.fromid == null ? string.Empty : (message.fromid.nick ?? string.Empty).Trim();
            var to = message.toid == null ? string.Empty : (message.toid.nick ?? string.Empty).Trim();
            if (seller.Length == 0 || from.Length == 0 || to.Length == 0) return;
            if (!DirectOrderIdentityResolver.IdentityEquals(to, seller)
                || DirectOrderIdentityResolver.IdentityEquals(from, seller)) return;

            var question = MessageText(message);
            if (string.IsNullOrWhiteSpace(question) || !ProgressIntentRegex.IsMatch(question)) return;

            string code;
            DateTime codeTime;
            var shop = ShopContextLocator.ResolveRuntimeBySeller(qn);
            var sourceClient = qn.CDP;
            var sourceServer = MyWebSocketServer.WSocketSvrInst;
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)
                || !string.Equals(shop.SellerNick, seller, StringComparison.Ordinal)
                || sourceClient == null || sourceClient.IsInvalidated || sourceClient.IsShopIdentityRetired
                || !string.Equals(sourceClient.ShopKey, shop.ShopKey, StringComparison.Ordinal)
                || sourceServer == null
                || !sourceServer.IsAuthoritativeShopSession(shop.ShopKey, sourceClient.SessionId))
                return;

            if (!TryFindRecentRedeemCode(seller, from, question, shop.ShopKey, out code, out codeTime))
            {
                Log.Info("买家询问充值进度，但同一会话未发现客服发送的明确兑换码标签: seller="
                    + seller + ", buyer=" + from);
                return;
            }

            var messageKey = IncomingMessageSafety.BuildMessageKey(message, question);
            if (string.IsNullOrWhiteSpace(messageKey)) return;
            var scopedMessageKey = shop.ShopKey + "|" + messageKey;
            var entry = new GateEntry
            {
                Until = DateTime.Now.AddMinutes(3),
                Note = "充值进度问题已由兑换码状态查询服务接管，未调用普通AI。"
            };
            if (!HandledMessages.TryAdd(scopedMessageKey, entry)) return;

            Log.Info("充值进度问题已接管: seller=" + seller
                + ", buyer=" + from
                + ", codeHash=" + Hash(code).Substring(0, 12)
                + ", codeAgeMinutes=" + Math.Max(0, (int)(DateTime.Now - codeTime).TotalMinutes));

            var detectedAt = DateTime.Now;
            Task.Run(async () => await QueryAndReplyAsync(
                qn, shop.ShopKey, sourceClient, sourceClient.SessionId,
                seller, from, question, code, detectedAt));
        }

        public static bool TryConsumeHandled(
            QNChatMessage message,
            string messageText,
            out string note,
            string shopKey = null)
        {
            note = string.Empty;
            Cleanup();
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0) return false;
            var key = shopKey + "|" + messageKey;
            GateEntry entry;
            if (string.IsNullOrWhiteSpace(key) || !HandledMessages.TryGetValue(key, out entry) || entry == null)
            {
                return false;
            }
            if (entry.Until < DateTime.Now)
            {
                GateEntry ignored;
                HandledMessages.TryRemove(key, out ignored);
                return false;
            }
            note = entry.Note ?? "充值进度查询已接管。";
            return true;
        }

        private static bool TryFindRecentRedeemCode(
            string seller,
            string buyer,
            string currentQuestion,
            string shopKey,
            out string code,
            out DateTime timestamp)
        {
            code = string.Empty;
            timestamp = DateTime.MinValue;
            var turns = ConversationContextStore.GetRecentTurns(seller, buyer, currentQuestion, 24, shopKey);
            foreach (var turn in turns
                .Where(x => x != null && x.Role == "assistant" && !x.Withdrawn)
                .OrderByDescending(x => x.Timestamp))
            {
                if (turn.Timestamp != DateTime.MinValue && turn.Timestamp < DateTime.Now.AddDays(-3)) continue;
                var match = RedeemCodeRegex.Match(turn.Text ?? string.Empty);
                if (!match.Success) continue;
                var candidate = match.Groups[1].Value.Trim();
                if (!Regex.IsMatch(candidate, @"^[A-Za-z0-9_-]{6,64}$")) continue;
                code = candidate;
                timestamp = turn.Timestamp == DateTime.MinValue ? DateTime.Now : turn.Timestamp;
                return true;
            }
            return false;
        }

        private static async Task QueryAndReplyAsync(
            QN qn,
            string shopKey,
            CDPClient sourceClient,
            string sourceSessionId,
            string seller,
            string buyer,
            string question,
            string code,
            DateTime detectedAt)
        {
            JObject result = null;
            string failure = string.Empty;
            try
            {
                result = await QueryServerAsync(seller, buyer, code);
            }
            catch (Exception ex)
            {
                failure = Safe(ex.Message);
            }

            var reply = string.Empty;
            var notifyHuman = false;
            var status = string.Empty;
            var category = string.Empty;

            if (result != null && Convert.ToBoolean(result["handled"]))
            {
                reply = Convert.ToString(result["reply_text"]);
                notifyHuman = Convert.ToBoolean(result["notify_human"]);
                status = Convert.ToString(result["r_status"]);
                category = Convert.ToString(result["category"]);
            }
            else if (result != null && string.Equals(Convert.ToString(result["reason"]), "disabled", StringComparison.OrdinalIgnoreCase))
            {
                // 开关可能刚刚在服务端关闭；立即停止本机后续接管。
                _serverEnabled = false;
                Log.Info("服务端已关闭充值结果自动查询，本次不发送状态答复。buyer=" + buyer);
                return;
            }
            else
            {
                reply = "充值进度查询暂时失败，正在转人工客服处理。";
                notifyHuman = true;
                category = "query_error";
            }

            if (string.IsNullOrWhiteSpace(reply))
            {
                reply = "暂未查询到明确的充值进度，请稍后再试或联系人工客服。";
            }

            var answer = BotOutboundMessageFormatter.EnsureAiMarker(
                BotFeatureStore.ApplyOutputPolicy(reply));
            if (!Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply()
                || !IsSourceSessionCurrent(qn, shopKey, sourceClient, sourceSessionId, seller))
            {
                Log.Info("充值状态自动答复发送前Bot开关或原店铺CDP身份已变化，取消迟到回复: buyer=" + buyer);
                return;
            }
            string relevanceReason;
            if (!ParallelReplyRelevanceGate.ShouldSend(
                seller, buyer, question, detectedAt, out relevanceReason, false, shopKey))
            {
                Log.Info("充值状态自动答复被最后会话相关性门禁阻止: buyer=" + buyer
                    + ", reason=" + relevanceReason);
                return;
            }
            Func<bool> preSendGuard = () =>
            {
                if (!Params.Robot.CanUseRobotReal || !Params.Robot.GetIsAutoReply()
                    || !IsSourceSessionCurrent(qn, shopKey, sourceClient, sourceSessionId, seller))
                    return false;
                string latestReason;
                var shouldSend = ParallelReplyRelevanceGate.ShouldSend(
                    seller, buyer, question, detectedAt, out latestReason, false, shopKey);
                if (!shouldSend)
                    Log.Info("充值状态等待发送资源期间会话已变化，取消迟到回复: buyer=" + buyer
                        + ", reason=" + latestReason);
                return shouldSend;
            };
            var sendOk = await qn.SendTextWithRetryAsync(
                buyer, answer, 1, CancellationToken.None, preSendGuard);
            if (sendOk)
            {
                ReplyDeduplicationService.RememberDelivered(seller, buyer, answer, shopKey);
            }

            Log.Info("充值结果自动答复完成: seller=" + seller
                + ", buyer=" + buyer
                + ", category=" + category
                + ", status=" + Safe(status)
                + ", sent=" + sendOk
                + (failure.Length == 0 ? string.Empty : ", queryError=" + failure));

            if (notifyHuman)
            {
                await NotifyHumanAsync(
                    shopKey,
                    seller,
                    buyer,
                    question,
                    code,
                    status,
                    category,
                    failure);
            }
        }

        private static bool IsSourceSessionCurrent(
            QN qn, string shopKey, CDPClient sourceClient, string sourceSessionId, string seller)
        {
            if (qn == null || sourceClient == null
                || sourceClient.IsInvalidated || sourceClient.IsShopIdentityRetired
                || !ReferenceEquals(qn.CDP, sourceClient)
                || !string.Equals(sourceClient.ShopKey, shopKey, StringComparison.Ordinal)
                || !string.Equals(sourceClient.SessionId, sourceSessionId, StringComparison.Ordinal))
                return false;
            var shop = ShopContextLocator.ResolveRuntimeByShopKey(shopKey);
            var server = MyWebSocketServer.WSocketSvrInst;
            return shop != null
                && string.Equals(shop.SellerNick, seller, StringComparison.Ordinal)
                && server != null
                && server.IsAuthoritativeShopSession(shopKey, sourceSessionId);
        }

        private static async Task<JObject> QueryServerAsync(string seller, string buyer, string code)
        {
            string serverUrl;
            string token;
            ReadConnection(out serverUrl, out token);
            if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token))
            {
                throw new Exception("统一API服务地址或客户端令牌未配置");
            }

            var payload = new JObject
            {
                ["code"] = code,
                ["seller"] = seller ?? string.Empty,
                ["buyer"] = buyer ?? string.Empty
            };
            using (var request = new HttpRequestMessage(
                HttpMethod.Post,
                serverUrl + "/api/runtime/v1/recharge-query/status"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-recharge-query/1.0");
                request.Content = new StringContent(
                    payload.ToString(Formatting.None),
                    Encoding.UTF8,
                    "application/json");
                using (var response = await Http.SendAsync(request))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body));
                    }
                    return JObject.Parse(body);
                }
            }
        }

        private static async Task NotifyHumanAsync(
            string shopKey,
            string seller,
            string buyer,
            string question,
            string code,
            string status,
            string category,
            string failure)
        {
            var dedupKey = (shopKey ?? string.Empty).Trim() + "#" + Normalize(seller) + "#" + Normalize(buyer)
                + "#" + Hash(code) + "#" + Normalize(status + category);
            DateTime until;
            if (HumanNotifyDedup.TryGetValue(dedupKey, out until) && until > DateTime.Now) return;
            HumanNotifyDedup[dedupKey] = DateTime.Now.AddMinutes(15);

            var suffix = code.Length <= 4 ? "****" : "****" + code.Substring(code.Length - 4);
            var reason = "充值状态需要人工处理；状态="
                + (string.IsNullOrWhiteSpace(status) ? category : status)
                + "；兑换码尾号=" + suffix
                + (string.IsNullOrWhiteSpace(failure) ? string.Empty : "；查询异常=" + failure);
            var decision = new AutoReplyRuleDecision
            {
                Matched = true,
                AllowAutoReply = false,
                UseAiReply = false,
                IsOffHours = false,
                Reason = reason,
                HitKeyword = "充值进度"
            };
            var result = await WeComAppBridgeClient.SendNotificationAsync(
                seller,
                buyer,
                question,
                decision,
                false);
            Log.Info("充值状态企业微信人工提醒结果: seller=" + seller
                + ", buyer=" + buyer + ", result=" + Safe(result));
        }

        private static async Task PollConfigLoopAsync()
        {
            while (true)
            {
                try
                {
                    string serverUrl;
                    string token;
                    ReadConnection(out serverUrl, out token);
                    if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(token))
                    {
                        _serverEnabled = false;
                        await Task.Delay(TimeSpan.FromSeconds(15));
                        continue;
                    }

                    using (var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        serverUrl + "/api/runtime/v1/recharge-query/config"))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        request.Headers.TryAddWithoutValidation("Accept", "application/json");
                        using (var response = await Http.SendAsync(request))
                        {
                            var body = await response.Content.ReadAsStringAsync();
                            if (!response.IsSuccessStatusCode)
                            {
                                throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body));
                            }
                            var json = JObject.Parse(body);
                            var enabled = Convert.ToBoolean(json["enabled"]);
                            if (_serverEnabled != enabled || _lastConfigSuccess == DateTime.MinValue)
                            {
                                Log.Info("充值结果自动查询服务端开关已更新: enabled=" + enabled);
                            }
                            _serverEnabled = enabled;
                            _lastConfigSuccess = DateTime.Now;
                        }
                    }
                    await Task.Delay(TimeSpan.FromMinutes(1));
                }
                catch (Exception ex)
                {
                    // 短暂网络失败继续保留最近一次成功开关，但超过十分钟后停止接管，
                    // 避免服务端长期不可达时压住普通AI回复。
                    if (_lastConfigSuccess == DateTime.MinValue
                        || DateTime.Now - _lastConfigSuccess > TimeSpan.FromMinutes(10))
                    {
                        _serverEnabled = false;
                    }
                    Log.ErrorWithMaxCount("刷新充值查询服务端开关失败：" + Safe(ex.Message), 20);
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
            }
        }

        private static string MessageText(QNChatMessage message)
        {
            if (message == null) return string.Empty;
            var parts = new List<string>();
            if (message.originalData != null)
            {
                if (!string.IsNullOrWhiteSpace(message.originalData.text)) parts.Add(message.originalData.text.Trim());
                if (message.originalData.header != null
                    && !string.IsNullOrWhiteSpace(message.originalData.header.summary))
                {
                    parts.Add(message.originalData.header.summary.Trim());
                }
            }
            if (!string.IsNullOrWhiteSpace(message.summary)) parts.Add(message.summary.Trim());
            return Regex.Replace(
                string.Join(" ", parts.Distinct(StringComparer.OrdinalIgnoreCase)),
                @"\s+",
                " ").Trim();
        }

        private static void ReadConnection(out string serverUrl, out string token)
        {
            serverUrl = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(
                "ControlPlaneUrl",
                "ai-control-plane",
                string.Empty);
            token = BotLib.Db.Sqlite.PersistentParams.GetParam2Key(
                "ControlPlaneClientToken",
                "ai-control-plane",
                string.Empty);
            serverUrl = (serverUrl ?? string.Empty).Trim().TrimEnd('/');
            if (serverUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                serverUrl = serverUrl.Substring(0, serverUrl.Length - 3).TrimEnd('/');
            }
            token = (token ?? string.Empty).Trim();
        }

        private static void Cleanup()
        {
            var now = DateTime.Now;
            foreach (var pair in HandledMessages)
            {
                if (pair.Value != null && pair.Value.Until >= now) continue;
                GateEntry ignored;
                HandledMessages.TryRemove(pair.Key, out ignored);
            }
            foreach (var pair in HumanNotifyDedup)
            {
                if (pair.Value >= now) continue;
                DateTime ignored;
                HumanNotifyDedup.TryRemove(pair.Key, out ignored);
            }
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string Normalize(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }

        private static string Safe(string value)
        {
            value = Regex.Replace((value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(), @"\s+", " ");
            return value.Length <= 300 ? value : value.Substring(0, 300) + "...";
        }
    }
}
