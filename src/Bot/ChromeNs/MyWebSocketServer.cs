using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using BotLib;
using DbEntity;
using SuperWebSocket;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Bot.ChromeNs;
using Bot.Automation;
using Bot.Automation.ChatDeskNs;
using SuperSocket.SocketBase.Config;
using Bot.AssistWindow.NotifyIcon;
using Bot.ShopScope;

namespace Bot.ChromeNs
{
    public class MyWebSocketServer
    {
        public static MyWebSocketServer WSocketSvrInst = null;
        private readonly ConcurrentDictionary<string, CDPClient> _clients = new ConcurrentDictionary<string, CDPClient>();
        private readonly ConcurrentDictionary<string, bool> _initialized = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, bool> _initializing = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, bool> _connectedSessions = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, string> _lastStatusBindings = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, DateTime> _lastStatusBindingRefreshUtc =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        // Async status lookups may complete out of order. A per-session generation
        // ensures only the newest status event can update the active buyer.
        private readonly ConcurrentDictionary<string, long> _statusBindingGenerations =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private long _statusBindingGenerationSequence;
        // Identity probes are fenced independently from buyer/status binding attempts:
        // even a heartbeat with unchanged display nicknames invalidates older CDP reads.
        private readonly ConcurrentDictionary<string, long> _shopIdentityProbeGenerations =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _shopSessions = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _sessionShopKeys = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _duplicateShopSessions = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, WebSocketSession> _liveSessions = new ConcurrentDictionary<string, WebSocketSession>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _sessionLastActivityUtc = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> _closingDuplicateSessions = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> _recoverableStandbySessions = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _standbyLoggedSessions = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly object _sellerSessionSync = new object();
        private readonly object _webSocketStartSync = new object();
        private const int MaxDuplicateShopSessions = 3;
        private const int WebSocketStartMaxAttempts = 5;
        private const int WebSocketStartRetryBaseDelayMs = 250;
        private static readonly TimeSpan DuplicateSessionIdleTimeout = TimeSpan.FromMinutes(4);
        private static readonly TimeSpan StatusBindingRefreshInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DuplicateSessionSweepInterval = TimeSpan.FromSeconds(30);
        private int _duplicateSessionSweeperStarted;
        private WebSocketServer _webSocketServer;
        // Process-local salt: aliases correlate only within this Bot lifetime and are never persisted.
        private static readonly byte[] RuntimeAcceptanceDiagnosticSalt = CreateRuntimeAcceptanceDiagnosticSalt();

        static MyWebSocketServer()
        {
            if (WSocketSvrInst == null) WSocketSvrInst = new MyWebSocketServer();
        }

        public EventHandler<WSocketNewMessageEventArgs> OnRecieveMessage;

