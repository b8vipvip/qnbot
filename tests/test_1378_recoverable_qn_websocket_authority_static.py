from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_server_uses_recoverable_offline_standby_per_shopkey():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    guard = read("src/Bot/ChromeNs/WebSocketPageLifecycleGuard.cs")

    close_helper = server[
        server.index("private void ScheduleDuplicateSessionClose"):
        server.index("private void EnforceDuplicateShopSessionCapLocked")
    ]
    cap_helper = server[
        server.index("private void EnforceDuplicateShopSessionCapLocked"):
        server.index("private bool TryClaimShopSession")
    ]
    assert 'method = "retireDuplicate"' in close_helper
    assert "var retryAfterMs = 300000 +" in close_helper
    assert "sessionId.GetHashCode() % 240000U" in close_helper
    assert "session.Send(command)" in close_helper
    assert 'DiagnosticRef("shop", shopKey)' in close_helper
    assert "_standbyLoggedSessions" in server
    assert "_recoverableStandbySessions" in server
    assert "_duplicateShopSessions" in cap_helper
    assert "string.Equals(x.Value, shopKey, StringComparison.Ordinal)" in cap_helper
    assert "ScheduleDuplicateSessionClose(victim, shopKey" in cap_helper

    assert 'method = "retireDuplicate"' not in guard
    assert ".Close()" not in guard
    assert "保持为轻量standby，不关闭通道" in guard


def test_status_arbitration_uses_exact_shopkey_and_rebinds_authority():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    status = server[
        server.index('if (wMsg.Type == "qnbotStatus")'):
        server.index('else if (wMsg.Type == "imsdkApiScan")')
    ]

    assert "var wasInitialized = _initialized.ContainsKey(session.SessionID);" in status
    assert "_sessionShopKeys.TryGetValue(session.SessionID, out sessionShopKey)" in status
    assert "IsAuthoritativeShopSession(sessionShopKey, session.SessionID)" in status
    assert "else if (!wasAuthoritative)" in status
    assert "同一ShopKey的重复千牛WebSocket页面" in status
    assert 'Task.Run(() => TryInitSession(session, "status", identityProbeGeneration));' in status
    assert "var bindingGeneration = ShouldRefreshStatusBinding(" in status
    assert "Task.Run(() => TryBindStatusConversation(\n                                                    session, loginNick, conversationNick, bindingGeneration, identityProbeGeneration));" in status
    assert "TryClaimShopSession(loginNick" not in status


def test_authority_promotion_reapplies_duplicate_cap_per_shopkey():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    promotion = server[
        server.index("private async Task<bool> TryPromoteDuplicateShopSessionAsync"):
        server.index("internal string GetShopKeyForSession")
    ]

    assert "_duplicateShopSessions[previousOwner] = shopKey;" in promotion
    assert "_duplicateShopSessions.TryRemove(sessionId, out ignored);" in promotion
    assert "EnforceDuplicateShopSessionCapLocked(shopKey);" in promotion
    assert promotion.index("_duplicateShopSessions[previousOwner] = shopKey;") < promotion.index(
        "_duplicateShopSessions.TryRemove(sessionId, out ignored);"
    )
    assert promotion.index("_duplicateShopSessions.TryRemove(sessionId, out ignored);") < promotion.index(
        "EnforceDuplicateShopSessionCapLocked(shopKey);"
    )
    assert "await client.GetCurrentUser().ConfigureAwait(false)" in promotion
    assert "ResolveRuntimeBySellerIdentity(user.Result)" in promotion
    assert "verifiedShop.ShopKey, shopKey" in promotion
    assert "TryClaimShopSession(verifiedShop.ShopKey, sessionId, expectedIdentityProbeGeneration)" in promotion
    assert "QN.GetByShopIdentity(user.Result)" in promotion
    assert "CDPClient.PreferRuntimeSession(" in promotion


