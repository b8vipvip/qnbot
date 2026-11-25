using Bot.ShopScope;
using Bot.UpdateNs;
using BotLib;
using BotLib.Db.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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
        private const int MaxPendingMessagesPerShop = 20000;
        private const long MaxPendingMessageBytesPerShop = 64L * 1024 * 1024;
        private static long _pendingMessageBackpressureCount;

        private sealed class WebMessage
        {
            public string MessageKey;
            public string Seller;
            public string Buyer;
            public string Role;
            public string Text;
            public string MessageType;
            public bool IsBot;
            public bool Withdrawn;
            public DateTime OccurredAt;
            public byte[] AttachmentBytes;
            public string AttachmentMime;
            public string AttachmentName;
            public string AttachmentSha256;
        }

        private sealed class ShopWebState
        {
            public ShopContext Shop;
            public readonly ConcurrentDictionary<string, WebMessage> PendingMessages =
                new ConcurrentDictionary<string, WebMessage>(StringComparer.Ordinal);
            public readonly object PendingMessagesSync = new object();
            public long PendingMessageBytes;
            public readonly ConcurrentDictionary<string, string> CapturedMessageSignatures =
                new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            public readonly ConcurrentDictionary<long, JObject> PendingCommandResults =
                new ConcurrentDictionary<long, JObject>();
            public readonly object PendingCommandResultsSync = new object();
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

        // The sync timer runs every second, but the underlying store writes
        // shops.json + the per-shop profile on GetOrCreate. A weak per-QN cache
        // preserves timely seller-identity changes without retaining dead QNs.
        private static readonly TimeSpan ShopSnapshotRefreshInterval = TimeSpan.FromMinutes(1);

        private sealed class CachedShopSnapshot
        {
            public readonly object Sync = new object();
            public string Nick;
            public string TargetId;
            public string Display;
            public ShopContext Shop;
            public DateTime NextRefreshUtc;
        }

        private static readonly ConditionalWeakTable<QN, CachedShopSnapshot> ShopSnapshots =
            new ConditionalWeakTable<QN, CachedShopSnapshot>();
        private static long _shopSnapshotCacheHits;
        private static long _shopSnapshotRefreshes;

        internal static long ShopSnapshotCacheHits
        {
            get { return Interlocked.Read(ref _shopSnapshotCacheHits); }
        }

        internal static long ShopSnapshotRefreshes
        {
            get { return Interlocked.Read(ref _shopSnapshotRefreshes); }
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
            if (state == null || !state.MessageSyncEnabled || burst == null) return;
            var seller = (burst.SellerNick ?? string.Empty).Trim();
            var buyer = (burst.BuyerNick ?? string.Empty).Trim();
            if (seller.Length == 0 || buyer.Length == 0) return;
            try
            {
                var turns = ConversationContextStore.GetMirrorTurns(seller, buyer, 100);
                foreach (var turn in turns)
                    CaptureTurn(state, seller, buyer, turn);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("收集本店 Bot Web 会话消息失败：" + Safe(ex.Message, 260), 10);
            }
        }

        private static void CaptureRecentConversations(ShopWebState state)
        {
            if (state == null || !state.MessageSyncEnabled || state.Shop == null) return;
            try
            {
                var snapshots = ConversationContextStore.GetMirrorConversationSnapshots(
                    state.Shop.ShopKey, int.MaxValue, 100);
                var liveKeys = state.CapturedMessageSignatures.Count > 30000
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : null;

                foreach (var snapshot in snapshots)
                {
                    if (snapshot == null || snapshot.Turns == null) continue;
                    foreach (var turn in snapshot.Turns)
                    {
                        var key = CaptureTurn(state, snapshot.Seller, snapshot.Buyer, turn);
                        if (liveKeys != null && !string.IsNullOrWhiteSpace(key))
                            liveKeys.Add(key);
                    }
                }

                if (liveKeys != null)
                {
                    foreach (var key in state.CapturedMessageSignatures.Keys)
                    {
                        if (liveKeys.Contains(key) || state.PendingMessages.ContainsKey(key)) continue;
                        string ignored;
                        state.CapturedMessageSignatures.TryRemove(key, out ignored);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("收集本店 Bot Web 买家会话快照失败：" + Safe(ex.Message, 260), 10);
            }
        }

        private static string CaptureTurn(
            ShopWebState state,
            string seller,
            string buyer,
            ConversationContextTurn turn)
        {
            if (state == null || turn == null) return string.Empty;
            var role = string.Equals(turn.Role, "assistant", StringComparison.Ordinal)
                ? "assistant"
                : (string.Equals(turn.Role, "user", StringComparison.Ordinal) ? "user" : string.Empty);
            if (role.Length == 0) return string.Empty;

            var text = (turn.Text ?? string.Empty).Replace("\0", string.Empty).Trim();
            var isBot = false;
            const string botEchoMarker = "[A]";
            if (!turn.Withdrawn && role == "assistant"
                && text.EndsWith(botEchoMarker, StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(0, text.Length - botEchoMarker.Length).TrimEnd();
                isBot = text.Length > 0;
            }
            var normalizedPlaceholder = (text ?? string.Empty).Trim();
            var isImage = turn.HasImage
                || string.Equals(normalizedPlaceholder, "[图片]", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedPlaceholder, "图片消息", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedPlaceholder, "[image]", StringComparison.OrdinalIgnoreCase);
            if (turn.Withdrawn && string.IsNullOrWhiteSpace(text))
                text = "已撤回一条消息";
            if (text.Length == 0 && isImage) text = "[图片]";
            if (text.Length == 0) return string.Empty;

            var occurredAt = turn.Timestamp == DateTime.MinValue ? DateTime.Now : turn.Timestamp;
            var messageKey = (turn.MessageKey ?? string.Empty).Trim();
            if (messageKey.Length == 0)
                messageKey = Hash(state.Shop.ShopKey + "|" + seller + "|" + buyer + "|"
                    + role + "|" + occurredAt.ToUniversalTime().Ticks + "|" + text);
            else if (messageKey.Length > 200)
                messageKey = Hash(messageKey);

            byte[] attachmentBytes = null;
            var attachmentMime = string.Empty;
            var attachmentName = string.Empty;
            var attachmentSha = string.Empty;
            if (isImage && !turn.Withdrawn)
            {
                try
                {
                    var imagePath = VisionImageCacheService.TryGetCompletedCachedImagePath(
                        state.Shop.ShopKey, seller, buyer, turn.MessageKey ?? messageKey);
                    if (!string.IsNullOrWhiteSpace(imagePath) && System.IO.File.Exists(imagePath))
                    {
                        var candidate = System.IO.File.ReadAllBytes(imagePath);
                        if (candidate.Length > 0 && candidate.Length <= VisualImageKnowledgeService.MaxImageBytes)
                        {
                            var mime = VisionImageResolver.DetectMime(candidate);
                            if (mime == "image/jpeg" || mime == "image/png"
                                || mime == "image/webp" || mime == "image/gif")
                            {
                                using (var sha = SHA256.Create())
                                    attachmentSha = BitConverter.ToString(sha.ComputeHash(candidate)).Replace("-", "").ToLowerInvariant();
                                attachmentBytes = candidate;
                                attachmentMime = mime;
                                attachmentName = "买家图片-" + occurredAt.ToString("yyyyMMdd-HHmmss")
                                    + System.IO.Path.GetExtension(imagePath);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("Bot Web镜像图片附件暂不可用，后续同步周期会重试: " + Safe(ex.Message, 180));
                }
            }

            var signature = Hash(role + "|" + text + "|" + occurredAt.ToUniversalTime().Ticks
                + "|" + turn.Withdrawn + "|" + isBot + "|" + isImage + "|" + attachmentSha);
            lock (state.PendingMessagesSync)
            {
                // Serialize capture/signature updates with the remote disable transition.
                // An image first arrives as a text-only placeholder while the browser cache
                // finishes; once its bytes are ready the attachment hash changes the signature
                // and the same message key is upserted with the original image.
                if (!state.MessageSyncEnabled) return string.Empty;

                string previous;
                if (state.CapturedMessageSignatures.TryGetValue(messageKey, out previous)
                    && string.Equals(previous, signature, StringComparison.Ordinal))
                    return messageKey;

                if (EnqueueMessage(state, seller, buyer, role, text, isImage ? "image" : "text",
                    occurredAt, messageKey, turn.Withdrawn, isBot,
                    attachmentBytes, attachmentMime, attachmentName, attachmentSha))
                    state.CapturedMessageSignatures[messageKey] = signature;
            }
            return messageKey;
        }

        private static long PendingMessageSizeBytes(WebMessage item)
        {
            if (item == null) return 0;
            return 256L
                + Encoding.UTF8.GetByteCount(item.MessageKey ?? string.Empty)
                + Encoding.UTF8.GetByteCount(item.Seller ?? string.Empty)
                + Encoding.UTF8.GetByteCount(item.Buyer ?? string.Empty)
                + Encoding.UTF8.GetByteCount(item.Role ?? string.Empty)
                + Encoding.UTF8.GetByteCount(item.Text ?? string.Empty)
                + Encoding.UTF8.GetByteCount(item.MessageType ?? string.Empty)
                + (item.AttachmentBytes == null ? 0 : item.AttachmentBytes.Length);
        }

        private static long GetPendingMessageBytes(ShopWebState state)
        {
            if (state == null) return 0;
            lock (state.PendingMessagesSync) return state.PendingMessageBytes;
        }

        private static bool EnqueueMessage(
            ShopWebState state,
            string seller,
            string buyer,
            string role,
            string text,
            string messageType,
            DateTime occurredAt,
            string messageKey,
            bool withdrawn = false,
            bool isBot = false,
            byte[] attachmentBytes = null,
            string attachmentMime = null,
            string attachmentName = null,
            string attachmentSha256 = null)
        {
            if (state == null || !state.MessageSyncEnabled) return false;
            seller = Safe(seller, 120);
            buyer = Safe(buyer, 120);
            role = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "assistant"
                : (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "system");
            text = (text ?? string.Empty).Replace("\0", string.Empty).Trim();
            if (text.Length == 0) return false;
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
                IsBot = isBot,
                Withdrawn = withdrawn,
                OccurredAt = occurredAt,
                AttachmentBytes = withdrawn ? null : attachmentBytes,
                AttachmentMime = withdrawn ? string.Empty : Safe(attachmentMime, 40),
                AttachmentName = withdrawn ? string.Empty : Safe(attachmentName, 180),
                AttachmentSha256 = withdrawn ? string.Empty : Safe(attachmentSha256, 64)
            };
            // Apply count and byte backpressure instead of evicting unsent messages.
            // A rejected item is deliberately not marked in CapturedMessageSignatures,
            // so the next sync cycle retries it after the current 500-message batch drains.
            lock (state.PendingMessagesSync)
            {
                // Re-check after taking the lock: the sync switch may have changed
                // after the fast-path check above.
                if (!state.MessageSyncEnabled) return false;

                WebMessage previous;
                var exists = state.PendingMessages.TryGetValue(item.MessageKey, out previous);
                var nextCount = state.PendingMessages.Count + (exists ? 0 : 1);
                var previousBytes = exists ? PendingMessageSizeBytes(previous) : 0;
                var nextBytes = Math.Max(0, state.PendingMessageBytes - previousBytes)
                    + PendingMessageSizeBytes(item);
                if (nextCount > MaxPendingMessagesPerShop || nextBytes > MaxPendingMessageBytesPerShop)
                {
                    var total = Interlocked.Increment(ref _pendingMessageBackpressureCount);
                    if (total <= 3 || total % 100 == 0)
                    {
                        Log.Info("Bot Web消息同步队列达到容量上限，未入队消息保留在桌面镜像中等待后续同步: "
                            + "backpressureEvents=" + total
                            + ", pending=" + state.PendingMessages.Count
                            + ", pendingBytes=" + state.PendingMessageBytes
                            + ", countLimit=" + MaxPendingMessagesPerShop
                            + ", byteLimit=" + MaxPendingMessageBytesPerShop);
                    }
                    return false;
                }
                state.PendingMessages[item.MessageKey] = item;
                state.PendingMessageBytes = nextBytes;
            }
            return true;
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
                        var shop = ResolveShopSnapshot(qn);
                        if (shop != null) result[shop.ShopKey] = shop;
                    }
                    catch { }
                }
            }
            catch { }
            return result.Values.ToList();
        }

        private static ShopContext ResolveShopSnapshot(QN qn)
        {
            var seller = qn == null ? null : qn.Seller;
            if (seller == null) return null;

            var nick = seller.Nick ?? string.Empty;
            var targetId = seller.TargetId ?? string.Empty;
            var display = seller.Display ?? string.Empty;
            var cache = ShopSnapshots.GetValue(qn, _ => new CachedShopSnapshot());
            lock (cache.Sync)
            {
                var now = DateTime.UtcNow;
                if (cache.Shop != null
                    && now < cache.NextRefreshUtc
                    && string.Equals(cache.Nick, nick, StringComparison.Ordinal)
                    && string.Equals(cache.TargetId, targetId, StringComparison.Ordinal)
                    && string.Equals(cache.Display, display, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _shopSnapshotCacheHits);
                    return cache.Shop;
                }

                // A new seller, TargetId, display name, or elapsed presence
                // interval refreshes immediately through the validated store.
                // A failed/corrupt registry still fails closed as before.
                var shop = Profiles.GetOrCreate(ShopIdentityResolver.Resolve(seller)).ToContext();
                cache.Nick = nick;
                cache.TargetId = targetId;
                cache.Display = display;
                cache.Shop = shop;
                cache.NextRefreshUtc = now.Add(ShopSnapshotRefreshInterval);
                Interlocked.Increment(ref _shopSnapshotRefreshes);
                return shop;
            }
        }

        private static void QueueSync(ShopWebState state)
        {
            if (state == null || !ShopTokenBindingService.ShouldAttemptSync(state.Shop)
                || Interlocked.Exchange(ref state.Syncing, 1) != 0) return;
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

                // Message mirroring and image-knowledge replication share credentials and
                // scheduling, but neither data plane may starve the other. A rejected or
                // oversized message attachment must not stop knowledge synchronization.
                try
                {
                    CaptureRecentConversations(state);
                    List<WebMessage> messageSnapshot;
                    lock (state.PendingMessagesSync)
                    {
                        messageSnapshot = state.MessageSyncEnabled
                            ? SelectPendingMessageBatch(state)
                            : new List<WebMessage>();
                    }
                    KeyValuePair<long, JObject>[] resultSnapshot;
                    lock (state.PendingCommandResultsSync)
                        resultSnapshot = state.PendingCommandResults.ToArray();
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
                    {
                        // A single original image attachment may add up to 8 MB to this request.
                        http.Timeout = TimeSpan.FromSeconds(45);
                        using (var response = await ShopRuntimeCredentialAuthority.SendWithUnauthorizedRecoveryAsync(
                            state.Shop,
                            token,
                            async requestToken =>
                            {
                                using (var request = new HttpRequestMessage(
                                    HttpMethod.Post,
                                    serverUrl.TrimEnd('/') + "/api/runtime/v1/bot-web/sync"))
                                {
                                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
                                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                                    request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-web-sync/2.0");
                                    request.Headers.TryAddWithoutValidation("X-Shop-Key", state.Shop.ShopKey);
                                    request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                                    return await http.SendAsync(request).ConfigureAwait(false);
                                }
                            }))
                        {
                            var body = await response.Content.ReadAsStringAsync();
                            if (!response.IsSuccessStatusCode)
                                throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body, 300));
                            var root = JObject.Parse(body);
                            // A successful response acknowledges only this exact snapshot.
                            lock (state.PendingMessagesSync)
                            {
                                foreach (var item in messageSnapshot)
                                {
                                    if (((ICollection<KeyValuePair<string, WebMessage>>)state.PendingMessages).Remove(
                                        new KeyValuePair<string, WebMessage>(item.MessageKey, item)))
                                        state.PendingMessageBytes = Math.Max(
                                            0, state.PendingMessageBytes - PendingMessageSizeBytes(item));
                                }
                            }
                            ApplyDesiredSettings(state, root["desired_settings"] as JObject);
                            await ApplyCommandsAsync(state, root["commands"] as JArray);
                        }
                    }

                    AcknowledgeCommandResults(state, resultSnapshot);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("本店 Bot Web消息镜像同步失败；图片知识同步仍将独立执行：" + Safe(ex.Message, 300), 20);
                }

                try
                {
                    await SyncImageKnowledgeAsync(state, serverUrl, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("本店图片知识云同步失败，下一周期会重试：" + Safe(ex.Message, 260), 10);
                }
            }
        }

        private static async Task SyncImageKnowledgeAsync(
            ShopWebState state, string serverUrl, string token)
        {
            if (state == null || state.Shop == null) return;
            var payload = VisualImageKnowledgeService.BuildCloudSyncRequest(
                state.Shop.ShopKey, state.Shop.SellerNick);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var handler = new HttpClientHandler { UseProxy = true, Proxy = WebRequest.DefaultWebProxy })
            using (var http = new HttpClient(handler))
            {
                http.Timeout = TimeSpan.FromSeconds(35);
                using (var response = await ShopRuntimeCredentialAuthority.SendWithUnauthorizedRecoveryAsync(
                    state.Shop,
                    token,
                    async requestToken =>
                    {
                        using (var request = new HttpRequestMessage(
                            HttpMethod.Post,
                            serverUrl.TrimEnd('/') + "/api/runtime/v1/bot-web/image-knowledge/sync"))
                        {
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
                            request.Headers.TryAddWithoutValidation("Accept", "application/json");
                            request.Headers.TryAddWithoutValidation("User-Agent", "qianniu-bot-image-knowledge-sync/1.0");
                            request.Headers.TryAddWithoutValidation("X-Shop-Key", state.Shop.ShopKey);
                            request.Headers.TryAddWithoutValidation("X-Shop-Seller", state.Shop.SellerNick);
                            request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                            return await http.SendAsync(request).ConfigureAwait(false);
                        }
                    }))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new Exception("HTTP " + (int)response.StatusCode + " " + Safe(body, 300));
                    VisualImageKnowledgeService.ApplyCloudSyncResponse(
                        state.Shop.ShopKey, state.Shop.SellerNick, JObject.Parse(body));
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
                ["pending_message_bytes"] = GetPendingMessageBytes(state),
                ["authorization"] = authorization,
                ["diagnostics"] = new JObject
                {
                    ["shop_key"] = state.Shop.ShopKey,
                    ["online_seller_count"] = sellers.Count,
                    ["pending_message_count"] = state.PendingMessages.Count,
                    ["pending_message_bytes"] = GetPendingMessageBytes(state),
                    ["pending_command_result_count"] = state.PendingCommandResults.Count,
                    ["sync_in_progress"] = state.Syncing != 0,
                    ["message_sync_enabled"] = state.MessageSyncEnabled,
                    ["manual_reply_enabled"] = state.AllowManualReply,
                    ["remote_pause"] = state.RemotePause,
                    ["process_id"] = Process.GetCurrentProcess().Id,
                    ["uptime_seconds"] = Math.Max(0, (long)(DateTime.Now - ProcessStartedAt).TotalSeconds),
                    ["sensitive_data_exposed"] = false
                },
                ["update"] = new JObject
                {
                    ["current_version"] = BotUpdateService.CurrentVersion,
                    ["latest_version"] = BotUpdateService.LatestRelease == null ? string.Empty : BotUpdateService.LatestRelease.Version,
                    ["update_available"] = BotUpdateService.LastResult != null && BotUpdateService.LastResult.UpdateAvailable,
                    ["last_success"] = BotUpdateService.LastResult == null ? (bool?)null : BotUpdateService.LastResult.Success,
                    ["last_message"] = BotUpdateService.LastResult == null ? string.Empty : Safe(BotUpdateService.LastResult.Message, 260),
                    ["install_started"] = BotUpdateService.LastResult != null && BotUpdateService.LastResult.InstallStarted
                },
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

        private static List<WebMessage> SelectPendingMessageBatch(ShopWebState state)
        {
            const long maxPayloadBytes = 9L * 1024 * 1024;
            var result = new List<WebMessage>();
            long bytes = 0;
            var imageIncluded = false;
            foreach (var item in state.PendingMessages.Values.OrderBy(x => x.OccurredAt).Take(500))
            {
                var isImage = item.AttachmentBytes != null && item.AttachmentBytes.Length > 0;
                if (isImage && imageIncluded) continue;
                var size = PendingMessageSizeBytes(item);
                if (result.Count > 0 && bytes + size > maxPayloadBytes) continue;
                if (size > maxPayloadBytes && result.Count > 0) continue;
                result.Add(item);
                bytes += size;
                if (isImage) imageIncluded = true;
                if (bytes >= maxPayloadBytes) break;
            }
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
                ["is_bot"] = item.IsBot,
                ["withdrawn"] = item.Withdrawn,
                ["occurred_at"] = item.OccurredAt.ToUniversalTime().ToString("o"),
                ["attachment"] = item.AttachmentBytes == null || item.AttachmentBytes.Length == 0
                    ? null
                    : new JObject
                    {
                        ["mime_type"] = item.AttachmentMime,
                        ["file_name"] = item.AttachmentName,
                        ["sha256"] = item.AttachmentSha256,
                        ["data_base64"] = Convert.ToBase64String(item.AttachmentBytes)
                    }
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
                lock (state.PendingMessagesSync)
                {
                    if (state.MessageSyncEnabled != messageSync)
                    {
                        state.MessageSyncEnabled = messageSync;
                        SaveBool(MessageSyncKey, messageSync);
                        if (!messageSync)
                        {
                            // Only discard signatures for messages that were not acknowledged.
                            // Retained signatures for already-sent messages prevent duplicates;
                            // removed signatures let pending messages be recaptured after re-enable.
                            foreach (var key in state.PendingMessages.Keys)
                            {
                                string ignored;
                                state.CapturedMessageSignatures.TryRemove(key, out ignored);
                            }
                            state.PendingMessages.Clear();
                            state.PendingMessageBytes = 0;
                        }
                    }
                }
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
                    SetPendingCommandResult(state, id, Result(id, true, string.Empty, new JObject { ["duplicate"] = true }));
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
                        result = BuildSafeDiagnostics(state);
                    else if (string.Equals(type, "update_check", StringComparison.OrdinalIgnoreCase))
                        result = await ExecuteUpdateCheckAsync();
                    else if (string.Equals(type, "update_install", StringComparison.OrdinalIgnoreCase))
                        result = await ExecuteUpdateInstallAsync(command["payload"] as JObject);
                    else
                        throw new Exception("不支持的远程命令：" + Safe(type, 80));
                    MarkProcessed(state, id);
                    SetPendingCommandResult(state, id, Result(id, true, string.Empty, result));
                }
                catch (Exception ex)
                {
                    MarkProcessed(state, id);
                    SetPendingCommandResult(state, id, Result(id, false, Safe(ex.Message, 600), new JObject()));
                }
            }
        }

        private static JObject BuildSafeDiagnostics(ShopWebState state)
        {
            var qns = FindQns(state.Shop);
            return new JObject
            {
                ["shop_key"] = state.Shop.ShopKey,
                ["captured_at"] = DateTime.UtcNow.ToString("o"),
                ["online_seller_count"] = qns.Count,
                ["pending_message_count"] = state.PendingMessages.Count,
                ["pending_message_bytes"] = GetPendingMessageBytes(state),
                ["pending_command_result_count"] = state.PendingCommandResults.Count,
                ["sync_in_progress"] = state.Syncing != 0,
                ["message_sync_enabled"] = state.MessageSyncEnabled,
                ["manual_reply_enabled"] = state.AllowManualReply,
                ["effective_auto_reply_enabled"] = Params.Robot.GetIsAutoReply() && !state.RemotePause,
                ["process_id"] = Process.GetCurrentProcess().Id,
                ["uptime_seconds"] = Math.Max(0, (long)(DateTime.Now - ProcessStartedAt).TotalSeconds),
                ["sensitive_data_exposed"] = false
            };
        }

        private static async Task<JObject> ExecuteUpdateCheckAsync()
        {
            var check = await BotUpdateService.CheckNowAsync(false);
            return new JObject
            {
                ["success"] = check != null && check.Success,
                ["current_version"] = BotUpdateService.CurrentVersion,
                ["latest_version"] = check == null || check.Release == null ? string.Empty : check.Release.Version,
                ["update_available"] = check != null && check.UpdateAvailable,
                ["message"] = check == null ? "未返回检查结果" : Safe(check.Message, 260)
            };
        }

        private static async Task<JObject> ExecuteUpdateInstallAsync(JObject payload)
        {
            if (payload == null || !payload.Value<bool?>("confirmed").GetValueOrDefault())
                throw new Exception("远程更新缺少明确二次确认");
            return await BotUpdateService.RemoteInstallLatestAsync();
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

        private static void SetPendingCommandResult(ShopWebState state, long id, JObject result)
        {
            if (state == null || id < 1 || result == null) return;
            lock (state.PendingCommandResultsSync)
                state.PendingCommandResults[id] = result;
        }

        private static void AcknowledgeCommandResults(
            ShopWebState state, IEnumerable<KeyValuePair<long, JObject>> acknowledged)
        {
            if (state == null || acknowledged == null) return;
            lock (state.PendingCommandResultsSync)
            {
                foreach (var pair in acknowledged)
                {
                    JObject current;
                    // A command with the same ID may have produced a newer result while
                    // this HTTP round-trip was in flight. Only acknowledge the exact
                    // immutable result object that was included in the successful request.
                    if (state.PendingCommandResults.TryGetValue(pair.Key, out current)
                        && ReferenceEquals(current, pair.Value))
                    {
                        JObject ignored;
                        state.PendingCommandResults.TryRemove(pair.Key, out ignored);
                    }
                }
            }
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