using Bot.ShopScope;
using BotLib;
using BotLib.Db.Sqlite;
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
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal static class ManualRechargeAssistantService
    {
        private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(12);
        private static readonly TimeSpan ConfigTtl = TimeSpan.FromSeconds(45);
        private static readonly ConcurrentDictionary<string, Session> Sessions =
            new ConcurrentDictionary<string, Session>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> RecentSubmissions =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly SemaphoreSlim ConfigGate = new SemaphoreSlim(1, 1);
        private static readonly HttpClient Http = CreateHttpClient();

        private static readonly Regex PhoneRegex = new Regex(
            @"(?<!\d)(1\d{10})(?!\d)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex CodeRegex = new Regex(
            @"(?<!\d)(\d{6})(?!\d)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RedeemCodeRegex = new Regex(
            @"(?:会员)?兑换码\s*[:：]\s*([A-Za-z0-9_-]{6,64})(?![A-Za-z0-9_-])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ConsentRegex = new Regex(
            @"帮我充|帮我充值|你帮我充|麻烦.{0,8}(?:充|充值)|(?:同意|可以).{0,8}代充|代充.{0,8}(?:可以|同意)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex CancelRegex = new Regex(
            @"不用代充|不要代充|不用帮我充|不要帮我充|取消代充|我自己充|自己操作",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static volatile bool _serverEnabled;
        private static DateTime _configCheckedAt = DateTime.MinValue;

        private enum Stage
        {
            AwaitPhone,
            AwaitCode,
            AwaitAccountSelection,
            AwaitMatchedConfirmation
        }

        private sealed class AccountCandidate
        {
            public string Nickname;
            public string UserId;
            public string Selector;
        }

        private sealed class Session
        {
            public string ShopKey;
            public string Seller;
            public string Buyer;
            public string OrderId;
            public string Phone;
            public string Code;
            public string OperationId;
            public long RecordId;
            public Stage Stage;
            public DateTime UpdatedUtc;
            public KugouAccountEvidence Evidence;
            public List<AccountCandidate> Accounts = new List<AccountCandidate>();
            public AccountCandidate Selected;
        }

        public static async Task<bool> TryHandleAsync(
            QN qn,
            BuyerMessageBurstItem item,
            CancellationToken cancellationToken)
        {
            if (qn == null || item == null || !item.VisionDecisionIsReplyable()) return false;
            Cleanup();
            if (!await IsServerEnabledAsync(cancellationToken).ConfigureAwait(false)) return false;

            var seller = (item.SellerNick ?? string.Empty).Trim();
            var buyer = (item.BuyerNick ?? string.Empty).Trim();
            var text = (item.DisplayText ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0 || text.Length == 0) return false;

            ShopContext shop;
            try { shop = ShopContextLocator.ResolveRuntimeBySellerNick(seller); }
            catch { shop = null; }
            if (shop == null) return false;

            var key = Key(shop.ShopKey, seller, buyer);
            Session state;
            if (!Sessions.TryGetValue(key, out state) || state == null)
            {
                if (!HasExplicitConsent(text)) return false;

                string orderId;
                if (!TryFindRecentRedeemCode(seller, buyer, out orderId)) return false;

                KugouAccountEvidence evidence;
                if (!RecentVisualContextService.TryGetRecentKugouAccountEvidence(seller, buyer, out evidence)
                    || evidence == null)
                {
                    return false;
                }

                state = new Session
                {
                    ShopKey = shop.ShopKey,
                    Seller = seller,
                    Buyer = buyer,
                    OrderId = orderId,
                    OperationId = "mr_" + Guid.NewGuid().ToString("N"),
                    Stage = Stage.AwaitPhone,
                    UpdatedUtc = DateTime.UtcNow,
                    Evidence = evidence
                };
                Sessions[key] = state;

                try
                {
                    var valid = await PostControlPlaneAsync(
                        "/api/runtime/v1/manual-recharge/check-order",
                        new JObject { ["order_id"] = state.OrderId },
                        cancellationToken).ConfigureAwait(false);
                    if (valid.Value<bool?>("valid") != true)
                        throw new Exception("兑换码未通过有效性校验");
                }
                catch (Exception ex)
                {
                    await StopAndHandoffAsync(qn, key, state, "兑换码校验", ex, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }
            }

            state.UpdatedUtc = DateTime.UtcNow;
            try
            {
                if (CancelRegex.IsMatch(text))
                {
                    Session ignored;
                    Sessions.TryRemove(key, out ignored);
                    await SendAsync(qn, buyer, "好的，已取消自动代充，不会继续提交。", cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                if (state.Stage == Stage.AwaitPhone)
                {
                    var phone = Extract(PhoneRegex, text);
                    if (phone.Length == 0)
                    {
                        await SendAsync(qn, buyer, "可以的，手机号发我，我这边帮您提交。", cancellationToken)
                            .ConfigureAwait(false);
                        return true;
                    }

                    state.Phone = phone;
                    var interval = await PostControlPlaneAsync(
                        "/api/runtime/v1/manual-recharge/check-send-interval",
                        new JObject { ["phone"] = state.Phone },
                        cancellationToken).ConfigureAwait(false);
                    if (interval.Value<bool?>("allowed") != true)
                        throw new Exception("验证码发送过于频繁，当前不能继续自动提交");

                    await PostControlPlaneAsync(
                        "/api/runtime/v1/manual-recharge/send-code",
                        new JObject
                        {
                            ["phone"] = state.Phone,
                            ["order_id"] = state.OrderId
                        },
                        cancellationToken).ConfigureAwait(false);
                    state.Stage = Stage.AwaitCode;
                    await SendAsync(
                        qn,
                        buyer,
                        "验证码已经发到您手机了，收到后把6位验证码发我。",
                        cancellationToken).ConfigureAwait(false);
                    return true;
                }

                if (state.Stage == Stage.AwaitCode)
                {
                    var code = Extract(CodeRegex, text);
                    if (code.Length == 0)
                    {
                        await SendAsync(qn, buyer, "请把短信里的6位验证码发我。", cancellationToken)
                            .ConfigureAwait(false);
                        return true;
                    }

                    state.Code = code;
                    var result = await PostControlPlaneAsync(
                        "/api/runtime/v1/manual-recharge/accounts",
                        new JObject
                        {
                            ["phone"] = state.Phone,
                            ["code"] = state.Code,
                            ["order_id"] = state.OrderId,
                            ["operation_id"] = state.OperationId
                        },
                        cancellationToken).ConfigureAwait(false);
                    var status = (result.Value<string>("status") ?? string.Empty).Trim().ToLowerInvariant();
                    if (status == "code_error") throw new Exception("验证码失效或错误");
                    if (status == "new_user") throw new Exception("当前手机号返回新用户状态，需要人工核对");

                    state.RecordId = result.Value<long?>("record_id") ?? 0;
                    state.Accounts = ParseAccounts(result["accounts"] as JArray);
                    if (state.Accounts.Count == 0)
                        throw new Exception("账号接口未返回可确认账号");

                    var matches = FindExactMatches(state.Accounts, state.Evidence);
                    if (matches.Count == 1)
                    {
                        state.Selected = matches[0];
                        state.Stage = Stage.AwaitMatchedConfirmation;
                        await SendAsync(
                            qn,
                            buyer,
                            "已和您刚才设备截图里的酷狗账号核对一致：" + DisplayAccount(state.Selected)
                                + "。确认给这个账号充值的话，请回复“确认充值”。",
                            cancellationToken).ConfigureAwait(false);
                        return true;
                    }

                    state.Stage = Stage.AwaitAccountSelection;
                    var prefix = matches.Count > 1
                        ? "设备截图信息对应到多个候选账号，不能自动选择。"
                        : "当前返回账号没有和设备截图形成唯一完全匹配，不能自动选择。";
                    await SendAsync(
                        qn,
                        buyer,
                        prefix + "\n" + BuildAccountPrompt(state.Accounts),
                        cancellationToken).ConfigureAwait(false);
                    return true;
                }

                if (state.Stage == Stage.AwaitMatchedConfirmation)
                {
                    if (!IsFinalConfirmation(text))
                    {
                        await SendAsync(
                            qn,
                            buyer,
                            "为避免充错账号，请明确回复“确认充值”后我再提交。",
                            cancellationToken).ConfigureAwait(false);
                        return true;
                    }
                    await SubmitFinalAsync(qn, key, state, state.Selected, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }

                if (state.Stage == Stage.AwaitAccountSelection)
                {
                    var selected = ParseExplicitSelection(text, state.Accounts);
                    if (selected == null)
                    {
                        await SendAsync(
                            qn,
                            buyer,
                            "为避免充错账号，请回复账号前的数字并明确确认，例如“1确认充值”。",
                            cancellationToken).ConfigureAwait(false);
                        return true;
                    }
                    await SubmitFinalAsync(qn, key, state, selected, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await StopAndHandoffAsync(qn, key, state, StageName(state.Stage), ex, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            return false;
        }

        private static async Task SubmitFinalAsync(
            QN qn,
            string key,
            Session state,
            AccountCandidate selected,
            CancellationToken cancellationToken)
        {
            if (selected == null) throw new Exception("最终账号选择为空");
            var duplicateKey = Hash(state.ShopKey + "|" + state.Seller + "|" + state.Buyer + "|" + state.OrderId);
            DateTime until;
            if (RecentSubmissions.TryGetValue(duplicateKey, out until) && until > DateTime.UtcNow)
                throw new Exception("两分钟内检测到重复充值提交");

            var result = await PostControlPlaneAsync(
                "/api/runtime/v1/manual-recharge/submit",
                new JObject
                {
                    ["phone"] = state.Phone,
                    ["code"] = state.Code,
                    ["order_id"] = state.OrderId,
                    ["operation_id"] = state.OperationId,
                    ["selector"] = selected.Selector,
                    ["record_id"] = state.RecordId
                },
                cancellationToken).ConfigureAwait(false);

            if (result.Value<bool?>("duplicate") == true)
                throw new Exception("服务端检测到重复充值提交");
            if (result.Value<bool?>("submitted") != true)
                throw new Exception("充值接口未确认提交成功");

            RecentSubmissions[duplicateKey] = DateTime.UtcNow.AddMinutes(2);
            Session ignored;
            Sessions.TryRemove(key, out ignored);
            state.Phone = string.Empty;
            state.Code = string.Empty;
            await SendAsync(
                qn,
                state.Buyer,
                "已经提交充值，通常几分钟后到账，稍后多刷新几次设备界面。",
                cancellationToken).ConfigureAwait(false);
        }

        private static List<AccountCandidate> ParseAccounts(JArray items)
        {
            var result = new List<AccountCandidate>();
            if (items == null) return result;
            foreach (var item in items.OfType<JObject>().Take(12))
            {
                var selector = (item.Value<string>("selector") ?? string.Empty).Trim().ToLowerInvariant();
                if (!Regex.IsMatch(selector, @"^zh([1-9]|1[0-2])$")) continue;
                var nickname = CleanAccount(item.Value<string>("nickname"), 120);
                var userId = CleanAccount(item.Value<string>("userid"), 120);
                if (nickname.Length == 0 && userId.Length == 0) continue;
                result.Add(new AccountCandidate
                {
                    Nickname = nickname,
                    UserId = userId,
                    Selector = selector
                });
            }
            return result;
        }

        private static List<AccountCandidate> FindExactMatches(
            IEnumerable<AccountCandidate> accounts,
            KugouAccountEvidence evidence)
        {
            if (evidence == null) return new List<AccountCandidate>();
            var nickname = NormalizeAccount(evidence.Nickname);
            var userId = NormalizeAccount(evidence.UserId);
            return (accounts ?? Enumerable.Empty<AccountCandidate>())
                .Where(x =>
                    (userId.Length > 0 && NormalizeAccount(x.UserId) == userId)
                    || (nickname.Length > 0 && NormalizeAccount(x.Nickname) == nickname))
                .ToList();
        }

        private static AccountCandidate ParseExplicitSelection(
            string text,
            IList<AccountCandidate> accounts)
        {
            if (!IsFinalConfirmation(text)) return null;
            var match = Regex.Match(text ?? string.Empty, @"(?<!\d)(\d{1,2})(?!\d)");
            int index;
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out index)) return null;
            return index >= 1 && index <= accounts.Count ? accounts[index - 1] : null;
        }

        private static bool IsFinalConfirmation(string text)
        {
            var compact = Regex.Replace((text ?? string.Empty), @"\s+", string.Empty);
            return compact.Contains("确认充值")
                || compact.Contains("确认给这个账号充")
                || compact.Contains("确认给该账号充");
        }

        private static string BuildAccountPrompt(IList<AccountCandidate> accounts)
        {
            var lines = new List<string> { "请确认要充值的账号：" };
            for (var i = 0; i < accounts.Count; i++)
                lines.Add((i + 1) + ". " + DisplayAccount(accounts[i]));
            lines.Add("请回复数字并明确确认，例如“1确认充值”。");
            return string.Join("\n", lines);
        }

        private static string DisplayAccount(AccountCandidate account)
        {
            var name = MaskName(account == null ? string.Empty : account.Nickname);
            var id = MaskId(account == null ? string.Empty : account.UserId);
            if (name.Length == 0) return "ID " + id;
            if (id.Length == 0) return name;
            return name + "（ID " + id + "）";
        }

        private static bool HasExplicitConsent(string text)
        {
            text = text ?? string.Empty;
            return ConsentRegex.IsMatch(text) && !CancelRegex.IsMatch(text);
        }

        private static bool TryFindRecentRedeemCode(string seller, string buyer, out string orderId)
        {
            orderId = string.Empty;
            var turns = ConversationContextStore.GetRecentTurns(seller, buyer, string.Empty, 24);
            foreach (var turn in turns
                .Where(x => x != null && x.Role == "assistant" && !x.Withdrawn)
                .OrderByDescending(x => x.Timestamp))
            {
                if (turn.Timestamp != DateTime.MinValue && turn.Timestamp < DateTime.Now.AddDays(-3)) continue;
                var match = RedeemCodeRegex.Match(turn.Text ?? string.Empty);
                if (!match.Success) continue;
                orderId = match.Groups[1].Value.Trim();
                return orderId.Length >= 6;
            }
            return false;
        }

        private static async Task<bool> IsServerEnabledAsync(CancellationToken cancellationToken)
        {
            if (_configCheckedAt != DateTime.MinValue
                && DateTime.UtcNow - _configCheckedAt < ConfigTtl)
            {
                return _serverEnabled;
            }

            await ConfigGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_configCheckedAt != DateTime.MinValue
                    && DateTime.UtcNow - _configCheckedAt < ConfigTtl)
                {
                    return _serverEnabled;
                }

                try
                {
                    var response = await SendControlPlaneAsync(
                        HttpMethod.Get,
                        "/api/runtime/v1/manual-recharge/config",
                        null,
                        cancellationToken).ConfigureAwait(false);
                    _serverEnabled = response.Value<bool?>("enabled") == true;
                }
                catch (Exception ex)
                {
                    _serverEnabled = false;
                    Log.ErrorWithMaxCount(
                        "刷新人工代充安全开关失败，已按关闭处理: " + RedactSensitive(ex.Message),
                        20);
                }
                _configCheckedAt = DateTime.UtcNow;
                return _serverEnabled;
            }
            finally
            {
                ConfigGate.Release();
            }
        }

        private static Task<JObject> PostControlPlaneAsync(
            string path,
            JObject payload,
            CancellationToken cancellationToken)
        {
            return SendControlPlaneAsync(HttpMethod.Post, path, payload, cancellationToken);
        }

        private static async Task<JObject> SendControlPlaneAsync(
            HttpMethod method,
            string path,
            JObject payload,
            CancellationToken cancellationToken)
        {
            string serverUrl;
            string token;
            ReadConnection(out serverUrl, out token);
            if (serverUrl.Length == 0 || token.Length == 0)
                throw new Exception("统一API服务地址或客户端令牌未配置");

            using (var request = new HttpRequestMessage(method, serverUrl + path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-manual-recharge/1.0");
                if (payload != null)
                {
                    request.Content = new StringContent(
                        payload.ToString(Formatting.None),
                        Encoding.UTF8,
                        "application/json");
                }
                using (var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if ((int)response.StatusCode == 409
                            && body.IndexOf("manual_recharge_disabled", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _serverEnabled = false;
                            _configCheckedAt = DateTime.UtcNow;
                        }
                        throw new Exception("统一API返回 HTTP " + (int)response.StatusCode);
                    }
                    try { return JObject.Parse(body); }
                    catch { throw new Exception("统一API返回未知结构"); }
                }
            }
        }

        private static void ReadConnection(out string serverUrl, out string token)
        {
            serverUrl = PersistentParams.GetParam2Key(
                "ControlPlaneUrl", "ai-control-plane", string.Empty);
            token = PersistentParams.GetParam2Key(
                "ControlPlaneClientToken", "ai-control-plane", string.Empty);
            serverUrl = (serverUrl ?? string.Empty).Trim().TrimEnd('/');
            if (serverUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                serverUrl = serverUrl.Substring(0, serverUrl.Length - 3).TrimEnd('/');
            token = (token ?? string.Empty).Trim();
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

        private static async Task SendAsync(
            QN qn,
            string buyer,
            string text,
            CancellationToken cancellationToken)
        {
            var target = BuyerIdentityAliasService.ResolveInternalNick(
                qn.Seller == null ? string.Empty : qn.Seller.Nick,
                buyer);
            var answer = BotOutboundMessageFormatter.EnsureAiMarker(text ?? string.Empty);
            if (!await qn.SendTextWithRetryAsync(target, answer, 1, cancellationToken).ConfigureAwait(false))
                throw new Exception("代充安全提示消息发送失败");
            ReplyDeduplicationService.RememberDelivered(
                qn.Seller == null ? string.Empty : qn.Seller.Nick,
                buyer,
                answer);
        }

        private static async Task StopAndHandoffAsync(
            QN qn,
            string key,
            Session state,
            string stage,
            Exception error,
            CancellationToken cancellationToken)
        {
            Session ignored;
            Sessions.TryRemove(key, out ignored);
            if (state != null)
            {
                state.Phone = string.Empty;
                state.Code = string.Empty;
            }

            var reason = RedactSensitive(error == null ? "未知异常" : error.Message);
            Log.Info("人工代充已安全停止并转人工: seller="
                + SafeText(state == null ? string.Empty : state.Seller, 80)
                + ", buyer=" + SafeText(state == null ? string.Empty : state.Buyer, 80)
                + ", stage=" + SafeText(stage, 60)
                + ", orderSuffix=" + OrderSuffix(state == null ? string.Empty : state.OrderId)
                + ", reason=" + SafeText(reason, 180));

            try
            {
                await SendAsync(
                    qn,
                    state == null ? string.Empty : state.Buyer,
                    "自动代充这一步没有通过安全校验，我已经停止自动操作，请稍等人工客服继续处理。",
                    cancellationToken).ConfigureAwait(false);
            }
            catch { }

            try
            {
                var decision = new AutoReplyRuleDecision
                {
                    Matched = true,
                    AllowAutoReply = false,
                    UseAiReply = false,
                    IsOffHours = false,
                    HitKeyword = "人工代充",
                    Reason = "人工代充阶段=" + SafeText(stage, 60)
                        + "；兑换码尾号=" + OrderSuffix(state == null ? string.Empty : state.OrderId)
                        + "；错误=" + SafeText(reason, 160)
                };
                await WeComAppBridgeClient.SendNotificationAsync(
                    state == null ? string.Empty : state.Seller,
                    state == null ? string.Empty : state.Buyer,
                    "人工代充安全转人工；" + decision.Reason,
                    decision,
                    false).ConfigureAwait(false);
            }
            catch (Exception notifyEx)
            {
                Log.ErrorWithMaxCount(
                    "人工代充企业微信提醒失败: " + RedactSensitive(notifyEx.Message),
                    10);
            }
        }

        private static string StageName(Stage stage)
        {
            switch (stage)
            {
                case Stage.AwaitPhone: return "等待手机号";
                case Stage.AwaitCode: return "等待验证码";
                case Stage.AwaitAccountSelection: return "等待账号选择";
                case Stage.AwaitMatchedConfirmation: return "等待匹配账号确认";
                default: return "未知阶段";
            }
        }

        private static string Extract(Regex regex, string text)
        {
            var match = regex.Match(text ?? string.Empty);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string NormalizeAccount(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", string.Empty);
        }

        private static string CleanAccount(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static string MaskName(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;
            if (value.Length == 1) return "*";
            return value.Substring(0, 1) + new string('*', Math.Min(4, value.Length - 1));
        }

        private static string MaskId(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;
            if (value.Length <= 4) return new string('*', value.Length);
            return value.Substring(0, 2) + "***" + value.Substring(value.Length - 2);
        }

        private static string OrderSuffix(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= 4 ? "****" : "****" + value.Substring(value.Length - 4);
        }

        private static string RedactSensitive(string value)
        {
            value = value ?? string.Empty;
            value = PhoneRegex.Replace(value, "[手机号]");
            value = CodeRegex.Replace(value, "[验证码]");
            value = RedeemCodeRegex.Replace(value, "兑换码：[已脱敏]");
            return SafeText(value, 260);
        }

        private static string SafeText(string value, int max)
        {
            value = Regex.Replace(
                (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(),
                @"\s+",
                " ");
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(
                        sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static string Key(string shopKey, string seller, string buyer)
        {
            return (shopKey ?? string.Empty) + "#"
                + NormalizeAccount(seller) + "#"
                + NormalizeAccount(buyer);
        }

        private static void Cleanup()
        {
            var cutoff = DateTime.UtcNow - StateTtl;
            foreach (var pair in Sessions)
            {
                if (pair.Value != null && pair.Value.UpdatedUtc >= cutoff) continue;
                Session ignored;
                Sessions.TryRemove(pair.Key, out ignored);
            }
            foreach (var pair in RecentSubmissions)
            {
                if (pair.Value >= DateTime.UtcNow) continue;
                DateTime ignored;
                RecentSubmissions.TryRemove(pair.Key, out ignored);
            }
        }

        private static bool VisionDecisionIsReplyable(this BuyerMessageBurstItem item)
        {
            return item != null
                && item.VisionDecision != null
                && item.VisionDecision.Kind != VisionDecisionKind.Skip;
        }
    }
}
