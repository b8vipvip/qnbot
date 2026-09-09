from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_server_never_physically_retires_duplicate_webviews():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    guard = read("src/Bot/ChromeNs/WebSocketPageLifecycleGuard.cs")

    close_helper = server[server.index("private void ScheduleDuplicateSessionClose"):server.index("private void EnforceDuplicateSellerSessionCapLocked")]
    assert 'method = "retireDuplicate"' not in close_helper
    assert ".Close()" not in close_helper
    assert "physicalClose=false" in close_helper
    assert "_standbyLoggedSessions" in server

    assert 'method = "retireDuplicate"' not in guard
    assert ".Close()" not in guard
    assert "保持为轻量standby，不关闭通道" in guard


def test_standby_promotion_forces_rebind_even_when_status_text_is_unchanged():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    status = server[server.index('if (wMsg.Type == "qnbotStatus")'):server.index('else if (wMsg.Type == "imsdkApiScan")')]

    assert "var wasInitialized = _initialized.ContainsKey(session.SessionID);" in status
    assert "var wasAuthoritative" in status
    assert "IsAuthoritativeSellerSession(loginNick, session.SessionID)" in status
    assert "else if (!wasAuthoritative && !string.IsNullOrWhiteSpace(loginNick))" in status
    assert "千牛standby页面接管权威会话，强制重建客服/CDP绑定" in status
    promotion = status[status.index("else if (!wasAuthoritative"):]
    assert "Task.Run(() => TryBindStatusConversation(session, loginNick, conversationNick));" in promotion


def test_first_authoritative_page_still_runs_full_cdp_initialization():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    status = server[server.index('if (wMsg.Type == "qnbotStatus")'):server.index('else if (wMsg.Type == "imsdkApiScan")')]
    assert "else if (!wasInitialized)" in status
    assert 'Task.Run(() => TryInitSession(session, "status"));' in status


def test_injected_page_still_reconnects_after_normal_socket_close():
    inject = read("src/Bin/inject.js")
    assert "function scheduleReconnect()" in inject
    assert "setupWebSocket();" in inject
    assert "socket.onclose = function ()" in inject
    assert "if (!websocketRetired) scheduleReconnect();" in inject
    assert 'sendStatus("websocket-open", true);' in inject


def test_auto_update_recovery_remains_non_destructive():
    recovery = read("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")
    assert "TryRestartQianniuOnceAsync" not in recovery
    assert "Process.Start" not in recovery
    assert "Mouse.Click" not in recovery
    assert "不自动重启千牛" in recovery
