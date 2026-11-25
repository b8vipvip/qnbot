from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_reply_send_waits_for_same_shop_authoritative_cdp_rebind():
    qn = read("src/Bot/ChromeNs/QN.cs")
    send = qn[
        qn.index("public async Task<bool> SendTextWithRetryAsync"):
        qn.index("private bool IsPreSendGuardSatisfied")
    ]
    helper = qn[
        qn.index("private async Task<string> WaitForCurrentShopAuthorityAsync"):
        qn.index("private async Task<bool> EnsureActiveBuyerForSendAsync")
    ]

    assert send.count("WaitForCurrentShopAuthorityAsync(") >= 2
    assert send.index("WaitForCurrentShopAuthorityAsync(") < send.index("EnsureActiveBuyerForSendAsync")
    assert "ShopSettingsScope.Current" in helper
    assert "client.ShopKey" in helper
    assert "server.IsAuthoritativeShopSession(expectedShopKey, client.SessionId)" in helper
    assert "Task.Delay(Math.Min(150, remainingMs), cancellationToken)" in helper
    assert "QN.CurQN" not in helper
    assert "已阻止跨店发送" in helper


def test_authoritative_cdp_reconnect_refreshes_uia_and_invalid_hwnd_clears_stale_status():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    init = server[
        server.index("private async Task TryInitSession"):
        server.index("private async Task RefreshRpaAfterAuthoritativeSessionAsync")
    ]
    refresh = server[
        server.index("private async Task RefreshRpaAfterAuthoritativeSessionAsync"):
        server.index("public class ConnectionDiagnosticsSnapshot")
    ]
    assert "RefreshRpaAfterAuthoritativeSessionAsync(qn, shopKey, session.SessionID)" in init
    assert "IsAuthoritativeShopSession(shopKey, sessionId)" in refresh
    assert "RefreshChatControlsAsync(true)" in refresh

    rpa = read("src/Bot/ChromeNs/QNRpa.ReliableSend.cs")
    invalid_hwnd = rpa[
        rpa.index("if (uia3Automation == null || sellerDesk.Hwnd == null"):
        rpa.index("var expectedHwnd", rpa.index("if (uia3Automation == null || sellerDesk.Hwnd == null"))
    ]
    assert "RecordRpaScan(false, false, \"seller HWND 无效\")" in invalid_hwnd
    assert "UIA输入框已识别但发送按钮未识别" in rpa


def test_failed_first_greeting_does_not_swallow_short_reply_or_image_vision():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    short_replies = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    local_start = short_replies.index("internal static class LocalShortReplyService")
    local_end = short_replies.index("namespace Bot.Knowledge", local_start)
    local = short_replies[local_start:local_end]

    assert "在么|在吗" in local
    assert "var firstDelivered = false;" in coordinator
    assert "firstDelivered = true;" in coordinator
    assert "failedBeforeSend" in coordinator
    assert 'string.Equals(question, "[图片]"' in coordinator
    assert "已释放首条资格并继续当前消息的后续规则/视觉理解" in coordinator
    assert "LocalShortReplyService.TryResolve(" in coordinator
    assert "if (firstDelivered)" in coordinator
    assert "return firstOutcome;" in coordinator
