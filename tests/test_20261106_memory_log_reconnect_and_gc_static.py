"""Regression contracts for 2026-11-06 field-log-backed client memory fixes.

These tests intentionally guard delivery/connection recovery, so a later
optimization cannot silently trade lower RAM for dropped buyer messages.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def src(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_duplicate_log_history_is_bounded_and_thread_safe():
    logger = src("src/BotLib/LogWriter.cs")
    assert "MaxWriteCountKeys = 2048;" in logger
    assert "lock (_writeCountSync)" in logger
    assert "_writeCountOrder.Dequeue()" in logger
    assert "_wcache.Remove(" in logger
    assert "if (text.Length > 8192) return 0;" in logger
    assert "GC.GetTotalMemory(false)" in logger
    assert "GC.GetTotalMemory(true)" not in logger


def test_closed_websocket_cannot_leave_static_page_guard_references():
    guard = src("src/Bot/ChromeNs/WebSocketPageLifecycleGuard.cs")
    server = src("src/Bot/ChromeNs/MyWebSocketServer.cs")
    assert "internal static void ForgetClosedSession(string sessionId)" in guard
    assert "SessionSellers.TryRemove(sessionId, out ignoredSeller);" in guard
    assert "StandbyLogged.TryRemove(sessionId, out ignoredStandby);" in guard
    assert "InertCandidates.TryRemove(sessionId, out ignoredCandidate);" in guard
    assert "WebSocketPageLifecycleGuard.ForgetClosedSession(session.SessionID);" in server
    assert "webSocket.SessionClosed += (session, value) =>" in server
    # Keep the deliberate standby/resumption safety net for actual live pages.
    assert "physicalClose=false" in server
    assert "IsAuthoritativeSellerSession" in server
    assert "RequestImmediateHistoryProbe" in guard


def test_stale_continuation_cache_is_pruned_without_erasing_active_context():
    receive = src("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "SemanticContinuationWindowSeconds = 180;" in receive
    assert "SoftRecentBuyerTextLimit = 512;" in receive
    assert "RecentBuyerTextMaxIdle = TimeSpan.FromMinutes(30);" in receive
    assert "SweepRecentBuyerTextsIfDue();" in receive
    assert "SemanticContinuationWindowSeconds + 60" in receive
    assert "LastTouchedUtc = DateTime.UtcNow" in receive
    assert "ICollection<KeyValuePair<string, RecentBuyerText>>" in receive
    assert "Remove(pair);" in receive


def test_memory_samples_report_gc_velocity_and_guard_count_without_forcing_gc():
    monitor = src("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    assert "gen2SinceLastSample=" in monitor
    assert "wsGuardSessionRefs=" in monitor
    assert "WebSocketPageLifecycleGuard.TrackedSessionCount" in monitor
    assert "GC.GetTotalMemory(false)" in monitor
    assert "GC.Collect(" not in monitor


def test_heartbeat_hot_path_skips_deserialization_but_touches_liveness():
    server = src("src/Bot/ChromeNs/MyWebSocketServer.cs")
    marker = 'if (string.Equals(value, "{\\\"type\\\":\\\"hi\\\"}", StringComparison.Ordinal))'
    assert marker in server
    assert server.index("TouchSession(session);", server.index("webSocket.NewMessageReceived"))
           < server.index(marker)
           < server.index("JsonConvert.DeserializeObject<WSocketMessage>(value)")
    # Other inbound message types and the existing fallback still go through
    # the established business dispatch and legacy heartbeat check.
    assert 'if (wMsg == null || wMsg.Type == "hi") return;' in server
    assert 'OnRecieveMessage(session, new WSocketNewMessageEventArgs(wMsg.Type, wMsg.Response))' in server
