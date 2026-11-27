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
    assert "FindAuthoritativeShopSendOwner(expectedShopKey)" in helper
    assert "已阻止跨店发送并等待同店权威会话接管" in helper
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
    status_bind = server[
        server.index("private async Task TryBindStatusConversation"):
        server.index("private async Task TryInitSession")
    ]
    assert "connectionSnapshot.AccessibilityStatus" in status_bind
    assert "connectionSnapshot.ButtonStatus" in status_bind
    assert "RefreshRpaAfterAuthoritativeSessionAsync(qn, shopKey, session.SessionID)" in status_bind
    assert "IsAuthoritativeShopSession(shopKey, sessionId)" in refresh
    assert "RefreshChatControlsAsync(true)" in refresh

    rpa = read("src/Bot/ChromeNs/QNRpa.ReliableSend.cs")
    invalid_hwnd = rpa[
        rpa.index("if (uia3Automation == null || sellerDesk.Hwnd == null"):
        rpa.index("var expectedHwnd", rpa.index("if (uia3Automation == null || sellerDesk.Hwnd == null"))
    ]
    assert "RecordRpaScan(false, false, \"seller HWND 无效\")" in invalid_hwnd
    assert "UIA输入框已识别但发送按钮未识别" in rpa


def test_manual_and_local_short_rules_precede_first_inquiry_and_image_fallback_is_preserved():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    canonical = coordinator[
        coordinator.index("internal static class CanonicalPreMergeDecisionService"):
        coordinator.index("internal sealed class BuyerMessageBurstCoordinator")
    ]
    manual = canonical.index("var manualDecision = BotFeatureStore.EvaluateAutoReplyRule(question)")
    local = canonical.index("LocalShortReplyService.TryResolve(")
    first = canonical.index("FirstInquiryFixedReplyService.TryResolve(")
    assert manual < local < first
    assert "manualRuleMatched" in canonical
    assert "FirstInquiryFixedReplyService.ReleaseReservation(" in canonical[manual:local]
    assert "firstReserved" in canonical[first:]
    assert "failedBeforeSend" in canonical[first:]
    assert "sendIsIndeterminate" in canonical[first:]
    assert "var failedBeforeSend = !sendIsIndeterminate" in canonical[first:]
    assert 'string.Equals(question, "[图片]"' in canonical[first:]
    assert "已释放首条资格并继续当前消息的后续视觉理解/AI" in canonical[first:]
    assert "if (firstOutcome == CanonicalPreMergeOutcome.Suppressed) return firstOutcome;" in canonical[first:]


def test_send_gate_rebinds_to_current_authoritative_qn_for_same_shop_before_first_physical_send():
    qn = read("src/Bot/ChromeNs/QN.cs")
    send = qn[
        qn.index("public async Task<bool> SendTextWithRetryAsync"):
        qn.index("private bool IsPreSendGuardSatisfied")
    ]

    assert "FindAuthoritativeShopSendOwner(expectedShopKey)" in send
    assert "ShopContextLocator.FindConnectedQnByShopKey(expectedShopKey)" in send
    assert "server.IsAuthoritativeShopSession(expectedShopKey, client.SessionId)" in send
    assert "TryHandoffToAuthoritativeShopQnAsync(" in send
    assert "ReleaseSendGate(ref sendGateHeld" in send
    assert "shopOwnerHandoffDepth < 1" in send

    first_physical_send = send.index("var ok = await SendTextAsync(buyer, text, preSendGuard);")
    pre_send_handoff = send.index("TryHandoffToAuthoritativeShopQnAsync(", send.index("WaitForCurrentShopAuthorityAsync("))
    assert pre_send_handoff < first_physical_send
    assert "owner.SendTextWithRetryAsync(" in send
    assert "同ShopKey权威会话转交" in send


def test_premerge_waits_for_authoritative_same_shop_before_running_fixed_rules():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    method = source.split("private static async Task<CanonicalPreMergeOutcome> HandleScopedAsync", 1)[1].split(
        "private static bool HasNewerSubstantiveConversationTurn", 1
    )[0]
    resolve = method.index("ShopContextLocator.FindConnectedQnByShopKey(item.ShopKey)")
    wait = method.index("while (connected == null && deadline.ElapsedMilliseconds < 5000)")
    lookup = method.index("ShopContextLocator.FindConnectedQnByShopKey(item.ShopKey)", wait)
    gate = method.index("var qn = connected;", lookup)
    assert resolve < wait < lookup < gate < method.index("ManualRechargeAssistantService.TryHandleAsync")
    assert "cancellationToken.ThrowIfCancellationRequested()" in method[wait:gate]
    assert "Task.Delay(150, cancellationToken)" in method[wait:lookup]
    assert "QN.CurQN" not in method


def test_policy_suppression_does_not_mark_first_reply_delivered_or_fall_through_to_ai():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    first = source.split("var firstReserved =", 1)[1].split(
        "private static bool HasNewerSubstantiveConversationTurn", 1
    )[0]
    assert first.index("firstOutcome == CanonicalPreMergeOutcome.Consumed") < first.index(
        "FirstInquiryFixedReplyService.MarkDelivered"
    ) < first.index("FirstInquiryFixedReplyService.ReleaseReservation") < first.index(
        "if (firstOutcome == CanonicalPreMergeOutcome.Suppressed) return firstOutcome;"
    )
    send = source.split("private static async Task<CanonicalPreMergeOutcome> SendFixedAsync", 1)[1].split(
        "private static bool TryResolveOffHours", 1
    )[0]
    before_send = send.split("cancellationToken.ThrowIfCancellationRequested();", 1)[0]
    assert "ShopReceptionPolicy.BlockOutgoing" in before_send
    assert "return CanonicalPreMergeOutcome.Suppressed" in before_send
    assert "CanonicalPreMergeOutcome.Consumed" not in before_send
    guard = send.split("Func<bool> preSendGuard = () =>", 1)[1].split("var ok = await", 1)[0]
    assert "ShopReceptionPolicy.BlockOutgoing" in guard
    assert "policySuppressed = true" in guard
    assert "if (!ok && policySuppressed)" in send
    assert "canonical_pre_merge_policy_suppressed" in source


def test_delayed_fixed_reply_does_not_discard_valid_shop_when_old_cdp_disappears():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    send = source.split("private static async Task<CanonicalPreMergeOutcome> SendFixedAsync", 1)[1].split(
        "private static bool TryResolveOffHours", 1
    )[0]
    delay = send.split("if (boundedDelay > 0)", 1)[1].split("Func<bool> preSendGuard", 1)[0]
    assert "qn.CDP" not in delay
    assert "verifiedShop.ShopKey, item.ShopKey" in delay
    assert "Params.Robot.GetIsAutoReply()" in delay
    assert "await qn.SendTextWithRetryAsync(" in send
