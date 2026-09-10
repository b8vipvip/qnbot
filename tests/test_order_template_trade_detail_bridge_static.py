from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LEGACY = ROOT / "src" / "Bot" / "ChromeNs" / "OrderTemplateTradeDetailBridge.cs"
V2 = ROOT / "src" / "Bot" / "ChromeNs" / "OrderTemplateRequiredFieldsV2.cs"
PROPS = ROOT / "src" / "Directory.Build.props"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_legacy_trade_detail_bridge_is_inert_and_not_compiled():
    legacy = read(LEGACY)
    props = read(PROPS)
    assert "OrderTemplateTradeDetailBridge.InitializeForApp()" in legacy
    assert "return new object();" in legacy
    assert "new Timer(_ => Attach()" not in legacy
    assert "qn.EvMessageNotity +=" not in legacy
    assert '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\OrderTemplateTradeDetailBridge.cs"' not in props


def test_v2_is_the_single_trade_enrichment_owner_and_uses_canonical_sku():
    v2 = read(V2)
    old_token = "{" + "规格" + "}"
    assert "TryEnrichFromTradeApiAsync" in v2
    assert "await qn.GetBuyerTrades" in v2
    assert "FindExactTrade(response, plan.OrderId)" in v2
    assert "OrderSkuPayloadRecoveryBridge.ResolveSkuTextFromPayload" in v2
    assert 'template.Contains("{sku}")' in v2
    assert old_token not in v2


def test_trade_probe_is_post_send_and_has_no_retry_delay_ladder():
    v2 = read(V2)
    method = v2[v2.index("private static async Task EnrichValidateAndSendAsync"):v2.index("private static async Task<EnrichmentProbe> TryEnrichFromTradeApiAsync")]
    send = method.index("ProcessOrderTemplateRequiredFieldsPlanAsync")
    probe = method.index("TryEnrichFromTradeApiAsync")
    assert send < probe
    assert "field_lookup_policy=post_send_nonblocking" in method
    assert "blocked_blank_template=true" not in method
    assert "new[] { 0, 250, 500, 1000, 1500 }" not in v2
    assert "var delays = new[] { 0 };" in v2


def test_probe_records_safe_sku_evidence_without_raw_payload_logging():
    v2 = read(V2)
    for marker in (
        "trade_item_count=",
        "sku_item_count=",
        "payload_sku_recovery_attempted=",
        "non_blocking=true",
        "sku_found=",
    ):
        assert marker in v2
    assert "Log.Info(raw" not in v2
    assert "Log.Error(raw" not in v2
    assert "File.WriteAllText" not in v2
