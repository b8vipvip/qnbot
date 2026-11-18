from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_embedded_settings_repairs_real_order_hint_after_rehosting():
    ui = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")

    assert "_legacyWindow.Content = null;" in ui
    assert "Content = hosted;" in ui
    assert "EnhanceEmbeddedOrderTemplateHint();" in ui
    assert ui.index("Content = hosted;") < ui.index("EnhanceEmbeddedOrderTemplateHint();")
    assert 'string.Equals(header, "自动回复规则", StringComparison.Ordinal)' in ui
    assert "OrganizeAutoReplyRulesPage();" in ui


def test_embedded_hint_exposes_complete_clickable_placeholder_set():
    ui = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")
    v2 = read("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")

    tokens = (
        "{客服}", "{买家}", "{订单号}", "{时间}", "{商品}", "{sku}",
        "{数量}", "{金额}", "{实付}", "{订单状态}", "{买家备注}", "{分段符}",
    )
    for token in tokens:
        assert f'"{token}"' in ui
        assert f'"{token}"' in v2

    assert "new Hyperlink(new Run(token))" in ui
    assert "link.Click += delegate { InsertOrderTemplateToken(token); };" in ui
    assert "box.SelectionStart = start + token.Length;" in ui
    assert "box.SelectionLength = 0;" in ui
    assert "{分段符} 会拆成多条千牛消息依次发送" in ui


def test_embedded_target_resolution_uses_legacy_fields_and_real_rendered_tree():
    ui = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")

    assert "GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)" in ui
    assert "cfg.OrderPlacedReplyText" in ui
    assert "FindOrderTemplateReplyTextBox" in ui
    assert "EnumerateElements(root)" in ui
    assert 'x.Key.IndexOf("order", StringComparison.OrdinalIgnoreCase)' in ui
    assert "candidates.Count == 1 ? candidates[0] : null" in ui


def test_generic_copy_ui_does_not_compete_with_embedded_order_hint():
    app = read("src/Bot/App.xaml.cs")

    assert "OrderTemplateRequiredFieldsV2.InitializeForApp();" in app
    assert "SelectableSettingsText.Initialize();" in app
    assert "if (IsOrderTemplateHint(source.Text)) return false;" in app



def test_product_rule_selector_failure_does_not_abort_the_feature_settings_shell():
    ui = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")
    segment = ui[ui.index("var orderReply ="):ui.index("private List<UIElement> BuildDeterministicRuleControls")]
    assert "try" in segment
    assert "new ShopOrderProductRulesUi(Seller, orderReply, orderMode)" in segment
    assert "_orderProductRulesUi = null" in segment
    assert "Log.Exception(ex)" in segment
    assert "if (_orderProductRulesUi != null)" in segment
