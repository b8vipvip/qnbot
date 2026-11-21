from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_cdp_exposes_bounded_expression_evaluation_without_new_transport():
    code = read("src/Bot/ChromeNs/CDPClient.cs")
    assert "internal Task<string> EvaluateExpressionAsync" in code
    helper = code[code.index("internal Task<string> EvaluateExpressionAsync"):]
    helper = helper[: helper.index("private string SendExecuteAndWait")]
    assert "SendExecuteAndWaitAsync(" in helper
    assert "InvokeTimeoutMs = 8000" in code


def test_direct_order_bridge_scans_visible_panel_on_background_notify_and_buyer_switch():
    code = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    assert "qn.EvBuyerSwitched += OnBuyerSwitched;" in code
    assert 'ScheduleVisibleOrderPanelScan(qn, seller, buyer, "shopRobotNotify")' in code
    assert '"buyerSwitched"' in code
    assert "VisiblePanelScanDelaysMs" in code
    for delay in ["250", "900", "1800", "3200", "5200", "8000"]:
        assert delay in code


def test_visible_panel_dom_reader_is_anchor_scoped_and_strict_about_order_ids():
    code = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    assert "近3个月订单" in code
    assert "近三个月订单" in code
    assert r"\d{16,24}" in code
    assert "doc.createTreeWalker(root,4,null,false)" in code
    assert "text.length>16000" in code
    assert "iframe,frame" in code


def test_visible_panel_publish_is_fail_closed_on_seller_and_buyer_identity():
    code = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    method = code[code.index("TryRecoverVisibleOrderPanelAsync"):]
    method = method[: method.index("private static string ExtractVisibleOrderPanelText")]
    assert "DirectOrderIdentityResolver.IdentityEquals(runtimeSeller, sellerHint)" in method
    assert method.count("GetCurrentConversationID()") >= 2
    assert "BuyerIdentityAliasService.AreEquivalent(runtimeSeller, before.Nick, buyerHint)" in method
    assert "BuyerIdentityAliasService.AreEquivalent(runtimeSeller, after.Nick, verifiedBuyer)" in method
    assert "DOM读取期间当前买家发生变化" in method


def test_visible_panel_only_publishes_fresh_verifiable_orders_into_existing_hub():
    code = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    method = code[code.index("TryRecoverVisibleOrderPanelAsync"):]
    method = method[: method.index("private static string ExtractVisibleOrderPanelText")]
    assert "_messageSafetyStartedAt.AddSeconds(-8)" in method
    assert "candidate.PaidAt ?? candidate.CreatedAt" in method
    assert "eventTime.Value < freshFloor" in method
    assert 'Source = "千牛右侧订单面板兜底"' in method
    assert "OrderEventHub.Publish(snapshot)" in method
    assert "ProcessOrderPlacedReplyAsync" not in method


def test_visible_panel_parser_requires_status_or_timestamp_and_rejects_closed_refund_autoreply():
    code = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    parser = code[code.index("ParseVisibleOrderPanelCandidates"):]
    assert "!createdAt.HasValue && !paidAt.HasValue && string.IsNullOrWhiteSpace(status)" in parser
    for status in ["待发货", "已付款", "退款中", "订单关闭", "交易关闭"]:
        assert status in code
    method = code[code.index("TryRecoverVisibleOrderPanelAsync"):]
    assert "VisiblePanelUnsupportedStatuses.Any" in method


def test_visible_panel_reuses_order_event_hub_auto_reply_pipeline():
    bridge = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    hub_fallback = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    assert "OrderEventHub.Publish(snapshot)" in bridge
    assert "ProcessAcceptedOrderEventFallbackAsync(snapshot, seenAt)" in hub_fallback
    assert "cfg.EnableOrderPlacedReply" in hub_fallback
    assert "await ProcessOrderPlacedReplyAsync(plan);" in hub_fallback

def test_message_center_order_deduplication_is_scoped_to_the_exact_shopkey():
    code = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    method = code[
        code.index("private static async void OnMessageCenterNotify"):
        code.index("private static void OnShopRobotNewMessage")
    ]

    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in method
    assert "var reservationKey = shop.ShopKey + \"#\" + hash;" in method
    assert "RawReservations.TryGetValue(reservationKey, out until)" in method
    assert "RawReservations[reservationKey]" in method
    assert "DirectOrderIdentityResolver.IdentityEquals(shop.SellerNick, envelope.Seller)" in method
    assert "using (ShopSettingsScope.Enter(shop, false))" in method
    assert "RawReservations.TryGetValue(hash, out until)" not in method


def test_visible_order_panel_retry_generations_are_shop_scoped_and_revalidate_qn_identity():
    code = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    method = code[
        code.index("private static void ScheduleVisibleOrderPanelScan"):
        code.index("private static string NormalizeIdentityKey")
    ]

    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in method
    assert 'var key = shop.ShopKey + \"#\" + NormalizeIdentityKey(seller)' in method
    assert "var capturedShopKey = shop.ShopKey;" in method
    assert "currentShop.ShopKey, capturedShopKey, StringComparison.Ordinal" in method
    assert "using (ShopSettingsScope.Enter(currentShop, false))" in method
    assert "await qn.TryRecoverVisibleOrderPanelAsync(seller, buyer, source)" in method

def test_receive_new_message_order_cards_run_inside_exact_shop_scope():
    code = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    method = code[
        code.index("private static async void OnReceiveNewMessage"):
        code.index("private static async void OnMessageCenterNotify")
    ]

    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in method
    assert "using (ShopSettingsScope.Enter(shop, false))" in method
    assert "DirectOrderIdentityResolver.IdentityEquals(shop.SellerNick, messageSeller)" in method
    assert method.index("ResolveRuntimeBySeller(qn)") < method.index("ProcessDirectOrderMessageAsync")
    assert method.index("IdentityEquals(shop.SellerNick, messageSeller)") < method.index("ProcessDirectOrderMessageAsync")


def test_visible_panel_buyer_alias_resolution_uses_the_captured_shop_scope():
    code = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    method = code[
        code.index("private static void ScheduleVisibleOrderPanelScan"):
        code.index("private static string NormalizeIdentityKey")
    ]

    scope = method.index("using (ShopSettingsScope.Enter(shop, false))")
    alias = method.index("BuyerIdentityAliasService.ResolveInternalNick(seller, buyer)")
    assert scope < alias
