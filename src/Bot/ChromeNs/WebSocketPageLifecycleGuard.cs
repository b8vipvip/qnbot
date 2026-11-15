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
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot
{
    public partial class App
    {
        private readonly object _webSocketPageLifecycleGuardBootstrap =
            ChromeNs.WebSocketPageLifecycleGuard.InitializeForApp();
    }
}

namespace Bot.ChromeNs
{
    /// <summary>
    /// Keeps inert Qianniu pages recoverable and uses duplicate-page receive events only as
    /// evidence for a bounded history probe. All probe ownership, throttling and payload
    /// fingerprints are scoped by ShopKey; seller nicknames are diagnostic data only.
    /// </summary>
    internal static class WebSocketPageLifecycleGuard
    {
        private sealed class InertCandidate
        {
            public WebSocketSession Session;
            public DateTime StartedAtUtc;
            public int ObservationScheduled;
        }

        private static readonly TimeSpan InertPageGrace = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ImmediateProbeDebounce = TimeSpan.FromMilliseconds(900);
        private static readonly TimeSpan DuplicateProbeFingerprintWindow = TimeSpan.FromMinutes(2);
        private const int MaxDuplicateProbeFingerprints = 2048;
        private static readonly ConcurrentDictionary<string, InertCandidate> InertCandidates =
            new ConcurrentDictionary<string, InertCandidate>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> StandbyLogged =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, string> SessionSellers =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> NextImmediateProbeAt =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> ImmediateProbeRunning =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> RecentDuplicateProbePayloads =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static long _duplicateProbePayloadsSuppressed;
        private static int _initialized;

        internal static void ForgetClosedSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            string ignoredSeller;
            byte ignoredStandby;
            InertCandidate ignoredCandidate;
            SessionSellers.TryRemove(sessionId, out ignoredSeller);
            StandbyLogged.TryRemove(sessionId, out ignoredStandby);
            InertCandidates.TryRemove(sessionId, out ignoredCandidate);
        }

