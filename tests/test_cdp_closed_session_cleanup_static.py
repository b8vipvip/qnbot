from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
CDP = ROOT / "src" / "Bot" / "ChromeNs" / "CDPClient.cs"
WS = ROOT / "src" / "Bot" / "ChromeNs" / "MyWebSocketServer.cs"


def test_closed_websocket_releases_cdp_runtime_state():
    cdp = CDP.read_text(encoding="utf-8-sig")
    ws = WS.read_text(encoding="utf-8-sig")

    assert "ReleaseClosedSession" in cdp
    assert "SessionClients.TryRemove(sessionId" in cdp
    assert "PreferredShopSessions" in cdp
    assert "CancelPendingWaiters();" in cdp
    assert "_webSocketSession = null;" in cdp

    assert "webSocket.SessionClosed" in ws
    assert "_clients.TryRemove(session.SessionID, out removed);" in ws
    assert "CDPClient.ReleaseClosedSession(session.SessionID" in ws


def test_timeout_invalidation_also_removes_static_session_registration():
    cdp = CDP.read_text(encoding="utf-8-sig")
    invalidate = cdp.split("private void InvalidateSession", 1)[1]

    assert "SessionClients.TryRemove(sessionId" in invalidate
    assert "CancelPendingWaiters();" in invalidate
    assert "socket.Close();" in invalidate
