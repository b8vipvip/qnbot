from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_duplicate_websocket_pages_use_bounded_recoverable_standby_leases():
    server = text("src/Bot/ChromeNs/MyWebSocketServer.cs")
    guard = text("src/Bot/ChromeNs/WebSocketPageLifecycleGuard.cs")
    inject = text("src/Bin/inject.js")

    assert "_recoverableStandbySessions" in server
    assert 'jo["recoverableStandby"]' in server
    assert 'method = "retireDuplicate"' in server
    assert "var retryAfterMs = 300000 +" in server
    assert "sessionId.GetHashCode() % 240000U" in server
    assert "session.Send(command)" in server
    assert 'method = "retireDuplicate"' not in guard
    assert 'Session.Close()' not in guard

    # The v12 payload keeps legacy command compatibility but treats standby as a
    # bounded lease; it never latches a page into a permanently retired state.
    assert "var websocketStandbyUntil = 0;" in inject
    assert "websocketRetired" not in inject
    assert "duplicateRetire: false" in inject
    assert "recoverableStandby: true" in inject
    assert 'param.method === "retireDuplicate"' in inject
    assert "var retryAfterMs = Number(param.retryAfterMs) || 300000;" in inject
    assert "scheduleReconnect(retryAfterMs);" in inject
    assert "pendingStatus = payload;" in inject
    assert "var businessEvent = type !== \"qnbotStatus\" && !diagnosticOnly;" in inject
    assert "scheduleReconnect();" in inject


def test_idle_duplicate_sweeper_uses_business_activity_not_transport_heartbeats():
    server = text("src/Bot/ChromeNs/MyWebSocketServer.cs")
    message_handler = server[
        server.index("webSocket.NewMessageReceived +="):
        server.index("webSocket.SessionClosed +=")
    ]

    assert "private void TouchSessionBusinessActivity(WebSocketSession session)" in server
    assert "_sessionLastActivityUtc.TryAdd(session.SessionID, DateTime.UtcNow);" in server
    assert "_sessionLastActivityUtc[session.SessionID] = DateTime.UtcNow;" in server
    assert 'if (string.Equals(value, "{\\"type\\":\\"hi\\"}", StringComparison.Ordinal))' in message_handler
    assert 'if (wMsg == null || wMsg.Type == "hi") return;' in message_handler
    assert 'wMsg.Type != "qnbotStatus"' in message_handler
    assert 'wMsg.Type != "imsdkApiScan"' in message_handler
    assert 'wMsg.Type != "imsdkInvokeTrace"' in message_handler
    assert "TouchSessionBusinessActivity(session);" in message_handler
    assert "TouchSession(session);" not in message_handler
    assert message_handler.index("if (wMsg == null || wMsg.Type == \"hi\") return;") < message_handler.index(
        "TouchSessionBusinessActivity(session);"
    )


def test_injection_marker_for_existing_payload_remains_consistent():
    inject = text("src/Bin/inject.js")
    qn_inject = text("src/Bot/Common/QNInject.cs")
    marker = "20261114-ws-recoverable-reconnect-v12"

    assert 'window.__qnbotInjectVersion = "' + marker + '";' in inject
    assert 'private const string injectVersionMarker = "' + marker + '";' in qn_inject


def test_websocket_startup_is_idempotent_and_retries_transient_start_failures():
    server = text("src/Bot/ChromeNs/MyWebSocketServer.cs")

    assert "private WebSocketServer _webSocketServer;" in server
    assert "private readonly object _webSocketStartSync = new object();" in server
    assert "private const int WebSocketStartMaxAttempts = 5;" in server
    assert "private const int WebSocketStartRetryBaseDelayMs = 250;" in server
    assert "lock (_webSocketStartSync)" in server
    assert "if (_webSocketServer != null && IsLoopbackListenerActive())" in server
    assert "ReleaseStaleSessions(" in server
    assert "if (!webSocket.Setup(config))" in server
    assert "for (var attempt = 1; attempt <= WebSocketStartMaxAttempts; attempt++)" in server
    assert "if (webSocket.Start())" in server
    assert "Thread.Sleep(retryDelayMs);" in server
    assert "_webSocketServer = webSocket;" in server

    setup_index = server.index("if (!webSocket.Setup(config))")
    retry_index = server.index("for (var attempt = 1; attempt <= WebSocketStartMaxAttempts; attempt++)")
    root_index = server.index("_webSocketServer = webSocket;")
    success_index = server.index('Log.Info("Bot WebSocket服务已启动: 127.0.0.1:41010")')
    assert setup_index < retry_index < root_index < success_index


