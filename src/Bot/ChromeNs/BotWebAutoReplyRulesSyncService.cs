using Bot.Options;
using Bot.ShopScope;
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
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    public partial class App
    {
        private readonly object _botWebAutoReplyRulesBootstrap =
            ChromeNs.BotWebAutoReplyRulesSyncService.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class BotWebAutoReplyRulesSyncService
    {
        private const int SyncIntervalSeconds = 5;
        private const int UnsupportedEndpointBackoffMinutes = 15;
        private const int AuthFailureBackoffMinutes = 5;
        private const int TransientBackoffBaseSeconds = 15;
        private const int TransientBackoffMaxSeconds = 300;
        private const string AiOffHoursMode = "AI告知下班时间";
        private const string FixedOffHoursMode = "固定预设答案";
        private const string FixedOrderReplyMode = "固定预设答案";
        private const string HttpOrderReplyMode = "调用HTTP接口";

        private sealed class ShopRuleState
        {
            public ShopContext Shop;
            public DateTime NextSyncUtc = DateTime.MinValue;
            public int Syncing;
            public int ConsecutiveFailures;
            public bool UnsupportedEndpointLogged;
            public string LastApplyError = string.Empty;
        }

        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);
        private static readonly ConcurrentDictionary<string, ShopRuleState> States =
            new ConcurrentDictionary<string, ShopRuleState>(StringComparer.Ordinal);
        private static Timer _timer;
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return new object();
            _timer = new Timer(_ => QueueDueSyncs(), null, 2200, 1000);
            Log.Info("Bot Web自动回复规则同步已启动：首次同步采纳 Windows 当前规则，后续按版本下发。" );
            return new object();
        }

        private static void QueueDueSyncs()
        {
            foreach (var shop in SnapshotActiveShops())
            {
                var state = States.GetOrAdd(shop.ShopKey, _ => new ShopRuleState { Shop = shop });
                state.Shop = shop;
                if (DateTime.UtcNow < state.NextSyncUtc) continue;
                state.NextSyncUtc = DateTime.UtcNow.AddSeconds(SyncIntervalSeconds);
                QueueSync(state);
            }
        }

        private static IList<ShopContext> SnapshotActiveShops()
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

        private static void QueueSync(ShopRuleState state)
        {
            if (state == null || Interlocked.Exchange(ref state.Syncing, 1) != 0) return;
            Task.Run(async () =>
            {
                try { await SyncOnceAsync(state); }
                catch (Exception ex)
                {
                    var delay = ScheduleTransientBackoff(state);
                    using (ShopSettingsScope.Enter(state.Shop))
                        Log.ErrorWithMaxCount("本店 Bot Web自动回复规则同步暂时失败，已退避重试："
                            + Safe(ex.Message, 260) + "，retrySeconds=" + (int)delay.TotalSeconds, 20);
                }
                finally { Interlocked.Exchange(ref state.Syncing, 0); }
            });
        }

        private static async Task SyncOnceAsync(ShopRuleState state)
        {
            using (ShopSettingsScope.Enter(state.Shop))
            {
                var connection = new ShopControlPlaneConnectionStore(state.Shop, Paths);
                var serverUrl = connection.GetServerUrl();
                string token;
                string tokenError;
                if (!connection.TryGetToken(out token, out tokenError)
                    || string.IsNullOrWhiteSpace(serverUrl)
                    || string.IsNullOrWhiteSpace(token)) return;

                var payload = new JObject
                {
                    ["current_settings"] = BuildCurrentSettings(),
                    ["last_error"] = Safe(state.LastApplyError, 1000)
                };

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
                using (var http = new HttpClient(handler))
                using (var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    serverUrl.TrimEnd('/') + "/api/runtime/v1/bot-web/auto-reply-rules/sync"))
                {
                    http.Timeout = TimeSpan.FromSeconds(25);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-web-auto-reply-rules/2.0");
                    request.Headers.TryAddWithoutValidation("X-Shop-Key", state.Shop.ShopKey);
                    request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    using (var response = await http.SendAsync(request))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                        {
                            var code = (int)response.StatusCode;
                            if (response.StatusCode == HttpStatusCode.NotFound
                                || response.StatusCode == HttpStatusCode.MethodNotAllowed)
                            {
                                state.ConsecutiveFailures = 0;
                                state.NextSyncUtc = DateTime.UtcNow.AddMinutes(UnsupportedEndpointBackoffMinutes);
                                if (!state.UnsupportedEndpointLogged)
                                {
                                    state.UnsupportedEndpointLogged = true;
                                    Log.Info("本店 Bot Web自动回复规则同步端点尚未部署，保留Windows本地规则并降频探测：HTTP "
                                        + code + "，retryMinutes=" + UnsupportedEndpointBackoffMinutes);
                                }
                                return;
                            }
                            if (response.StatusCode == HttpStatusCode.Unauthorized
                                || response.StatusCode == HttpStatusCode.Forbidden)
                            {
                                state.ConsecutiveFailures++;
                                state.NextSyncUtc = DateTime.UtcNow.AddMinutes(AuthFailureBackoffMinutes);
                                Log.ErrorWithMaxCount("本店 Bot Web自动回复规则同步鉴权失败，保留本地规则并退避：HTTP "
                                    + code + " " + Safe(body, 300) + "，retryMinutes=" + AuthFailureBackoffMinutes, 10);
                                return;
                            }

                            var delay = ScheduleTransientBackoff(state);
                            Log.ErrorWithMaxCount("本店 Bot Web自动回复规则同步服务暂不可用，保留本地规则并退避：HTTP "
                                + code + " " + Safe(body, 300) + "，retrySeconds=" + (int)delay.TotalSeconds, 20);
                            return;
                        }
                        state.ConsecutiveFailures = 0;
                        state.UnsupportedEndpointLogged = false;
                        state.NextSyncUtc = DateTime.UtcNow.AddSeconds(SyncIntervalSeconds);
                        var root = JObject.Parse(body);
                        var desired = root["desired_settings"] as JObject;
                        if (desired == null) return;
                        try
                        {
                            ApplyDesiredSettings(desired);
                            state.LastApplyError = string.Empty;
                        }
                        catch (Exception ex)
                        {
                            state.LastApplyError = Safe(ex.Message, 1000);
                            Log.ErrorWithMaxCount("应用 Bot Web自动回复规则失败：" + Safe(ex.Message, 260), 20);
                        }
                    }
                }
            }
        }

        private static TimeSpan ScheduleTransientBackoff(ShopRuleState state)
        {
            state.ConsecutiveFailures = Math.Min(16, state.ConsecutiveFailures + 1);
            var exponent = Math.Min(5, Math.Max(0, state.ConsecutiveFailures - 1));
            var seconds = Math.Min(
                TransientBackoffMaxSeconds,
                TransientBackoffBaseSeconds * (1 << exponent));
            var delay = TimeSpan.FromSeconds(seconds);
            state.NextSyncUtc = DateTime.UtcNow.Add(delay);
            return delay;
        }

        private static JObject BuildCurrentSettings()
        {
            var cfg = BotFeatureStore.GetAutoReplyRules() ?? AutoReplyRuleConfig.Default();
            return new JObject
            {
                ["auto_reply_rules_enabled"] = cfg.Enabled,
                ["manual_handoff_keywords"] = Clean(cfg.ManualKeywords, 2000),
                ["manual_confirm_keywords"] = Clean(cfg.NoAutoReplyKeywords, 2000),
                ["work_hours_enabled"] = cfg.EnableWorkHours,
                ["work_start_time"] = NormalizeTime(cfg.WorkStartTime, "09:00"),
                ["work_end_time"] = NormalizeTime(cfg.WorkEndTime, "18:00"),
                ["off_hours_reply_mode"] = NormalizeMode(cfg.OffHoursReplyMode),
                ["off_hours_fixed_text"] = Clean(cfg.OffHoursFixedText, 3000),
                ["order_placed_reply_enabled"] = cfg.EnableOrderPlacedReply,
                ["order_placed_reply_mode"] = NormalizeOrderReplyMode(cfg.OrderPlacedReplyMode),
                ["order_placed_reply_text"] = Clean(cfg.OrderPlacedReplyText, 5000),
                ["order_placed_api_timeout_seconds"] = NormalizeOrderApiTimeout(cfg.OrderPlacedApiTimeoutSeconds),
                ["order_placed_reply_delay_seconds"] = OrderPlacedReplyDelaySettings.GetSeconds()
            };
        }

        private static void ApplyDesiredSettings(JObject desired)
        {
            var cfg = BotFeatureStore.GetAutoReplyRules() ?? AutoReplyRuleConfig.Default();
            var changed = false;

            changed |= SetBool(desired, "auto_reply_rules_enabled", cfg.Enabled, v => cfg.Enabled = v);
            changed |= SetText(desired, "manual_handoff_keywords", cfg.ManualKeywords, 2000, v => cfg.ManualKeywords = v);
            changed |= SetText(desired, "manual_confirm_keywords", cfg.NoAutoReplyKeywords, 2000, v => cfg.NoAutoReplyKeywords = v);
            changed |= SetBool(desired, "work_hours_enabled", cfg.EnableWorkHours, v => cfg.EnableWorkHours = v);

            if (desired["work_start_time"] != null)
            {
                var value = NormalizeTime(desired.Value<string>("work_start_time"), "09:00");
                if (!string.Equals(cfg.WorkStartTime, value, StringComparison.Ordinal))
                {
                    cfg.WorkStartTime = value;
                    changed = true;
                }
            }
            if (desired["work_end_time"] != null)
            {
                var value = NormalizeTime(desired.Value<string>("work_end_time"), "18:00");
                if (!string.Equals(cfg.WorkEndTime, value, StringComparison.Ordinal))
                {
                    cfg.WorkEndTime = value;
                    changed = true;
                }
            }
            if (desired["off_hours_reply_mode"] != null)
            {
                var value = NormalizeMode(desired.Value<string>("off_hours_reply_mode"));
                if (!string.Equals(cfg.OffHoursReplyMode, value, StringComparison.Ordinal))
                {
                    cfg.OffHoursReplyMode = value;
                    changed = true;
                }
            }
            changed |= SetText(desired, "off_hours_fixed_text", cfg.OffHoursFixedText, 3000, v => cfg.OffHoursFixedText = v);

            // Post-order remote management deliberately excludes the endpoint URL and Bearer token.
            // The mobile console may select the existing Windows-local HTTP mode and adjust timeout,
            // but it can never inject a network target or credential into the Windows client.
            changed |= SetBool(desired, "order_placed_reply_enabled", cfg.EnableOrderPlacedReply, v => cfg.EnableOrderPlacedReply = v);
            if (desired["order_placed_reply_mode"] != null)
            {
                var value = NormalizeOrderReplyMode(desired.Value<string>("order_placed_reply_mode"));
                var currentValue = NormalizeOrderReplyMode(cfg.OrderPlacedReplyMode);
                if (!string.Equals(currentValue, value, StringComparison.Ordinal))
                {
                    cfg.OrderPlacedReplyMode = value;
                    changed = true;
                }
            }
            changed |= SetText(desired, "order_placed_reply_text", cfg.OrderPlacedReplyText, 5000, v => cfg.OrderPlacedReplyText = v);
            if (desired["order_placed_api_timeout_seconds"] != null)
            {
                var value = NormalizeOrderApiTimeout(desired.Value<int>("order_placed_api_timeout_seconds"));
                var currentValue = NormalizeOrderApiTimeout(cfg.OrderPlacedApiTimeoutSeconds);
                if (currentValue != value)
                {
                    cfg.OrderPlacedApiTimeoutSeconds = value;
                    changed = true;
                }
            }
            if (desired["order_placed_reply_delay_seconds"] != null)
            {
                var delay = desired.Value<int>("order_placed_reply_delay_seconds");
                if (delay != 0)
                    throw new InvalidOperationException("下单固定回复当前强制立即发送，delay 必须为 0。" );
                // Runtime already hard-codes zero seconds. Do not write params.db every sync tick.
            }

            // Only the explicitly whitelisted fields above are changed. Notification webhooks,
            // SMTP credentials, OrderPlacedApiUrl, OrderPlacedApiToken and every other local rule
            // field remain untouched on the existing config object.
            if (changed) BotFeatureStore.SaveAutoReplyRules(cfg);
        }

        private static bool SetBool(JObject desired, string key, bool current, Action<bool> setter)
        {
            if (desired[key] == null || desired[key].Type == JTokenType.Null) return false;
            var value = desired.Value<bool>(key);
            if (value == current) return false;
            setter(value);
            return true;
        }

        private static bool SetText(JObject desired, string key, string current, int limit, Action<string> setter)
        {
            if (desired[key] == null || desired[key].Type == JTokenType.Null) return false;
            var value = Clean(desired.Value<string>(key), limit);
            if (string.Equals(current ?? string.Empty, value, StringComparison.Ordinal)) return false;
            setter(value);
            return true;
        }

        private static string NormalizeTime(string value, string fallback)
        {
            value = Clean(value, 5);
            TimeSpan parsed;
            if (value.Length == 5
                && value[2] == ':'
                && TimeSpan.TryParse(value, out parsed)
                && parsed >= TimeSpan.Zero
                && parsed < TimeSpan.FromDays(1))
                return value;
            return fallback;
        }

        private static string NormalizeMode(string value)
        {
            return string.Equals(Clean(value, 40), FixedOffHoursMode, StringComparison.Ordinal)
                ? FixedOffHoursMode
                : AiOffHoursMode;
        }

        private static string NormalizeOrderReplyMode(string value)
        {
            return string.Equals(Clean(value, 40), HttpOrderReplyMode, StringComparison.Ordinal)
                ? HttpOrderReplyMode
                : FixedOrderReplyMode;
        }

        private static int NormalizeOrderApiTimeout(int value)
        {
            return Math.Max(3, Math.Min(60, value));
        }

        private static string Clean(string value, int limit)
        {
            value = (value ?? string.Empty).Replace("\0", string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Trim();
            return value.Length <= limit ? value : value.Substring(0, limit);
        }

        private static string Safe(string value, int limit)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= limit ? value : value.Substring(0, limit) + "...";
        }
    }
}
