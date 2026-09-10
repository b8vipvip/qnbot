from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DIRECT = ROOT / "src" / "Bot" / "ChromeNs" / "DirectOrderEventBridge.cs"
V2 = ROOT / "src" / "Bot" / "ChromeNs" / "OrderTemplateRequiredFieldsV2.cs"
SERVICE = ROOT / "src" / "Bot" / "ChromeNs" / "OrderPlacedAutoReplyService.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_expanded_notification_plan_is_routed_to_v2_before_direct_send():
    direct = read(DIRECT)
    claim = direct.index("OrderTemplateRequiredFieldsV2.TryOwnExistingPlan(this, plan, source)")
    send = direct.index("await ProcessOrderPlacedReplyAsync(plan)", claim)
    assert claim < send


def test_v2_accepts_an_already_parsed_plan_and_keeps_trade_probe_available():
    source = read(V2)
    method = source.index("internal static bool TryOwnExistingPlan")
    start = source.index("StartOwnedPlan(qn, plan", method)
    enrich = source.index("TryEnrichFromTradeApiAsync", start)
    assert method < start < enrich


def test_partial_or_missing_fields_never_block_the_configured_reply():
    source = read(V2)
    scope = source[
        source.index("private static async Task EnrichValidateAndSendAsync"):
        source.index("private static async Task<EnrichmentProbe> TryEnrichFromTradeApiAsync")
    ]
    assert "order_template_partial_send=true" in scope
    assert "field_lookup_policy=post_send_nonblocking" in scope
    assert "blocked_blank_template=true" not in scope
    assert "PresentRequiredFields" in source
    assert 'present=" + string.Join(",", present)' in scope


def test_missing_field_causes_and_probe_state_are_structured_in_logs():
    source = read(V2)
    for marker in [
        "trade_query_attempts=",
        "buyer_search_attempted=",
        "missing_reason=",
        "not_queried_before_send",
        "trade_found_but_sku_empty",
        "trade_not_found_after_",
        "trade_query_error_after_",
        "non_blocking=true",
    ]:
        assert marker in source


def test_final_renderer_keeps_present_fields_and_preserves_authored_layout():
    source = read(SERVICE)
    assert 'RenderTemplate(cfg.OrderPlacedReplyText, plan, "fixed-preset")' in source
    assert "partial=" in source
    assert "all_requested_fields_missing=" in source
    assert 'Regex.Replace(rendered, @"[ \\t]{2,}", " ")' not in source
    assert 'Regex.Replace(rendered, @"([：:])\\s+", "$1")' not in source


def test_all_missing_dynamic_fields_preserve_authored_literal_text():
    source = read(SERVICE)
    assert "allRequestedFieldsMissing = missing.Count > 0 && present.Count == 0" in source
    assert "return rendered;" in source
    assert "return allRequestedFieldsMissing ? string.Empty : rendered" not in source


def test_http_response_and_fallback_have_the_same_diagnostics():
    source = read(SERVICE)
    assert 'RenderTemplate(reply, plan, "http-response")' in source
    assert 'RenderTemplate(cfg.OrderPlacedReplyText, plan, "http-fallback")' in source


def test_known_non_order_placeholders_render_independently_of_missing_sku():
    service = read(SERVICE)
    assert '.Replace("{订单号}", plan == null ? string.Empty : plan.OrderId ?? string.Empty)' in service
    assert '.Replace("{时间}", plan == null || plan.EventTime == DateTime.MinValue ? string.Empty : plan.EventTime.ToString("yyyy-MM-dd HH:mm:ss"))' in service
    assert '.Replace("{sku}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)' in service


def test_trade_found_reason_has_priority_over_an_earlier_transient_error():
    source = read(V2)
    trade_found = source.index("else if (probe.TradeFound)")
    query_error = source.index("trade_query_error_after_", trade_found)
    assert trade_found < query_error