def test_uia_control_refreshes_share_one_inflight_scan():
    source = text("src/Bot/ChromeNs/QNRpa.ReliableSend.cs")

    assert "private readonly object _chatControlsRefreshSync = new object();" in source
    assert "private Task<bool> _activeChatControlsRefreshTask;" in source
    assert "lock (_chatControlsRefreshSync)" in source
    assert "&& !_activeChatControlsRefreshTask.IsCompleted" in source
    assert "return _activeChatControlsRefreshTask;" in source
    assert "_activeChatControlsRefreshTask = RefreshChatControlsCoreAsync();" in source
    assert "private async Task<bool> RefreshChatControlsCoreAsync()" in source


def test_total_ai_budget_stops_waiting_for_non_cooperative_provider_calls():
    source = text("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    assert "var answerTask = StreamingBuyerAnswerService.GetAnswerAsync(" in source
    assert "answer = await AwaitWithCancellationAsync(answerTask, generationCts.Token);" in source
    assert "private static async Task<T> AwaitWithCancellationAsync<T>" in source
    assert "var cancelled = Task.Delay(Timeout.Infinite, token);" in source
    assert "var completed = await Task.WhenAny(task, cancelled).ConfigureAwait(false);" in source
    # The late-task observer must run for success, cancellation and faults,
    # so the pending-task diagnostic counter never remains permanently raised.
    assert "TaskContinuationOptions.ExecuteSynchronously" in source
    assert "var observed = t.Exception;" in source
    assert "Interlocked.Decrement(ref _unfinishedAnswerTasksAfterCancel);" in source
    assert "token.ThrowIfCancellationRequested();" in source
    assert "answer = await StreamingBuyerAnswerService.GetAnswerAsync(" not in source



def test_duplicate_probe_fingerprint_cache_enforces_its_declared_hard_cap():
    guard = text("src/Bot/ChromeNs/WebSocketPageLifecycleGuard.cs")
    method = guard[
        guard.index("private static bool TryClaimDuplicateProbePayload"):
        guard.index("private static string BuildDuplicateProbeFingerprint")
    ]

    assert "MaxDuplicateProbeFingerprints = 2048" in guard
    assert "TrimOldestDuplicateProbeFingerprints();" in method
    assert "private static void TrimOldestDuplicateProbeFingerprints()" in guard
    assert "RecentDuplicateProbePayloads.OrderBy(x => x.Value).Take(removeCount)" in guard
    assert "targetCount = MaxDuplicateProbeFingerprints * 3 / 4" in guard

def test_late_websocket_events_cannot_recreate_cdp_client_after_session_close():
    server = text("src/Bot/ChromeNs/MyWebSocketServer.cs")
    client_factory = server[
        server.index("private CDPClient GetOrCreateClient"):
        server.index("private static string ReadJsonString")
    ]
    closed = server[
        server.index("webSocket.SessionClosed +="):
        server.index("var config = new ServerConfig()")
    ]

    assert "if (session == null || string.IsNullOrWhiteSpace(session.SessionID)) return null;" in client_factory
    assert "lock (_sellerSessionSync)" in client_factory
    assert "if (!_connectedSessions.ContainsKey(sessionId)) return null;" in client_factory
    assert client_factory.index("_connectedSessions.ContainsKey(sessionId)") < client_factory.index("_clients.GetOrAdd")
    assert "lock (_sellerSessionSync)" in closed
    assert "_connectedSessions.TryRemove(session.SessionID, out _);" in closed
    assert closed.index("_connectedSessions.TryRemove(session.SessionID, out _);") < closed.index("_clients.TryRemove(session.SessionID, out removed);")

def test_websocket_server_drops_late_frames_after_session_close():
    server = text("src/Bot/ChromeNs/MyWebSocketServer.cs")
    handler = server[
        server.index("webSocket.NewMessageReceived +="):
        server.index("webSocket.SessionClosed +=")
    ]

    assert "if (session == null || !_connectedSessions.ContainsKey(session.SessionID)) return;" in handler
    assert handler.index("_connectedSessions.ContainsKey(session.SessionID)") < handler.index("JsonConvert.DeserializeObject<WSocketMessage>(value)")

def test_closed_websocket_status_generation_entries_are_pruned_without_generation_reuse():
    server = text("src/Bot/ChromeNs/MyWebSocketServer.cs")
    next_generation = server[
        server.index("private long NextStatusBindingGenerationLocked"):
        server.index("private bool IsCurrentStatusBindingGenerationLocked")
    ]
    closed = server[
        server.index("webSocket.SessionClosed +="):
        server.index("var config = new ServerConfig()")
    ]

    assert "private long _statusBindingGenerationSequence;" in server
    assert "Interlocked.Increment(ref _statusBindingGenerationSequence)" in next_generation
    assert "_statusBindingGenerations[sessionId] = next;" in next_generation
    assert "NextStatusBindingGenerationLocked(session.SessionID);" in closed
    assert "_statusBindingGenerations.TryRemove(session.SessionID, out ignoredStatusGeneration);" in closed
    assert closed.index("NextStatusBindingGenerationLocked(session.SessionID);") < closed.index("_statusBindingGenerations.TryRemove(session.SessionID, out ignoredStatusGeneration);")


def test_listener_restart_releases_both_session_generation_dictionaries():
    server = text("src/Bot/ChromeNs/MyWebSocketServer.cs")
    release = server.split("private void ReleaseStaleSessions", 1)[1].split(
        "public void Start()", 1
    )[0]
    assert "NextStatusBindingGenerationLocked(sessionId);" in release
    assert "_statusBindingGenerations.TryRemove(sessionId, out ignoredStatusGeneration);" in release
    assert "NextShopIdentityProbeGenerationLocked(sessionId);" in release
    assert "_shopIdentityProbeGenerations.TryRemove(sessionId, out ignoredIdentityProbeGeneration);" in release
    assert "ReleaseClosedSession(sessionId, reason, removed);" in release


def test_listener_disappearance_stops_and_disposes_old_supersocket_instance_before_rebuild():
    server = text("src/Bot/ChromeNs/MyWebSocketServer.cs")
    start = server.split("public void Start()", 1)[1].split(
        "private bool IsLoopbackListenerActive", 1
    )[0]
    stale = start.split("if (_webSocketServer != null)", 1)[1].split(
        "else if (IsLoopbackListenerActive())", 1
    )[0]
    assert "var staleServer = _webSocketServer;" in stale
    assert 'StopAndDisposeWebSocketServer(staleServer, "listener disappeared");' in stale
    assert "ReleaseStaleSessions(" in stale
    assert stale.index("StopAndDisposeWebSocketServer(staleServer,") < stale.index("ReleaseStaleSessions(")
    assert stale.index("ReleaseStaleSessions(") < stale.index("_webSocketServer = null;")


def test_failed_websocket_setup_and_start_dispose_unpublished_server_instances():
    server = text("src/Bot/ChromeNs/MyWebSocketServer.cs")
    start = server.split("public void Start()", 1)[1].split(
        "private async Task RefreshRpaAfterAuthoritativeSessionAsync", 1
    )[0]
    helper = server.split("private static void StopAndDisposeWebSocketServer", 1)[1].split(
        "public void Start()", 1
    )[0]
    assert "server.Stop();" in helper
    assert "server as IDisposable" in helper
    assert "StopAndDisposeWebSocketServer(webSocket, \"setup failed\")" in start
    assert "StopAndDisposeWebSocketServer(webSocket, \"start failed\")" in start
    assert "StopAndDisposeWebSocketServer(webSocket, \"startup exception\")" in start
    assert "WebSocketServer webSocket = null;" in start
