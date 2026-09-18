using Bot.ShopScope;
using BotLib;
using BotLib.Db.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    public partial class App
    {
        private readonly object _botWebConsoleBootstrap =
            ChromeNs.BotWebConsoleSyncService.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    internal static class BotWebConsoleSyncService
    {
        private const string Scope = "shop-cloud";
        private const string PauseKey = "BotWebRemotePause";
        private const string MessageSyncKey = "BotWebMessageSync";
        private const string ManualReplyKey = "BotWebAllowManualReply";
        private const string IntervalKey = "BotWebSyncIntervalSeconds";
        private const string ProcessedCommandsKey = "BotWebProcessedCommandIds";

        private sealed class WebMessage
        {
            public string MessageKey;
            public string Seller;
            public string Buyer;
            public string Role;
            public string Text;
            public string MessageType;
            public DateTime OccurredAt;
        }

        private sealed class ShopWebState
        {
            public ShopContext Shop;
            public readonly ConcurrentDictionary<string, WebMessage> PendingMessages =
                new ConcurrentDictionary<string, WebMessage>(StringComparer.Ordinal);
            public readonly ConcurrentDictionary<long, JObject> PendingCommandResults =
                new ConcurrentDictionary<long, JObject>();
            public readonly object ProcessedSync = new object();
            public readonly HashSet<long> ProcessedCommands = new HashSet<long>();
            public volatile bool RemotePause;
            public volatile bool MessageSyncEnabled = true;
            public volatile bool AllowManualReply = true;
            public volatile int SyncIntervalSeconds = 3;
            public DateTime NextSyncUtc = DateTime.MinValue;
            public int Syncing;
            public bool Loaded;
        }

        private static readonly ShopScopedPathProvider Paths = new ShopScopedPathProvider();
        private static readonly ShopProfileStore Profiles = new ShopProfileStore(Paths);
        private static readonly ConcurrentDictionary<string, ShopWebState> States =
            new ConcurrentDictionary<string, ShopWebState>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<int, Func<BuyerMessageBurstLease, Task>> InstalledWrappers =
            new ConcurrentDictionary<int, Func<BuyerMessageBurstLease, Task>>();
        private static readonly ConcurrentDictionary<int, byte> HandlerReplacementWarnings =
            new ConcurrentDictionary<int, byte>();
        private static readonly DateTime ProcessStartedAt = SafeProcessStart();

        private static Timer _syncTimer;
        private static Timer _patchTimer;
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0) return new object();
            PatchExisting();
            _patchTimer = new Timer(_ => PatchExisting(), null, 350, 900);
            _syncTimer = new Timer(_ => QueueDueSyncs(), null, 1500, 1000);
            Log.Info("Bot Web端同步已启动：每个 ShopKey 使用独立令牌、消息队列、命令状态和远程开关。" );
            return new object();
        }

        internal static bool IsRemotePaused
        {
            get
            {
                var shop = ShopSettingsScope.Current;
                return shop != null && GetState(shop).RemotePause;
            }
        }

        private static ShopWebState GetState(ShopContext shop)
        {
            if (shop == null) throw new ArgumentNullException(nameof(shop));
            var state = States.GetOrAdd(shop.ShopKey, _ => new ShopWebState { Shop = shop });
            state.Shop = shop;
            if (!state.Loaded) LoadSettings(state);
            return state;
        }

        private static void LoadSettings(ShopWebState state)
        {
            lock (state.ProcessedSync)
            {
                if (state.Loaded) return;
                using (ShopSettingsScope.Enter(state.Shop))
                {
                    state.RemotePause = ReadBool(PauseKey, false);
                    state.MessageSyncEnabled = ReadBool(MessageSyncKey, true);
                    state.AllowManualReply = ReadBool(ManualReplyKey, true);
                    int parsed;
                    var interval = PersistentParams.GetParam2Key(IntervalKey, Scope, "3");
                    state.SyncIntervalSeconds = int.TryParse(interval, out parsed)
                        ? Math.Max(2, Math.Min(60, parsed))
                        : 3;
                    state.ProcessedCommands.Clear();
                    var raw = PersistentParams.GetParam2Key(ProcessedCommandsKey, Scope, string.Empty);
                    foreach (var part in (raw ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        long id;
                        if (long.TryParse(part, out id) && id > 0) state.ProcessedCommands.Add(id);
                    }
                }
                state.NextSyncUtc = DateTime.UtcNow;
                state.Loaded = true;
            }
        }

        private static bool ReadBool(string key, bool defaultValue)
        {
            return string.Equals(
                PersistentParams.GetParam2Key(key, Scope, defaultValue ? "true" : "false"),
                "true",
                StringComparison.OrdinalIgnoreCase);
        }

        private static void SaveBool(string key, bool value)
        {
            PersistentParams.TrySaveParam2Key(key, Scope, value ? "true" : "false");
        }

        private static void PatchExisting()
        {
            try
            {
                QN[] qns;
                try { qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray(); }
                catch { return; }

                var coordinatorField = typeof(QN).GetField(
                    "_buyerMessageBurstCoordinator",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var handlerField = typeof(BuyerMessageBurstCoordinator).GetField(
                    "_handler",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (coordinatorField == null || handlerField == null) return;

                foreach (var qn in qns)
                {
                    if (qn == null) continue;
                    var coordinator = coordinatorField.GetValue(qn) as BuyerMessageBurstCoordinator;
                    if (coordinator == null) continue;
                    var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(coordinator);
                    var current = handlerField.GetValue(coordinator) as Func<BuyerMessageBurstLease, Task>;
                    if (current == null) continue;

                    Func<BuyerMessageBurstLease, Task> installed;
                    if (InstalledWrappers.TryGetValue(key, out installed))
                    {
                        if (!ReferenceEquals(current, installed) && HandlerReplacementWarnings.TryAdd(key, 0))
                            Log.Info("Bot Web端消息处理器已被其他模块继续包装；保持单次安装，避免闭包链增长。" );
                        continue;
                    }

                    var capturedQn = qn;
                    var next = current;
                    Func<BuyerMessageBurstLease, Task> wrapped =
                        lease => HandleBurstAsync(capturedQn, next, lease);
                    handlerField.SetValue(coordinator, wrapped);
                    InstalledWrappers[key] = wrapped;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("安装 Bot Web端店铺消息观察器失败：" + Safe(ex.Message, 260), 10);
            }
        }

        private static async Task HandleBurstAsync(
            QN qn,
            Func<BuyerMessageBurstLease, Task> next,
            BuyerMessageBurstLease lease)
        {
            if (next == null) return;
            var shop = ResolveShop(qn, lease == null ? null : lease.Burst);
            if (shop == null)
            {
                await next(lease);
                return;
            }

            using (ShopSettingsScope.Enter(shop))
            {
                var state = GetState(shop);
                var burst = lease == null ? null : lease.Burst;
                if (burst != null)
                {
                    CaptureConversation(state, burst);
                    if (state.RemotePause && burst.HasReplyableItem)
                    {
                        Log.Info("Bot Web端已暂停本店智能自动回复: shop=" + shop.ShopKey
                            + ", seller=" + Safe(burst.SellerNick, 80)
                            + ", buyer=" + Safe(burst.BuyerNick, 80));
                        return;
                    }
                }
                await next(lease);
            }
        }

        private static ShopContext ResolveShop(QN qn, BuyerMessageBurst burst)
        {
            try
            {
                if (qn != null && qn.Seller != null)
                    return Profiles.GetOrCreate(ShopIdentityResolver.Resolve(qn.Seller)).ToContext();
                if (burst != null && !string.IsNullOrWhiteSpace(burst.SellerNick))
                    return ShopContextLocator.ResolveRuntimeBySellerNick(burst.SellerNick);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("Bot Web端无法解析消息所属店铺：" + Safe(ex.Message, 220), 20);
            }
            return null;
        }

        private static void CaptureConversation(ShopWebState state, BuyerMessageBurst burst)
        {
            if (!state.MessageSyncEnabled || burst == null) return;
            var seller = (burst.SellerNick ?? string.Empty).Trim();
            var buyer = (burst.BuyerNick ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0) return;
            try
            {
                var turns = ConversationContextStore.GetRecentTurns(seller, buyer, string.Empty, 24);
                foreach (var turn in turns)
                {
                    if (turn == null || turn.Withdrawn || string.IsNullOrWhiteSpace(turn.Text)) continue;
                    EnqueueMessage(state, seller, buyer, turn.Role, turn.Text, "text",
                        turn.Timestamp == DateTime.MinValue ? DateTime.Now : turn.Timestamp,
                        turn.MessageKey);
                }
                if (!string.IsNullOrWhiteSpace(burst.CombinedQuestion))
                {
                    var at = burst.Items == null
                        ? DateTime.Now
                        : burst.Items.Where(x => x != null)
                            .Select(x => x.ReceivedAt == DateTime.MinValue ? DateTime.Now : x.ReceivedAt)
                            .DefaultIfEmpty(DateTime.Now).Max();
                    EnqueueMessage(state, seller, buyer, "user", burst.CombinedQuestion, "text", at, string.Empty);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("收集本店 Bot Web 会话消息失败：" + Safe(ex.Message, 260), 10);
            }
        }

        private static void EnqueueMessage(
            ShopWebState state,
            string seller,
            string buyer,
            string role,
            string text,
            string messageType,
            DateTime occurredAt,
            string messageKey)
        {
            if (state == null || !state.MessageSyncEnabled) return;
            seller = Safe(seller, 120);
            buyer = Safe(buyer, 120);
            role = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "assistant"
                : (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "system");
            text = (text ?? string.Empty).Replace("\0", string.Empty).Trim();
            if (text.Length == 0) return;
            if (text.Length > 6000) text = text.Substring(0, 6000);
            if (occurredAt == DateTime.MinValue) occurredAt = DateTime.Now;
            if (string.IsNullOrWhiteSpace(messageKey))
                messageKey = Hash(state.Shop.ShopKey + "|" + seller + "|" + buyer + "|" + role + "|"
                    + occurredAt.ToUniversalTime().Ticks + "|" + text);
            var item = new WebMessage
            {
                MessageKey = Safe(messageKey, 200),
                Seller = seller,
                Buyer = buyer,
                Role = role,
                Text = text,
                MessageType = Safe(messageType, 50),
                OccurredAt = occurredAt
            };
            state.PendingMessages[item.MessageKey] = item;
            if (state.PendingMessages.Count > 1500)
            {
                foreach (var old in state.PendingMessages.Values
                    .OrderBy(x => x.OccurredAt).Take(state.PendingMessages.Count - 1200))
                {
                    WebMessage ignored;
                    state.PendingMessages.TryRemove(old.MessageKey, out ignored);
                }
            }
        }

        private static void QueueDueSyncs()
        {
            foreach (var shop in SnapshotActiveShops())
            {
                var state = GetState(shop);
                if (DateTime.UtcNow < state.NextSyncUtc) continue;
                state.NextSyncUtc = DateTime.UtcNow.AddSeconds(Math.Max(2, state.SyncIntervalSeconds));
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

        private static void QueueSync(ShopWebState state)
        {
            if (state == null || Interlocked.Exchange(ref state.Syncing, 1) != 0) return;
            Task.Run(async () =>
            {
                try { await SyncOnceAsync(state); }
                catch (Exception ex)
                {
                    using (ShopSettingsScope.Enter(state.Shop))
                        Log.ErrorWithMaxCount("本店 Bot Web端同步失败：" + Safe(ex.Message, 300), 20);
                }
                finally { Interlocked.Exchange(ref state.Syncing, 0); }
            });
        }

        private static async Task SyncOnceAsync(ShopWebState state)
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

                var messageSnapshot = state.MessageSyncEnabled
                    ? state.PendingMessages.Values.OrderBy(x => x.OccurredAt).Take(500).ToList()
                    : new List<WebMessage>();
                var resultSnapshot = state.PendingCommandResults.ToArray();
                var payload = new JObject
                {
                    ["shop_key"] = state.Shop.ShopKey,
                    ["status"] = BuildStatus(state),
                    ["current_settings"] = new JObject
                    {
                        ["auto_reply_enabled"] = !state.RemotePause,
                        ["message_sync_enabled"] = state.MessageSyncEnabled,
                        ["allow_web_manual_reply"] = state.AllowManualReply,
                        ["sync_interval_seconds"] = state.SyncIntervalSeconds
                    },
                    ["messages"] = new JArray(messageSnapshot.Select(ToJson)),
                    ["command_results"] = new JArray(resultSnapshot.Select(x => x.Value))
                };

                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
                using (var http = new HttpClient(handler))
                using (var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    serverUrl.TrimEnd('/') + "/api/runtime/v1/bot-web/sync"))
                {
                    http.Timeout = TimeSpan.FromSeconds(25);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-web-sync/2.0");
                    request.Headers.TryAddWithoutValidation("X-Shop-Key", state.Shop.ShopKey);
                    request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    using (var response = await http.SendAsync(request))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body, 300));
                        var root = JObject.Parse(body);
                        ApplyDesiredSettings(state, root["desired_settings"] as JObject);
                        await ApplyCommandsAsync(state, root["commands"] as JArray);
                    }
                }

                foreach (var item in messageSnapshot)
                {
                    WebMessage ignored;
                    state.PendingMessages.TryRemove(item.MessageKey, out ignored);
                }
                foreach (var pair in resultSnapshot)
                {
                    JObject ignored;
                    state.PendingCommandResults.TryRemove(pair.Key, out ignored);
                }
            }
        }

        private static JObject BuildStatus(ShopWebState state)
        {
            var qns = FindQns(state.Shop);
            var sellers = qns.Where(x => x.Seller != null)
                .Select(x => (x.Seller.Nick ?? string.Empty).Trim())
                .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
            var windowsAuto = Params.Robot.GetIsAutoReply();
            var connection = new ShopControlPlaneConnectionStore(state.Shop, Paths);
            var authorization = new JObject
            {
                ["shop_key"] = state.Shop.ShopKey,
                ["control_scope"] = "current_shop_only",
                ["seller_accounts"] = new JArray(sellers),
                ["seller_count"] = sellers.Count,
                ["control_plane_configured"] = connection.HasShopServerUrl,
                ["client_token_bound"] = connection.HasToken,
                ["credentials_location"] = "windows_local_only",
                ["sensitive_credentials_exposed"] = false,
                ["capabilities"] = new JArray(
                    "status",
                    "messages",
                    state.AllowManualReply ? "manual_reply" : "manual_reply_disabled",
                    "safe_settings",
                    "knowledge",
                    "notifications")
            };
            return new JObject
            {
                ["shop_key"] = state.Shop.ShopKey,
                ["app_version"] = GetVersion(),
                ["seller_nicks"] = new JArray(sellers),
                ["seller_count"] = sellers.Count,
                ["current_seller"] = sellers.FirstOrDefault() ?? string.Empty,
                ["windows_auto_reply_enabled"] = windowsAuto,
                ["remote_pause"] = state.RemotePause,
                ["effective_auto_reply_enabled"] = windowsAuto && !state.RemotePause,
                ["message_sync_enabled"] = state.MessageSyncEnabled,
                ["allow_web_manual_reply"] = state.AllowManualReply,
                ["pending_message_count"] = state.PendingMessages.Count,
                ["authorization"] = authorization,
                ["uptime_seconds"] = Math.Max(0, (long)(DateTime.Now - ProcessStartedAt).TotalSeconds),
                ["process_id"] = Process.GetCurrentProcess().Id,
                ["synced_at"] = DateTime.UtcNow.ToString("o")
            };
        }

        private static IList<QN> FindQns(ShopContext shop)
        {
            var result = new List<QN>();
            try
            {
                var qns = QN.QNSet == null ? new QN[0] : QN.QNSet.ToArray();
                foreach (var qn in qns)
                {
                    if (qn == null || qn.Seller == null) continue;
                    try
                    {
                        var current = ShopIdentityResolver.Resolve(qn.Seller);
                        if (string.Equals(current.ShopKey, shop.ShopKey, StringComparison.Ordinal)) result.Add(qn);
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        private static JObject ToJson(WebMessage item)
        {
            return new JObject
            {
                ["message_key"] = item.MessageKey,
                ["seller"] = item.Seller,
                ["buyer"] = item.Buyer,
                ["role"] = item.Role,
                ["text"] = item.Text,
                ["message_type"] = item.MessageType,
                ["occurred_at"] = item.OccurredAt.ToUniversalTime().ToString("o")
            };
        }

        private static void ApplyDesiredSettings(ShopWebState state, JObject desired)
        {
            if (desired == null) return;
            var pause = desired["auto_reply_enabled"] != null && desired["auto_reply_enabled"].Type != JTokenType.Null
                ? !desired.Value<bool>("auto_reply_enabled") : state.RemotePause;
            var messageSync = desired["message_sync_enabled"] == null
                ? state.MessageSyncEnabled : desired.Value<bool>("message_sync_enabled");
            var manual = desired["allow_web_manual_reply"] == null
                ? state.AllowManualReply : desired.Value<bool>("allow_web_manual_reply");
            var interval = desired["sync_interval_seconds"] == null
                ? state.SyncIntervalSeconds
                : Math.Max(2, Math.Min(60, desired.Value<int>("sync_interval_seconds")));

            if (state.RemotePause != pause)
            {
                state.RemotePause = pause;
                SaveBool(PauseKey, pause);
                Params.Robot.SetIsAutoReply(!pause);
                Log.Info("本店 Web端远程智能回复开关已应用: shop=" + state.Shop.ShopKey + ", enabled=" + (!pause));
            }
            if (state.MessageSyncEnabled != messageSync)
            {
                state.MessageSyncEnabled = messageSync;
                SaveBool(MessageSyncKey, messageSync);
                if (!messageSync) state.PendingMessages.Clear();
            }
            if (state.AllowManualReply != manual)
            {
                state.AllowManualReply = manual;
                SaveBool(ManualReplyKey, manual);
            }
            if (state.SyncIntervalSeconds != interval)
            {
                state.SyncIntervalSeconds = interval;
                PersistentParams.TrySaveParam2Key(IntervalKey, Scope, interval.ToString());
            }
        }

        private static async Task ApplyCommandsAsync(ShopWebState state, JArray commands)
        {
            if (commands == null) return;
            foreach (var token in commands.Take(30))
            {
                var command = token as JObject;
                if (command == null) continue;
                var id = command.Value<long?>("id") ?? 0;
                if (id < 1) continue;
                if (WasProcessed(state, id))
                {
                    state.PendingCommandResults[id] = Result(id, true, string.Empty, new JObject { ["duplicate"] = true });
                    continue;
                }
                var type = Convert.ToString(command["type"]);
                try
                {
                    JObject result;
                    if (string.Equals(type, "send_text", StringComparison.OrdinalIgnoreCase))
                        result = await ExecuteSendTextAsync(state, id, command["payload"] as JObject);
                    else if (string.Equals(type, "knowledge_v2_smart_import", StringComparison.OrdinalIgnoreCase))
                        result = await ExecuteKnowledgeV2SmartImportAsync(state, id, command["payload"] as JObject);
                    else if (string.Equals(type, "knowledge_v2_settings_get", StringComparison.OrdinalIgnoreCase))
                        result = ExecuteKnowledgeV2SettingsGet(state);
                    else if (string.Equals(type, "knowledge_v2_settings_set", StringComparison.OrdinalIgnoreCase))
                        result = ExecuteKnowledgeV2SettingsSet(state, command["payload"] as JObject);
                    else if (string.Equals(type, "diagnostics_snapshot", StringComparison.OrdinalIgnoreCase))
                        result = ExecuteDiagnosticsSnapshot(state);
                    else if (string.Equals(type, "diagnostics_flow_probe", StringComparison.OrdinalIgnoreCase))
                        result = await ExecuteDiagnosticsFlowProbeAsync(state);
                    else
                        throw new Exception("不支持的远程命令：" + Safe(type, 80));
                    MarkProcessed(state, id);
                    state.PendingCommandResults[id] = Result(id, true, string.Empty, result);
                }
                catch (Exception ex)
                {
                    MarkProcessed(state, id);
                    state.PendingCommandResults[id] = Result(id, false, Safe(ex.Message, 600), new JObject());
                }
            }
        }

        private static async Task<JObject> ExecuteSendTextAsync(ShopWebState state, long commandId, JObject payload)
        {
            if (!state.AllowManualReply) throw new Exception("本店 Web端人工回复已关闭");
            if (payload == null) throw new Exception("远程回复参数为空");
            var seller = Safe(Convert.ToString(payload["seller"]), 120);
            var buyer = Safe(Convert.ToString(payload["buyer"]), 120);
            var text = Convert.ToString(payload["text"] ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0 || text.Length == 0)
                throw new Exception("客服账号、买家或回复内容为空");
            if (text.Length > 2000) throw new Exception("回复内容超过 2000 字");

            var qn = FindQns(state.Shop).FirstOrDefault(x => x.Seller != null
                && string.Equals((x.Seller.Nick ?? string.Empty).Trim(), seller, StringComparison.OrdinalIgnoreCase));
            if (qn == null) throw new Exception("本店未找到对应的在线客服账号");
            var resolvedBuyer = BuyerIdentityAliasService.ResolveInternalNick(seller, buyer);
            var ok = await qn.SendTextWithRetryAsync(resolvedBuyer, text, 1);
            if (!ok)
            {
                var reason = qn.Rpa == null ? "未知发送失败" : qn.Rpa.GetSendFailureReason();
                throw new Exception(reason);
            }
            EnqueueMessage(state, seller, resolvedBuyer, "assistant", text, "web_sent", DateTime.Now, "command:" + commandId);
            return new JObject { ["sent"] = true, ["shop_key"] = state.Shop.ShopKey };
        }

        private static JObject ExecuteKnowledgeV2SettingsGet(ShopWebState state)
        {
            var seller = (state.Shop.DisplayName ?? string.Empty).Trim();
            if (seller.Length == 0) throw new Exception("本店客服账号未识别，不能读取 V2 运行设置");
            var settings = Bot.Knowledge.KnowledgeEngineV2Service.GetSettingsView(seller);
            return new JObject
            {
                ["enabled"] = settings.Enabled,
                ["mode"] = settings.Mode,
                ["direct_threshold"] = settings.DirectThreshold,
                ["min_confidence"] = settings.MinConfidence,
                ["shop_key"] = state.Shop.ShopKey
            };
        }

        private static JObject ExecuteKnowledgeV2SettingsSet(ShopWebState state, JObject payload)
        {
            if (payload == null) throw new Exception("V2 运行设置参数为空");
            var seller = (state.Shop.DisplayName ?? string.Empty).Trim();
            if (seller.Length == 0) throw new Exception("本店客服账号未识别，不能保存 V2 运行设置");
            var enabled = payload.Value<bool?>("enabled") ?? true;
            var mode = (payload.Value<string>("mode") ?? "production").Trim().ToLowerInvariant();
            if (mode != "production" && mode != "shadow") throw new Exception("V2 运行模式无效");
            var threshold = payload.Value<double?>("direct_threshold") ?? 0.82;
            var confidence = payload.Value<double?>("min_confidence") ?? 0.72;
            if (threshold < 0.70 || threshold > 0.96) throw new Exception("V2 本地直答匹配阈值必须在 0.70~0.96");
            if (confidence < 0.50 || confidence > 0.95) throw new Exception("V2 最低知识可信度必须在 0.50~0.95");
            Bot.Knowledge.KnowledgeEngineV2Service.SetSettings(seller, enabled, mode, threshold, confidence);
            Log.Info("Bot Web 已更新 Knowledge V2 运行设置: shop=" + state.Shop.ShopKey
                + ", enabled=" + enabled + ", mode=" + mode
                + ", threshold=" + threshold.ToString("0.000")
                + ", minConfidence=" + confidence.ToString("0.000"));
            return ExecuteKnowledgeV2SettingsGet(state);
        }

        private static async Task<JObject> ExecuteKnowledgeV2SmartImportAsync(ShopWebState state, long commandId, JObject payload)
        {
            if (payload == null) throw new Exception("V2 智能导入参数为空");
            var text = Convert.ToString(payload["text"] ?? string.Empty).Trim();
            if (text.Length == 0) throw new Exception("V2 智能导入资料为空");
            if (text.Length > 20000) throw new Exception("V2 智能导入文字超过 20000 字");
            var timeout = payload.Value<int?>("timeout_seconds") ?? 90;
            timeout = Math.Max(15, Math.Min(180, timeout));
            var seller = (state.Shop.DisplayName ?? string.Empty).Trim();
            if (seller.Length == 0) throw new Exception("本店客服账号未识别，不能执行 V2 智能导入");
            var data = new Bot.Knowledge.ClipboardKnowledgeData { Text = text };
            var service = new Bot.Knowledge.KnowledgeV2SmartImportService();
            var imported = await service.ImportAsync(
                seller, data, timeout, CancellationToken.None, null,
                progress => Log.Info("Bot Web V2智能导入: command=" + commandId + ", shop=" + state.Shop.ShopKey + ", " + Safe(progress, 260)));
            return new JObject
            {
                ["import_id"] = imported.ImportId,
                ["ai_generated"] = imported.AiGenerated,
                ["added"] = imported.Added,
                ["duplicate_skipped"] = imported.DuplicateSkipped,
                ["text_chars"] = imported.TextChars,
                ["shop_key"] = state.Shop.ShopKey
            };
        }

        private static JObject ExecuteDiagnosticsSnapshot(ShopWebState state)
        {
            var reports = SlowResponseAnomalyService.GetReports(40) ?? new List<SlowResponseAnomalyReport>();
            var sendFailures = reports
                .Where(x => x != null && (x.AnswerSource ?? string.Empty).StartsWith("发送异常", StringComparison.Ordinal))
                .Take(20)
                .Select(ToSafeDiagnosticReport)
                .ToList();
            var slowResponses = reports
                .Where(x => x != null && !(x.AnswerSource ?? string.Empty).StartsWith("发送异常", StringComparison.Ordinal))
                .Take(20)
                .Select(ToSafeDiagnosticReport)
                .ToList();

            return new JObject
            {
                ["mode"] = "safe_redacted_snapshot",
                ["shop_key"] = state.Shop.ShopKey,
                ["generated_at"] = DateTime.UtcNow.ToString("o"),
                ["recent_logs"] = new JArray(ReadSafeDiagnosticLogLines(120)),
                ["send_failures"] = new JArray(sendFailures),
                ["slow_responses"] = new JArray(slowResponses),
                ["privacy"] = new JObject
                {
                    ["raw_customer_text_exposed"] = false,
                    ["raw_credentials_exposed"] = false,
                    ["phone_and_code_redacted"] = true
                }
            };
        }

        private static async Task<JObject> ExecuteDiagnosticsFlowProbeAsync(ShopWebState state)
        {
            var candidate = await BotFlowTestService.PickRandomCandidateAsync();
            var sellerOnline = candidate != null
                && FindQns(state.Shop).Any(x => x != null && x.Seller != null
                    && string.Equals(
                        (x.Seller.Nick ?? string.Empty).Trim(),
                        (candidate.Seller ?? string.Empty).Trim(),
                        StringComparison.Ordinal));
            return new JObject
            {
                ["mode"] = "dry_run_no_send",
                ["success"] = candidate != null && sellerOnline,
                ["candidate_available"] = candidate != null,
                ["seller_online"] = sellerOnline,
                ["candidate_source"] = candidate == null ? string.Empty : SafeDiagnosticText(candidate.Source, 120),
                ["question_chars"] = candidate == null || candidate.Question == null ? 0 : candidate.Question.Length,
                ["message_sent"] = false,
                ["detail"] = candidate == null
                    ? "未找到适合安全预检的近期买家问题；未发送任何消息。"
                    : (sellerOnline
                        ? "已完成当前店铺/客服/候选问题只读预检；未调用真实流程发送。"
                        : "已找到候选问题，但对应客服连接不可用；未发送任何消息。")
            };
        }

        private static JObject ToSafeDiagnosticReport(SlowResponseAnomalyReport report)
        {
            return new JObject
            {
                ["id"] = Safe(report == null ? string.Empty : report.Id, 80),
                ["created_at"] = report == null || report.CreatedAt == DateTime.MinValue
                    ? string.Empty : report.CreatedAt.ToUniversalTime().ToString("o"),
                ["severity"] = SafeDiagnosticText(report == null ? string.Empty : report.Severity, 120),
                ["analysis_status"] = SafeDiagnosticText(report == null ? string.Empty : report.AnalysisStatus, 160),
                ["answer_source"] = SafeDiagnosticText(report == null ? string.Empty : report.AnswerSource, 160),
                ["total_ms"] = report == null ? 0 : Math.Max(0, report.TotalMilliseconds),
                ["queue_ms"] = report == null ? 0 : Math.Max(0, report.QueueMilliseconds),
                ["generation_ms"] = report == null ? 0 : Math.Max(0, report.GenerationMilliseconds),
                ["summary"] = SafeDiagnosticText(report == null ? string.Empty : report.Summary, 900),
                ["likely_cause"] = SafeDiagnosticText(report == null ? string.Empty : report.LikelyCause, 900),
                ["evidence"] = SafeDiagnosticText(report == null ? string.Empty : report.Evidence, 900),
                ["recommendations"] = SafeDiagnosticText(report == null ? string.Empty : report.Recommendations, 900)
            };
        }

        private static IEnumerable<string> ReadSafeDiagnosticLogLines(int maxLines)
        {
            try
            {
                Log.Flush();
                var path = Log.CurrentFileName;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return new[] { "当前没有可读取的运行日志。" };
                var lines = File.ReadLines(path)
                    .Reverse()
                    .Take(Math.Max(1, Math.Min(300, maxLines)))
                    .Reverse()
                    .Select(x => SafeDiagnosticText(x, 700))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray();
                return lines.Length == 0 ? new[] { "当前运行日志为空。" } : lines;
            }
            catch (Exception ex)
            {
                return new[] { "读取安全日志摘要失败：" + SafeDiagnosticText(ex.Message, 240) };
            }
        }

        private static string SafeDiagnosticText(string value, int max)
        {
            value = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (value.Length == 0) return string.Empty;
            value = Regex.Replace(
                value,
                @"(?i)(authorization|bearer|api[-_ ]?key|token|cookie|password|secret)\s*[:=：]\s*[^\s,，;；|]+",
                "$1=[REDACTED]");
            value = Regex.Replace(
                value,
                @"(?i)(question|answer|text|message|prompt|content|buyer|seller|phone|code|order[_-]?id|手机号|验证码|订单号)\s*[:=：]\s*[^,，;；|]+",
                "$1=[REDACTED]");
            value = Regex.Replace(value, @"(?<!\d)1\d{10}(?!\d)", "[PHONE]");
            value = Regex.Replace(value, @"(?<!\d)\d{6}(?!\d)", "[CODE]");
            value = Regex.Replace(value, @"https?://[^\s]+", "[URL]", RegexOptions.IgnoreCase);
            while (value.Contains("  ")) value = value.Replace("  ", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private static JObject Result(long id, bool success, string error, JObject result)
        {
            return new JObject
            {
                ["id"] = id,
                ["success"] = success,
                ["error"] = error ?? string.Empty,
                ["result"] = result ?? new JObject()
            };
        }

        private static bool WasProcessed(ShopWebState state, long id)
        {
            lock (state.ProcessedSync) return state.ProcessedCommands.Contains(id);
        }

        private static void MarkProcessed(ShopWebState state, long id)
        {
            lock (state.ProcessedSync)
            {
                state.ProcessedCommands.Add(id);
                while (state.ProcessedCommands.Count > 200)
                    state.ProcessedCommands.Remove(state.ProcessedCommands.OrderBy(x => x).First());
                PersistentParams.TrySaveParam2Key(
                    ProcessedCommandsKey,
                    Scope,
                    string.Join(",", state.ProcessedCommands.OrderBy(x => x)));
            }
        }

        private static string GetVersion()
        {
            try
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                return version == null ? string.Empty : version.ToString();
            }
            catch { return string.Empty; }
        }

        private static DateTime SafeProcessStart()
        {
            try { return Process.GetCurrentProcess().StartTime; }
            catch { return DateTime.Now; }
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty).ToLowerInvariant();
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