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
        private readonly object _botWebAiModelSettingsBootstrap =
            ChromeNs.BotWebAiModelSettingsSyncService.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class BotWebAiModelSettingsSyncService
    {
        private const int SyncIntervalSeconds = 5;
        private const int UnsupportedEndpointBackoffMinutes = 15;
        private const int AuthFailureBackoffMinutes = 5;
        private const int TransientBackoffBaseSeconds = 15;
        private const int TransientBackoffMaxSeconds = 300;

        private sealed class ShopAiState
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
        private static readonly ConcurrentDictionary<string, ShopAiState> States =
            new ConcurrentDictionary<string, ShopAiState>(StringComparer.Ordinal);
        private static Timer _timer;
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return new object();
            _timer = new Timer(_ => QueueDueSyncs(), null, 2600, 1000);
            Log.Info("Bot Web AI模型设置同步已启动：仅同步安全白名单字段，BaseUrl/ApiKey始终保留在Windows本机。");
            return new object();
        }

        private static void QueueDueSyncs()
        {
            foreach (var shop in SnapshotActiveShops())
            {
                var state = States.GetOrAdd(shop.ShopKey, _ => new ShopAiState { Shop = shop });
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

        private static void QueueSync(ShopAiState state)
        {
            if (state == null || Interlocked.Exchange(ref state.Syncing, 1) != 0) return;
            Task.Run(async () =>
            {
                try { await SyncOnceAsync(state); }
                catch (Exception ex)
                {
                    var delay = ScheduleTransientBackoff(state);
                    using (ShopSettingsScope.Enter(state.Shop))
                        Log.ErrorWithMaxCount("本店 Bot Web AI模型设置同步暂时失败，已退避重试："
                            + Safe(ex.Message, 260) + "，retrySeconds=" + (int)delay.TotalSeconds, 20);
                }
                finally { Interlocked.Exchange(ref state.Syncing, 0); }
            });
        }

        private static async Task SyncOnceAsync(ShopAiState state)
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
                    serverUrl.TrimEnd('/') + "/api/runtime/v1/bot-web/ai-model-settings/sync"))
                {
                    http.Timeout = TimeSpan.FromSeconds(25);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-web-ai-model-settings/1.0");
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
                                    Log.Info("本店 Bot Web AI模型设置同步端点尚未部署，保留Windows本地设置并降频探测：HTTP "
                                        + code + "，retryMinutes=" + UnsupportedEndpointBackoffMinutes);
                                }
                                return;
                            }
                            if (response.StatusCode == HttpStatusCode.Unauthorized
                                || response.StatusCode == HttpStatusCode.Forbidden)
                            {
                                state.ConsecutiveFailures++;
                                state.NextSyncUtc = DateTime.UtcNow.AddMinutes(AuthFailureBackoffMinutes);
                                Log.ErrorWithMaxCount("本店 Bot Web AI模型设置同步鉴权失败，保留本地设置并退避：HTTP "
                                    + code + " " + Safe(body, 300) + "，retryMinutes=" + AuthFailureBackoffMinutes, 10);
                                return;
                            }
                            var delay = ScheduleTransientBackoff(state);
                            Log.ErrorWithMaxCount("本店 Bot Web AI模型设置同步服务暂不可用，保留本地设置并退避：HTTP "
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
                            Log.ErrorWithMaxCount("应用 Bot Web AI模型设置失败：" + Safe(ex.Message, 260), 20);
                        }
                    }
                }
            }
        }

        private static TimeSpan ScheduleTransientBackoff(ShopAiState state)
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
            var items = new JArray();
            foreach (var endpoint in AiEndpointStore.GetEndpoints())
            {
                endpoint.NormalizeVisionDefaults();
                items.Add(new JObject
                {
                    ["id"] = Clean(endpoint.Id, 80),
                    ["name"] = Clean(endpoint.Name, 120),
                    ["enabled"] = endpoint.Enabled,
                    ["text_model"] = Clean(endpoint.TextModel, 200),
                    ["vision_model"] = Clean(endpoint.VisionModel, 200),
                    ["supports_vision"] = endpoint.SupportsVision,
                    ["max_image_size_mb"] = Clamp(endpoint.MaxImageSizeMb, 1, 20, 5),
                    ["vision_timeout_seconds"] = Clamp(endpoint.VisionTimeoutSeconds, 10, 180, 45),
                    ["system_prompt"] = Clean(endpoint.SystemPrompt, 12000),
                    ["priority"] = Clamp(endpoint.Priority, 1, 1000, 1),
                    ["weight"] = Clamp(endpoint.Weight, 1, 100, 1),
                    ["timeout_seconds"] = Clamp(endpoint.TimeoutSeconds, 5, 300, 35),
                    ["retry_count"] = Clamp(endpoint.RetryCount, 0, 10, 0),
                    ["last_status"] = Clean(endpoint.LastStatus, 500),
                    ["last_latency_ms"] = Math.Max(0L, endpoint.LastLatencyMs),
                    ["last_test_time"] = endpoint.LastTestTime <= DateTime.MinValue.AddSeconds(1)
                        ? string.Empty
                        : endpoint.LastTestTime.ToString("o")
                });
            }
            return new JObject { ["endpoints"] = items };
        }

        private static void ApplyDesiredSettings(JObject desired)
        {
            var desiredItems = desired["endpoints"] as JArray;
            if (desiredItems == null) return;

            var endpoints = AiEndpointStore.GetEndpoints();
            var byId = endpoints
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Id))
                .ToDictionary(item => item.Id, item => item, StringComparer.Ordinal);
            var changed = false;

            foreach (var token in desiredItems)
            {
                var item = token as JObject;
                if (item == null) continue;
                var id = Clean(item.Value<string>("id"), 80);
                AiEndpointConfig endpoint;
                if (string.IsNullOrWhiteSpace(id) || !byId.TryGetValue(id, out endpoint)) continue;

                changed |= SetText(item, "name", endpoint.Name, 120, value => endpoint.Name = value);
                changed |= SetBool(item, "enabled", endpoint.Enabled, value => endpoint.Enabled = value);
                if (item["text_model"] != null)
                {
                    var value = Clean(item.Value<string>("text_model"), 200);
                    if (!string.Equals(endpoint.TextModel ?? string.Empty, value, StringComparison.Ordinal))
                    {
                        endpoint.TextModel = value;
                        endpoint.Model = value;
                        changed = true;
                    }
                }
                changed |= SetText(item, "vision_model", endpoint.VisionModel, 200, value => endpoint.VisionModel = value);
                changed |= SetBool(item, "supports_vision", endpoint.SupportsVision, value => endpoint.SupportsVision = value);
                changed |= SetInt(item, "max_image_size_mb", endpoint.MaxImageSizeMb, 1, 20, 5, value => endpoint.MaxImageSizeMb = value);
                changed |= SetInt(item, "vision_timeout_seconds", endpoint.VisionTimeoutSeconds, 10, 180, 45, value => endpoint.VisionTimeoutSeconds = value);
                changed |= SetText(item, "system_prompt", endpoint.SystemPrompt, 12000, value => endpoint.SystemPrompt = value);
                changed |= SetInt(item, "priority", endpoint.Priority, 1, 1000, 1, value => endpoint.Priority = value);
                changed |= SetInt(item, "weight", endpoint.Weight, 1, 100, 1, value => endpoint.Weight = value);
                changed |= SetInt(item, "timeout_seconds", endpoint.TimeoutSeconds, 5, 300, 35, value => endpoint.TimeoutSeconds = value);
                changed |= SetInt(item, "retry_count", endpoint.RetryCount, 0, 10, 0, value => endpoint.RetryCount = value);
            }

            // BaseUrl and ApiKey are never present in the remote schema and are never
            // assigned here. SaveEndpoints persists the existing local values unchanged.
            if (changed) AiEndpointStore.SaveEndpoints(endpoints);
        }

        private static bool SetBool(JObject item, string key, bool current, Action<bool> setter)
        {
            if (item[key] == null || item[key].Type == JTokenType.Null) return false;
            var value = item.Value<bool>(key);
            if (value == current) return false;
            setter(value);
            return true;
        }

        private static bool SetText(JObject item, string key, string current, int limit, Action<string> setter)
        {
            if (item[key] == null || item[key].Type == JTokenType.Null) return false;
            var value = Clean(item.Value<string>(key), limit);
            if (string.Equals(current ?? string.Empty, value, StringComparison.Ordinal)) return false;
            setter(value);
            return true;
        }

        private static bool SetInt(
            JObject item,
            string key,
            int current,
            int low,
            int high,
            int fallback,
            Action<int> setter)
        {
            if (item[key] == null || item[key].Type == JTokenType.Null) return false;
            var value = Clamp(item.Value<int>(key), low, high, fallback);
            var normalizedCurrent = Clamp(current, low, high, fallback);
            if (value == normalizedCurrent) return false;
            setter(value);
            return true;
        }

        private static int Clamp(int value, int low, int high, int fallback)
        {
            if (value < low || value > high)
            {
                if (value <= 0) value = fallback;
                value = Math.Max(low, Math.Min(high, value));
            }
            return value;
        }

        private static string Clean(string value, int limit)
        {
            value = (value ?? string.Empty)
                .Replace("\0", string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Trim();
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
