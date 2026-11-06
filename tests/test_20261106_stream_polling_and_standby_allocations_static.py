"""Regression checks for log-correlated, low-risk memory allocation fixes.

Do not optimize away cancellation, business message routing, or reconnect leases.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def src(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_stream_response_avoids_120ms_polling_allocations():
    stream = src("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    # One never-completing cancellation task per stream/body request, rather
    # than building Task.Delay(120, token) each time a read stays pending.
    assert stream.count("Task.Delay(Timeout.Infinite, token)") >= 2
    assert "Task.Delay(120, token)" not in stream
    assert "Task.WhenAny(pendingRead, cancelled)" in stream
    assert "token.ThrowIfCancellationRequested();" in stream
    assert "throw new OperationCanceledException(token);" in stream
    assert "using (var response = await Http.SendAsync(" in stream
    assert "using (var stream = await response.Content.ReadAsStreamAsync())" in stream
    assert "using (var reader = new StreamReader(stream, Encoding.UTF8))" in stream
    assert "return Ok(sw, payloadText, answer);" in stream


def test_standby_page_observation_is_scheduled_once_per_candidate():
    guard = src("src/Bot/ChromeNs/WebSocketPageLifecycleGuard.cs")
    assert "public int ObservationScheduled;" in guard
    assert "Interlocked.CompareExchange(ref candidate.ObservationScheduled, 1, 0)" in guard
    assert "InertCandidates.TryRemove(sessionId, out ignoredCandidate);" in guard
    assert "InertCandidates.TryRemove(sessionId, out removed)" in guard
    assert "physicalClose=false" in guard
    assert "RequestImmediateHistoryProbe" in guard


def test_live_websocket_and_reply_safety_still_owned_by_existing_protocol():
    server = src("src/Bot/ChromeNs/MyWebSocketServer.cs")
    stream = src("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    assert "TryPromoteDuplicateSellerSession" in server
    assert "WebSocketPageLifecycleGuard.ForgetClosedSession(session.SessionID);" in server
    assert "await ProcessTextBurstStreamingAsync(qn, lease);" in stream
    assert "if (!lease.IsCurrent)" in stream
    assert "lease.MarkSending(" in stream
