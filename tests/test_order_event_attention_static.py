from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_order_card_parser_builds_structured_snapshot_without_persisting_raw_json():
    code = read("src/Bot/ChromeNs/OrderEventHub.cs")
    for field in ["OrderId", "ItemId", "ItemTitle", "SkuId", "SkuText", "Quantity", "TotalAmount", "PaidAmount", "TradeStatus", "IsPaid", "CreatedAt", "PaidAt", "ProductUrl", "ImageUrl", "RawCardHash", "EventType"]:
        assert "public " in code and field in code
    assert "JObject.FromObject(message)" in code
    assert "SHA256.Create()" in code
    assert "order-event-state.json" in code
    assert "stream.Flush(true)" in code
    assert "File.Replace(temp, path, null, true)" in code
    assert "File.Delete(path)" not in code
    assert "RawCardJson" not in code
    assert "seller + orderId + eventType" not in code

def test_order_event_hub_separates_created_and_paid_and_persists_dedup():
    code = read("src/Bot/ChromeNs/OrderEventHub.cs")
    assert "Created = 0" in code; assert "Paid = 1" in code; assert 'return Normalize(snapshot.Seller) + "#" + snapshot.OrderId + "#" + snapshot.EventType;' in code; assert "AddDays(-30)" in code; assert "Take(2000)" in code; assert "相同订单事件已处理" in code

def test_order_detection_is_independent_from_auto_reply_and_queues_attention():
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    parse = order.index("OrderCardParser.TryParse"); publish = order.index("OrderEventHub.Publish(snapshot)", parse); enqueue = order.index("qn.EnqueueNewOrderAttention(snapshot)", publish); config = order.index("BotFeatureStore.GetAutoReplyRules()", enqueue)
    assert parse < publish < enqueue < config
    assert 'snapshot.EventType == OrderEventType.Created || snapshot.EventType == OrderEventType.Paid' in order
    assert "manualReplyDoesNotSuppress=true" in order

def test_background_recovery_uses_same_structured_order_parser():
    recovery = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    assert "BotActivityCoordinator.Begin(\"后台消息补偿\"" in recovery; assert "OrderCardParser.TryParse(" in recovery; assert "im.singlemsg.GetRemoteHisMsg" in recovery; assert "ProcessOrderPlacedReplyAsync(orderPlan)" in recovery

def test_idle_queue_checks_all_safety_guards_and_verifies_target_buyer():
    queue = read("src/Bot/ChromeNs/NewOrderAttentionQueue.cs")
    for guard in ["_incomingMessageGate.CurrentCount", "_sendGate.CurrentCount", "_backgroundRecoveryGate.CurrentCount", "BotActivityCoordinator.IsSafeToAutoFocus", "TryGetInputboxEmptyAsync", "GetCurrentConversationID", "OpenChat(snapshot.Buyer)", "SetActiveConversationByNick(snapshot.Seller, snapshot.Buyer, \"newOrderAutoFocus\")"]:
        assert guard in queue
    assert "await Task.Delay(650)" in queue; assert "TimeSpan.FromMinutes(30)" in queue; assert "OrderByDescending(x => x.Snapshot.EventType == OrderEventType.Paid)" in queue; assert "连续多次无法确认目标买家会话" in queue

def test_buyer_reply_work_is_visible_to_activity_coordinator():
    burst = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "public BotActivityLease ActivityLease;" in burst; assert 'BotActivityCoordinator.Begin("买家消息聚合/回复"' in burst; assert "DisposeActivity(state)" in burst

def test_order_summary_ui_and_settings_are_wired():
    queue = read("src/Bot/ChromeNs/NewOrderAttentionQueue.cs"); settings = read("src/Bot/Options/OrderAttentionSettings.cs"); app = read("src/Bot/App.xaml.cs"); targets = read("src/Directory.Build.targets")
    assert "【新订单待处理】" in queue; assert "snapshot.BuildSummary()" in queue; assert "等待Bot空闲后自动切换到该买家" in queue; assert "当前无任务时自动切换到新下单买家" in settings; assert "DefaultHumanProtectionSeconds = 12" in settings; assert "DefaultSwitchIntervalSeconds = 5" in settings; assert "OrderAttentionSettings.Initialize();" in app
    for source in ["BotActivityCoordinator.cs", "OrderEventHub.cs", "NewOrderAttentionQueue.cs", "OrderAttentionSettings.cs"]: assert source in targets

def test_order_http_payload_and_templates_include_structured_fields():
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    for field in ['payload["itemId"]', 'payload["itemTitle"]', 'payload["skuId"]', 'payload["skuText"]', 'payload["quantity"]', 'payload["totalAmount"]', 'payload["paidAmount"]', 'payload["tradeStatus"]', 'payload["isPaid"]']: assert field in order
    for placeholder in ["{商品}", "{sku}", "{数量}", "{金额}", "{实付}", "{订单状态}"]: assert placeholder in order
