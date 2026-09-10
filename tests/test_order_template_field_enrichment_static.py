from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_rich_order_bridge_starts_before_legacy_order_bridges():
    app = read("src/Bot/App.xaml.cs")
    settings = read("src/Bot/Options/OrderPlacedReplyDelaySettings.cs")

    assert app.index("OrderPlacedReplyDelaySettings.Initialize();") < app.index("DirectOrderEventBridge.Initialize();")
    assert "OrderRichPayloadBridge.Initialize();" in settings
    assert "new Timer(_ => Attach(), null, 0, 100)" in settings
    assert "qn.EvMessageNotity += OnMessageNotify" in settings
    assert "qn.EvRecieveNewMessage += OnReceiveNewMessage" in settings


def test_raw_payload_parser_preserves_nested_sku_quantity_and_payment_fields():
    code = read("src/Bot/Options/OrderPlacedReplyDelaySettings.cs")

    for token in [
        '"skutext"', '"skuname"', '"skupropertiesname"', '"specification"',
        '"quantity"', '"buynum"', '"itemcount"', '"orderquantity"',
        '"paidamount"', '"actualpay"', '"realpay"', '"payment"',
        '"totalamount"', '"totalfee"', '"orderamount"', '"totalprice"',
    ]:
        assert token in code

    assert "Walk(JToken.Parse(value), path + \".json\"" in code
    assert "ExtractQuantity(combined)" in code
    assert "ExtractPaidAmount(combined)" in code
    assert "ExtractTotalAmount(combined)" in code
    assert '"实收"' in code
    assert '@"(?:×|x|X|\\*)\\s*(\\d{1,5})' in code


def test_paid_event_uses_single_available_amount_as_explicit_fallback():
    code = read("src/Bot/Options/OrderPlacedReplyDelaySettings.cs")

    assert "eventType == OrderEventType.Paid && !paidAmount.HasValue && totalAmount.HasValue" in code
    assert "paidAmount = totalAmount;" in code
    assert "付款事件仅有一个金额字段，实付采用该金额" in code
    assert "if (!totalAmount.HasValue && paidAmount.HasValue) totalAmount = paidAmount;" in code


def test_complete_snapshot_wins_before_sparse_synthetic_message():
    code = read("src/Bot/Options/OrderPlacedReplyDelaySettings.cs")

    publish = code.index("var published = OrderEventHub.Publish(snapshot)")
    plan = code.index("var plan = new OrderPlacedReplyPlan", publish)
    send = code.index("await ProcessOrderPlacedReplyAsync(plan)", plan)

    assert publish < plan < send
    assert "HasTemplateFieldEvidence(snapshot)" in code
    assert "旧桥接随后会被 OrderEventHub 去重" in code
    assert "下单固定模板将使用完整订单字段" in code


def test_existing_template_placeholders_still_read_snapshot_fields():
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")

    assert '.Replace("{sku}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)' in order
    assert '.Replace("{数量}", snapshot == null || snapshot.Quantity <= 0 ? string.Empty : snapshot.Quantity.ToString())' in order
    assert '.Replace("{实付}", snapshot == null || !snapshot.PaidAmount.HasValue ? string.Empty : snapshot.PaidAmount.Value.ToString("0.00"))' in order


def test_raw_payload_is_not_persisted_or_logged_verbatim():
    code = read("src/Bot/Options/OrderPlacedReplyDelaySettings.cs")

    assert "RawCardHash = Hash(raw)" in code
    assert "RawCardJson" not in code
    assert "File.WriteAllText" not in code
    assert "原始 JSON，只保留现有 OrderSnapshot 中的结构化字段和哈希" in code
