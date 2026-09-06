from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_business_send_authority_is_not_owned_by_progress_ui():
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    rpa = read("src/Bot/ChromeNs/QNRpa.cs")
    tracker = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert "internal static class ReliableSendAuthority" in order
    assert "AsyncLocal<ScopeState>" in order
    assert "BeginBusinessCritical(" in order
    assert "order_action_ledger" in order
    assert "ReliableSendAuthority.IsProtectedFromBuyerUpdate" in rpa
    assert 'SetSendCancellation(stage, "买家已发送更新消息，旧答案不会发送")' in rpa
    assert "ResponseProgressTracker.IsMandatoryOrderAnswer" not in rpa
    assert "IsMandatoryOrderAnswer" not in tracker


def test_ai_response_body_inherits_generation_cancellation():
    ai = read("src/Bot/ChromeNs/MyOpenAI.cs")
    assert "ReadResponseBodyWithCancellationAsync" in ai
    assert "HttpCompletionOption.ResponseHeadersRead" in ai
    assert "stream.ReadAsync(" in ai
    assert "chunk, 0, chunk.Length, cancellationToken" in ai
    assert "CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)" in ai
    assert "deadline.CancelAfter(TimeSpan.FromSeconds(effectiveTimeout))" in ai
    raw = ai[ai.index("private static StructuredChatResult CallRawChatCompletions(AiEndpointConfig endpoint, JArray messages, int maxTokens, double temperature, int timeoutSeconds"):]
    assert "response.Content.ReadAsStringAsync()" not in raw


def test_duplicate_websocket_recovery_channels_are_bounded_and_reaped():
    ws = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    assert "MaxDuplicateSellerSessions = 3" in ws
    assert "DuplicateSessionIdleTimeout = TimeSpan.FromMinutes(4)" in ws
    assert "EnforceDuplicateSellerSessionCapLocked(sellerNick)" in ws
    assert "StartDuplicateSessionSweeper()" in ws
    assert "duplicate_cap" in ws
    assert "duplicate_idle_timeout" in ws
    assert "_liveSessions.TryRemove(session.SessionID" in ws
    assert "_sessionLastActivityUtc.TryRemove(session.SessionID" in ws
