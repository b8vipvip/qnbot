from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "Bot" / "ChromeNs" / "OrderTemplateRequiredFieldsV2.cs"
PROPS = ROOT / "src" / "Directory.Build.props"
RENDERER = ROOT / "src" / "Bot" / "ChromeNs" / "OrderPlacedAutoReplyService.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_unified_v2_bridge_replaces_the_two_racing_runtime_bridges():
    source = read(SOURCE)
    props = read(PROPS)
    assert "OrderTemplateRequiredFieldsV2.cs" in props
    assert '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\OrderTemplateRequiredFieldsV2.cs"' in props
    assert '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\OrderTemplateTradeDetailBridge.cs"' not in props
    assert '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\OrderTemplateReceiveNewMessageTradeDetailBridge.cs"' not in props
    assert "supersedes" in props
    assert "qn.EvRecieveNewMessage += OnReceiveNewMessage" in source
    assert "qn.EvMessageNotity += OnMessageNotify" in source


def test_sku_is_the_only_supported_sku_placeholder():
    source = read(SOURCE)
    renderer = read(RENDERER)
    old_token = "{" + "规格" + "}"
    assert 'template.Contains("{sku}")' in source
    assert '.Replace("{sku}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)' in renderer
    assert old_token not in source
    assert old_token not in renderer
    assert '"{sku}"' in source


def test_sku_triggers_required_field_diagnostics_without_becoming_a_send_gate():
    source = read(SOURCE)
    assert 'template.Contains("{sku}")' in source
    assert 'missing.Add("sku")' in source
    assert 'missing.Add("quantity")' in source
    assert 'missing.Add("paid")' in source
    assert 'missing.Add("total")' in source
    assert 'missing.Add("item")' in source
    assert 'missing.Add("status")' in source
    assert "blocked_blank_template=true" not in source
    assert "field_lookup_policy=post_send_nonblocking" in source


def test_missing_configured_fields_are_sent_first_and_probed_afterwards():
    source = read(SOURCE)
    start = source.index("private static async Task EnrichValidateAndSendAsync")
    end = source.index("private static async Task<EnrichmentProbe> TryEnrichFromTradeApiAsync", start)
    scope = source[start:end]
    send = scope.index("ProcessOrderTemplateRequiredFieldsPlanAsync")
    probe = scope.index("TryEnrichFromTradeApiAsync")
    assert send < probe
    assert "await TryEnrichFromTradeApiAsync(qn, plan)" not in scope[:send]
    assert "not_queried_before_send" in source


def test_enrichment_probe_is_safe_and_explains_sku_failures():
    source = read(SOURCE)
    for field in (
        "trade_found=",
        "buyer_security_id_found=",
        "sku_found=",
        "quantity_found=",
        "paid_found=",
        "total_found=",
        "trade_item_count=",
        "sku_item_count=",
        "payload_sku_recovery_attempted=",
        "non_blocking=true",
    ):
        assert field in source
    assert "RawCardHash = Hash(raw)" in source
    assert "Log.Info(raw" not in source
    assert "Log.Error(raw" not in source
    assert "File.WriteAllText" not in source


def test_trade_probe_is_single_attempt_and_uses_structured_sku_recovery():
    source = read(SOURCE)
    assert "var delays = new[] { 0 };" in source
    assert "new[] { 0, 250, 500, 1000, 1500 }" not in source
    assert "SkuText = OrderSkuPayloadRecoveryBridge.ResolveSkuTextFromPayload(raw)" in source
    assert "JObject.FromObject(trade).ToString(Formatting.None)" in source
    assert "OrderSkuPayloadRecoveryBridge.ResolveSkuTextFromPayload(" in source
    assert "TradeItemCount" in source
    assert "SkuItemCount" in source


def test_send_path_reuses_existing_safe_order_reply_pipeline():
    source = read(SOURCE)
    publish = source.index("OrderEventHub.Publish(snapshot)")
    observe = source.index("OrderGuidanceDeliveryGuard.ObserveOrder(snapshot)", publish)
    attention = source.index("qn.EnqueueNewOrderAttention(snapshot)", observe)
    send = source.index("await qn.ProcessOrderTemplateRequiredFieldsPlanAsync(plan)", attention)
    assert publish < observe < attention < send
    assert "return ProcessOrderPlacedReplyAsync(plan)" in source
    assert "BotActivityCoordinator.Begin" in source
