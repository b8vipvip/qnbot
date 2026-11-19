using Bot.ChatRecord;
using BotLib;
using Bot.ShopScope;
using DbEntity;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SuperWebSocket;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Bot
{
    public partial class App
    {
        private readonly object _duplicateCdpInboundRecoveryBridgeBootstrap =
            ChromeNs.DuplicateCdpInboundRecoveryBridge.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// Qianniu can expose several recent.html/iframe WebSocket pages for one logged-in seller.
    /// MyWebSocketServer intentionally keeps exactly one authoritative CDP for commands/sending,
    /// but buyer notifications and conversation-change events can arrive on a different page.
    /// This bridge forwards inbound/state events into the authoritative CDP without ever changing
    /// outbound ownership or opening/switching the visible conversation itself.
    /// </summary>
    internal static class DuplicateCdpInboundRecoveryBridge
    {
        private sealed class PendingInboundEvent
        {
            public string Seller;
            public string ShopKey;
            public string SourceSession;
            public bool FingerprintAccepted;
            public string Type;
            public string Response;
            public DateTime ReceivedAt;
            public long PayloadBytes;
        }

        private sealed class SessionSellerBinding
        {
            public string Seller;
            public DateTime LastSeenAt;
        }

        // The same Qianniu global event is commonly emitted by more than one injected recent.html
        // page. Hashing the complete payload means distinct messages with identical human text still
        // have independent ids/timestamps. Keep an exact replay suppressed across watchdog/recovery
        // cadences instead of accepting the same physical event again after only a few seconds.
        private static readonly TimeSpan InboundFingerprintWindow = TimeSpan.FromMinutes(2);
        // Conversation changes are state transitions, not durable message identities.
        // Keep only a short cross-page burst window so switching A -> B -> A within
        // two minutes is not mistaken for a replay of the first A event.
        private static readonly TimeSpan ConversationChangeFingerprintWindow = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan InboundFingerprintRetention = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan SessionSellerRetention = TimeSpan.FromHours(2);
        private const int MaxPendingInboundEvents = 512;
        private const int MaxRecentInboundFingerprints = 65536;
        // The WebSocket listener accepts payloads up to 5 MiB. A count-only queue
        // could therefore retain multiple GiB during an authority outage.
        private const long MaxPendingInboundPayloadBytes = 16L * 1024L * 1024L;

        private static readonly ConcurrentDictionary<string, SessionSellerBinding> SessionSellers =
            new ConcurrentDictionary<string, SessionSellerBinding>(StringComparer.Ordinal);
        // A physical WebSocket that has just changed login identity must not forward
        // queued events captured under its previous ShopKey while the next status
        // event establishes the new binding.
        private static readonly ConcurrentDictionary<string, byte> RetiredSourceSessions =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        // Serialize the final source-identity check with transition/close. Fixed stripes
        // avoid retaining one lock object per transient WebSocket session.
        private static readonly object[] SessionTransitionLocks =
            Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
        private static readonly ConcurrentDictionary<string, DateTime> RecentInboundFingerprints =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly object PendingSync = new object();
        private static readonly Queue<PendingInboundEvent> Pending =
            new Queue<PendingInboundEvent>();
        private static long _pendingPayloadBytes;
        private static Timer _retryTimer;
        private static int _initialized;
        private static int _draining;
        private static int _cleanupTick;
        private static long _suppressedDuplicateCount;
        private static long _droppedPendingCount;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                MyWebSocketServer.WSocketSvrInst.OnRecieveMessage += OnWebSocketMessage;
                _retryTimer = new Timer(_ => DrainPending(), null, 250, 250);
                Log.Info("重复千牛CDP入站消息恢复桥已启动：发送通道仍保持单一权威会话；暂存队列上限="
                    + MaxPendingInboundEvents + " 条 / "
                    + (MaxPendingInboundPayloadBytes / 1024 / 1024) + " MiB。");
            }
            return new object();
        }

        private static void OnWebSocketMessage(object sender, WSocketNewMessageEventArgs e)
        {
            var session = sender as WebSocketSession;
            if (session == null || e == null) return;
            var sessionId = (session.SessionID ?? string.Empty).Trim();
            if (sessionId.Length == 0) return;
            var server = MyWebSocketServer.WSocketSvrInst;
            if (server == null || !server.IsWebSocketSessionConnected(sessionId)) return;

            if (string.Equals(e.Type, "qnbotStatus", StringComparison.Ordinal))
            {
                ObserveStatusSeller(sessionId, e.Value);
                return;
            }

            if (!IsRecoverableInboundType(e.Type)) return;
            if (IsSessionIdentityTransitioning(sessionId)) return;
            var response = e.Value ?? string.Empty;
            if (response.Length == 0) return;

            var shopKey = server.GetShopKeyForSession(sessionId);
            var seller = ResolveSeller(sessionId, e.Type, response);
            if (seller.Length > 0) TouchSessionSeller(sessionId, seller, DateTime.Now);
            if (shopKey.Length == 0)
            {
                // A seller nickname is diagnostic only, not a stable routing identity.
                // If this physical socket has no verified ShopKey at event arrival,
                // do not queue a payload that could later be rebound to another shop.
                // The authoritative page/history recovery remains responsible for reconciliation.
                Log.Info("重复CDP入站消息缺少来源稳定ShopKey，拒绝跨会话暂存: sellerRef="
                    + PrivacyToken("seller", seller) + ", sessionRef="
                    + PrivacyToken("session", sessionId) + ", type=" + e.Type);
                return;
            }

            var now = DateTime.Now;
            var item = new PendingInboundEvent
            {
                Seller = seller,
                ShopKey = shopKey,
                SourceSession = sessionId,
                Type = e.Type,
                Response = response,
                ReceivedAt = now
            };
            if (shopKey.Length > 0)
            {
                if (!TryAcceptInboundFingerprint(shopKey, e.Type, response, now))
                {
                    MaybeLogSuppressedDuplicate(seller, sessionId, e.Type);
                    MaybeCleanupTransientState(now);
                    return;
                }
                item.FingerprintAccepted = true;
            }
            MaybeCleanupTransientState(now);

            if (!TryDeliverLive(item, false))
            {
                EnqueuePendingBounded(item, "initial");
                if (!IsSessionIdentityTransitioning(sessionId))
                {
                    Log.Info("千牛入站消息已暂存等待ShopKey权威CDP就绪: sellerRef=" + PrivacyToken("seller", seller)
                        + ", shopRef=" + PrivacyToken("shop", item.ShopKey)
                        + ", sessionRef=" + PrivacyToken("session", sessionId) + ", type=" + e.Type
                        + ", pending=" + GetPendingCount() + "/" + MaxPendingInboundEvents);
                }
            }
        }

        private static bool IsRecoverableInboundType(string type)
        {
            // onChatDlgActive may be synthesized by periodic page polling and therefore must never
            // drive current-buyer state across duplicate pages. onConversationChange is different:
            // it is emitted from the Qianniu conversation-change event and contains the changed
            // Conversation object itself. messageCenterNotify is a business notification and does
            // not mutate the visible conversation, so it is safe and important to recover as well.
            return string.Equals(type, "receiveNewMsg", StringComparison.Ordinal)
                || string.Equals(type, "onShopRobotReceriveNewMsgs", StringComparison.Ordinal)
                || string.Equals(type, "onConversationChange", StringComparison.Ordinal)
                || string.Equals(type, "messageCenterNotify", StringComparison.Ordinal);
        }

        private static void ObserveStatusSeller(string sessionId, string response)
        {
            try
            {
                var jo = JObject.Parse(response ?? "{}");
                var seller = Convert.ToString(jo["loginNick"] ?? string.Empty).Trim();
                if (seller.Length > 0) TouchSessionSeller(sessionId, seller, DateTime.Now);
            }
            catch
            {
            }
        }

        private static void TouchSessionSeller(string sessionId, string seller, DateTime now)
        {
            sessionId = (sessionId ?? string.Empty).Trim();
            seller = (seller ?? string.Empty).Trim();
            if (sessionId.Length == 0 || seller.Length == 0) return;
            SessionSellers[sessionId] = new SessionSellerBinding
            {
                Seller = seller,
                LastSeenAt = now
            };
        }

        private static string ResolveSeller(string sessionId, string type, string response)
        {
            SessionSellerBinding known;
            if (SessionSellers.TryGetValue(sessionId, out known)
                && known != null
                && !string.IsNullOrWhiteSpace(known.Seller))
            {
                TouchSessionSeller(sessionId, known.Seller, DateTime.Now);
                return known.Seller.Trim();
            }

            if (string.Equals(type, "onShopRobotReceriveNewMsgs", StringComparison.Ordinal)
                || string.Equals(type, "onConversationChange", StringComparison.Ordinal))
            {
                try
                {
                    var active = JsonConvert.DeserializeObject<ActiveLocalUser>(response);
                    var seller = active == null || active.LoginID == null
                        ? string.Empty
                        : (active.LoginID.Nick ?? string.Empty).Trim();
                    if (seller.Length > 0) return seller;
                }
                catch
                {
                }
            }

            if (string.Equals(type, "receiveNewMsg", StringComparison.Ordinal))
            {
                try
                {
                    var chat = JsonConvert.DeserializeObject<ChatResponse>(response);
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var message in chat == null || chat.result == null
                        ? new List<QNChatMessage>()
                        : chat.result.Where(x => x != null))
                    {
                        if (message.fromid != null && !string.IsNullOrWhiteSpace(message.fromid.nick))
                            ids.Add(message.fromid.nick.Trim());
                        if (message.toid != null && !string.IsNullOrWhiteSpace(message.toid.nick))
                            ids.Add(message.toid.nick.Trim());
                    }

                    var sellers = QN.GetRuntimeSafetySnapshot()
                        .Where(qn => qn != null && qn.Seller != null
                            && !string.IsNullOrWhiteSpace(qn.Seller.Nick)
                            && ids.Contains(qn.Seller.Nick.Trim()))
                        .Select(qn => qn.Seller.Nick.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    if (sellers.Count == 1) return sellers[0];
                }
                catch
                {
                }
            }

            return string.Empty;
        }

        private static bool TryAcceptInboundFingerprint(
            string shopKey,
            string type,
            string response,
            DateTime now)
        {
            var fingerprint = BuildInboundFingerprint(shopKey, type, response);
            var fingerprintWindow = string.Equals(type, "onConversationChange", StringComparison.Ordinal)
                ? ConversationChangeFingerprintWindow
                : InboundFingerprintWindow;
            while (true)
            {
                DateTime seenAt;
                if (!RecentInboundFingerprints.TryGetValue(fingerprint, out seenAt))
                {
                    if (RecentInboundFingerprints.TryAdd(fingerprint, now)) return true;
                    continue;
                }

                if (now - seenAt <= fingerprintWindow) return false;
                if (RecentInboundFingerprints.TryUpdate(fingerprint, now, seenAt)) return true;
            }
        }

        private static string BuildInboundFingerprint(string shopKey, string type, string response)
        {
            // Scope exact replay suppression to the verified ShopKey, never to a display nickname.
            // Distinct shops with the same seller nickname must not suppress each other's events.
            var raw = (shopKey ?? string.Empty) + "\u001f"
                + (type ?? string.Empty) + "\u001f"
                + (response ?? string.Empty);
            return StableHash64(raw).ToString("x16");
        }

        private static ulong StableHash64(string value)
        {
            // Deterministic FNV-1a over UTF-16 bytes. This is an event fingerprint/privacy token,
            // not a security primitive, and avoids retaining raw buyer/seller payloads in the cache.
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            unchecked
            {
                foreach (var ch in value ?? string.Empty)
                {
                    hash ^= (byte)(ch & 0xff);
                    hash *= prime;
                    hash ^= (byte)(ch >> 8);
                    hash *= prime;
                }
            }
            return hash;
        }

        private static string PrivacyToken(string kind, string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return (kind ?? "id") + "#none";
            var hash = StableHash64(value).ToString("x16");
            return (kind ?? "id") + "#" + hash.Substring(0, 10);
        }

        private static void MaybeLogSuppressedDuplicate(string seller, string sessionId, string type)
        {
            var count = Interlocked.Increment(ref _suppressedDuplicateCount);
            // Duplicate pages can emit the same event at very high frequency. Keep the first few
            // diagnostics and periodic milestones without writing one log line per replay.
            if (count > 3 && count % 100 != 0) return;
            Log.Info("重复千牛CDP入站事件已长窗去重: sellerRef=" + PrivacyToken("seller", seller)
                + ", sessionRef=" + PrivacyToken("session", sessionId)
                + ", type=" + type + ", suppressedTotal=" + count);
        }

        private static int GetSessionTransitionLockIndex(string sessionId)
        {
            var hash = StringComparer.Ordinal.GetHashCode(sessionId ?? string.Empty);
            return (hash & int.MaxValue) % SessionTransitionLocks.Length;
        }

        private static object GetSessionTransitionLock(string sessionId)
        {
            return SessionTransitionLocks[GetSessionTransitionLockIndex(sessionId)];
        }

        // A forwarded event depends on two physical sockets: its source and the
        // authoritative target. Acquire both fixed stripes in deterministic order
        // so neither identity can change between the final checks and dispatch.
        private static bool WithSessionTransitionLocks(
            string sourceSession, string targetSession, Func<bool> action)
        {
            if (action == null) return false;
            var sourceIndex = GetSessionTransitionLockIndex(sourceSession);
            var targetIndex = GetSessionTransitionLockIndex(targetSession);
            if (sourceIndex == targetIndex)
            {
                lock (SessionTransitionLocks[sourceIndex]) return action();
            }
            var firstIndex = Math.Min(sourceIndex, targetIndex);
            var secondIndex = Math.Max(sourceIndex, targetIndex);
            lock (SessionTransitionLocks[firstIndex])
            {
                lock (SessionTransitionLocks[secondIndex])
                {
                    return action();
                }
            }
        }

        // The original CDP dispatcher must use the same transition stripe as the
        // recovery queue. A check followed by an unlocked dispatch leaves a narrow
        // A->B race in which an old physical socket can still deliver to its old QN.
        internal static bool TryDispatchRawInbound(
            CDPClient client, string type, string response)
        {
            if (client == null || string.IsNullOrEmpty(response)) return false;
            var sessionId = (client.SessionId ?? string.Empty).Trim();
            if (sessionId.Length == 0) return false;
            var server = MyWebSocketServer.WSocketSvrInst;
            if (server == null) return false;

            lock (GetSessionTransitionLock(sessionId))
            {
                if (client.IsInvalidated || !server.IsWebSocketSessionConnected(sessionId))
                    return false;

                // Status is the identity-discovery channel and remains available to
                // connected standby sockets. All business traffic must still match
                // the exact current ShopKey and authoritative owner.
                if (!string.Equals(type, "qnbotStatus", StringComparison.Ordinal))
                {
                    if (IsSessionIdentityTransitioning(sessionId) || client.IsShopIdentityRetired)
                        return false;
                    var shopKey = (client.ShopKey ?? string.Empty).Trim();
                    if (shopKey.Length == 0
                        || !string.Equals(
                            server.GetShopKeyForSession(sessionId), shopKey, StringComparison.Ordinal)
                        || !server.IsAuthoritativeShopSession(shopKey, sessionId))
                        return false;
                }

                client.DispatchInboundEvent(type, response);
                return true;
            }
        }

        private static void ReleaseAcceptedFingerprint(PendingInboundEvent item)
        {
            if (item == null || !item.FingerprintAccepted || string.IsNullOrWhiteSpace(item.ShopKey))
                return;
            var fingerprint = BuildInboundFingerprint(item.ShopKey, item.Type, item.Response);
            DateTime ignored;
            RecentInboundFingerprints.TryRemove(fingerprint, out ignored);
            item.FingerprintAccepted = false;
        }

        private static bool IsSourceSessionConnected(string sessionId)
        {
            var server = MyWebSocketServer.WSocketSvrInst;
            return server != null && server.IsWebSocketSessionConnected(sessionId);
        }

        internal static void MarkSessionIdentityTransition(string sessionId)
        {
            sessionId = (sessionId ?? string.Empty).Trim();
            if (sessionId.Length == 0) return;
            lock (GetSessionTransitionLock(sessionId))
            {
                RetiredSourceSessions[sessionId] = 0;
                SessionSellerBinding ignored;
                SessionSellers.TryRemove(sessionId, out ignored);
                var dropped = DropPendingForSourceSession(sessionId);
                if (dropped > 0)
                {
                    var total = Interlocked.Add(ref _droppedPendingCount, dropped);
                    Log.Info("千牛WebSocket登录身份变化，已清除旧身份暂存事件: sessionRef="
                        + PrivacyToken("session", sessionId) + ", dropped=" + dropped
                        + ", droppedTotal=" + total);
                }
            }
        }

        internal static void ClearSessionIdentityTransition(string sessionId)
        {
            sessionId = (sessionId ?? string.Empty).Trim();
            if (sessionId.Length == 0) return;
            lock (GetSessionTransitionLock(sessionId))
            {
                byte ignored;
                RetiredSourceSessions.TryRemove(sessionId, out ignored);
            }
        }

        internal static bool IsSessionIdentityTransitioning(string sessionId)
        {
            sessionId = (sessionId ?? string.Empty).Trim();
            return sessionId.Length > 0 && RetiredSourceSessions.ContainsKey(sessionId);
        }

        private static int DropPendingForSourceSession(string sessionId)
        {
            var dropped = 0;
            lock (PendingSync)
            {
                var retained = new Queue<PendingInboundEvent>();
                while (Pending.Count > 0)
                {
                    var pending = Pending.Dequeue();
                    if (pending == null) continue;
                    if (string.Equals(pending.SourceSession, sessionId, StringComparison.Ordinal))
                    {
                        _pendingPayloadBytes = Math.Max(0, _pendingPayloadBytes - pending.PayloadBytes);
                        ReleaseAcceptedFingerprint(pending);
                        dropped++;
                    }
                    else
                    {
                        retained.Enqueue(pending);
                    }
                }
                while (retained.Count > 0) Pending.Enqueue(retained.Dequeue());
            }
            return dropped;
        }

        internal static void ForgetClosedSession(string sessionId)
        {
            sessionId = (sessionId ?? string.Empty).Trim();
            if (sessionId.Length == 0) return;
            lock (GetSessionTransitionLock(sessionId))
            {
                RetiredSourceSessions[sessionId] = 0;
                SessionSellerBinding ignored;
                SessionSellers.TryRemove(sessionId, out ignored);
                var dropped = DropPendingForSourceSession(sessionId);
                byte ignoredTransition;
                RetiredSourceSessions.TryRemove(sessionId, out ignoredTransition);
                if (dropped > 0)
                    Interlocked.Add(ref _droppedPendingCount, dropped);
            }
        }

        private static void MaybeCleanupTransientState(DateTime now)
        {
            if ((Interlocked.Increment(ref _cleanupTick) & 127) != 0) return;

            foreach (var pair in RecentInboundFingerprints)
            {
                if (now - pair.Value <= InboundFingerprintRetention) continue;
                DateTime ignored;
                RecentInboundFingerprints.TryRemove(pair.Key, out ignored);
            }
            TrimOldestRecentFingerprints();

            foreach (var pair in SessionSellers)
            {
                var binding = pair.Value;
                if (binding == null || now - binding.LastSeenAt > SessionSellerRetention)
                {
                    SessionSellerBinding ignored;
                    SessionSellers.TryRemove(pair.Key, out ignored);
                }
            }
        }

        private static void TrimOldestRecentFingerprints()
        {
            var count = RecentInboundFingerprints.Count;
            if (count <= MaxRecentInboundFingerprints) return;
            // Trim in batches to 75% so a busy queue does not sort on every insert
            // while still keeping the cache strictly bounded after each cleanup pass.
            var targetCount = MaxRecentInboundFingerprints * 3 / 4;
            var removeCount = count - targetCount;
            foreach (var pair in RecentInboundFingerprints.OrderBy(x => x.Value).Take(removeCount))
            {
                DateTime ignored;
                RecentInboundFingerprints.TryRemove(pair.Key, out ignored);
            }
        }

        private static void EnqueuePendingBounded(PendingInboundEvent item, string reason)
        {
            if (item == null) return;
            item.PayloadBytes = (long)(item.Response ?? string.Empty).Length * sizeof(char);
            if (item.PayloadBytes > MaxPendingInboundPayloadBytes)
            {
                ReleaseAcceptedFingerprint(item);
                var oversizedTotal = Interlocked.Increment(ref _droppedPendingCount);
                if (oversizedTotal <= 3 || oversizedTotal % 50 == 0)
                {
                    Log.ErrorWithMaxCount(
                        "重复CDP入站补偿事件超过单队列内存预算，已丢弃超大事件: droppedTotal="
                        + oversizedTotal + ", payloadBytes=" + item.PayloadBytes
                        + ", limitBytes=" + MaxPendingInboundPayloadBytes
                        + ", reason=" + (reason ?? string.Empty)
                        + ", type=" + item.Type,
                        100);
                }
                return;
            }

            long droppedCount = 0;
            string lastDroppedType = string.Empty;
            int pendingCount;
            long pendingBytes;
            lock (PendingSync)
            {
                // Identity retirement can race the initial delivery attempt. Recheck
                // under the queue lock so a transition cannot enqueue an old-ShopKey
                // payload after MarkSessionIdentityTransition drained the queue.
                if (IsSessionIdentityTransitioning(item.SourceSession)
                    || !IsSourceSessionConnected(item.SourceSession))
                {
                    ReleaseAcceptedFingerprint(item);
                    return;
                }
                while (Pending.Count >= MaxPendingInboundEvents
                    || _pendingPayloadBytes + item.PayloadBytes > MaxPendingInboundPayloadBytes)
                {
                    if (Pending.Count == 0) break;
                    var dropped = Pending.Dequeue();
                    _pendingPayloadBytes = Math.Max(0, _pendingPayloadBytes - dropped.PayloadBytes);
                    ReleaseAcceptedFingerprint(dropped);
                    lastDroppedType = dropped.Type ?? string.Empty;
                    droppedCount++;
                }

                Pending.Enqueue(item);
                _pendingPayloadBytes += item.PayloadBytes;
                pendingCount = Pending.Count;
                pendingBytes = _pendingPayloadBytes;
            }

            if (droppedCount > 0)
            {
                var total = Interlocked.Add(ref _droppedPendingCount, droppedCount);
                if (total <= 3 || total % 50 < droppedCount)
                {
                    Log.ErrorWithMaxCount(
                        "重复CDP入站暂存队列达到数量/内存预算，已丢弃最旧补偿事件: droppedTotal="
                        + total + ", limit=" + MaxPendingInboundEvents
                        + ", payloadLimitBytes=" + MaxPendingInboundPayloadBytes
                        + ", pending=" + pendingCount + ", pendingBytes=" + pendingBytes
                        + ", reason=" + (reason ?? string.Empty)
                        + ", lastDroppedType=" + lastDroppedType,
                        100);
                }
            }
        }

        private static bool TryDequeuePending(out PendingInboundEvent item)
        {
            lock (PendingSync)
            {
                if (Pending.Count == 0)
                {
                    item = null;
                    return false;
                }
                item = Pending.Dequeue();
                _pendingPayloadBytes = Math.Max(0, _pendingPayloadBytes - (item == null ? 0 : item.PayloadBytes));
                return true;
            }
        }

        private static int GetPendingCount()
        {
            lock (PendingSync) return Pending.Count;
        }

        private static bool TryDeliverLive(PendingInboundEvent item, bool replayPending)
        {
            if (item == null) return false;
            if (IsSessionIdentityTransitioning(item.SourceSession)
                || !IsSourceSessionConnected(item.SourceSession)) return false;
            var server = MyWebSocketServer.WSocketSvrInst;
            if (server == null) return false;

            var mappedShopKey = server.GetShopKeyForSession(item.SourceSession);
            var shopKey = (item.ShopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0)
                shopKey = mappedShopKey;
            else if (mappedShopKey.Length > 0
                && !string.Equals(shopKey, mappedShopKey, StringComparison.Ordinal))
            {
                // The source WebSocket changed login identity after this event was queued.
                // Never reinterpret the old payload under the new ShopKey.
                Log.Info("千牛入站暂存事件来源WebSocket身份已变化，拒绝跨店补发: oldShopRef="
                    + PrivacyToken("shop", shopKey)
                    + ", currentShopRef=" + PrivacyToken("shop", mappedShopKey)
                    + ", sessionRef=" + PrivacyToken("session", item.SourceSession));
                ReleaseAcceptedFingerprint(item);
                return true;
            }
            if (shopKey.Length == 0) return false;
            item.ShopKey = shopKey;

            if (!item.FingerprintAccepted)
            {
                if (!TryAcceptInboundFingerprint(shopKey, item.Type, item.Response, DateTime.Now))
                {
                    MaybeLogSuppressedDuplicate(item.Seller, item.SourceSession, item.Type);
                    return true;
                }
                item.FingerprintAccepted = true;
            }

            // Resolve the receiver only from the ShopKey that was bound to this physical
            // WebSocket. Seller nickname is diagnostic data and must never choose a QN.
            var qn = ShopContextLocator.FindConnectedQnByShopKey(shopKey);
            var target = qn == null ? null : qn.CDP;
            if (target == null || target.IsInvalidated || target.IsShopIdentityRetired
                || !server.IsAuthoritativeShopSession(shopKey, target.SessionId))
                return false;

            // The authoritative page already receives its own live global event normally.
            // A queued startup event is the exception: replay it even if its source page has
            // since become authoritative, because no event consumer existed at arrival time.
            if (!replayPending
                && string.Equals(target.SessionId, item.SourceSession, StringComparison.Ordinal))
                return true;

            // Source and target identities can transition independently. Lock both
            // fixed stripes in deterministic order so a target login switch cannot
            // race the source-side check and receive a stale forwarded event.
            var forwarded = false;
            var accepted = WithSessionTransitionLocks(item.SourceSession, target.SessionId, () =>
            {
                if (IsSessionIdentityTransitioning(item.SourceSession)
                    || !IsSourceSessionConnected(item.SourceSession))
                    return false;

                var finalMappedShopKey = server.GetShopKeyForSession(item.SourceSession);
                if (!string.Equals(finalMappedShopKey, shopKey, StringComparison.Ordinal))
                {
                    ReleaseAcceptedFingerprint(item);
                    return true;
                }

                if (IsSessionIdentityTransitioning(target.SessionId)
                    || target.IsInvalidated
                    || target.IsShopIdentityRetired
                    || !IsSourceSessionConnected(target.SessionId)
                    || !string.Equals(
                        server.GetShopKeyForSession(target.SessionId), shopKey, StringComparison.Ordinal)
                    || !server.IsAuthoritativeShopSession(shopKey, target.SessionId))
                    return false;

                using (CDPClient.BeginForwardedInbound(item.SourceSession))
                {
                    target.DispatchInboundEvent(item.Type, item.Response);
                }
                forwarded = true;
                return true;
            });
            if (!accepted) return false;
            if (forwarded)
            {
                Log.Info("重复千牛CDP入站消息已按ShopKey转交权威会话: shopRef=" + PrivacyToken("shop", shopKey)
                    + ", sellerRef=" + PrivacyToken("seller", item.Seller)
                    + ", fromSessionRef=" + PrivacyToken("session", item.SourceSession)
                    + ", toSessionRef=" + PrivacyToken("session", target.SessionId)
                    + ", type=" + item.Type);
            }
            return true;
        }

        private static void DrainPending()
        {
            if (Interlocked.Exchange(ref _draining, 1) != 0) return;
            try
            {
                MaybeCleanupTransientState(DateTime.Now);
                var count = Math.Min(GetPendingCount(), MaxPendingInboundEvents);
                for (var i = 0; i < count; i++)
                {
                    PendingInboundEvent item;
                    if (!TryDequeuePending(out item) || item == null) continue;
                    if (DateTime.Now - item.ReceivedAt > TimeSpan.FromSeconds(15))
                    {
                        ReleaseAcceptedFingerprint(item);
                        Log.Info("千牛入站暂存消息等待权威CDP超时，已放弃: sellerRef="
                            + PrivacyToken("seller", item.Seller)
                            + ", sessionRef=" + PrivacyToken("session", item.SourceSession)
                            + ", type=" + item.Type);
                        continue;
                    }

                    if (!TryDeliverLive(item, true))
                    {
                        EnqueuePendingBounded(item, "retry_wait_shopkey_authoritative_cdp");
                        continue;
                    }
                    Log.Info("已补发初始化期间暂存的千牛入站消息: shopRef="
                        + PrivacyToken("shop", item.ShopKey)
                        + ", sellerRef=" + PrivacyToken("seller", item.Seller)
                        + ", fromSessionRef=" + PrivacyToken("session", item.SourceSession)
                        + ", type=" + item.Type);
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("重复千牛CDP入站消息恢复失败: " + ex.Message, 10);
            }
            finally
            {
                Interlocked.Exchange(ref _draining, 0);
            }
        }
    }
}