        internal static int TrackedSessionCount
        {
            get { return SessionSellers.Count; }
        }

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                MyWebSocketServer.WSocketSvrInst.OnRecieveMessage += OnWebSocketMessage;
                Log.Info("千牛WebSocket页面生命周期守卫已启动：无业务能力页面保持可恢复standby；重复页实时入站按ShopKey与payload指纹合并后触发现有历史权威快速核对。");
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
                ObserveStatus(session, sessionId, e.Value);
                return;
            }

            if (!string.Equals(e.Type, "receiveNewMsg", StringComparison.Ordinal)) return;
            var server = MyWebSocketServer.WSocketSvrInst;
            if (server == null) return;
            var shopKey = server.GetShopKeyForSession(sessionId);
            if (string.IsNullOrWhiteSpace(shopKey)) return;

            // The authoritative CDP receives the normal event directly. A duplicate page
            // is only allowed to request a ShopKey-scoped reconciliation probe.
            if (server.IsAuthoritativeShopSession(shopKey, sessionId)) return;
            var seller = ResolveSeller(sessionId, e.Value);
            if (seller.Length > 0) SessionSellers[sessionId] = seller;

            if (!TryClaimDuplicateProbePayload(shopKey, seller, e.Type, e.Value)) return;
            RequestImmediateHistoryProbe(shopKey, seller, sessionId);
        }

        private static void ObserveStatus(WebSocketSession session, string sessionId, string response)
        {
            JObject status;
            try { status = JObject.Parse(response ?? "{}"); }
            catch { return; }

            var seller = Convert.ToString(status["loginNick"] ?? string.Empty).Trim();
            if (seller.Length > 0) SessionSellers[sessionId] = seller;

            var legacyRetireCapable = ReadBool(status, "duplicateRetire");
            var businessCapable = ReadBool(status, "hasImsdk")
                || ReadBool(status, "hasQN")
                || ReadBool(status, "hasVs")
                || ReadBool(status, "hasLoginID");

            if (businessCapable)
            {
                InertCandidate ignored;
                byte standbyIgnored;
                InertCandidates.TryRemove(sessionId, out ignored);
                StandbyLogged.TryRemove(sessionId, out standbyIgnored);
                return;
            }

            var candidate = InertCandidates.GetOrAdd(sessionId, _ => new InertCandidate
            {
                Session = session,
                StartedAtUtc = DateTime.UtcNow
            });
            candidate.Session = session;
            if (DateTime.UtcNow - candidate.StartedAtUtc < InertPageGrace)
                ScheduleStandbyObservation(sessionId, candidate, legacyRetireCapable);
        }

        private static void ScheduleStandbyObservation(string sessionId, InertCandidate candidate, bool legacyRetireCapable)
        {
            if (Interlocked.CompareExchange(ref candidate.ObservationScheduled, 1, 0) != 0)
                return;
            Task.Run(async () =>
            {
                var remaining = InertPageGrace - (DateTime.UtcNow - candidate.StartedAtUtc);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining).ConfigureAwait(false);

                InertCandidate current;
                if (!InertCandidates.TryGetValue(sessionId, out current)
                    || !ReferenceEquals(current, candidate)
                    || current.Session == null) return;

                InertCandidate removed;
                if (!InertCandidates.TryRemove(sessionId, out removed)) return;
                if (!StandbyLogged.TryAdd(sessionId, 0)) return;

                Log.Info("无业务能力千牛WebSocket页面保持为轻量standby，不关闭通道: sessionRef="
                    + MyWebSocketServer.DiagnosticRef("session", sessionId)
                    + ", graceSeconds=" + (int)InertPageGrace.TotalSeconds
                    + ", legacyRetireCapable=" + legacyRetireCapable
                    + ", evidence=noLogin+noImsdk+noQN+noVs, physicalClose=false");
            });
        }

        private static bool ReadBool(JObject jo, string name)
        {
            if (jo == null) return false;
            var token = jo[name];
            if (token == null) return false;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            bool value;
            return bool.TryParse(token.ToString(), out value) && value;
        }

        private static string ResolveSeller(string sessionId, string response)
        {
            string seller;
            if (SessionSellers.TryGetValue(sessionId, out seller) && !string.IsNullOrWhiteSpace(seller))
                return seller.Trim();

            try
            {
                var chat = JsonConvert.DeserializeObject<ChatResponse>(response ?? string.Empty);
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
                return sellers.Count == 1 ? sellers[0] : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static bool TryClaimDuplicateProbePayload(string shopKey, string seller, string type, string response)
        {
            var now = DateTime.UtcNow;
            var fingerprint = BuildDuplicateProbeFingerprint(shopKey, type, response);
            if (fingerprint.Length == 0) return true;

            DateTime seenAt;
            if (RecentDuplicateProbePayloads.TryGetValue(fingerprint, out seenAt)
                && now - seenAt <= DuplicateProbeFingerprintWindow)
            {
                var suppressed = Interlocked.Increment(ref _duplicateProbePayloadsSuppressed);
                if (suppressed <= 5 || suppressed % 100 == 0)
                    Log.Info("重复千牛standby实时入站已按ShopKey+payload指纹合并，不重复触发CDP历史核对: shopRef="
                        + MyWebSocketServer.DiagnosticRef("shop", shopKey)
                        + ", sellerRef=" + MyWebSocketServer.DiagnosticRef("seller", seller)
                        + ", suppressedTotal=" + suppressed);
                return false;
            }

            RecentDuplicateProbePayloads[fingerprint] = now;
            if (RecentDuplicateProbePayloads.Count > MaxDuplicateProbeFingerprints)
            {
                var cutoff = now - DuplicateProbeFingerprintWindow;
                foreach (var pair in RecentDuplicateProbePayloads.ToArray())
                {
                    if (pair.Value >= cutoff) continue;
                    DateTime ignored;
                    RecentDuplicateProbePayloads.TryRemove(pair.Key, out ignored);
                }
            }
            return true;
        }

        private static string BuildDuplicateProbeFingerprint(string shopKey, string type, string response)
        {
            try
            {
                var input = (shopKey ?? string.Empty).Trim() + "\n"
                    + (type ?? string.Empty).Trim() + "\n" + (response ?? string.Empty);
                using (var sha = SHA256.Create())
                {
                    var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                    return Convert.ToBase64String(digest);
                }
            }
            catch { return string.Empty; }
        }

        private static void RequestImmediateHistoryProbe(string shopKey, string seller, string sourceSession)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (shopKey.Length == 0) return;
            var now = DateTime.UtcNow;
            DateTime next;
            if (NextImmediateProbeAt.TryGetValue(shopKey, out next) && next > now) return;
            NextImmediateProbeAt[shopKey] = now.Add(ImmediateProbeDebounce);
            if (!ImmediateProbeRunning.TryAdd(shopKey, 0)) return;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(120).ConfigureAwait(false);
                    var qn = ShopContextLocator.FindConnectedQnByShopKey(shopKey);
                    var server = MyWebSocketServer.WSocketSvrInst;
                    if (qn == null || qn.CDP == null || qn.CDP.IsInvalidated
                        || qn.CDP.IsShopIdentityRetired || server == null
                        || !server.IsAuthoritativeShopSession(shopKey, qn.CDP.SessionId)) return;

                    Log.Info("重复千牛页面实时入站触发当前ShopKey权威会话历史快速核对: shopRef="
                        + MyWebSocketServer.DiagnosticRef("shop", shopKey)
                        + ", sellerRef=" + MyWebSocketServer.DiagnosticRef("seller", seller)
                        + ", sourceSessionRef=" + MyWebSocketServer.DiagnosticRef("session", sourceSession));
                    await qn.ProbeActiveConversationForMissedInboundAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("重复千牛页面实时入站快速核对失败: " + ex.Message, 20);
                }
                finally
                {
                    NextImmediateProbeAt[shopKey] = DateTime.UtcNow.AddSeconds(3);
                    byte ignored;
                    ImmediateProbeRunning.TryRemove(shopKey, out ignored);
                }
            });
        }
    }
}
