using Bot.ChatRecord;
using BotLib;
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
            public string SourceSession;
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
        // The WebSocket listener accepts payloads up to 5 MiB. A count-only queue
        // could therefore retain multiple GiB during an authority outage.
        private const long MaxPendingInboundPayloadBytes = 16L * 1024L * 1024L;

        private static readonly ConcurrentDictionary<string, SessionSellerBinding> SessionSellers =
            new ConcurrentDictionary<string, SessionSellerBinding>(StringComparer.Ordinal);
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

            if (string.Equals(e.Type, "qnbotStatus", StringComparison.Ordinal))
            {
                ObserveStatusSeller(sessionId, e.Value);
                return;
            }

            if (!IsRecoverableInboundType(e.Type)) return;
            var response = e.Value ?? string.Empty;
            if (response.Length == 0) return;

            var seller = ResolveSeller(sessionId, e.Type, response);
            if (seller.Length == 0)
            {
                Log.Info("重复CDP入站消息暂无法确定seller，等待正常通道处理: sessionRef="
                    + PrivacyToken("session", sessionId) + ", type=" + e.Type);
                return;
            }

            var now = DateTime.Now;
            TouchSessionSeller(sessionId, seller, now);
            if (!TryAcceptInboundFingerprint(seller, e.Type, response, now))
            {
                MaybeLogSuppressedDuplicate(seller, sessionId, e.Type);
                MaybeCleanupTransientState(now);
                return;
            }
            MaybeCleanupTransientState(now);

            var item = new PendingInboundEvent
            {
                Seller = seller,
                SourceSession = sessionId,
                Type = e.Type,
                Response = response,
                ReceivedAt = now
            };

            if (!TryDeliverLive(item))
            {
                EnqueuePendingBounded(item, "initial");
                Log.Info("千牛入站消息已暂存等待权威CDP就绪: sellerRef=" + PrivacyToken("seller", seller)
                    + ", sessionRef=" + PrivacyToken("session", sessionId) + ", type=" + e.Type
                    + ", pending=" + GetPendingCount() + "/" + MaxPendingInboundEvents);
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
            string seller,
            string type,
            string response,
            DateTime now)
        {
            var fingerprint = BuildInboundFingerprint(seller, type, response);
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

        private static string BuildInboundFingerprint(string seller, string type, string response)
        {
            // Hash the complete event rather than buyer text. Distinct messages with identical human
            // text still carry different message ids/timestamps and therefore remain independent.
            var raw = (seller ?? string.Empty) + "\u001f"
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

        private static void MaybeCleanupTransientState(DateTime now)
        {
            if ((Interlocked.Increment(ref _cleanupTick) & 127) != 0) return;

            foreach (var pair in RecentInboundFingerprints)
            {
                if (now - pair.Value <= InboundFingerprintRetention) continue;
                DateTime ignored;
                RecentInboundFingerprints.TryRemove(pair.Key, out ignored);
            }

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

        private static void EnqueuePendingBounded(PendingInboundEvent item, string reason)
        {
            if (item == null) return;
            item.PayloadBytes = (long)(item.Response ?? string.Empty).Length * sizeof(char);
            if (item.PayloadBytes > MaxPendingInboundPayloadBytes)
            {
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
                while (Pending.Count >= MaxPendingInboundEvents
                    || _pendingPayloadBytes + item.PayloadBytes > MaxPendingInboundPayloadBytes)
                {
                    if (Pending.Count == 0) break;
                    var dropped = Pending.Dequeue();
                    _pendingPayloadBytes = Math.Max(0, _pendingPayloadBytes - dropped.PayloadBytes);
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

        private static bool TryDeliverLive(PendingInboundEvent item)
        {
            var qn = QN.FindExistingBySellerNick(item.Seller);
            var target = qn == null ? null : qn.CDP;
            if (target == null || target.IsInvalidated) return false;

            // The authoritative page already receives its own live global event normally.
            // Only duplicate-page events need explicit forwarding.
            if (string.Equals(target.SessionId, item.SourceSession, StringComparison.Ordinal))
                return true;

            // Preserve the physical source while dispatching through the logical authoritative
            // CDP. CDPClient may use precise onConversationChange source evidence for command routing,
            // but the bridge itself still never changes qn.CDP or opens/switches the visible chat.
            using (CDPClient.BeginForwardedInbound(item.SourceSession))
            {
                target.DispatchInboundEvent(item.Type, item.Response);
            }
            Log.Info("重复千牛CDP入站消息已转交权威会话: sellerRef=" + PrivacyToken("seller", item.Seller)
                + ", fromSessionRef=" + PrivacyToken("session", item.SourceSession)
                + ", toSessionRef=" + PrivacyToken("session", target.SessionId)
                + ", type=" + item.Type);
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
                        Log.Info("千牛入站暂存消息等待权威CDP超时，已放弃: sellerRef="
                            + PrivacyToken("seller", item.Seller)
                            + ", sessionRef=" + PrivacyToken("session", item.SourceSession)
                            + ", type=" + item.Type);
                        continue;
                    }

                    var qn = QN.FindExistingBySellerNick(item.Seller);
                    var target = qn == null ? null : qn.CDP;
                    if (target == null || target.IsInvalidated)
                    {
                        EnqueuePendingBounded(item, "retry_wait_authoritative_cdp");
                        continue;
                    }

                    // This item was queued specifically because no QN event consumer was ready at
                    // arrival time. Replay even if that same source page later became authoritative.
                    using (CDPClient.BeginForwardedInbound(item.SourceSession))
                    {
                        target.DispatchInboundEvent(item.Type, item.Response);
                    }
                    Log.Info("已补发初始化期间暂存的千牛入站消息: sellerRef="
                        + PrivacyToken("seller", item.Seller)
                        + ", fromSessionRef=" + PrivacyToken("session", item.SourceSession)
                        + ", toSessionRef=" + PrivacyToken("session", target.SessionId)
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