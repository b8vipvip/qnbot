from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_duplicate_websocket_pages_are_standby_not_permanently_retired():
    server = text("src/Bot/ChromeNs/MyWebSocketServer.cs")
    guard = text("src/Bot/ChromeNs/WebSocketPageLifecycleGuard.cs")
    inject = text("src/Bin/inject.js")

    assert "_duplicateRetireCapableSessions" in server
    assert 'jo["duplicateRetire"]' in server
    assert 'method = "retireDuplicate"' not in server
    assert 'physicalClose=false' in server
    assert '保持在线standby，不物理关闭' in server
    assert 'method = "retireDuplicate"' not in guard
    assert 'Session.Close()' not in guard

    # Keep compatibility with already-loaded v10 pages. They still understand the old command,
    # but the new server/guard never sends it, so normal Bot restarts cannot permanently retire them.
    assert "var websocketRetired = false;" in inject
    assert "duplicateRetire: true" in inject
    assert 'param.method === "retireDuplicate"' in inject
    assert "if (!websocketRetired) scheduleReconnect();" in inject


def test_injection_marker_for_existing_payload_remains_consistent():
    inject = text("src/Bin/inject.js")
    qn_inject = text("src/Bot/Common/QNInject.cs")
    marker = "20260906-zh-cn-ws-retire-v10"

    assert 'window.__qnbotInjectVersion = "' + marker + '";' in inject
    assert 'private const string injectVersionMarker = "' + marker + '";' in qn_inject


def test_websocket_startup_is_idempotent_and_retries_transient_start_failures():
    server = text("src/Bot/ChromeNs/MyWebSocketServer.cs")

    assert "private WebSocketServer _webSocketServer;" in server
    assert "private readonly object _webSocketStartSync = new object();" in server
    assert "private const int WebSocketStartMaxAttempts = 5;" in server
    assert "private const int WebSocketStartRetryBaseDelayMs = 250;" in server
    assert "lock (_webSocketStartSync)" in server
    assert "if (_webSocketServer != null)" in server
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


def test_total_ai_budget_hard_stops_non_cooperative_provider_calls():
    source = text("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    assert "var answerTask = StreamingBuyerAnswerService.GetAnswerAsync(" in source
    assert "answer = await AwaitWithCancellationAsync(answerTask, generationCts.Token);" in source
    assert "private static async Task<T> AwaitWithCancellationAsync<T>" in source
    assert "var cancelled = Task.Delay(Timeout.Infinite, token);" in source
    assert "var completed = await Task.WhenAny(task, cancelled).ConfigureAwait(false);" in source
    assert "TaskContinuationOptions.OnlyOnFaulted" in source
    assert "token.ThrowIfCancellationRequested();" in source
    assert "answer = await StreamingBuyerAnswerService.GetAnswerAsync(" not in source
