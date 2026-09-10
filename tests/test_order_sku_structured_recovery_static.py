from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "Bot" / "Options" / "LegacyAboutUpdateRedirect.cs"
TEMPLATE = ROOT / "src" / "Bot" / "ChromeNs" / "OrderPlacedAutoReplyService.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_sku_recovery_bootstraps_before_constructor_order_bridges():
    source = read(SOURCE)
    assert "private readonly object _orderSkuPayloadRecoveryBootstrap" in source
    assert "OrderSkuPayloadRecoveryBridge.InitializeForApp()" in source
    assert "new Timer(_ => Attach(), null, 0, 25)" in source
    assert "qn.EvMessageNotity += OnMessageNotify" in source
    assert "qn.EvRecieveNewMessage += OnReceiveNewMessage" in source


def test_structured_sku_name_value_pairs_are_reconstructed():
    source = read(SOURCE)
    for token in (
        '"pname"', '"vname"', '"propname"', '"propvalue"',
        '"propertyname"', '"propertyvalue"', '"specname"', '"specvalue"',
        '"attributename"', '"attributevalue"', '"name"', '"value"',
    ):
        assert token in source
    assert "ResolveStructuredSkuPairs(flat)" in source
    assert 'name + ":" + value' in source
    assert 'strategy = "属性名/属性值组合"' in source
    assert "GroupBy(x => ParentPath(x.Path)" in source
    assert "IsSkuRelatedPath" in source


def test_visible_sku_text_restores_missing_separator():
    source = read(SOURCE)
    assert "专辑名称一个月（老账号特价）" in source
    assert "专辑名称:一个月（老账号特价）" in source
    assert "value.Replace('：', ':')" in source
    assert "套餐名称|套餐|期限|时长|会员类型" in source


def test_recovered_snapshot_keeps_working_quantity_and_paid_fields():
    source = read(SOURCE)
    assert "quantity <= 0 || (!paidAmount.HasValue && !totalAmount.HasValue)" in source
    assert "Quantity = quantity" in source
    assert "PaidAmount = paidAmount" in source
    assert "SkuText = Clean(skuText, 240)" in source
    assert "ProcessRichOrderSnapshotAsync" in source
    assert "订单SKU结构恢复成功" in source


def test_template_renders_only_canonical_sku_placeholder_and_preserves_text_when_missing():
    template = read(TEMPLATE)
    old_token = "{" + "规格" + "}"
    primary = '.Replace("{sku}", snapshot == null ? string.Empty : snapshot.SkuText ?? string.Empty)'
    assert primary in template
    assert old_token not in template
    assert "return rendered;" in template
    assert "return allRequestedFieldsMissing ? string.Empty : rendered;" not in template
    assert '.Replace("{数量}", snapshot == null || snapshot.Quantity <= 0 ? string.Empty : snapshot.Quantity.ToString())' in template
    assert '.Replace("{实付}", snapshot == null || !snapshot.PaidAmount.HasValue ? string.Empty : snapshot.PaidAmount.Value.ToString("0.00"))' in template


def test_raw_order_payload_is_not_persisted_or_logged_verbatim():
    source = read(SOURCE)
    bridge = source.split("internal static class OrderSkuPayloadRecoveryBridge", 1)[1]
    assert "RawCardHash = Hash(raw)" in bridge
    assert "File.WriteAllText" not in bridge
    assert 'Log.Info("订单SKU结构恢复成功:' in bridge
    assert '+ raw' not in bridge
