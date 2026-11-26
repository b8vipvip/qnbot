using BotLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Bot.ShopScope
{
    internal sealed class ShopTokenBindingClaimResult
    {
        public bool Success { get; set; }
        public bool Conflict { get; set; }
        public bool Rebound { get; set; }
        public string BoundShopKey { get; set; }
        public string Error { get; set; }
    }

    internal sealed class ShopRuntimeCredentialResolution
    {
        public bool Success { get; set; }
        public bool TokenChanged { get; set; }
        public ShopContext Shop { get; set; }
        public string ServerUrl { get; set; }
        public string Token { get; set; }
        public string TokenFingerprint { get; set; }
        public string Error { get; set; }
    }

    internal static class ShopRuntimeCredentialAuthority
    {
        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();

        public static bool ResolveForCurrentShop(
            out ShopContext shop,
            out string serverUrl,
            out string token,
            out string error)
        {
            shop = ShopSettingsScope.Current;
            serverUrl = string.Empty;
            token = string.Empty;
            error = string.Empty;
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey))
            {
                error = "当前请求没有可确认的 ShopKey，已阻止控制面调用";
                return false;
            }

            var connection = new ShopControlPlaneConnectionStore(shop, Paths);
            serverUrl = connection.GetServerUrl();
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                error = "当前店铺没有可用的控制面地址";
                return false;
            }
            if (!connection.TryGetToken(out token, out error) || string.IsNullOrWhiteSpace(token))
            {
                error = string.IsNullOrWhiteSpace(error) ? "当前店铺没有可用的客户端令牌" : error;
                return false;
            }
            token = token.Trim();
            return true;
        }

        public static async Task<ShopRuntimeCredentialResolution> RecoverUnauthorizedAsync(
            ShopContext expectedShop,
            string rejectedToken)
        {
            var result = new ShopRuntimeCredentialResolution { Shop = expectedShop };
            if (expectedShop == null || string.IsNullOrWhiteSpace(expectedShop.ShopKey))
            {
                result.Error = "401恢复缺少当前 ShopKey";
                return result;
            }

            var ambient = ShopSettingsScope.Current;
            if (ambient == null || !string.Equals(ambient.ShopKey, expectedShop.ShopKey, StringComparison.Ordinal))
            {
                result.Error = "401恢复时活动 ShopKey 已变化，已阻止跨店凭据回退";
                Log.Info("401同店令牌恢复: shopKey=" + expectedShop.ShopKey
                    + ", rejectedFingerprint=" + Fingerprint(rejectedToken)
                    + ", result=blocked_shop_scope_changed");
                return result;
            }

            var connection = new ShopControlPlaneConnectionStore(expectedShop, Paths);
            result.ServerUrl = connection.GetServerUrl();
            string refreshed;
            string error;
            if (!connection.TryGetToken(out refreshed, out error) || string.IsNullOrWhiteSpace(refreshed))
            {
                result.Error = string.IsNullOrWhiteSpace(error) ? "同店令牌重读失败" : error;
                Log.Info("401同店令牌恢复: shopKey=" + expectedShop.ShopKey
                    + ", rejectedFingerprint=" + Fingerprint(rejectedToken)
                    + ", result=token_missing");
                return result;
            }

            refreshed = refreshed.Trim();
            result.Token = refreshed;
            result.TokenFingerprint = Fingerprint(refreshed);
            result.TokenChanged = !string.Equals(
                (rejectedToken ?? string.Empty).Trim(),
                refreshed,
                StringComparison.Ordinal);
            if (result.TokenChanged)
            {
                result.Success = true;
                Log.Info("401同店令牌恢复: shopKey=" + expectedShop.ShopKey
                    + ", rejectedFingerprint=" + Fingerprint(rejectedToken)
                    + ", refreshedFingerprint=" + result.TokenFingerprint
                    + ", tokenChanged=true, result=retry_same_shop");
                return result;
            }

            var claim = await ShopTokenBindingService.ClaimAsync(expectedShop, refreshed, false).ConfigureAwait(false);
            result.Success = claim != null && claim.Success;
            result.Error = result.Success ? string.Empty : (claim == null ? "同店绑定验证没有返回结果" : claim.Error);
            Log.Info("401同店令牌恢复: shopKey=" + expectedShop.ShopKey
                + ", rejectedFingerprint=" + Fingerprint(rejectedToken)
                + ", refreshedFingerprint=" + result.TokenFingerprint
                + ", tokenChanged=false, claimSuccess=" + result.Success
                + ", conflict=" + (claim != null && claim.Conflict)
                + ", result=" + (result.Success ? "retry_same_shop" : "fail_closed"));
            return result;
        }

        /// <summary>
        /// Retry one authenticated request only after re-reading credentials for the same ShopKey.
        /// The send callback must create a fresh HttpRequestMessage on every attempt.
        /// </summary>
        public static async Task<HttpResponseMessage> SendWithUnauthorizedRecoveryAsync(
            ShopContext expectedShop,
            string rejectedToken,
            Func<string, Task<HttpResponseMessage>> send)
        {
            if (expectedShop == null) throw new ArgumentNullException(nameof(expectedShop));
            if (send == null) throw new ArgumentNullException(nameof(send));

            var first = await send(rejectedToken).ConfigureAwait(false);
            if (first == null) throw new InvalidOperationException("控制面请求没有返回 HTTP 响应。");
            if (first.StatusCode != HttpStatusCode.Unauthorized) return first;

            var firstBody = await ReadResponseBodySafeAsync(first).ConfigureAwait(false);
            first.Dispose();
            ShopRuntimeCredentialResolution recovered;
            try
            {
                recovered = await RecoverUnauthorizedAsync(expectedShop, rejectedToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                recovered = new ShopRuntimeCredentialResolution
                {
                    Shop = expectedShop,
                    Success = false,
                    Error = SafeError(ex.Message, 220)
                };
            }

            if (recovered == null || !recovered.Success || string.IsNullOrWhiteSpace(recovered.Token))
            {
                var recoveryError = recovered == null ? "同店令牌恢复没有返回结果" : SafeError(recovered.Error, 220);
                var failure = "HTTP 401：服务端拒绝本店客户端令牌；同店凭据恢复失败。"
                    + (string.IsNullOrWhiteSpace(firstBody) ? string.Empty : " 首次响应：" + SafeError(firstBody, 180))
                    + (string.IsNullOrWhiteSpace(recoveryError) ? string.Empty : " 恢复结果：" + recoveryError)
                    + "。请检查新服务器的 Bot 客户端令牌记录是否存在且已启用。";
                ShopTokenBindingService.ReportFailure(expectedShop, failure);
                throw new UnauthorizedAccessException(failure);
            }

            var retry = await send(recovered.Token).ConfigureAwait(false);
            if (retry == null) throw new InvalidOperationException("同店令牌恢复重试没有返回 HTTP 响应。");
            if (retry.StatusCode == HttpStatusCode.Unauthorized)
            {
                var retryBody = await ReadResponseBodySafeAsync(retry).ConfigureAwait(false);
                retry.Dispose();
                var failure = "HTTP 401：同店令牌恢复后重试仍被服务端拒绝。"
                    + (string.IsNullOrWhiteSpace(retryBody) ? string.Empty : " 响应：" + SafeError(retryBody, 180))
                    + "。该令牌可能未登记在当前服务器，或已被停用。";
                ShopTokenBindingService.ReportFailure(expectedShop, failure);
                throw new UnauthorizedAccessException(failure);
            }

            if (retry.IsSuccessStatusCode)
                ShopTokenBindingService.ReportSuccess(expectedShop);
            return retry;
        }

        private static async Task<string> ReadResponseBodySafeAsync(HttpResponseMessage response)
        {
            try
            {
                return response == null || response.Content == null
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch { return string.Empty; }
        }

        private static string SafeError(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static string Fingerprint(string token)
        {
            token = (token ?? string.Empty).Trim();
            if (token.Length == 0) return "none";
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            unchecked
            {
                foreach (var ch in token)
                {
                    hash ^= (byte)(ch & 0xff);
                    hash *= prime;
                    hash ^= (byte)(ch >> 8);
                    hash *= prime;
                }
            }
            return "token#" + hash.ToString("x16").Substring(0, 10);
        }
    }

    internal static class ShopTokenBindingService
    {
        private sealed class ConnectionState
        {
            public DateTime LastSuccessUtc;
            public DateTime LastFailureUtc;
            public string LastError;
            public bool Conflict;
            public string BoundShopKey;
        }

        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);
        private static readonly ConcurrentDictionary<string, ConnectionState> States =
            new ConcurrentDictionary<string, ConnectionState>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> PromptedAt =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> UnauthorizedBackoffUntil =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> PromptOpen =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        public static void ReportSuccess(ShopContext shop)
        {
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return;
            var state = States.GetOrAdd(shop.ShopKey, _ => new ConnectionState());
            state.LastSuccessUtc = DateTime.UtcNow;
            state.LastError = string.Empty;
            state.Conflict = false;
            state.BoundShopKey = shop.ShopKey;
            DateTime ignoredBackoff;
            UnauthorizedBackoffUntil.TryRemove(shop.ShopKey, out ignoredBackoff);
        }

        public static void ReportFailure(ShopContext shop, string error)
        {
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return;
            var state = States.GetOrAdd(shop.ShopKey, _ => new ConnectionState());
            state.LastFailureUtc = DateTime.UtcNow;
            state.LastError = Safe(error, 240);
            if ((error ?? string.Empty).IndexOf("HTTP 401", StringComparison.OrdinalIgnoreCase) >= 0
                || (error ?? string.Empty).IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
                UnauthorizedBackoffUntil[shop.ShopKey] = DateTime.UtcNow.AddMinutes(2);
        }

        public static bool ShouldAttemptSync(ShopContext shop)
        {
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return false;
            DateTime until;
            if (!UnauthorizedBackoffUntil.TryGetValue(shop.ShopKey, out until)) return true;
            if (until <= DateTime.UtcNow)
            {
                UnauthorizedBackoffUntil.TryRemove(shop.ShopKey, out until);
                return true;
            }
            return false;
        }

        public static void ResetFailure(ShopContext shop)
        {
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return;
            DateTime ignored;
            UnauthorizedBackoffUntil.TryRemove(shop.ShopKey, out ignored);
            ConnectionState state;
            if (States.TryGetValue(shop.ShopKey, out state))
            {
                state.LastFailureUtc = DateTime.MinValue;
                state.LastError = string.Empty;
                state.Conflict = false;
            }
        }

        public static void ReportConflict(ShopContext shop, string boundShopKey, string error)
        {
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey)) return;
            var state = States.GetOrAdd(shop.ShopKey, _ => new ConnectionState());
            state.LastFailureUtc = DateTime.UtcNow;
            state.LastError = Safe(error, 240);
            state.Conflict = true;
            state.BoundShopKey = (boundShopKey ?? string.Empty).Trim();
        }

        public static string GetStatusText(ShopContext shop)
        {
            if (shop == null) return "识别店铺中";
            try
            {
                var connection = new ShopControlPlaneConnectionStore(shop, Paths);
                if (string.IsNullOrWhiteSpace(connection.GetServerUrl())) return "服务端未配置";
                if (!connection.HasToken) return "未绑定Token";

                ConnectionState state;
                if (!States.TryGetValue(shop.ShopKey, out state)) return "连接中";
                if (state.Conflict) return "令牌冲突";
                // Explicit 401 is a credential problem, not an unreachable control plane.
                // Prefer the newer failure over a still-recent prior success.
                if (state.LastFailureUtc > state.LastSuccessUtc)
                {
                    if (!string.IsNullOrWhiteSpace(state.LastError)
                        && state.LastError.IndexOf("HTTP 401", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "令牌已失效";
                    return "连接失败";
                }
                if (state.LastSuccessUtc != DateTime.MinValue
                    && DateTime.UtcNow - state.LastSuccessUtc <= TimeSpan.FromSeconds(20))
                    return "已连接";
                return "连接中";
            }
            catch
            {
                return "检测失败";
            }
        }

        public static string GetStatusToolTip(ShopContext shop)
        {
            if (shop == null) return "正在识别当前店铺。";
            try
            {
                var connection = new ShopControlPlaneConnectionStore(shop, Paths);
                var server = connection.GetServerUrl();
                ConnectionState state;
                States.TryGetValue(shop.ShopKey, out state);
                var suffix = state == null || string.IsNullOrWhiteSpace(state.LastError)
                    ? string.Empty
                    : "\n最近错误：" + state.LastError;
                return "服务端：" + server
                    + "\nShopKey：" + shop.ShopKey
                    + "\nToken：" + (connection.HasToken ? "已绑定" : "未绑定")
                    + suffix;
            }
            catch (Exception ex)
            {
                return "服务端状态检测失败：" + Safe(ex.Message, 180);
            }
        }

        public static async Task<ShopTokenBindingClaimResult> ClaimAsync(
            ShopContext shop,
            string token,
            bool force)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            token = (token ?? string.Empty).Trim();
            if (token.Length == 0)
                return new ShopTokenBindingClaimResult { Success = false, Error = "Bot Token 为空" };

            var connection = new ShopControlPlaneConnectionStore(shop, Paths);
            var serverUrl = connection.GetServerUrl();
            if (string.IsNullOrWhiteSpace(serverUrl))
                return new ShopTokenBindingClaimResult { Success = false, Error = "Bot 服务端地址为空" };

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
            using (var http = new HttpClient(handler))
            using (var request = new HttpRequestMessage(
                HttpMethod.Post,
                serverUrl.TrimEnd('/') + "/api/runtime/v1/shop-binding/claim"))
            {
                http.Timeout = TimeSpan.FromSeconds(15);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-shop-binding/1.0");
                request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey);
                var payload = new JObject
                {
                    ["force"] = force,
                    ["seller"] = shop.DisplayName ?? string.Empty
                };
                request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");

                using (var response = await http.SendAsync(request).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.Conflict)
                    {
                        var bound = ExtractBoundShopKey(body);
                        ReportConflict(shop, bound, body);
                        return new ShopTokenBindingClaimResult
                        {
                            Success = false,
                            Conflict = true,
                            BoundShopKey = bound,
                            Error = Safe(body, 240)
                        };
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        ReportFailure(shop, "HTTP " + (int)response.StatusCode + " " + Safe(body, 220));
                        return new ShopTokenBindingClaimResult
                        {
                            Success = false,
                            Error = "HTTP " + (int)response.StatusCode + " " + Safe(body, 220)
                        };
                    }

                    var root = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
                    ReportSuccess(shop);
                    return new ShopTokenBindingClaimResult
                    {
                        Success = true,
                        Rebound = root.Value<bool?>("rebound") == true,
                        BoundShopKey = root.Value<string>("shop_key") ?? shop.ShopKey
                    };
                }
            }
        }

        public static void QueueConflictPrompt(ShopContext shop, string token, string boundShopKey)
        {
            if (shop == null || string.IsNullOrWhiteSpace(shop.ShopKey) || string.IsNullOrWhiteSpace(token)) return;
            var now = DateTime.UtcNow;
            DateTime previous;
            if (PromptedAt.TryGetValue(shop.ShopKey, out previous)
                && now - previous < TimeSpan.FromMinutes(5)) return;
            if (!PromptOpen.TryAdd(shop.ShopKey, 0)) return;
            PromptedAt[shop.ShopKey] = now;

            var app = Application.Current;
            if (app == null)
            {
                byte ignored;
                PromptOpen.TryRemove(shop.ShopKey, out ignored);
                return;
            }

            app.Dispatcher.BeginInvoke(new Action(async delegate
            {
                try
                {
                    var oldShop = string.IsNullOrWhiteSpace(boundShopKey) ? "其他店铺" : boundShopKey;
                    var message =
                        "当前 Bot 客户端令牌已经绑定到店铺：" + oldShop + "。\n\n"
                        + "是否踢出旧店铺，并把这个令牌重新绑定到当前店铺："
                        + (shop.DisplayName ?? shop.ShopKey) + "？\n\n"
                        + "为防止跨店数据污染，确认迁移后服务端会清空该令牌旧店铺的运行缓存、消息、云知识和云备份索引；旧店铺将立即失去该令牌的访问权限。";
                    if (MessageBox.Show(
                        message,
                        "Bot 令牌已绑定其他店铺",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        return;
                    }

                    var result = await ClaimAsync(shop, token, true);
                    if (!result.Success)
                    {
                        MessageBox.Show(
                            "重新绑定失败：" + result.Error,
                            "Bot 令牌绑定",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }

                    var localCleanupComplete = ClearDuplicateLocalTokenCopies(shop, token);
                    MessageBox.Show(
                        localCleanupComplete
                            ? "令牌已重新绑定到当前店铺。旧店铺的本机重复令牌已清除；当前店铺会继续连接服务端。"
                            : "令牌已在服务端重新绑定到当前店铺，但本机旧店铺重复令牌清理未完全完成。当前店铺可以继续连接；请检查其他店铺的本机令牌设置。",
                        localCleanupComplete ? "Bot 令牌绑定完成" : "Bot 令牌已绑定，需检查本机清理",
                        MessageBoxButton.OK,
                        localCleanupComplete ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                    MessageBox.Show(
                        "重新绑定异常：" + ex.Message,
                        "Bot 令牌绑定",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    byte ignored;
                    PromptOpen.TryRemove(shop.ShopKey, out ignored);
                }
            }));
        }

        internal static bool ClearDuplicateLocalTokenCopies(ShopContext currentShop, string token)
        {
            if (currentShop == null || string.IsNullOrWhiteSpace(currentShop.ShopKey)
                || string.IsNullOrWhiteSpace(token)) return false;

            IList<ShopProfile> profiles;
            try
            {
                profiles = Profiles.GetAll();
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("服务端令牌已完成重新绑定，但读取本机店铺注册表失败，无法确认旧令牌清理：" + Safe(ex.Message, 160), 10);
                return false;
            }

            var cleanupComplete = true;
            foreach (var profile in profiles ?? new List<ShopProfile>())
            {
                if (profile == null
                    || string.Equals(profile.ShopKey, currentShop.ShopKey, StringComparison.Ordinal)) continue;
                try
                {
                    var other = new ShopControlPlaneConnectionStore(profile.ToContext(), Paths);
                    string otherToken;
                    string error;
                    if (!other.TryGetToken(out otherToken, out error))
                    {
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            cleanupComplete = false;
                            Log.Info("读取旧店铺令牌失败，未能确认是否存在重复令牌: shop=" + profile.ShopKey
                                + ", error=" + Safe(error, 160));
                        }
                        continue;
                    }
                    if (string.Equals((otherToken ?? string.Empty).Trim(), token.Trim(), StringComparison.Ordinal))
                    {
                        other.ClearToken();
                        Log.Info("已清除旧店铺重复Bot令牌: shop=" + profile.ShopKey);
                    }
                }
                catch (Exception ex)
                {
                    cleanupComplete = false;
                    Log.Info("清理旧店铺重复Bot令牌失败: shop=" + profile.ShopKey + ", error=" + Safe(ex.Message, 160));
                }
            }
            return cleanupComplete;
        }

        private static string ExtractBoundShopKey(string body)
        {
            try
            {
                var root = JObject.Parse(body ?? "{}");
                var detail = root["detail"] as JObject;
                return detail == null ? string.Empty : (detail.Value<string>("bound_shop_key") ?? string.Empty).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string Safe(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }
    }
}
