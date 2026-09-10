from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
V2 = ROOT / "src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs"


def test_order_sku_probe_cannot_precede_primary_send():
    source = V2.read_text(encoding="utf-8-sig")
    scope = source[source.index("private static async Task EnrichValidateAndSendAsync"):source.index("private static async Task<EnrichmentProbe> TryEnrichFromTradeApiAsync")]
    send = scope.index("ProcessOrderTemplateRequiredFieldsPlanAsync")
    probe = scope.index("TryEnrichFromTradeApiAsync")
    assert send < probe
    assert "field_lookup_policy=post_send_nonblocking" in scope


def test_order_sku_probe_has_no_wait_ladder():
    source = V2.read_text(encoding="utf-8-sig")
    assert "var delays = new[] { 0 };" in source
    assert "new[] { 0, 250, 500, 1000, 1500 }" not in source