def test_first_page_initializes_from_its_own_cdp_login_identity():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    status = server[
        server.index('if (wMsg.Type == "qnbotStatus")'):
        server.index('else if (wMsg.Type == "imsdkApiScan")')
    ]
    init = server[server.index("private async Task TryInitSession"):]
    assert "if (!wasInitialized)" in status
    assert 'Task.Run(() => TryInitSession(session, "status", identityProbeGeneration));' in status
    assert "ResolveRuntimeBySellerIdentity(user.Result)" in init
    assert "QN.GetByShopIdentity(user.Result)" in init
    assert "TryClaimShopSession(shopKey, session.SessionID, identityProbeGeneration, () =>" in init
    assert "cdp.ShopKey = shopKey;" in init


def test_injected_page_still_reconnects_after_normal_socket_close():
    inject = read("src/Bin/inject.js")
    assert "function scheduleReconnect(delayMs)" in inject
    assert "setupWebSocket();" in inject
    assert "socket.onclose = function ()" in inject
    assert "scheduleReconnect();" in inject
    assert "websocketStandbyUntil" in inject
    assert "reconnectDelayMs = 3000;" in inject
    assert "pendingStatus = payload;" in inject
    assert 'var businessEvent = type !== "qnbotStatus" && !diagnosticOnly;' in inject
    assert "scheduleReconnect(retryAfterMs);" in inject
    assert 'sendStatus("websocket-open", true);' in inject


def test_auto_update_recovery_remains_non_destructive():
    recovery = read("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")
    assert "TryRestartQianniuOnceAsync" not in recovery
    assert "Process.Start" not in recovery
    assert "Mouse.Click" not in recovery
    assert "不自动重启千牛" in recovery

def test_authoritative_shop_session_requires_live_physical_websocket():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    authority = server[
        server.index("internal bool IsAuthoritativeShopSession"):
        server.index("private void ReleaseShopSession")
    ]
    closed = server[
        server.index("webSocket.SessionClosed += (session, value) =>"):
        server.index("var config = new ServerConfig()", server.index("webSocket.SessionClosed += (session, value) =>"))
    ]

    assert "_connectedSessions.ContainsKey(sessionId)" in authority
    assert "_connectedSessions.TryRemove(session.SessionID, out _);" in closed
    assert "ReleaseShopSession(session.SessionID);" in closed
    assert closed.index("_connectedSessions.TryRemove(session.SessionID, out _);") < closed.index(
        "ReleaseShopSession(session.SessionID);"
    )


def test_duplicate_retirement_cannot_close_a_promoted_or_rebound_shop_session():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    retire = server[
        server.index("private void ScheduleDuplicateSessionClose"):
        server.index("private void EnforceDuplicateShopSessionCapLocked")
    ]
    promote = server[
        server.index("private async Task<bool> TryPromoteDuplicateShopSessionAsync"):
        server.index("internal string GetShopKeyForSession")
    ]
    available = server[
        server.index("private async Task TryPromoteAvailableStandbyAsync"):
        server.index("private bool TryReserveStatusBindingRefresh")
    ]

    assert "lock (_sellerSessionSync)" in retire
    assert "_connectedSessions.ContainsKey(sessionId)" in retire
    assert "_duplicateShopSessions.TryGetValue(sessionId, out duplicateShopKey)" in retire
    assert "_sessionShopKeys.TryGetValue(sessionId, out mappedShopKey)" in retire
    assert "_shopSessions.TryGetValue(shopKey, out currentOwner)" in retire
    assert "_standbyLoggedSessions.TryAdd(sessionId, shopKey)" in retire
    assert "_standbyLoggedSessions.ContainsKey(sessionId)" in promote
    assert "!_standbyLoggedSessions.ContainsKey(x.Key)" in available

    assert "ConcurrentDictionary<string, string> _standbyLoggedSessions" in server
    assert "TryGetValue(sessionId, out pendingShopKey)" in retire
    assert "string.Equals(pendingShopKey, shopKey, StringComparison.Ordinal)" in retire
    assert "session.Send(command);" in retire
    assert "_standbyLoggedSessions.TryRemove(sessionId, out ignoredRetirementShop);" in server
