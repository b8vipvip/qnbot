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
    /// Field logs from 1.1.1362 proved that Qianniu can keep dozens of injected recent.html
    /// sockets alive even though only one page owns the business CDP. Some pages never acquire
    /// _vs/imsdk/QN/login data. These pages are intentionally kept as lightweight standby sockets:
    /// closing them used to send retireDuplicate, which permanently disabled their JS reconnect
    /// loop until the whole Qianniu WebView was reloaded.
    ///
    /// This guard does not choose CDP ownership and does not process buyer messages. It observes
    /// inert pages for diagnostics only. A page that later acquires business capability immediately
    /// leaves standby state and remains eligible for normal authority election.
    ///
    /// A second field-log failure mode is a real receiveNewMsg arriving first on a non-authoritative
    /// page while the 12-second liveness timer is still asleep. In that case we request a debounced
    /// run of the existing remote-history reconciliation authority. The normal message ledger and
    /// ProcessIncomingMessageAsync remain the only side-effect/reply owners.
    /// </summary>
    internal static class WebSocketPageLifecycleGuard
    {
        private sealed class InertCandidate
        {
            public WebSocketSession Session;
            public DateTime StartedAtUtc;
        }

        private static readonly TimeSpan InertPageGrace = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ImmediateProbeDebounce = TimeSpan.FromMilliseconds(900);
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
        private static int _initialized;

        public static object InitializeForApp()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
            {
                MyWebSocketServer.WSocketSvrInst.OnRecieveMessage += OnWebSocketMessage;
                Log.Info("千牛WebSocket页面生命周期守卫已启动：无业务能力页面保持可恢复standby；重复页实时入站触发现有历史权威快速核对。");
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
            var seller = ResolveSeller(sessionId, e.Value);
            if (seller.Length == 0) return;
            SessionSellers[sessionId] = seller;

            // The authoritative CDP receives the normal event directly. Only a physical duplicate
            // page is evidence that a prompt history reconciliation may be needed.
            if (MyWebSocketServer.WSocketSvrInst.IsAuthoritativeSellerSession(seller, sessionId)) return;
            RequestImmediateHistoryProbe(seller, sessionId);
        }

        private static void ObserveStatus(WebSocketSession session, string sessionId, string response)
        {
            JObject status;
            try { status = JObject.Parse(response ?? "{}"); }
            catch { return; }

            var seller = Convert.ToString(status["loginNick"] ?? string.Empty).Trim();
            if (seller.Length > 0) SessionSellers[sessionId] = seller;

            // duplicateRetire remains readable for rolling-upgrade diagnostics, but it no longer
            // grants permission to physically close a page. A server restart must always be able
            // to reuse an already loaded WebView without requiring a Qianniu restart.
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
            {
                ScheduleStandbyObservation(sessionId, candidate, legacyRetireCapable);
            }
        }

        private static void ScheduleStandbyObservation(string sessionId, InertCandidate candidate, bool legacyRetireCapable)
        {
            Task.Run(async () =>
            {
                var remaining = InertPageGrace - (DateTime.UtcNow - candidate.StartedAtUtc);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining).ConfigureAwait(false);

                InertCandidate current;
                if (!InertCandidates.TryGetValue(sessionId, out current)
                    || !ReferenceEquals(current, candidate)
                    || current.Session == null)
                {
                    return;
                }

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
            catch
            {
                return string.Empty;
            }
        }

        private static void RequestImmediateHistoryProbe(string seller, string sourceSession)
        {
            seller = (seller ?? string.Empty).Trim();
            if (seller.Length == 0) return;
            var now = DateTime.UtcNow;
            DateTime next;
            if (NextImmediateProbeAt.TryGetValue(seller, out next) && next > now) return;
            NextImmediateProbeAt[seller] = now.Add(ImmediateProbeDebounce);
            if (!ImmediateProbeRunning.TryAdd(seller, 0)) return;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(120).ConfigureAwait(false);
                    var qn = QN.FindExistingBySellerNick(seller);
                    if (qn == null || qn.CDP == null || qn.CDP.IsInvalidated) return;
                    Log.Info("重复千牛页面实时入站触发业务历史快速核对: sellerRef="
                        + MyWebSocketServer.DiagnosticRef("seller", seller)
                        + ", sourceSessionRef=" + MyWebSocketServer.DiagnosticRef("session", sourceSession));
                    await qn.ProbeActiveConversationForMissedInboundAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.ErrorWithMaxCount("重复千牛页面实时入站快速核对失败: " + ex.Message, 20);
                }
                finally
                {
                    byte ignored;
                    ImmediateProbeRunning.TryRemove(seller, out ignored);
                }
            });
        }
    }
}
