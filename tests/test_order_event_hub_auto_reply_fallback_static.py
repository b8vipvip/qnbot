from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_order_event_hub_fallback_bootstraps_from_existing_compiled_source():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    assert "_orderEventAutoReplyFallbackBootstrap" in code; assert "OrderEventAutoReplyFallback.InitializeForApp()" in code; assert "order-event-state.json" in code; assert 'root["Events"] as JArray' in code; assert 'item["Snapshot"].ToObject<OrderSnapshot>()' in code

def test_fallback_only_consumes_fresh_created_or_paid_events():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    assert "HubEventFreshnessWindow = TimeSpan.FromMinutes(5)" in code
    assert 'item["AcceptedAt"]' in code
    assert "acceptedAt = seenAt" in code  # legacy state compatibility
    assert "IsFreshAcceptedAt(acceptedAt, StartedAt)" in code
    assert "acceptedAt < now.Subtract(HubEventFreshnessWindow)" in code
    assert "snapshot.EventType != OrderEventType.Created" in code
    assert "snapshot.EventType != OrderEventType.Paid" in code
    assert "Scheduled.TryAdd(key, DateTime.Now)" in code
    assert "HandleAsync(snapshot, acceptedAt, key)" in code
    assert "CleanupScheduled()" in code
    assert "DateTime.Now.AddDays(-31)" in code

def test_fixed_preset_fallback_is_immediate_while_http_keeps_enrichment():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    method = code[code.index("ProcessAcceptedOrderEventFallbackAsync"):]
    assert 'var fixedPreset = !string.Equals(mode, "调用HTTP接口", StringComparison.Ordinal);' in method
    assert "if (!fixedPreset" in method
    assert 'OrderTemplateRequiredFieldsV2.TryOwnExistingPlan(this, plan, "OrderEventHub统一兜底")' in method
    assert "固定预设进入即时发送路径，不等待交易字段补全" in method
    assert "await ProcessOrderPlacedReplyAsync(plan);" in method
    assert "await Task.Delay(1200)" not in method
    assert "BotActivityCoordinator.GetSnapshot(snapshot.Seller)" not in method

def test_fallback_resolves_exact_shop_scope_and_never_guesses_cross_shop():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    assert "QN.FindExistingBySellerNick(seller)" in code; assert "DirectOrderIdentityResolver.IdentityEquals(x.Seller.Nick, seller)" in code; assert "ShopContextLocator.ResolveBySellerNick(snapshot.Seller)" in code; assert "using (ShopSettingsScope.Enter(shop))" in code; assert "拒绝跨店猜测" in code

def test_accepted_hub_event_builds_plan_without_republishing_order_event():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs"); method = code[code.index("ProcessAcceptedOrderEventFallbackAsync"):]
    assert "_messageSafetyStartedAt.AddSeconds(-8)" in method; assert "BotFeatureStore.GetAutoReplyRules()" in method; assert "cfg.EnableOrderPlacedReply" in method; assert "BuyerIdentityAliasService.ResolveInternalNick" in method; assert "OrderGuidanceDeliveryGuard.ObserveOrder(snapshot)" in method; assert "EnqueueNewOrderAttention(snapshot)" in method; assert "new OrderPlacedReplyPlan" in method; assert "OrderEventHub.Publish(snapshot)" not in method

def test_fallback_reuses_existing_enrichment_and_mandatory_safe_send_pipeline():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs"); method = code[code.index("ProcessAcceptedOrderEventFallbackAsync"):]
    assert 'OrderTemplateRequiredFieldsV2.TryOwnExistingPlan(this, plan, "OrderEventHub统一兜底")' in method; assert "await ProcessOrderPlacedReplyAsync(plan);" in method
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "SendMandatoryOrderTextAsync" in order; assert "KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, text);" in order
    process = order[order.index("private async Task ProcessOrderPlacedReplyAsync"):]
    assert "ResponseProgressTracker.HasActiveManualIntervention" not in process; assert "OrderGuidanceDeliveryGuard.MarkDelivered(" in order

def test_fallback_logs_explicit_no_send_reasons_for_operator_diagnostics():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    for text in ["未发送原因=Bot已停用", "未发送原因=本店下单自动发送关闭", "卖家身份不匹配", "缺少可验证买家身份", "Hub兜底跳过历史订单", "Hub兜底接管已确认订单"]: assert text in code
def test_fallback_execution_boundary_rejects_stale_hub_history_before_side_effects():
    code = read("src/Bot/ChromeNs/BotActivityCoordinator.cs")
    method = code[code.index("ProcessAcceptedOrderEventFallbackAsync"):]
    gate = method.index("OrderEventAutoReplyFallback.IsFreshAcceptedAt(hubSeenAt, _messageSafetyStartedAt)")
    observe = method.index("OrderGuidanceDeliveryGuard.ObserveOrder(snapshot)")
    attention = method.index("EnqueueNewOrderAttention(snapshot)")
    plan = method.index("new OrderPlacedReplyPlan")
    assert gate >= 0
    assert gate < observe < attention < plan
    assert "Hub兜底拒绝历史回放，未发送" in method
    assert "hubAcceptedAt=" in method
