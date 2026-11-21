from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_order_event_hub_requires_and_keys_by_verified_shopkey():
    source = read("src/Bot/ChromeNs/OrderEventHub.cs")
    assert "public string ShopKey { get; set; }" in source
    assert "TryEnsureShopKey(snapshot, out scopeError)" in source
    assert "ResolveRuntimeByShopKey(requestedShopKey)" in source
    assert "seller_shopkey_mismatch" in source
    assert 'Normalize(snapshot.ShopKey) + "#" + Normalize(snapshot.Seller)' in source
    assert "legacy_shop_scope_ambiguous_duplicate_blocked" in source


def test_order_event_fallback_routes_to_exact_shop_and_qn():
    source = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    assert "private static QN ResolveQn(string shopKey)" in source
    assert "FindConnectedQnByShopKey(shopKey)" in source
    assert "ResolveRuntimeByShopKey(snapshot.ShopKey)" in source
    assert "runtimeShop.ShopKey, snapshot.ShopKey" in source
    assert "ShopKey = snapshot.ShopKey" in source
    assert "BuildOrderEventFallbackReservationKey(snapshot.ShopKey" in source


def test_auto_delivery_queue_and_confirmation_barrier_are_shop_scoped():
    queue = read("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    ledger = read("src/Bot/ChromeNs/AutoDeliveryConfirmationLedger.cs")
    assert "BuildKey(snapshot.ShopKey, snapshot.Seller, snapshot.OrderId)" in queue
    assert "FindConnectedQnByShopKey(snapshot.ShopKey)" in queue
    assert "runtimeShop.ShopKey, snapshot.ShopKey" in queue
    assert "HasIntent(string shopKey, string seller, string orderId)" in ledger
    assert "TryRecordIntent(string shopKey, string seller, string orderId" in ledger
    assert "MarkResolved(string shopKey, string seller, string orderId" in ledger
    assert "BuildLegacyKey(seller, orderId)" in ledger
    assert "旧版任务无法唯一恢复ShopKey" in queue


def test_auto_delivery_event_cursors_are_per_shopkey_not_seller_nickname():
    source = read("src/Bot/ChromeNs/AutoDeliveryOrderEventSeed.cs")
    assert "public string ShopKey { get; set; }" in source
    assert "GetCursorLocked(string shopKey, string seller)" in source
    assert "ResolveRuntimeBySeller(qn)" in source
    assert "x.Snapshot.ShopKey" in source
    assert "ShopSettingsScope.Enter(shop)" in source


def test_order_reply_action_and_template_inflight_keys_are_shop_scoped():
    action = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    template = read("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")
    assert "public string ShopKey { get; set; }" in action
    assert "SameShopScope(record.ShopKey, plan.ShopKey)" in action
    assert "BuildReservationKey(string shopKey, string seller" in action
    assert "FindCanonicalOrderIdLocked(string shopKey" in action
    assert "BuildOrderKey(plan.ShopKey, plan.Seller, plan.Buyer, plan.OrderId)" in template


def test_order_notification_correlation_is_shop_scoped():
    source = read("src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs")
    assert "public string ShopKey;" in source
    assert "var key = shop.ShopKey;" in source
    assert "var buyerKey = shopKey + \"#\" + Normalize(seller)" in source
    assert "GenericWakes.TryGetValue(shopKey, out wake)" in source
    assert "var key = shopKey + \"#\" + Normalize(seller)" in source
    assert "messageShop.ShopKey + \"#\" + Hash(raw)" in source
    assert "resolvedShop.ShopKey, shopKey" in source


def test_human_interaction_and_order_attention_are_shopkey_scoped():
    activity = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    attention = read("src/Bot/ChromeNs/NewOrderAttentionQueue.cs")

    assert "public string ShopKey;" in activity
    assert "Begin(string kind, string seller, string buyer, string shopKey = null)" in activity
    assert "HumanKey(shopKey, seller)" in activity
    assert "IsSafeToAutoFocus(string seller, out string reason, string shopKey = null)" in activity
    assert "BotActivityCoordinator.Begin(\"新订单自动切换\", snapshot.Seller, snapshot.Buyer, snapshot.ShopKey)" in attention
    assert "snapshot.ShopKey ?? string.Empty" in attention
    assert "MarkHumanInteraction(snapshot.Seller, \"客服输入框中存在未发送内容\", snapshot.ShopKey)" in attention


def test_order_guidance_delivery_and_commerce_context_are_shopkey_isolated():
    source = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs")
    assert "public string ShopKey { get; set; }" in source
    assert "ResolveShopKey(snapshot.Seller, snapshot.ShopKey)" in source
    assert "FindInternal(shopKey, snapshot.Seller, snapshot.Buyer, snapshot.OrderId)" in source
    assert "FindLatestInternal(shopKey, seller, buyer)" in source
    assert "Same(x.ShopKey, shopKey)" in source
    assert "HasLegacyRecord(plan.Seller, plan.Buyer, plan.OrderId)" in source
    assert "无法确认当前店铺身份，已阻止跨店创建充值流程续问" in source
    assert "SchemaVersion != 2" in source


def test_order_attention_queue_pins_focus_actions_to_authoritative_shop_session():
    attention = read("src/Bot/ChromeNs/NewOrderAttentionQueue.cs")
    assert "public CDPClient SourceClient { get; set; }" in attention
    assert "TryRefreshOrderAttentionSource(item)" in attention
    assert "IsOrderAttentionSourceCurrent(item)" in attention
    assert "server.IsAuthoritativeShopSession(shopKey, sourceClient.SessionId)" in attention
    assert "FindConnectedQnByShopKey(shopKey, sourceClient.SessionId)" in attention
    assert "FocusOrderBuyerAsync(item)" in attention

    focus = attention[
        attention.index("private async Task<bool> FocusOrderBuyerAsync"):
        attention.index("private bool TryRefreshOrderAttentionSource")
    ]
    assert focus.index("IsOrderAttentionSourceCurrent(item)") < focus.index("OpenChat(snapshot.Buyer)")
    assert focus.index("OpenChat(snapshot.Buyer)") < focus.index("IsOrderAttentionSourceCurrent(item)", focus.index("OpenChat(snapshot.Buyer)"))
    assert "await Task.Delay(250);" in focus