        private CDPClient GetOrCreateClient(WebSocketSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.SessionID)) return null;
            var sessionId = session.SessionID.Trim();
            lock (_sellerSessionSync)
            {
                // SessionClosed removes the connected marker under this same lock.
                // A late onConversationChange/receive event must not recreate a
                // CDPClient after the close handler has already released its client.
                if (!_connectedSessions.ContainsKey(sessionId)) return null;
                return _clients.GetOrAdd(sessionId, id => new CDPClient(session));
            }
        }

        private static string ReadJsonString(JObject jo, string name)
        {
            if (jo == null) return string.Empty;
            var token = jo[name];
            return token == null ? string.Empty : token.ToString().Trim();
        }

        internal static string DiagnosticRef(string kind, string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return (kind ?? "id") + "#none";
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            unchecked
            {
                foreach (var ch in value)
                {
                    hash ^= (byte)(ch & 0xff);
                    hash *= prime;
                    hash ^= (byte)(ch >> 8);
                    hash *= prime;
                }
            }
            return (kind ?? "id") + "#" + hash.ToString("x16").Substring(0, 10);
        }

        private static byte[] CreateRuntimeAcceptanceDiagnosticSalt()
        {
            var salt = new byte[32];
            try
            {
                using (var random = RandomNumberGenerator.Create())
                    random.GetBytes(salt);
            }
            catch
            {
                var first = Guid.NewGuid().ToByteArray();
                var second = Guid.NewGuid().ToByteArray();
                Buffer.BlockCopy(first, 0, salt, 0, first.Length);
                Buffer.BlockCopy(second, 0, salt, first.Length, second.Length);
            }
            return salt;
        }

        internal static string RuntimeAcceptanceDiagnosticRef(string kind, string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return (kind ?? "id") + "#none";
            using (var hmac = new HMACSHA256(RuntimeAcceptanceDiagnosticSalt))
            {
                var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
                return (kind ?? "id") + "#"
                    + BitConverter.ToString(digest, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private int? GetRuntimeSessionActivityAgeSeconds(string sessionId, DateTime nowUtc)
        {
            DateTime lastActivity;
            if (!_sessionLastActivityUtc.TryGetValue(sessionId, out lastActivity)) return null;
            return Math.Max(0, (int)Math.Floor((nowUtc - lastActivity).TotalSeconds));
        }

        internal void QueueReadOnlyAcceptanceSnapshotRefresh()
        {
            // Do not run diagnostics inline on a CDP/UI event callback. The snapshot
            // writer takes the seller-session lock and must never stall or re-enter
            // the foreground activation path.
            Task.Run(() => WriteReadOnlyAcceptanceSnapshot());
        }

        private void WriteReadOnlyAcceptanceSnapshot()
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                object snapshot;
                using (var process = Process.GetCurrentProcess())
                {
                    var processId = process.Id;
                    var processStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                    string activeShopKey;
                    string activeSessionId;
                    bool activeFocusProofFresh;
                    int? activeFocusProofAgeSeconds;
                    ActiveShopSessionRegistry.GetReadOnlyAcceptanceState(
                        out activeShopKey,
                        out activeSessionId,
                        out activeFocusProofFresh,
                        out activeFocusProofAgeSeconds);
                    lock (_sellerSessionSync)
                    {
                        var mappedSessionIds = new HashSet<string>(_sessionShopKeys.Keys, StringComparer.Ordinal);
                        var shopRows = new List<object>();
                        foreach (var shopPair in _shopSessions.OrderBy(x => x.Key, StringComparer.Ordinal))
                        {
                            var shopKey = shopPair.Key;
                            var ownerSessionId = shopPair.Value ?? string.Empty;
                            CDPClient ownerClient;
                            var hasOwnerClient = _clients.TryGetValue(ownerSessionId, out ownerClient)
                                && ownerClient != null;
                            long ownerIdentityGeneration;
                            long ownerStatusGeneration;
                            _shopIdentityProbeGenerations.TryGetValue(ownerSessionId, out ownerIdentityGeneration);
                            _statusBindingGenerations.TryGetValue(ownerSessionId, out ownerStatusGeneration);

                            var standbys = new List<object>();
                            var recoverableStandbyCount = 0;
                            foreach (var standby in _duplicateShopSessions
                                .Where(x => string.Equals(x.Value, shopKey, StringComparison.Ordinal))
                                .OrderBy(x => x.Key, StringComparer.Ordinal))
                            {
                                CDPClient standbyClient;
                                var hasStandbyClient = _clients.TryGetValue(standby.Key, out standbyClient)
                                    && standbyClient != null;
                                var recoverable = _recoverableStandbySessions.ContainsKey(standby.Key);
                                if (recoverable) recoverableStandbyCount++;
                                long identityGeneration;
                                long statusGeneration;
                                _shopIdentityProbeGenerations.TryGetValue(standby.Key, out identityGeneration);
                                _statusBindingGenerations.TryGetValue(standby.Key, out statusGeneration);
                                standbys.Add(new
                                {
                                    SessionRef = RuntimeAcceptanceDiagnosticRef("session", standby.Key),
                                    Connected = _connectedSessions.ContainsKey(standby.Key),
                                    Recoverable = recoverable,
                                    Initialized = _initialized.ContainsKey(standby.Key),
                                    ClientPresent = hasStandbyClient,
                                    ClientInvalidated = hasStandbyClient && standbyClient.IsInvalidated,
                                    ClientIdentityRetired = hasStandbyClient && standbyClient.IsShopIdentityRetired,
                                    Closing = _closingDuplicateSessions.ContainsKey(standby.Key),
                                    IdentityProbeGeneration = identityGeneration,
                                    StatusBindingGeneration = statusGeneration,
                                    LastActivityAgeSeconds = GetRuntimeSessionActivityAgeSeconds(standby.Key, nowUtc)
                                });
                            }

                            shopRows.Add(new
                            {
                                ShopRef = RuntimeAcceptanceDiagnosticRef("shop", shopKey),
                                OwnerSessionRef = RuntimeAcceptanceDiagnosticRef("session", ownerSessionId),
                                OwnerConnected = _connectedSessions.ContainsKey(ownerSessionId),
                                OwnerInitialized = _initialized.ContainsKey(ownerSessionId),
                                OwnerClientPresent = hasOwnerClient,
                                OwnerClientInvalidated = hasOwnerClient && ownerClient.IsInvalidated,
                                OwnerClientIdentityRetired = hasOwnerClient && ownerClient.IsShopIdentityRetired,
                                OwnerIdentityProbeGeneration = ownerIdentityGeneration,
                                OwnerStatusBindingGeneration = ownerStatusGeneration,
                                OwnerLastActivityAgeSeconds = GetRuntimeSessionActivityAgeSeconds(ownerSessionId, nowUtc),
                                StandbyCount = standbys.Count,
                                RecoverableStandbyCount = recoverableStandbyCount,
                                Standbys = standbys
                            });
                        }

                        var unmappedConnected = _connectedSessions.Keys.Count(x => !mappedSessionIds.Contains(x));
                        snapshot = new
                        {
                            SchemaVersion = 1,
                            CollectedAtUtc = nowUtc.ToString("o"),
                            ProcessId = processId,
                            ProcessStartTimeUtcTicks = processStartTimeUtcTicks,
                            ActiveShopRef = RuntimeAcceptanceDiagnosticRef("shop", activeShopKey),
                            ActiveSessionRef = RuntimeAcceptanceDiagnosticRef("session", activeSessionId),
                            ActiveFocusProofFresh = activeFocusProofFresh,
                            ActiveFocusProofAgeSeconds = activeFocusProofAgeSeconds,
                            Counts = new
                            {
                                ConnectedSessions = _connectedSessions.Count,
                                AuthoritativeShopCount = _shopSessions.Count,
                                DuplicateSessions = _duplicateShopSessions.Count,
                                // The capability set also contains the authoritative owner; count actual standby sessions only from duplicate mappings.
                                RecoverableStandbySessions = _duplicateShopSessions.Count(x => _recoverableStandbySessions.ContainsKey(x.Key)),
                                StandbyCapabilitySessions = _recoverableStandbySessions.Count,
                                InitializedSessions = _initialized.Count,
                                ClientSessions = _clients.Count,
                                UnmappedConnectedSessions = unmappedConnected
                            },
                            Shops = shopRows
                        };
                    }
                }

                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "QianniuAiBot", "diagnostics");
                var path = Path.Combine(directory, "runtime-snapshot.json");
                string error;
                if (!CrossProcessAtomicStateFile.WriteAllTextAtomic(
                    path, JsonConvert.SerializeObject(snapshot, Formatting.None), 2, 40, out error))
                    Log.ErrorWithMaxCount("只读千牛验收运行态快照写入失败；不影响业务运行。", 5);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("只读千牛验收运行态快照采集失败；不影响业务运行。 errorType="
                    + ex.GetType().Name, 5);
            }
        }

        private void TouchSession(WebSocketSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.SessionID)) return;
            _liveSessions[session.SessionID] = session;
            _sessionLastActivityUtc.TryAdd(session.SessionID, DateTime.UtcNow);
        }

        private void TouchSessionBusinessActivity(WebSocketSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.SessionID)) return;
            _sessionLastActivityUtc[session.SessionID] = DateTime.UtcNow;
        }

        private void ScheduleDuplicateSessionClose(string sessionId, string shopKey, string reason)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            WebSocketSession session;
            if (!_liveSessions.TryGetValue(sessionId, out session) || session == null) return;
            lock (_sellerSessionSync)
            {
                string duplicateShopKey;
                string mappedShopKey;
                string currentOwner;
                // Revalidate the exact standby lease while serialized with promotion.
                // A stale retirement command must never close a session that has since
                // become authoritative or changed its verified ShopKey.
                if (!_connectedSessions.ContainsKey(sessionId)
                    || !_duplicateShopSessions.TryGetValue(sessionId, out duplicateShopKey)
                    || !string.Equals(duplicateShopKey, shopKey, StringComparison.Ordinal)
                    || !_sessionShopKeys.TryGetValue(sessionId, out mappedShopKey)
                    || !string.Equals(mappedShopKey, shopKey, StringComparison.Ordinal)
                    || (_shopSessions.TryGetValue(shopKey, out currentOwner)
                        && string.Equals(currentOwner, sessionId, StringComparison.Ordinal))
                    || !_standbyLoggedSessions.TryAdd(sessionId, shopKey))
                    return;
            }

            // Retire excess duplicate sockets for one ShopKey only. Different shops
            // with the same seller nickname must retain independent WebSocket owners.
            Task.Run(() =>
            {
                try
                {
                    var retryAfterMs = 300000 + (int)((uint)sessionId.GetHashCode() % 240000U);
                    var command = JsonConvert.SerializeObject(new
                    {
                        method = "retireDuplicate",
                        reason = reason ?? "duplicate",
                        retryAfterMs = retryAfterMs
                    });
                    lock (_sellerSessionSync)
                    {
                        string pendingShopKey;
                        string duplicateShopKey;
                        string mappedShopKey;
                        string currentOwner;
                        // Identity can change after scheduling but before this task runs.
                        // Revalidate the exact retirement lease immediately before sending;
                        // an old-shop command must not close the same page after A→B rebinding.
                        if (!_connectedSessions.ContainsKey(sessionId)
                            || !_standbyLoggedSessions.TryGetValue(sessionId, out pendingShopKey)
                            || !string.Equals(pendingShopKey, shopKey, StringComparison.Ordinal)
                            || !_duplicateShopSessions.TryGetValue(sessionId, out duplicateShopKey)
                            || !string.Equals(duplicateShopKey, shopKey, StringComparison.Ordinal)
                            || !_sessionShopKeys.TryGetValue(sessionId, out mappedShopKey)
                            || !string.Equals(mappedShopKey, shopKey, StringComparison.Ordinal)
                            || (_shopSessions.TryGetValue(shopKey, out currentOwner)
                                && string.Equals(currentOwner, sessionId, StringComparison.Ordinal)))
                        {
                            if (_standbyLoggedSessions.TryGetValue(sessionId, out pendingShopKey)
                                && string.Equals(pendingShopKey, shopKey, StringComparison.Ordinal))
                            {
                                string ignored;
                                _standbyLoggedSessions.TryRemove(sessionId, out ignored);
                            }
                            return;
                        }
                        session.Send(command);
                    }
                    Log.Info("非权威千牛WebSocket页面已降级为可恢复离线standby: shopRef="
                        + DiagnosticRef("shop", shopKey)
                        + ", sessionRef=" + DiagnosticRef("session", sessionId)
                        + ", reason=" + reason + ", retryAfterMs=" + retryAfterMs);
                }
                catch (Exception ex)
                {
                    lock (_sellerSessionSync)
                    {
                        string pendingShopKey;
                        if (_standbyLoggedSessions.TryGetValue(sessionId, out pendingShopKey)
                            && string.Equals(pendingShopKey, shopKey, StringComparison.Ordinal))
                        {
                            string ignored;
                            _standbyLoggedSessions.TryRemove(sessionId, out ignored);
                        }
                    }
                    Log.Exception(ex, "RetireDuplicateSession:" + DiagnosticRef("session", sessionId));
                }
            });
        }

        private void EnforceDuplicateShopSessionCapLocked(string shopKey)
        {
            var active = _duplicateShopSessions
                .Where(x => string.Equals(x.Value, shopKey, StringComparison.Ordinal)
                    && !_closingDuplicateSessions.ContainsKey(x.Key))
                .Select(x => x.Key)
                .OrderBy(x =>
                {
                    DateTime last;
                    return _sessionLastActivityUtc.TryGetValue(x, out last) ? last : DateTime.MinValue;
                })
                .ToList();
            var excess = active.Count - MaxDuplicateShopSessions;
            if (excess <= 0) return;

            foreach (var victim in active
                .Where(id => _recoverableStandbySessions.ContainsKey(id))
                .Take(excess))
            {
                ScheduleDuplicateSessionClose(victim, shopKey, "duplicate_cap_standby");
            }
        }

        private void StartDuplicateSessionSweeper()
        {
            if (Interlocked.CompareExchange(ref _duplicateSessionSweeperStarted, 1, 0) != 0) return;
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(DuplicateSessionSweepInterval).ConfigureAwait(false);
                    try
                    {
                        RevalidateConnectedShopSessions();
                        var cutoff = DateTime.UtcNow - DuplicateSessionIdleTimeout;
                        List<KeyValuePair<string, string>> stale;
                        lock (_sellerSessionSync)
                        {
                            stale = _duplicateShopSessions
                                .Where(x => !_closingDuplicateSessions.ContainsKey(x.Key))
                                .Where(x =>
                                {
                                    DateTime last;
                                    return !_sessionLastActivityUtc.TryGetValue(x.Key, out last) || last < cutoff;
                                })
                                .ToList();
                        }
                        foreach (var pair in stale)
                            ScheduleDuplicateSessionClose(pair.Key, pair.Value, "duplicate_idle_standby");
                        WriteReadOnlyAcceptanceSnapshot();
                    }
                    catch (Exception ex)
                    {
                        Log.Info("千牛重复WebSocket页面通道清理异常: " + ex.Message);
                    }
                }
            });
        }

        private bool TryClaimShopSession(string shopKey, string sessionId, long? expectedIdentityProbeGeneration = null, Action onClaimed = null)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            sessionId = (sessionId ?? string.Empty).Trim();
            if (shopKey.Length == 0 || sessionId.Length == 0) return false;

            string previousShopKey = string.Empty;
            var identityChanged = false;
            var releasedPreviousAuthority = false;
            lock (_sellerSessionSync)
            {
                if (expectedIdentityProbeGeneration.HasValue)
                {
                    long currentIdentityProbeGeneration;
                    _shopIdentityProbeGenerations.TryGetValue(sessionId, out currentIdentityProbeGeneration);
                    if (currentIdentityProbeGeneration != expectedIdentityProbeGeneration.Value) return false;
                }
                // Async login/status probes may complete after SessionClosed. Never let
                // their late result resurrect a disconnected socket as the ShopKey owner.
                if (!_connectedSessions.ContainsKey(sessionId)) return false;

                if (_sessionShopKeys.TryGetValue(sessionId, out previousShopKey)
                    && !string.IsNullOrWhiteSpace(previousShopKey)
                    && !string.Equals(previousShopKey, shopKey, StringComparison.Ordinal))
                {
                    // Mark the source before removing its old ShopKey mapping. The
                    // duplicate-page bridge must stop interpreting any in-flight payload
                    // from this socket under the previous shop during the transition.
                    DuplicateCdpInboundRecoveryBridge.MarkSessionIdentityTransition(sessionId);
                    // Cancel any retirement lease scoped to the previous shop. Its queued
                    // task revalidates the stored ShopKey before sending and cannot affect
                    // a later identity bound to this same physical page.
                    string ignoredRetirementShop;
                    _standbyLoggedSessions.TryRemove(sessionId, out ignoredRetirementShop);
                    // One physical WebSocket must never silently migrate its old QN/CDP
                    // object to a different shop. Detach the previous identity first and
                    // let the next status event establish a fresh ShopKey binding.
                    string ignoredShop;
                    _sessionShopKeys.TryRemove(sessionId, out ignoredShop);
                    string ignoredDuplicate;
                    _duplicateShopSessions.TryRemove(sessionId, out ignoredDuplicate);
                    string previousOwner;
                    if (_shopSessions.TryGetValue(previousShopKey, out previousOwner)
                        && string.Equals(previousOwner, sessionId, StringComparison.Ordinal))
                    {
                        _shopSessions.TryRemove(previousShopKey, out ignoredShop);
                        releasedPreviousAuthority = true;
                    }
                    identityChanged = true;
                    NextShopIdentityProbeGenerationLocked(sessionId);
                    BotConnectionDiagnostics.RecordAuthoritativeCdpSessionCount(_shopSessions.Count);
                }

                if (!identityChanged)
                {
                    string owner;
                    if (_shopSessions.TryGetValue(shopKey, out owner))
                    {
                        if (string.Equals(owner, sessionId, StringComparison.Ordinal))
                        {
                            _sessionShopKeys[sessionId] = shopKey;
                            string ignoredDuplicate;
                            _duplicateShopSessions.TryRemove(sessionId, out ignoredDuplicate);
                            DuplicateCdpInboundRecoveryBridge.ClearSessionIdentityTransition(sessionId);
                            BotConnectionDiagnostics.RecordAuthoritativeCdpSessionCount(_shopSessions.Count);
                            if (onClaimed != null) onClaimed();
                            return true;
                        }

                        if (!string.IsNullOrWhiteSpace(owner) && _connectedSessions.ContainsKey(owner))
                        {
                            _duplicateShopSessions[sessionId] = shopKey;
                            _sessionShopKeys[sessionId] = shopKey;
                            DuplicateCdpInboundRecoveryBridge.ClearSessionIdentityTransition(sessionId);
                            EnforceDuplicateShopSessionCapLocked(shopKey);
                            return false;
                        }

                        string staleOwner;
                        _shopSessions.TryRemove(shopKey, out staleOwner);
                        if (!string.IsNullOrWhiteSpace(staleOwner))
                        {
                            string staleShopKey;
                            _sessionShopKeys.TryRemove(staleOwner, out staleShopKey);
                        }
                    }

                    _shopSessions[shopKey] = sessionId;
                    _sessionShopKeys[sessionId] = shopKey;
                    string ignored;
                    _duplicateShopSessions.TryRemove(sessionId, out ignored);
                    DuplicateCdpInboundRecoveryBridge.ClearSessionIdentityTransition(sessionId);
                    BotConnectionDiagnostics.RecordAuthoritativeCdpSessionCount(_shopSessions.Count);
                    if (onClaimed != null) onClaimed();
                    Log.Info("已选定ShopKey权威千牛CDP会话: shopRef=" + DiagnosticRef("shop", shopKey)
                        + ", sessionRef=" + DiagnosticRef("session", sessionId));
                    return true;
                }
            }

            if (identityChanged)
            {
                try
                {
                    var previousQn = QN.FindExistingByShopKey(previousShopKey);
                    if (previousQn != null && previousQn.CDP != null
                        && string.Equals(previousQn.CDP.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        previousQn.CDP.IsShopIdentityRetired = true;
                        previousQn.CDP = null;
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("千牛WebSocket身份变化时解绑旧QN实例失败: " + ex.Message);
                }
                CDPClient.ReleasePreferredRuntimeSession(previousShopKey, sessionId);
                string ignoredStatusBinding;
                _lastStatusBindings.TryRemove(sessionId, out ignoredStatusBinding);
                DateTime ignoredStatusRefresh;
                _lastStatusBindingRefreshUtc.TryRemove(sessionId, out ignoredStatusRefresh);
                if (releasedPreviousAuthority)
                    Task.Run(() => TryPromoteAvailableStandbyAsync(previousShopKey, "shop_identity_changed", sessionId));
                Log.Info("检测到同一WebSocket切换了登录店铺身份，已解绑旧ShopKey并等待下一次状态事件重新绑定: oldShopRef="
                    + DiagnosticRef("shop", previousShopKey)
                    + ", newShopRef=" + DiagnosticRef("shop", shopKey)
                    + ", sessionRef=" + DiagnosticRef("session", sessionId));
                return false;
            }
            return false;
        }

        private async Task<bool> TryPromoteDuplicateShopSessionAsync(
            string shopKey, WebSocketSession session, string reason, bool requireVacant = false,
            long? expectedIdentityProbeGeneration = null)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            if (session == null || string.IsNullOrWhiteSpace(session.SessionID) || shopKey.Length == 0) return false;
            var sessionId = session.SessionID.Trim();
            // A page already committed to retirement is not a promotion candidate.
            // Avoid spending a CDP identity probe on a lease that cannot be granted.
            if (_standbyLoggedSessions.ContainsKey(sessionId)) return false;
            if (expectedIdentityProbeGeneration.HasValue
                && !IsCurrentShopIdentityProbe(sessionId, expectedIdentityProbeGeneration.Value))
                return false;
            var client = GetOrCreateClient(session);
            if (client == null || client.IsInvalidated) return false;

            // A standby can remain open while the user changes the logged-in account.
            // Re-read identity on this exact physical WebSocket before granting it authority;
            // never clear the retired-identity gate based only on an old standby mapping.
            LocalUserResponse user;
            try
            {
                user = await client.GetCurrentUser().ConfigureAwait(false);
                if (expectedIdentityProbeGeneration.HasValue
                    && !IsCurrentShopIdentityProbe(sessionId, expectedIdentityProbeGeneration.Value))
                    return false;
            }
            catch (Exception ex)
            {
                Log.Info("standby晋升前无法验证当前千牛登录身份，保持非权威: shopRef="
                    + DiagnosticRef("shop", shopKey)
                    + ", sessionRef=" + DiagnosticRef("session", sessionId)
                    + ", reason=" + ex.Message);
                return false;
            }
            if (user == null || user.Result == null || string.IsNullOrWhiteSpace(user.Result.Nick)) return false;

            ShopContext verifiedShop;
            try
            {
                verifiedShop = ShopContextLocator.ResolveRuntimeBySellerIdentity(user.Result);
            }
            catch (Exception ex)
            {
                client.IsShopIdentityRetired = true;
                Log.Info("standby晋升身份解析不唯一，保持fail-closed: expectedShopRef="
                    + DiagnosticRef("shop", shopKey)
                    + ", sessionRef=" + DiagnosticRef("session", sessionId)
                    + ", reason=" + ex.Message);
                return false;
            }

            if (verifiedShop == null || string.IsNullOrWhiteSpace(verifiedShop.ShopKey))
            {
                client.IsShopIdentityRetired = true;
                Log.Info("standby当前登录身份缺少可验证ShopKey，保持非权威: expectedShopRef="
                    + DiagnosticRef("shop", shopKey)
                    + ", sessionRef=" + DiagnosticRef("session", sessionId));
                return false;
            }

            if (expectedIdentityProbeGeneration.HasValue
                && !IsCurrentShopIdentityProbe(sessionId, expectedIdentityProbeGeneration.Value))
                return false;

            if (!string.Equals(verifiedShop.ShopKey, shopKey, StringComparison.Ordinal))
            {
                client.IsShopIdentityRetired = true;
                // This also detaches the stale standby mapping. The next status event
                // will bind the physical page to its newly verified ShopKey.
                TryClaimShopSession(verifiedShop.ShopKey, sessionId, expectedIdentityProbeGeneration);
                Log.Info("standby登录身份已变化，拒绝按旧ShopKey晋升并等待重新绑定: expectedShopRef="
                    + DiagnosticRef("shop", shopKey)
                    + ", actualShopRef=" + DiagnosticRef("shop", verifiedShop.ShopKey)
                    + ", sessionRef=" + DiagnosticRef("session", sessionId));
                return false;
            }

            QN qn;
            try { qn = QN.GetByShopIdentity(user.Result); }
            catch (Exception ex)
            {
                Log.Info("standby晋升无法绑定已验证的QN实例: shopRef="
                    + DiagnosticRef("shop", shopKey) + ", reason=" + ex.Message);
                return false;
            }
            if (qn == null) return false;

            string previousOwner = string.Empty;
            lock (_sellerSessionSync)
            {
                if (!_connectedSessions.ContainsKey(sessionId) || client.IsInvalidated
                    || _standbyLoggedSessions.ContainsKey(sessionId)
                    || (expectedIdentityProbeGeneration.HasValue
                        && !IsCurrentShopIdentityProbeLocked(sessionId, expectedIdentityProbeGeneration.Value)))
                    return false;
                string duplicateShopKey;
                string mappedShopKey;
                var knownDuplicate = _duplicateShopSessions.TryGetValue(sessionId, out duplicateShopKey)
                    && string.Equals(duplicateShopKey, shopKey, StringComparison.Ordinal);
                var knownShopSession = _sessionShopKeys.TryGetValue(sessionId, out mappedShopKey)
                    && string.Equals(mappedShopKey, shopKey, StringComparison.Ordinal);
                if (!knownDuplicate && !knownShopSession) return false;

                _shopSessions.TryGetValue(shopKey, out previousOwner);
                if (requireVacant
                    && !string.Equals(previousOwner, sessionId, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(previousOwner)
                    && _connectedSessions.ContainsKey(previousOwner))
                    return false;
                var alreadyOwner = string.Equals(previousOwner, sessionId, StringComparison.Ordinal);
                if (!alreadyOwner)
                {
                    if (!string.IsNullOrWhiteSpace(previousOwner) && _connectedSessions.ContainsKey(previousOwner))
                    {
                        _duplicateShopSessions[previousOwner] = shopKey;
                        _sessionShopKeys[previousOwner] = shopKey;
                    }
                    else if (!string.IsNullOrWhiteSpace(previousOwner))
                    {
                        string ignoredStaleShop;
                        _sessionShopKeys.TryRemove(previousOwner, out ignoredStaleShop);
                        string ignoredStaleDuplicate;
                        _duplicateShopSessions.TryRemove(previousOwner, out ignoredStaleDuplicate);
                    }

                    _shopSessions[shopKey] = sessionId;
                }
                _sessionShopKeys[sessionId] = shopKey;
                string ignored;
                _duplicateShopSessions.TryRemove(sessionId, out ignored);
                _standbyLoggedSessions.TryRemove(sessionId, out _);
                _sessionLastActivityUtc[sessionId] = DateTime.UtcNow;
                EnforceDuplicateShopSessionCapLocked(shopKey);
                client.ShopKey = shopKey;
                client.IsShopIdentityRetired = false;
                client.Nick = qn.Seller == null ? user.Result.Nick : qn.Seller.Nick;
                qn.CDP = client;
                string ignoredStatusBinding;
                _lastStatusBindings.TryRemove(sessionId, out ignoredStatusBinding);
                DateTime ignoredStatusRefresh;
                _lastStatusBindingRefreshUtc.TryRemove(sessionId, out ignoredStatusRefresh);
                CDPClient.PreferRuntimeSession(
                    shopKey, client.Nick, sessionId, string.Empty, "standby_promoted");
                BotConnectionDiagnostics.RecordAuthoritativeCdpSessionCount(_shopSessions.Count);
            }

            Log.Info("已验证当前CDP登录身份并提升ShopKey standby为权威会话: shopRef=" + DiagnosticRef("shop", shopKey)
                + ", previousSessionRef=" + DiagnosticRef("session", previousOwner)
                + ", activeSessionRef=" + DiagnosticRef("session", sessionId)
                + ", reason=" + (reason ?? "foreground"));
            return true;
        }

        internal bool IsWebSocketSessionConnected(string sessionId)
        {
            sessionId = (sessionId ?? string.Empty).Trim();
            return sessionId.Length > 0 && _connectedSessions.ContainsKey(sessionId);
        }

        internal string GetShopKeyForSession(string sessionId)
        {
            sessionId = (sessionId ?? string.Empty).Trim();
            string shopKey;
            return sessionId.Length > 0 && _sessionShopKeys.TryGetValue(sessionId, out shopKey)
                ? shopKey
                : string.Empty;
        }

        internal bool IsAuthoritativeShopSession(string shopKey, string sessionId)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            sessionId = (sessionId ?? string.Empty).Trim();
            if (shopKey.Length == 0 || sessionId.Length == 0) return false;
            string owner;
            // SessionClosed removes the physical connection before releasing the ShopKey
            // owner mapping. Requiring the live socket here closes that race window so a
            // native send cannot pass authority validation on a disconnected WebSocket.
            return _connectedSessions.ContainsKey(sessionId)
                && _shopSessions.TryGetValue(shopKey, out owner)
                && string.Equals(owner, sessionId, StringComparison.Ordinal);
        }

        private void ReleaseShopSession(string sessionId, bool promoteStandby = true)
        {
            sessionId = (sessionId ?? string.Empty).Trim();
            if (sessionId.Length == 0) return;
            var releasedAuthority = false;
            var releasedShopKey = string.Empty;
            lock (_sellerSessionSync)
            {
                string ignoredDuplicate;
                _duplicateShopSessions.TryRemove(sessionId, out ignoredDuplicate);
                string ignoredStatus;
                DateTime ignoredStatusRefresh;
                _lastStatusBindings.TryRemove(sessionId, out ignoredStatus);
                _lastStatusBindingRefreshUtc.TryRemove(sessionId, out ignoredStatusRefresh);
                string shopKey;
                if (!_sessionShopKeys.TryRemove(sessionId, out shopKey)
                    || string.IsNullOrWhiteSpace(shopKey))
                {
                    BotConnectionDiagnostics.RecordAuthoritativeCdpSessionCount(_shopSessions.Count);
                    return;
                }

                string owner;
                if (_shopSessions.TryGetValue(shopKey, out owner)
                    && string.Equals(owner, sessionId, StringComparison.Ordinal))
                {
                    string removed;
                    _shopSessions.TryRemove(shopKey, out removed);
                    releasedAuthority = true;
                    releasedShopKey = shopKey;
                    Log.Info("ShopKey权威千牛CDP会话已释放，正在检查在线standby页面: shopRef="
                        + DiagnosticRef("shop", shopKey) + ", sessionRef=" + DiagnosticRef("session", sessionId));
                }
                BotConnectionDiagnostics.RecordAuthoritativeCdpSessionCount(_shopSessions.Count);
            }

            // Do not promote during a full listener teardown: all stale sessions are about
            // to be discarded. On an ordinary socket close, elect one already-authenticated
            // standby immediately instead of waiting for a future onChatDlgActive event.
            if (releasedAuthority && promoteStandby)
                Task.Run(() => TryPromoteAvailableStandbyAsync(
                    releasedShopKey, "authority_disconnected", sessionId));
        }

        private async Task TryPromoteAvailableStandbyAsync(string shopKey, string reason, string excludedSessionId = null)
        {
            shopKey = (shopKey ?? string.Empty).Trim();
            excludedSessionId = (excludedSessionId ?? string.Empty).Trim();
            if (shopKey.Length == 0) return;

            List<string> candidates;
            lock (_sellerSessionSync)
            {
                candidates = _duplicateShopSessions
                    .Where(x => string.Equals(x.Value, shopKey, StringComparison.Ordinal)
                        && !string.Equals(x.Key, excludedSessionId, StringComparison.Ordinal)
                        && !_closingDuplicateSessions.ContainsKey(x.Key)
                        && !_standbyLoggedSessions.ContainsKey(x.Key)
                        && _connectedSessions.ContainsKey(x.Key))
                    .OrderByDescending(x =>
                    {
                        DateTime last;
                        return _sessionLastActivityUtc.TryGetValue(x.Key, out last) ? last : DateTime.MinValue;
                    })
                    .Select(x => x.Key)
                    .ToList();
            }

            foreach (var candidateId in candidates)
            {
                WebSocketSession candidate;
                if (!_liveSessions.TryGetValue(candidateId, out candidate) || candidate == null) continue;
                long identityProbeGeneration;
                lock (_sellerSessionSync)
                {
                    if (!_connectedSessions.ContainsKey(candidateId)) continue;
                    _shopIdentityProbeGenerations.TryGetValue(candidateId, out identityProbeGeneration);
                }
                if (await TryPromoteDuplicateShopSessionAsync(
                    shopKey, candidate, reason, requireVacant: true,
                    expectedIdentityProbeGeneration: identityProbeGeneration).ConfigureAwait(false)) return;
            }

            Log.Info("ShopKey权威千牛CDP会话已释放，但当前没有可提升的在线standby: shopRef="
                + DiagnosticRef("shop", shopKey) + ", reason=" + (reason ?? string.Empty));
        }

        private bool TryReserveStatusBindingRefresh(string sessionId, DateTime now, bool force)
        {
            while (true)
            {
                DateTime lastRefreshUtc;
                if (_lastStatusBindingRefreshUtc.TryGetValue(sessionId, out lastRefreshUtc))
                {
                    if (!force && now - lastRefreshUtc < StatusBindingRefreshInterval) return false;
                    if (_lastStatusBindingRefreshUtc.TryUpdate(sessionId, now, lastRefreshUtc)) return true;
                    continue;
                }
                if (_lastStatusBindingRefreshUtc.TryAdd(sessionId, now)) return true;
            }
        }

        private long NextStatusBindingGenerationLocked(string sessionId)
        {
            // A process-wide sequence lets closed-session entries be pruned without
            // allowing a recycled SessionID to reuse an old generation number.
            var next = Interlocked.Increment(ref _statusBindingGenerationSequence);
            if (next <= 0)
            {
                Interlocked.Exchange(ref _statusBindingGenerationSequence, 1L);
                next = 1L;
            }
            _statusBindingGenerations[sessionId] = next;
            return next;
        }

        private bool IsCurrentStatusBindingGenerationLocked(string sessionId, long generation)
        {
            long current;
            return generation > 0
                && _statusBindingGenerations.TryGetValue(sessionId, out current)
                && current == generation;
        }

        private long NextShopIdentityProbeGenerationLocked(string sessionId)
        {
            long current;
            _shopIdentityProbeGenerations.TryGetValue(sessionId, out current);
            var next = current == long.MaxValue ? 1L : current + 1L;
            _shopIdentityProbeGenerations[sessionId] = next;
            return next;
        }

        private long BeginShopIdentityProbe(string sessionId)
        {
            sessionId = (sessionId ?? string.Empty).Trim();
            if (sessionId.Length == 0) return 0;
            lock (_sellerSessionSync)
            {
                if (!_connectedSessions.ContainsKey(sessionId)) return 0;
                return NextShopIdentityProbeGenerationLocked(sessionId);
            }
        }

        private bool IsCurrentShopIdentityProbeLocked(string sessionId, long generation)
        {
            long current;
            _shopIdentityProbeGenerations.TryGetValue(sessionId, out current);
            return generation > 0
                && _connectedSessions.ContainsKey(sessionId)
                && current == generation;
        }

        private bool IsCurrentShopIdentityProbe(string sessionId, long generation)
        {
            lock (_sellerSessionSync)
                return IsCurrentShopIdentityProbeLocked(sessionId, generation);
        }

        private long BeginStatusBindingAttempt(string sessionId)
        {
            sessionId = (sessionId ?? string.Empty).Trim();
            if (sessionId.Length == 0) return 0;
            lock (_sellerSessionSync)
            {
                if (!_connectedSessions.ContainsKey(sessionId)) return 0;
                return NextStatusBindingGenerationLocked(sessionId);
            }
        }

        private long ShouldRefreshStatusBinding(string sessionId, string loginNick, string conversationNick)
        {
            sessionId = (sessionId ?? string.Empty).Trim();
            if (sessionId.Length == 0) return 0;
            var key = (loginNick ?? string.Empty).Trim() + "\n" + (conversationNick ?? string.Empty).Trim();
            lock (_sellerSessionSync)
            {
                if (!_connectedSessions.ContainsKey(sessionId)) return 0;
                string previous;
                var keyChanged = !_lastStatusBindings.TryGetValue(sessionId, out previous)
                    || !string.Equals(previous, key, StringComparison.Ordinal);
                if (!TryReserveStatusBindingRefresh(sessionId, DateTime.UtcNow, keyChanged)) return 0;
                _lastStatusBindings[sessionId] = key;
                return NextStatusBindingGenerationLocked(sessionId);
            }
        }

        private void ForgetStatusBindingAttempt(WebSocketSession session, string loginNick, string conversationNick)
        {
            if (session == null) return;
            var key = (loginNick ?? string.Empty).Trim() + "\n" + (conversationNick ?? string.Empty).Trim();
            // Remove only the failed attempt's exact value. A newer status event may
            // have already installed a different key while this async bind was running.
            ((ICollection<KeyValuePair<string, string>>)_lastStatusBindings).Remove(
                new KeyValuePair<string, string>(session.SessionID, key));
        }

        private void RevalidateConnectedShopSessions()
        {
            var now = DateTime.UtcNow;
            foreach (var pair in _liveSessions)
            {
                var session = pair.Value;
                if (session == null || !_connectedSessions.ContainsKey(pair.Key)
                    || _initializing.ContainsKey(pair.Key)
                    || _closingDuplicateSessions.ContainsKey(pair.Key))
                    continue;
                if (!TryReserveStatusBindingRefresh(pair.Key, now, false)) continue;

                long bindingGeneration;
                lock (_sellerSessionSync)
                {
                    if (!_connectedSessions.ContainsKey(pair.Key)) continue;
                    _shopIdentityProbeGenerations.TryGetValue(pair.Key, out bindingGeneration);
                }

                // Exact CDP TargetId/ShopKey verification; a newer qnbotStatus event
                // increments this generation and invalidates any older asynchronous read.
                Task.Run(() => TryRevalidateShopIdentity(session, bindingGeneration));
            }
        }

        private async Task TryRevalidateShopIdentity(WebSocketSession session, long bindingGeneration)
        {
            if (session == null
                || !IsCurrentShopIdentityProbe(session.SessionID, bindingGeneration))
                return;
            try
            {
                var cdp = GetOrCreateClient(session);
                var user = await cdp.GetCurrentUser().ConfigureAwait(false);
                if (!IsCurrentShopIdentityProbe(session.SessionID, bindingGeneration)
                    || user == null || user.Result == null
                    || string.IsNullOrWhiteSpace(user.Result.Nick))
                    return;

                var shop = ShopContextLocator.ResolveRuntimeBySellerIdentity(user.Result);
                var shopKey = shop.ShopKey;
                var qn = QN.GetByShopIdentity(user.Result);
                if (qn == null || qn.Seller == null) return;
                var sellerNick = qn.Seller.Nick ?? user.Result.Nick;

                // Claiming the ShopKey and installing its CDP runtime binding must be
                // one atomic operation. A newer qnbotStatus event cannot land between them.
                if (TryClaimShopSession(shopKey, session.SessionID, bindingGeneration, () =>
                {
                    cdp.ShopKey = shopKey;
                    cdp.IsShopIdentityRetired = false;
                    cdp.Nick = sellerNick;
                    qn.CDP = cdp;
                    CDPClient.PreferRuntimeSession(
                        shopKey, sellerNick, session.SessionID, string.Empty,
                        "periodic_shop_identity_revalidation");
                    _initialized[session.SessionID] = true;
                }))
                    return;

                // A verified duplicate/standby may retain only its exact ShopKey mapping.
                // A late result must not restore even that mapping after a newer status event.
                lock (_sellerSessionSync)
                {
                    string mappedShopKey;
                    if (!IsCurrentShopIdentityProbeLocked(session.SessionID, bindingGeneration)
                        || !_sessionShopKeys.TryGetValue(session.SessionID, out mappedShopKey)
                        || !string.Equals(mappedShopKey, shopKey, StringComparison.Ordinal))
                        return;

                    cdp.ShopKey = shopKey;
                    cdp.IsShopIdentityRetired = false;
                    cdp.Nick = sellerNick;
                    _initialized[session.SessionID] = true;
                }
            }
            catch (Exception ex)
            {
                Log.Info("千牛WebSocket周期性ShopKey身份复核失败: sessionRef="
                    + DiagnosticRef("session", session.SessionID) + ", error=" + ex.Message);
            }
        }

        private async Task TryBindStatusConversation(
            WebSocketSession session, string loginNick, string conversationNick,
            long bindingGeneration, long identityProbeGeneration)
        {
            if (session == null || bindingGeneration <= 0 || identityProbeGeneration <= 0
                || !IsCurrentShopIdentityProbe(session.SessionID, identityProbeGeneration))
                return;
            loginNick = (loginNick ?? string.Empty).Trim();
            conversationNick = (conversationNick ?? string.Empty).Trim();

            try
            {
                // Never select a QN by loginNick first: this CDP session may belong
                // to another registered shop with the same display nickname.
                var cdp = GetOrCreateClient(session);
                var user = await cdp.GetCurrentUser().ConfigureAwait(false);
                if (!IsCurrentShopIdentityProbe(session.SessionID, identityProbeGeneration)
                    || user == null || user.Result == null || string.IsNullOrWhiteSpace(user.Result.Nick))
                {
                    BotConnectionDiagnostics.RecordBuyerSeller(loginNick, conversationNick);
                    ForgetStatusBindingAttempt(session, loginNick, conversationNick);
                    return;
                }

                var shop = ShopContextLocator.ResolveRuntimeBySellerIdentity(user.Result);
                var shopKey = shop.ShopKey;
                var qn = QN.GetByShopIdentity(user.Result);
                var claimed = false;
                var duplicateShopKey = string.Empty;
                var sellerNick = qn.Seller == null ? user.Result.Nick : qn.Seller.Nick;
                lock (_sellerSessionSync)
                {
                    // GetCurrentUser is asynchronous; ignore a late result if a newer
                    // status event has already superseded this physical socket's request.
                    if (!IsCurrentStatusBindingGenerationLocked(session.SessionID, bindingGeneration)
                        || !_connectedSessions.ContainsKey(session.SessionID)
                        || cdp.IsInvalidated)
                        return;
                    if (!IsCurrentShopIdentityProbeLocked(session.SessionID, identityProbeGeneration))
                    {
                        ForgetStatusBindingAttempt(session, loginNick, conversationNick);
                        return;
                    }

                    claimed = TryClaimShopSession(shopKey, session.SessionID, identityProbeGeneration);
                    if (claimed
                        && _connectedSessions.ContainsKey(session.SessionID)
                        && !cdp.IsInvalidated
                        && IsCurrentStatusBindingGenerationLocked(session.SessionID, bindingGeneration)
                        && IsCurrentShopIdentityProbeLocked(session.SessionID, identityProbeGeneration)
                        && IsAuthoritativeShopSession(shopKey, session.SessionID))
                    {
                        cdp.ShopKey = shopKey;
                        cdp.IsShopIdentityRetired = false;
                        cdp.Nick = sellerNick;
                        qn.CDP = cdp;
                        qn.SetActiveConversationByNick(sellerNick, conversationNick, "qnbotStatus");
                        _initialized[session.SessionID] = true;
                    }
                    else if (!claimed && IsCurrentShopIdentityProbeLocked(session.SessionID, identityProbeGeneration))
                    {
                        _initialized[session.SessionID] = true;
                        _duplicateShopSessions.TryGetValue(session.SessionID, out duplicateShopKey);
                    }
                    else if (qn.CDP == cdp)
                    {
                        // The socket closed/lost authority during the claim; don't leave
                        // a now-invalid CDP client installed on the QN runtime.
                        qn.CDP = null;
                    }
                }

                if (!claimed)
                {
                    if (!string.IsNullOrWhiteSpace(duplicateShopKey))
                        Log.Info("已忽略同一ShopKey的重复千牛CDP状态会话: shopRef="
                            + DiagnosticRef("shop", duplicateShopKey)
                            + ", sessionRef=" + DiagnosticRef("session", session.SessionID));
                    else
                        Log.Info("千牛WebSocket登录ShopKey发生变化，旧绑定已释放，等待下一次状态事件重新绑定: shopRef="
                            + DiagnosticRef("shop", shopKey)
                            + ", sessionRef=" + DiagnosticRef("session", session.SessionID));
                    return;
                }

                if (loginNick.Length > 0 && !string.Equals(loginNick, sellerNick, StringComparison.Ordinal))
                    Log.Info("状态事件loginNick与CDP登录身份昵称不同，已以TargetId/ShopKey为准: shopRef="
                        + DiagnosticRef("shop", shopKey)
                        + ", sellerRef=" + DiagnosticRef("seller", sellerNick));
                BotConnectionDiagnostics.RecordCdpStatus(true, "已获取", sellerNick, conversationNick);
                var connectionSnapshot = BotConnectionDiagnostics.GetSnapshot();
                if (!string.Equals(connectionSnapshot.AccessibilityStatus, "可用", StringComparison.Ordinal)
                    || !connectionSnapshot.ButtonStatus.StartsWith("已识别发送按钮", StringComparison.Ordinal))
                {
                    _ = RefreshRpaAfterAuthoritativeSessionAsync(qn, shopKey, session.SessionID);
                }
            }
            catch (Exception ex)
            {
                // Remove only this attempt's status key; never erase a newer event's key.
                ForgetStatusBindingAttempt(session, loginNick, conversationNick);
                Log.Exception(ex);
            }
        }

        private async Task TryInitSession(WebSocketSession session, string reason, long identityProbeGeneration)
        {
            if (session == null || identityProbeGeneration <= 0
                || !IsCurrentShopIdentityProbe(session.SessionID, identityProbeGeneration))
                return;
            if (_initialized.ContainsKey(session.SessionID)) return;
            if (!_initializing.TryAdd(session.SessionID, true)) return;

            try
            {
                var cdp = GetOrCreateClient(session);
                Log.Info("开始初始化千牛CDP, reason=" + reason + ", sessionRef=" + DiagnosticRef("session", session.SessionID));
                var user = await cdp.GetCurrentUser().ConfigureAwait(false);
                if (!IsCurrentShopIdentityProbe(session.SessionID, identityProbeGeneration)) return;
                var ver = await cdp.GetVersion().ConfigureAwait(false);
                if (!IsCurrentShopIdentityProbe(session.SessionID, identityProbeGeneration)) return;
                if (user == null || user.Result == null || string.IsNullOrEmpty(user.Result.Nick))
                {
                    BotConnectionDiagnostics.RecordCdpStatus(false, "未获取登录用户", string.Empty, string.Empty);
                    Log.Error("千牛CDP初始化跳过：未获取到登录用户, sessionRef=" + DiagnosticRef("session", session.SessionID));
                    return;
                }

                var sellerNick = (user.Result.Nick ?? string.Empty).Trim();
                var shop = ShopContextLocator.ResolveRuntimeBySellerIdentity(user.Result);
                var shopKey = shop.ShopKey;
                QN qn = QN.GetByShopIdentity(user.Result);
                if (qn == null || qn.Seller == null) return;
                // Claiming the ShopKey and installing the CDP runtime binding must be
                // one atomic operation. A newer qnbotStatus event cannot land between them.
                var claimed = TryClaimShopSession(shopKey, session.SessionID, identityProbeGeneration, () =>
                {
                    qn.QnVersion = ver != null ? ver.version : string.Empty;
                    cdp.ShopKey = shopKey;
                    cdp.IsShopIdentityRetired = false;
                    cdp.Nick = sellerNick;
                    qn.CDP = cdp;
                    _initialized[session.SessionID] = true;
                });
                if (!claimed)
                {
                    lock (_sellerSessionSync)
                    {
                        if (!IsCurrentShopIdentityProbeLocked(session.SessionID, identityProbeGeneration)) return;
                        _initialized[session.SessionID] = true;
                        string duplicateShopKey;
                        if (_duplicateShopSessions.TryGetValue(session.SessionID, out duplicateShopKey))
                            Log.Info("重复千牛CDP会话已完成识别但不接管该ShopKey运行通道: shopRef="
                                + DiagnosticRef("shop", duplicateShopKey)
                                + ", sessionRef=" + DiagnosticRef("session", session.SessionID));
                        else
                            Log.Info("千牛WebSocket登录ShopKey发生变化，旧绑定已释放，等待下一次状态事件重新绑定: shopRef="
                                + DiagnosticRef("shop", shopKey)
                                + ", sessionRef=" + DiagnosticRef("session", session.SessionID));
                    }
                    return;
                }

                Log.Info("千牛登录身份已绑定独立ShopKey运行通道: shopRef=" + DiagnosticRef("shop", shopKey)
                    + ", sellerRef=" + DiagnosticRef("seller", sellerNick));

                var buyerNick = string.Empty;
                try
                {
                    var conv = await cdp.GetCurrentConversationID().ConfigureAwait(false);
                    if (conv != null && conv.Result != null && !string.IsNullOrWhiteSpace(conv.Result.Nick)
                        && IsCurrentShopIdentityProbe(session.SessionID, identityProbeGeneration)
                        && IsAuthoritativeShopSession(shopKey, session.SessionID))
                    {
                        buyerNick = conv.Result.Nick;
                        qn.SetActiveConversationByNick(qn.Seller.Nick, buyerNick, "initConversation");
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("初始化时获取当前买家失败: " + ex.Message);
                }

                BotConnectionDiagnostics.RecordCdpStatus(true, "已获取", qn.Seller.Nick, buyerNick);
                WndNotifyIcon.Inst.AddSellerMenuItem(qn.Seller.Nick);
                Log.Info("千牛CDP初始化成功, sellerRef=" + DiagnosticRef("seller", qn.Seller.Nick)
                    + ", buyerRef=" + DiagnosticRef("buyer", buyerNick)
                    + ", version=" + qn.QnVersion + ", sessionRef=" + DiagnosticRef("session", session.SessionID));
                _ = RefreshRpaAfterAuthoritativeSessionAsync(qn, shopKey, session.SessionID);
            }
            catch (Exception ex)
            {
                BotConnectionDiagnostics.RecordCdpStatus(false, ex.Message, string.Empty, string.Empty);
                Log.Exception(ex);
            }
            finally
            {
                bool tmp;
                _initializing.TryRemove(session.SessionID, out tmp);
            }
        }

                private static bool IsLoopbackListenerActive()
        {
            try
            {
                return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                    .Any(endpoint => endpoint.Port == 41010
                        && (System.Net.IPAddress.IsLoopback(endpoint.Address)
                            || endpoint.Address.Equals(System.Net.IPAddress.Any)
                            || endpoint.Address.Equals(System.Net.IPAddress.IPv6Any)));
            }
            catch { return false; }
        }

        private void ReleaseStaleSessions(string reason)
        {
            var sessionIds = new HashSet<string>(_connectedSessions.Keys, StringComparer.Ordinal);
            sessionIds.UnionWith(_liveSessions.Keys);
            sessionIds.UnionWith(_clients.Keys);

            foreach (var sessionId in sessionIds)
            {
                WebSocketSession ignoredSession;
                bool ignoredBool;
                DateTime ignoredTime;
                string ignoredString;
                _liveSessions.TryRemove(sessionId, out ignoredSession);
                lock (_sellerSessionSync)
                {
                    _connectedSessions.TryRemove(sessionId, out ignoredBool);
                    // Invalidate any in-flight status/identity probes before removing their
                    // generation entries. SessionClosed may never fire when the listener
                    // disappears, so this path must release the same per-session state.
                    NextStatusBindingGenerationLocked(sessionId);
                    long ignoredStatusGeneration;
                    _statusBindingGenerations.TryRemove(sessionId, out ignoredStatusGeneration);
                    NextShopIdentityProbeGenerationLocked(sessionId);
                    long ignoredIdentityProbeGeneration;
                    _shopIdentityProbeGenerations.TryRemove(sessionId, out ignoredIdentityProbeGeneration);
                }
                _sessionLastActivityUtc.TryRemove(sessionId, out ignoredTime);
                _closingDuplicateSessions.TryRemove(sessionId, out ignoredBool);
                _recoverableStandbySessions.TryRemove(sessionId, out ignoredBool);
                _standbyLoggedSessions.TryRemove(sessionId, out ignoredString);
                _lastStatusBindings.TryRemove(sessionId, out ignoredString);
                _lastStatusBindingRefreshUtc.TryRemove(sessionId, out ignoredTime);
                _initialized.TryRemove(sessionId, out ignoredBool);
                _initializing.TryRemove(sessionId, out ignoredBool);
                _duplicateShopSessions.TryRemove(sessionId, out ignoredString);

                CDPClient removed;
                _clients.TryRemove(sessionId, out removed);
                CDPClient.ReleaseClosedSession(sessionId, reason, removed);
                ReleaseShopSession(sessionId, false);
                BotConnectionDiagnostics.RecordWebSocketClose(sessionId);
                WebSocketPageLifecycleGuard.ForgetClosedSession(sessionId);
                DuplicateCdpInboundRecoveryBridge.ForgetClosedSession(sessionId);
            }

            lock (_sellerSessionSync)
            {
                _shopSessions.Clear();
                _sessionShopKeys.Clear();
                _duplicateShopSessions.Clear();
                BotConnectionDiagnostics.RecordAuthoritativeCdpSessionCount(0);
            }
        }

        public void Start()
        {
            lock (_webSocketStartSync)
            {
                if (_webSocketServer != null && IsLoopbackListenerActive())
                {
                    BotConnectionDiagnostics.RecordWebSocketServerStarted();
                    return;
                }

                if (_webSocketServer != null)
                {
                    Log.Error("Bot WebSocket服务对象仍有引用但41010已不再监听；释放失效会话并重建服务实例。");
                    ReleaseStaleSessions("Bot WebSocket listener disappeared");
                    _webSocketServer = null;
                }
                else if (IsLoopbackListenerActive())
                {
                    const string listenerConflict = "127.0.0.1:41010已有监听，但当前Bot没有对应的服务实例；拒绝把外部监听误报为Bot服务正常。";
                    BotConnectionDiagnostics.RecordWebSocketServerError(listenerConflict);
                    Log.Error(listenerConflict);
                    return;
                }

                try
                {
                    var webSocket = new WebSocketServer();
                    webSocket.NewSessionConnected += (session) =>
                    {
                        try
                        {
                            _connectedSessions[session.SessionID] = true;
                            TouchSession(session);
                            BotConnectionDiagnostics.RecordWebSocketConnect(session.SessionID);
                            Log.Info("千牛注入脚本已连接 Bot WebSocket: sessionRef=" + DiagnosticRef("session", session.SessionID));
                            // Do not allocate a full CDPClient for every injected recent.html/iframe.
                            // Raw duplicate sockets remain connected so DuplicateCdpInboundRecoveryBridge
                            // can repair missed inbound events. A command client is created lazily only
                            // for an authoritative session or a page that emits a real conversation change.
                        }
                        catch (Exception ex)
                        {
                            Log.Exception(ex);
                        }
                    };
                    webSocket.NewMessageReceived += (session, value) =>
                    {
                        try
                        {
                            // A close callback removes this marker before releasing the client.
                            // Ignore any late frame emitted by the old physical socket.
                            if (session == null || !_connectedSessions.ContainsKey(session.SessionID)) return;
                            // Transport heartbeats and diagnostic/status snapshots are not
                            // seller activity. They must not keep idle duplicate pages alive
                            // forever or the idle standby sweeper can never retire them.
                            if (string.Equals(value, "{\"type\":\"hi\"}", StringComparison.Ordinal))
                                return;
                            var wMsg = JsonConvert.DeserializeObject<WSocketMessage>(value);
                            if (wMsg == null || wMsg.Type == "hi") return;
                            if (wMsg.Type != "qnbotStatus"
                                && wMsg.Type != "imsdkApiScan"
                                && wMsg.Type != "imsdkInvokeTrace")
                            {
                                TouchSessionBusinessActivity(session);
                            }

                            Log.Info("收到千牛WebSocket事件: type=" + wMsg.Type);

                            if (wMsg.Type == "qnbotStatus")
                            {
                                var identityProbeGeneration = BeginShopIdentityProbe(session.SessionID);
                                Log.Info("千牛注入状态: " + wMsg.Response);
                                try
                                {
                                    var jo = JObject.Parse(wMsg.Response ?? "{}");
                                    var hasLoginId = jo["hasLoginID"] != null && jo["hasLoginID"].Value<bool>();
                                    var hasImsdk = jo["hasImsdk"] != null && jo["hasImsdk"].Value<bool>();
                                    var hasQn = jo["hasQN"] != null && jo["hasQN"].Value<bool>();
                                    var hasVs = jo["hasVs"] != null && jo["hasVs"].Value<bool>();
                                    var supportsRecoverableStandby = jo["recoverableStandby"] != null && jo["recoverableStandby"].Value<bool>();
                                    var loginNick = ReadJsonString(jo, "loginNick");
                                    var conversationNick = ReadJsonString(jo, "conversationNick");
                                    if (supportsRecoverableStandby)
                                    {
                                        _recoverableStandbySessions[session.SessionID] = true;
                                    }
                                    else
                                    {
                                        bool ignoredCapability;
                                        _recoverableStandbySessions.TryRemove(session.SessionID, out ignoredCapability);
                                    }
                                    BotConnectionDiagnostics.RecordInjectionStatus(true, hasImsdk, hasLoginId, hasQn, hasVs, wMsg.Response);
                                    BotConnectionDiagnostics.RecordBuyerSeller(loginNick, conversationNick);
                                    if (hasLoginId || hasImsdk)
                                    {
                                        var wasInitialized = _initialized.ContainsKey(session.SessionID);
                                        string sessionShopKey;
                                        var hasShopKey = _sessionShopKeys.TryGetValue(session.SessionID, out sessionShopKey)
                                            && !string.IsNullOrWhiteSpace(sessionShopKey);
                                        var wasAuthoritative = hasShopKey
                                            && IsAuthoritativeShopSession(sessionShopKey, session.SessionID);
                                        if (!wasInitialized)
                                        {
                                            // Obtain exact TargetId/ShopKey from this CDP session;
                                            // loginNick is display/diagnostic data, not an owner key.
                                            Task.Run(() => TryInitSession(session, "status", identityProbeGeneration));
                                        }
                                        else if (!hasShopKey)
                                        {
                                            var bindingGeneration = ShouldRefreshStatusBinding(
                                                session.SessionID, loginNick, conversationNick);
                                            if (bindingGeneration > 0)
                                                Task.Run(() => TryBindStatusConversation(
                                                    session, loginNick, conversationNick, bindingGeneration, identityProbeGeneration));
                                        }
                                        else if (!wasAuthoritative)
                                        {
                                            _initialized[session.SessionID] = true;
                                            // Duplicate pages also need periodic exact ShopKey
                                            // verification; display nicknames can be identical
                                            // across different shops.
                                            var bindingGeneration = ShouldRefreshStatusBinding(
                                                session.SessionID, loginNick, conversationNick);
                                            if (bindingGeneration > 0)
                                                Task.Run(() => TryBindStatusConversation(
                                                    session, loginNick, conversationNick, bindingGeneration, identityProbeGeneration));
                                            Log.Info("检测到同一ShopKey的重复千牛WebSocket页面，保留为可恢复standby: shopRef="
                                                + DiagnosticRef("shop", sessionShopKey)
                                                + ", ignoredSessionRef=" + DiagnosticRef("session", session.SessionID));
                                        }
                                        else if (!string.IsNullOrWhiteSpace(loginNick) || !string.IsNullOrWhiteSpace(conversationNick))
                                        {
                                            var bindingGeneration = ShouldRefreshStatusBinding(
                                                session.SessionID, loginNick, conversationNick);
                                            if (bindingGeneration > 0)
                                                Task.Run(() => TryBindStatusConversation(
                                                    session, loginNick, conversationNick, bindingGeneration, identityProbeGeneration));
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    BotConnectionDiagnostics.RecordInjectionStatus(false, false, false, false, false, "解析注入状态失败：" + ex.Message);
                                }
                            }
                            else if (wMsg.Type == "imsdkApiScan")
                            {
                                Log.Info("IMSDK API鎵弿缁撴灉: " + wMsg.Response);
                            }
                            else if (wMsg.Type == "imsdkInvokeTrace")
                            {
                                Log.Info("IMSDK璋冪敤璺熻釜: " + wMsg.Response);
                            }
                            else if (wMsg.Type == "onConversationChange")
                            {
                                // Precise activity evidence may need this physical WebView for future CDP
                                // commands; create only the lightweight client, without seller initialization.
                                GetOrCreateClient(session);
                            }
                            else if (wMsg.Type == "onChatDlgActive")
                            {
                                var identityProbeGeneration = BeginShopIdentityProbe(session.SessionID);
                                string duplicateShopKey;
                                if (_duplicateShopSessions.TryGetValue(session.SessionID, out duplicateShopKey))
                                {
                                    Task.Run(async () =>
                                    {
                                        if (await TryPromoteDuplicateShopSessionAsync(
                                            duplicateShopKey, session, "onChatDlgActive",
                                            expectedIdentityProbeGeneration: identityProbeGeneration).ConfigureAwait(false))
                                        {
                                            var activeBuyer = string.Empty;
                                            try
                                            {
                                                var activePayload = JObject.Parse(wMsg.Response ?? "{}");
                                                activeBuyer = ReadJsonString(activePayload, "conversationNick");
                                                if (string.IsNullOrWhiteSpace(activeBuyer))
                                                    activeBuyer = ReadJsonString(activePayload, "buyerNick");
                                            }
                                            catch { }
                                            var bindingGeneration = BeginStatusBindingAttempt(session.SessionID);
                                            await TryBindStatusConversation(
                                                session, string.Empty, activeBuyer, bindingGeneration, identityProbeGeneration)
                                                .ConfigureAwait(false);
                                        }
                                    });
                                }
                                else
                                {
                                    Task.Run(() => TryInitSession(
                                        session, "event:onChatDlgActive", identityProbeGeneration));
                                }
                            }
                            else if (wMsg.Type == "receiveNewMsg" || wMsg.Type == "onShopRobotReceriveNewMsgs")
                            {
                                string duplicateSeller;
                                if (!_duplicateShopSessions.TryGetValue(session.SessionID, out duplicateSeller))
                                {
                                    var identityProbeGeneration = BeginShopIdentityProbe(session.SessionID);
                                    Task.Run(() => TryInitSession(
                                        session, "event:" + wMsg.Type, identityProbeGeneration));
                                }
                            }

                            if (OnRecieveMessage != null)
                                OnRecieveMessage(session, new WSocketNewMessageEventArgs(wMsg.Type, wMsg.Response));
                        }
                        catch (Exception ex)
                        {
                            Log.Exception(ex);
                        }
                    };
                    webSocket.SessionClosed += (session, value) =>
                    {
                        lock (_sellerSessionSync)
                        {
                            _connectedSessions.TryRemove(session.SessionID, out _);
                            NextStatusBindingGenerationLocked(session.SessionID);
                            long ignoredStatusGeneration;
                            _statusBindingGenerations.TryRemove(session.SessionID, out ignoredStatusGeneration);
                            NextShopIdentityProbeGenerationLocked(session.SessionID);
                        }
                        WebSocketSession ignoredSession;
                        DateTime ignoredActivity;
                        bool ignoredClosing;
                        bool ignoredCapability;
                        string ignoredStandbyLog;
                        _liveSessions.TryRemove(session.SessionID, out ignoredSession);
                        _sessionLastActivityUtc.TryRemove(session.SessionID, out ignoredActivity);
                        _closingDuplicateSessions.TryRemove(session.SessionID, out ignoredClosing);
                        _recoverableStandbySessions.TryRemove(session.SessionID, out ignoredCapability);
                        _standbyLoggedSessions.TryRemove(session.SessionID, out ignoredStandbyLog);
                        ReleaseShopSession(session.SessionID);
                        BotConnectionDiagnostics.RecordWebSocketClose(session.SessionID);
                        // Session-scoped lifecycle-guard dictionaries must not keep
                        // a strong reference to every old 12-second reconnect lease.
                        WebSocketPageLifecycleGuard.ForgetClosedSession(session.SessionID);
                        DuplicateCdpInboundRecoveryBridge.ForgetClosedSession(session.SessionID);
                        Log.Info("千牛注入脚本 WebSocket 已断开: sessionRef=" + DiagnosticRef("session", session.SessionID)
                            + ", reason=" + value);
                        CDPClient removed;
                        bool b;
                        string statusBinding;
                        string duplicateSeller;
                        long ignoredIdentityProbeGeneration;
                        _shopIdentityProbeGenerations.TryRemove(session.SessionID, out ignoredIdentityProbeGeneration);
                        _clients.TryRemove(session.SessionID, out removed);
                        CDPClient.ReleaseClosedSession(session.SessionID, Convert.ToString(value), removed);
                        _initialized.TryRemove(session.SessionID, out b);
                        _initializing.TryRemove(session.SessionID, out b);
                        _lastStatusBindings.TryRemove(session.SessionID, out statusBinding);
                        DateTime ignoredStatusRefresh;
                        _lastStatusBindingRefreshUtc.TryRemove(session.SessionID, out ignoredStatusRefresh);
                        _duplicateShopSessions.TryRemove(session.SessionID, out duplicateSeller);
                    };
                    var config = new ServerConfig()
                    {
                        MaxRequestLength = 5 * 1024 * 1024,
                        Ip = "127.0.0.1",
                        Port = 41010
                    };
                    if (!webSocket.Setup(config))
                    {
                        const string setupError = "Bot WebSocket服务启动失败：Setup返回false，127.0.0.1:41010 未建立监听。";
                        BotConnectionDiagnostics.RecordWebSocketServerError(setupError);
                        Log.Error(setupError);
                        return;
                    }

                    var started = false;
                    Exception lastStartException = null;
                    for (var attempt = 1; attempt <= WebSocketStartMaxAttempts; attempt++)
                    {
                        try
                        {
                            if (webSocket.Start())
                            {
                                started = true;
                                if (attempt > 1)
                                {
                                    Log.Info("Bot WebSocket服务启动重试成功: 127.0.0.1:41010, attempt=" + attempt);
                                }
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            lastStartException = ex;
                        }

                        if (attempt < WebSocketStartMaxAttempts)
                        {
                            var retryDelayMs = WebSocketStartRetryBaseDelayMs * (1 << (attempt - 1));
                            Log.Error("Bot WebSocket服务启动暂时失败，将在短退避后重试: 127.0.0.1:41010, attempt="
                                + attempt + ", retryInMs=" + retryDelayMs
                                + (lastStartException == null ? string.Empty : ", error=" + lastStartException.Message));
                            Thread.Sleep(retryDelayMs);
                        }
                    }

                    if (!started)
                    {
                        var startError = lastStartException == null
                            ? "Bot WebSocket服务启动失败：连续" + WebSocketStartMaxAttempts + "次Start返回false，127.0.0.1:41010 未建立监听。"
                            : "Bot WebSocket服务启动失败：连续" + WebSocketStartMaxAttempts + "次Start未成功，127.0.0.1:41010 未建立监听。最后异常=" + lastStartException.Message;
                        BotConnectionDiagnostics.RecordWebSocketServerError(startError);
                        Log.Error(startError);
                        return;
                    }

                    // Keep a strong reference to the active server. Without this, the first listener
                    // can become collectible before any injected page connects during startup races.
                    _webSocketServer = webSocket;
                    StartDuplicateSessionSweeper();
                    WriteReadOnlyAcceptanceSnapshot();
                    BotConnectionDiagnostics.RecordWebSocketServerStarted();
                    Log.Info("Bot WebSocket服务已启动: 127.0.0.1:41010");
                }
                catch (Exception ex)
                {
                    BotConnectionDiagnostics.RecordWebSocketServerError(ex.Message);
                    Log.Exception(ex);
                }
            }
        }

        private async Task RefreshRpaAfterAuthoritativeSessionAsync(QN qn, string shopKey, string sessionId)
        {
            try
            {
                await Task.Delay(200).ConfigureAwait(false);
                if (!IsAuthoritativeShopSession(shopKey, sessionId)) return;
                var rpa = qn == null ? null : qn.Rpa;
                if (rpa == null)
                {
                    Log.Info("权威CDP重连后跳过UIA刷新：客服RPA尚未初始化, shopRef="
                        + DiagnosticRef("shop", shopKey));
                    return;
                }

                var inputReady = await rpa.RefreshChatControlsAsync(true).ConfigureAwait(false);
                if (!IsAuthoritativeShopSession(shopKey, sessionId)) return;
                Log.Info("权威CDP重连后已刷新UIA控件诊断: shopRef="
                    + DiagnosticRef("shop", shopKey)
                    + ", sellerRef=" + DiagnosticRef("seller", qn.Seller == null ? string.Empty : qn.Seller.Nick)
                    + ", sessionRef=" + DiagnosticRef("session", sessionId)
                    + ", inputReady=" + inputReady);
            }
            catch (Exception ex)
            {
                Log.Info("权威CDP重连后UIA刷新失败，等待下一次发送前扫描: type="
                    + ex.GetType().Name);
            }
        }

    }
    public class ConnectionDiagnosticsSnapshot
    {
        public bool WebSocketServerStarted { get; set; }
        public bool InjectionConnected { get; set; }
        public bool CdpReady { get; set; }
        public int WebSocketSessionCount { get; set; }
        public int AuthoritativeCdpSessionCount { get; set; }
        public string WebSocketStatus { get; set; }
        public string InjectionStatus { get; set; }
        public string QnParamStatus { get; set; }
        public string AccessibilityStatus { get; set; }
        public string ButtonStatus { get; set; }
        public string SendStatus { get; set; }
        public string LanguageStatus { get; set; }
        public string LanguageDetail { get; set; }
        public bool LanguageOk { get; set; }
        public string Summary { get; set; }
        public string Seller { get; set; }
        public string Buyer { get; set; }
        public DateTime LastUpdateTime { get; set; }
    }

    public static class BotConnectionDiagnostics
    {
        private static readonly object SyncObj = new object();
        private static readonly HashSet<string> wsSessions = new HashSet<string>(StringComparer.Ordinal);
        private static bool wsStarted;
        private static int wsSessionCount;
        private static int authoritativeCdpSessionCount;
        private static bool injectionConnected;
        private static bool hasImsdk;
        private static bool hasLoginId;
        private static bool hasQn;
        private static bool hasVs;
        private static bool cdpReady;
        private static string cdpMessage = "未获取";
        private static string seller = string.Empty;
        private static string buyer = string.Empty;
        private static bool uiAccessible;
        private static bool sendButtonFound;
        private static bool inputFound;
        private static string lastSendStatus = "未测试";
        private static bool languageOk = true;
        private static string languageStatus = "语言：正在检测";
        private static string languageDetail = string.Empty;
        private static string lastWsError = string.Empty;
        private static DateTime lastUpdate = DateTime.MinValue;

        public static void RecordLanguageStatus(bool ok, string status, string detail)
        {
            lock (SyncObj)
            {
                languageOk = ok;
                languageStatus = string.IsNullOrWhiteSpace(status) ? (ok ? "语言：简体中文 ✓" : "语言：检测异常") : status;
                languageDetail = detail ?? string.Empty;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordWebSocketServerStarted()
        {
            lock (SyncObj)
            {
                wsStarted = true;
                lastWsError = string.Empty;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordWebSocketServerError(string error)
        {
            lock (SyncObj)
            {
                wsStarted = false;
                lastWsError = error ?? string.Empty;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordWebSocketConnect(string sessionId)
        {
            lock (SyncObj)
            {
                if (!string.IsNullOrWhiteSpace(sessionId)) wsSessions.Add(sessionId.Trim());
                wsSessionCount = wsSessions.Count;
                injectionConnected = wsSessionCount > 0;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordWebSocketClose(string sessionId)
        {
            lock (SyncObj)
            {
                if (!string.IsNullOrWhiteSpace(sessionId)) wsSessions.Remove(sessionId.Trim());
                wsSessionCount = wsSessions.Count;
                injectionConnected = wsSessionCount > 0;
                if (wsSessionCount == 0)
                {
                    hasImsdk = false;
                    hasLoginId = false;
                    hasQn = false;
                    hasVs = false;
                    cdpReady = false;
                    cdpMessage = "等待注入重连";
                    seller = string.Empty;
                    buyer = string.Empty;
                    uiAccessible = false;
                    sendButtonFound = false;
                    inputFound = false;
                }
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordAuthoritativeCdpSessionCount(int count)
        {
            lock (SyncObj)
            {
                authoritativeCdpSessionCount = Math.Max(0, count);
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordInjectionStatus(bool connected, bool imsdk, bool loginId, bool qn, bool vs, string raw)
        {
            lock (SyncObj)
            {
                injectionConnected = connected;
                hasImsdk = imsdk;
                hasLoginId = loginId;
                hasQn = qn;
                hasVs = vs;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordCdpStatus(bool ready, string message, string sellerNick, string buyerNick)
        {
            lock (SyncObj)
            {
                cdpReady = ready;
                cdpMessage = message ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(sellerNick)) seller = sellerNick;
                if (!string.IsNullOrWhiteSpace(buyerNick)) buyer = buyerNick;
                if (!ready && authoritativeCdpSessionCount == 0)
                {
                    seller = string.Empty;
                    buyer = string.Empty;
                }
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordBuyerSeller(string sellerNick, string buyerNick)
        {
            lock (SyncObj)
            {
                if (!string.IsNullOrWhiteSpace(sellerNick)) seller = sellerNick;
                if (!string.IsNullOrWhiteSpace(buyerNick)) buyer = buyerNick;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordRpaScan(bool buttonFound, bool textInputFound, string note)
        {
            lock (SyncObj)
            {
                sendButtonFound = buttonFound;
                inputFound = textInputFound;
                uiAccessible = buttonFound && textInputFound;
                lastUpdate = DateTime.Now;
            }
        }

        public static void RecordSendAttempt(bool success, string message)
        {
            lock (SyncObj)
            {
                lastSendStatus = success ? "成功" : "失败";
                if (!string.IsNullOrWhiteSpace(message)) lastSendStatus += "：" + message;
                lastUpdate = DateTime.Now;
            }
        }

        private static string BuildSummary(bool wsOk, bool injectionOk, bool qnOk, bool sellerOk, bool uiOk, bool buttonOk, bool inputOk)
        {
            // Connection health describes the transport/session/UI prerequisites only. A failed
            // business send remains visible through SendStatus, but must not relabel an otherwise
            // healthy Qianniu connection as disconnected or unhealthy.
            if (wsOk && injectionOk && qnOk && sellerOk && uiOk && buttonOk && inputOk) return "连接正常";
            if (!wsStarted) return string.IsNullOrWhiteSpace(lastWsError) ? "WS服务未启动" : "WS服务异常";
            if (!wsOk) return "WS未连接";
            if (!injectionOk) return "注入未连接";
            if (!qnOk) return "千牛参数未获取";
            if (!sellerOk) return "客服ID未识别";
            if (!uiOk || !buttonOk || !inputOk) return "无障碍/按钮未就绪";
            return "检测中/需检查";
        }

        public static ConnectionDiagnosticsSnapshot GetSnapshot()
        {
            lock (SyncObj)
            {
                var ws = wsStarted
                    ? (wsSessionCount > 0
                        ? "已连接｜业务CDP=" + authoritativeCdpSessionCount + "｜页面通道=" + wsSessionCount
                        : "已监听")
                    : (string.IsNullOrWhiteSpace(lastWsError) ? "未启动" : "异常：" + lastWsError);
                var inject = injectionConnected ? "已连接" : "未连接";
                if (injectionConnected)
                {
                    inject += "｜imsdk=" + (hasImsdk ? "是" : "否") + "｜login=" + (hasLoginId ? "是" : "否");
                }
                var sellerOk = !string.IsNullOrWhiteSpace(seller);
                var qnStatus = cdpReady ? "已获取" : cdpMessage;
                if (cdpReady) qnStatus += sellerOk ? "｜客服=" + seller : "｜客服未识别";
                var access = uiAccessible ? "可用" : (inputFound || sendButtonFound ? "部分可用" : "未确认");
                var btn = sendButtonFound ? "已识别发送按钮" : "未识别发送按钮";
                if (!inputFound) btn += "｜输入框未识别";

                var wsOk = wsStarted && wsSessionCount > 0;
                var injectionOk = injectionConnected;
                var qnOk = cdpReady;
                var uiOk = uiAccessible;
                var buttonOk = sendButtonFound;
                var inputOk = inputFound;
                var summary = BuildSummary(wsOk, injectionOk, qnOk, sellerOk, uiOk, buttonOk, inputOk);

                return new ConnectionDiagnosticsSnapshot
                {
                    WebSocketServerStarted = wsStarted,
                    InjectionConnected = injectionConnected,
                    CdpReady = cdpReady,
                    WebSocketSessionCount = wsSessionCount,
                    AuthoritativeCdpSessionCount = authoritativeCdpSessionCount,
                    WebSocketStatus = ws,
                    InjectionStatus = inject,
                    QnParamStatus = qnStatus,
                    AccessibilityStatus = access,
                    ButtonStatus = btn,
                    SendStatus = lastSendStatus,
                    LanguageStatus = languageStatus,
                    LanguageDetail = languageDetail,
                    LanguageOk = languageOk,
                    Summary = summary,
                    Seller = seller,
                    Buyer = buyer,
                    LastUpdateTime = lastUpdate
                };
            }
        }
    }

    public static class QianniuRecoveryManager
    {
        private const string DestructiveRecoveryEnvironmentKey = "QNBOT_ALLOW_DESTRUCTIVE_QIANNIU_RECOVERY";
        private static readonly object SyncObj = new object();
        private static bool recovering;
        private static DateTime lastRecoverTime = DateTime.MinValue;

        private static bool IsDestructiveRecoveryExplicitlyEnabled()
        {
            var value = (Environment.GetEnvironmentVariable(DestructiveRecoveryEnvironmentKey) ?? string.Empty).Trim();
            return value == "1"
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        public static void RequestRecover(string reason)
        {
            if (!IsDestructiveRecoveryExplicitlyEnabled())
            {
                Log.ErrorWithMaxCount(
                    "千牛连接异常已进入非破坏性降级：已阻止旧版自动杀进程/重启千牛恢复，保留当前登录态。 reason="
                    + (reason ?? string.Empty),
                    20);
                return;
            }

            lock (SyncObj)
            {
                if (recovering) return;
                if ((DateTime.Now - lastRecoverTime).TotalMinutes < 3) return;
                recovering = true;
                lastRecoverTime = DateTime.Now;
            }

            Task.Run(async () =>
            {
                try
                {
                    await RecoverAsync(reason);
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                }
                finally
                {
                    lock (SyncObj)
                    {
                        recovering = false;
                    }
                }
            });
        }

        private static async Task RecoverAsync(string reason)
        {
            var shopKey = string.Empty;
            var lastBuyer = string.Empty;
            var previous = QN.CurQN;
            var previousClient = previous == null ? null : previous.CDP;
            if (previousClient != null)
            {
                var candidateShopKey = previousClient.ShopKey;
                if (ReferenceEquals(ShopContextLocator.FindConnectedQnByShopKey(
                    candidateShopKey, previousClient.SessionId), previous))
                {
                    shopKey = candidateShopKey;
                    var buyer = previous.Buyer;
                    lastBuyer = buyer == null ? string.Empty : buyer.Nick;
                }
            }

            Log.Error("触发显式授权的千牛破坏性自动恢复：" + reason);
            KillQianniuProcesses();
            await Task.Delay(3000);

            var exe = FindLaunchFile();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                Log.Error("千牛自动恢复失败：未找到 AliWorkbench.exe/wwcmd.exe");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = true
                });
                Log.Info("已启动千牛恢复进程");
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                return;
            }

            var started = await WaitForProcessAsync("AliWorkbench", 60);
            if (!started)
            {
                Log.Error("千牛自动恢复失败：启动后60秒内未检测到 AliWorkbench 进程");
                return;
            }

            Log.Info("千牛进程已启动，等待接待窗口和客服参数恢复...");
            await WaitForChatDeskAndOpenLastBuyerAsync(shopKey, lastBuyer, 75);
        }

        private static void KillQianniuProcesses()
        {
            foreach (var name in new[] { "AliWorkbench", "AliRender", "wwcmd", "wangwang" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        p.Kill();
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(ex);
                    }
                }
            }
        }

        private static async Task<bool> WaitForProcessAsync(string processName, int seconds)
        {
            var end = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < end)
            {
                try
                {
                    if (Process.GetProcessesByName(processName).Length > 0) return true;
                }
                catch
                {
                }
                await Task.Delay(1000);
            }
            return false;
        }

        private static async Task WaitForChatDeskAndOpenLastBuyerAsync(string shopKey, string lastBuyer, int seconds)
        {
            var end = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < end)
            {
                try
                {
                    var qn = string.IsNullOrWhiteSpace(shopKey)
                        ? QN.CurQN
                        : ShopContextLocator.FindConnectedQnByShopKey(shopKey);
                    var client = qn == null ? null : qn.CDP;
                    var server = MyWebSocketServer.WSocketSvrInst;
                    if (client != null && qn.Seller != null
                        && !client.IsInvalidated && !client.IsShopIdentityRetired
                        && server != null && server.IsAuthoritativeShopSession(client.ShopKey, client.SessionId))
                    {
                        Log.Info("千牛自动恢复成功: sellerRef=" + MyWebSocketServer.DiagnosticRef("seller", qn.Seller.Nick));
                        var desk = DeskSellerBindingRegistry.FindSellerDesk(qn.Seller.Nick, client.ShopKey);
                        if (desk != null)
                        {
                            desk.Show();
                            desk.BringTop();
                        }

                        if (!string.IsNullOrWhiteSpace(shopKey) && !string.IsNullOrWhiteSpace(lastBuyer)
                            && qn.Rpa != null && await qn.Rpa.VerifyShopIdentityBeforeSendAsync(shopKey)
                            && ReferenceEquals(qn.CDP, client)
                            && server.IsAuthoritativeShopSession(shopKey, client.SessionId))
                        {
                            client.OpenChat(lastBuyer);
                            Log.Info("已尝试恢复打开最近买家会话: buyerRef=" + MyWebSocketServer.DiagnosticRef("buyer", lastBuyer));
                        }
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception(ex);
                }
                await Task.Delay(1000);
            }
            Log.Error("千牛已启动，但未能在限定时间内识别到接待窗口/客服ID。请确认千牛设置中已开启无障碍模式，并至少打开一次客服接待窗口。");
        }

        private static string FindLaunchFile()
        {
            var installPath = FindInstallPathFromRegistry();
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(installPath))
            {
                candidates.Add(Path.Combine(installPath, "AliWorkbench.exe"));
                candidates.Add(Path.Combine(installPath, "wwcmd.exe"));
            }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AliWorkbench", "AliWorkbench.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AliWorkbench", "AliWorkbench.exe"));

            foreach (var path in candidates)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                }
                catch
                {
                }
            }
            return string.Empty;
        }

        private static string FindInstallPathFromRegistry()
        {
            try
            {
                using (var key = Registry.ClassesRoot.OpenSubKey(@"aliim\Shell\Open\Command"))
                {
                    if (key == null) return string.Empty;
                    var raw = (key.GetValue("") ?? string.Empty).ToString();
                    var exe = ExtractExePath(raw);
                    if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return string.Empty;
                    var dir = Directory.GetParent(exe);
                    if (dir == null) return string.Empty;
                    var parent = dir.Parent;
                    return parent == null ? dir.FullName : parent.FullName;
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                return string.Empty;
            }
        }

        private static string ExtractExePath(string command)
        {
            command = (command ?? string.Empty).Trim();
            if (command.Length < 1) return string.Empty;
            if (command.StartsWith("\""))
            {
                var end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : string.Empty;
            }
            var idx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? command.Substring(0, idx + 4).Trim() : command;
        }
    }

    public class WSocketMessage
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("response")]
        public string Response { get; set; }
    }

    public class WSocketNewMessageEventArgs : EventArgs
    {
        public string Type { get; private set; }
        public string Value { get; private set; }

        public WSocketNewMessageEventArgs(string type, string value)
        {
            Type = type;
            Value = value;
        }
    }
}
