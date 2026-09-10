from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_required_fields_priority_guard_is_compiled_with_v2_runtime():
    props = read("src/Directory.Build.props")
    bot_props = read("src/Bot/Directory.Build.props")
    assert "..\\Directory.Build.props" in bot_props
    v2 = '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\OrderTemplateRequiredFieldsV2.cs" />'
    priority = 'ChromeNs\\OrderTemplateRequiredFieldsPriority.cs'
    assert v2 in props
    assert priority in props
    v2_pos = props.index("OrderTemplateRequiredFieldsV2.cs")
    priority_pos = props.index("OrderTemplateRequiredFieldsPriority.cs", v2_pos)
    recharge_pos = props.index("RechargeStatusAutoQueryService.cs", priority_pos)
    assert v2_pos < priority_pos < recharge_pos


def test_priority_guard_keeps_required_field_owner_before_legacy_order_consumers():
    priority = read("src/Bot/ChromeNs/OrderTemplateRequiredFieldsPriority.cs")
    v2 = read("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")
    assert "OrderTemplateRequiredFieldsPriority.InitializeForApp()" in priority
    assert "EnsureRequiredOrderFieldsNotifyHandlerFirst" in priority
    assert "typeof(OrderTemplateRequiredFieldsV2)" in priority
    assert 'string.Equals(d.Method.Name, "OnMessageNotify", StringComparison.Ordinal)' in priority
    assert "EvMessageNotity = null;" in priority
    assert "EvMessageNotity += (EventHandler<MessageNotifyEventArgs>)handler;" in priority
    assert "订单模板字段 V2 已提升为 messageCenterNotify 第一消费者" in priority
    start = v2.index("private static void StartOwnedPlan")
    block = v2[start:v2.index("private static async Task EnrichValidateAndSendAsync", start)]
    complete = block.index("OrderPlacedAutoReplyService.Complete(plan, true)")
    background = block.index("Task.Run")
    assert complete < background


def test_v2_sends_without_waiting_for_trade_fields_then_probes_once():
    v2 = read("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")
    enrich_start = v2.index("private static async Task EnrichValidateAndSendAsync")
    enrich_end = v2.index("private static async Task<EnrichmentProbe> TryEnrichFromTradeApiAsync", enrich_start)
    scope = v2[enrich_start:enrich_end]
    process_call = scope.index("ProcessOrderTemplateRequiredFieldsPlanAsync")
    trade_call = scope.index("TryEnrichFromTradeApiAsync")
    assert process_call < trade_call
    assert "field_lookup_policy=post_send_nonblocking" in scope
    assert "var delays = new[] { 0 };" in v2
    assert "TradeQueryAttempts" in v2
    assert "SkuFound" in v2
    assert "BuyerRemarkFound" in v2
    assert "non_blocking=true" in v2
