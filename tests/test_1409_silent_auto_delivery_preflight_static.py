from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_auto_delivery_silently_queries_exact_trade_before_any_foreground_navigation():
    src = read("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    method = src[src.index("internal async Task<AutoDeliveryAttemptResult> TryExecuteVirtualGoodsAutoDeliveryAsync"):
                 src.index("private async Task<AutoDeliveryPreflightResult> TrySilentAutoDeliveryPreflightAsync")]

    preflight = method.index("TrySilentAutoDeliveryPreflightAsync(snapshot)")
    input_probe = method.index("TryGetInputboxEmptyAsync", preflight)
    buyer_probe = method.index("TryGetCurrentBuyerAsync", input_probe)
    open_chat = method.index("OpenChat(expectedBuyer)", buyer_probe)
    assert preflight < input_probe < buyer_probe < open_chat
    assert "if (preflight.Kind != AutoDeliveryPreflightKind.Candidate)" in method
    assert "不切换前台买家且绝不再次确认发货" in method


def test_silent_preflight_uses_contact_search_and_exact_trade_query_without_openchat():
    src = read("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    helper = src[src.index("private async Task<AutoDeliveryPreflightResult> TrySilentAutoDeliveryPreflightAsync"):
                 src.index("private async Task<string> ResolveAutoDeliverySecurityBuyerUidAsync")]
    identity = src[src.index("private async Task<string> ResolveAutoDeliverySecurityBuyerUidAsync"):
                   src.index("private static bool TradeContainsExactOrder")]

    assert "GetBuyerTrades(securityBuyerUid, orderId)" in helper
    assert "TradeContainsExactOrder(x, orderId)" in helper
    assert "trade.consignTime.HasValue" in helper
    assert "trade.endTime.HasValue" in helper
    assert "trade.riskOrder" in helper
    assert "x.refundStatus != 0" in helper
    assert "trade.payTime.HasValue" in helper
    assert "foregroundChanged=false" in helper
    assert "OpenChat(" not in helper
    assert "SetActiveConversationByNick(" not in helper
    assert "TryGetCurrentBuyerAsync" not in helper
    assert "TryGetInputboxEmptyAsync" not in helper

    assert "SearchBuyerUser(buyer)" in identity
    assert "EncryptAccountId" in identity
    assert "AutoDeliveryBuyerIdentityCacheTtl = TimeSpan.FromMinutes(20)" in src


def test_only_safe_candidate_commits_one_buyer_switch_then_revalidates_rendered_order():
    src = read("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    method = src[src.index("internal async Task<AutoDeliveryAttemptResult> TryExecuteVirtualGoodsAutoDeliveryAsync"):
                 src.index("private async Task<AutoDeliveryPreflightResult> TrySilentAutoDeliveryPreflightAsync")]

    candidate_gate = method.index("preflight.Kind != AutoDeliveryPreflightKind.Candidate")
    open_chat = method.index("OpenChat(expectedBuyer)")
    rendered = method.index("WaitForOrderStateAsync(snapshot.OrderId, TimeSpan.FromSeconds(3))")
    click_ship = method.index("ReadOrderStateAsync(snapshot.OrderId, true)")
    durable_barrier = method.index("AutoDeliveryCoordinator.TryPersistConfirmationIntent")
    confirm = method.index('ReadModalStateAsync("confirm")')

    assert candidate_gate < open_chat < rendered < click_ship < durable_barrier < confirm
    assert method.count("OpenChat(expectedBuyer)") == 1
    assert "静默预检已命中安全候选，现仅为实际发货切换一次目标买家" in method


def test_silent_preflight_failures_back_off_without_foreground_oscillation():
    src = read("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    assert 'reason.IndexOf("静默预检", StringComparison.Ordinal) >= 0' in src
    assert "ConversationNavigationRetryDelay = TimeSpan.FromMinutes(2)" in src
    assert "DeferSellerNavigationRecords(record, ConversationNavigationRetryDelay, result.Reason)" in src
    assert "静默预检未找到准确订单，不切换聊天窗口" in src
    assert "静默预检尚未取得已付款证据，不切换聊天窗口" in src


def test_silent_preflight_is_fail_closed_and_final_ui_revalidation_remains_authoritative():
    src = read("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    assert "paid=true, consigned=false, terminal=false, foregroundChanged=false" in src
    assert "Silent API evidence is" in src
    assert "never enough to authorize an irreversible click on its own" in src
    assert "if (!before.Pending)" in src
    assert "if (!before.ShipButton)" in src
    assert "ReadOrderStateAsync(snapshot.OrderId, true)" in src
    assert "AutoDeliveryCoordinator.TryPersistConfirmationIntent" in src
