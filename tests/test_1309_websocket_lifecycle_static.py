from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_duplicate_websocket_retirement_is_capability_gated():
    server = text("src/Bot/ChromeNs/MyWebSocketServer.cs")
    inject = text("src/Bin/inject.js")

    assert "_duplicateRetireCapableSessions" in server
    assert 'jo["duplicateRetire"]' in server
    assert 'method = "retireDuplicate"' in server
    assert ".Where(id => _duplicateRetireCapableSessions.ContainsKey(id))" in server
    assert "await Task.Delay(150).ConfigureAwait(false);" in server

    assert "var websocketRetired = false;" in inject
    assert "duplicateRetire: true" in inject
    assert 'param.method === "retireDuplicate"' in inject
    assert "websocketRetired = true;" in inject
    assert "if (websocketRetired) return;" in inject
    assert "if (!websocketRetired) scheduleReconnect();" in inject


def test_injection_marker_for_retirement_rollout_matches_embedded_payload():
    inject = text("src/Bin/inject.js")
    qn_inject = text("src/Bot/Common/QNInject.cs")
    marker = "20260906-zh-cn-ws-retire-v10"

    assert 'window.__qnbotInjectVersion = "' + marker + '";' in inject
    assert 'private const string injectVersionMarker = "' + marker + '";' in qn_inject


def test_websocket_startup_never_reports_success_for_failed_setup_or_start():
    server = text("src/Bot/ChromeNs/MyWebSocketServer.cs")

    assert "private WebSocketServer _webSocketServer;" in server
    assert "if (!webSocket.Setup(config))" in server
    assert "if (!webSocket.Start())" in server
    assert "_webSocketServer = webSocket;" in server

    success_index = server.index('Log.Info("Bot WebSocket服务已启动: 127.0.0.1:41010")')
    setup_index = server.index("if (!webSocket.Setup(config))")
    start_index = server.index("if (!webSocket.Start())")
    root_index = server.index("_webSocketServer = webSocket;")
    assert setup_index < start_index < root_index < success_index


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
