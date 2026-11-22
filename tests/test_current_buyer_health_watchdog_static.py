from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_runtime_monitor_actively_polls_and_stably_repairs_current_buyer():
    source = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    assert "ConversationProbeIntervalMilliseconds = 2500" in source
    assert "ProbeCurrentConversationAsync" in source
    assert source.count("await qn.GetCurrentConversationID()") >= 2
    assert "await Task.Delay(220)" in source
    assert '"runtimeConversationProbe"' in source
    assert "BuyerIdentityAliasService.AreEquivalent" in source
    assert "当前买家由主动探测修正" in source
    assert "SetActiveConversationByNick" in source
    assert "人工回复只作为学习证据，不取消Bot任务" in source


def test_runtime_monitor_emits_periodic_liveness_heartbeat_and_failure_details():
    source = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    assert "HeartbeatIntervalSeconds = 60" in source
    assert "Bot运行心跳:" in source
    assert "当前买家主动探测失败" in source
    assert "当前买家主动探测已恢复" in source
    assert "diagnosticsAgeSeconds=" in source


def test_cdp_execute_requests_are_serialized_and_timeout_invalidates_session():
    source = text("src/Bot/ChromeNs/CDPClient.cs")

    assert "SemaphoreSlim _executeGate" in source
    assert "ExecuteGateWaitTimeoutMs = 1500" in source
    assert "await _executeGate.WaitAsync(effectiveGateWaitTimeoutMs).ConfigureAwait(false)" in source
    assert "gateWaitTimeoutMs = ExecuteGateWaitTimeoutMs" in source
    assert "HistoryGateWaitTimeoutMs = 10000" in source
    assert "CDP调用等待串行门超时，已快速失败避免排队放大" in source
    assert "if (gateAcquired) _executeGate.Release();" in source
    assert "CDP调用超时" in source
    assert "InvalidateSession(\"调用超时:" in source

    # Invalidation must make the shared field unusable before closing the captured
    # socket, and must release every other registration/waiter that could keep the
    # stale WebView session alive. Do not regress to closing the field in place.
    assert "var socket = _webSocketSession;" in source
    assert "_webSocketSession = null;" in source
    assert "if (socket != null) socket.Close();" in source
    assert "SessionClients.TryRemove(sessionId" in source
    assert "CancelPendingWaiters();" in source
    assert "CDP会话已失效并请求WebSocket重连" in source


def test_even_non_generic_execute_calls_consume_their_response_channel():
    source = text("src/Bot/ChromeNs/CDPClient.cs")

    assert "private string SendExecuteAndWait" in source
    assert 'SendExecuteAndWait(cmd, "Invoke:" + apiName)' in source
    assert 'SendExecuteAndWait(cmd, "InvokeMTop:" + apiName)' in source
    assert "旧实现允许多个异步调用并发" in source


def test_runtime_session_snapshot_consumers_match_the_list_contract():
    contract = text("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    assert "internal static List<QN> GetRuntimeSafetySnapshot()" in contract

    for path in (
        "src/Bot/ChromeNs/BusinessInboundLivenessRecoveryBridge.cs",
        "src/Bot/Knowledge/KnowledgeCenterWindow.cs",
        "src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs",
    ):
        source = text(path)
        assert "GetRuntimeSafetySnapshot() ?? new QN[0]" not in source
        assert "CleanupRetiredQnState(IEnumerable<QN> sessions)" in source
        assert "sessions ?? Enumerable.Empty<QN>()" in source

    monitor = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    assert '", monitoredQn=" + QN.GetRuntimeSafetySnapshot().Count' in monitor
