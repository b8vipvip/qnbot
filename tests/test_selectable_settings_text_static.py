from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "src" / "Bot" / "App.xaml.cs"
ORDER_SERVICE = ROOT / "src" / "Bot" / "ChromeNs" / "OrderPlacedAutoReplyService.cs"
QN = ROOT / "src" / "Bot" / "ChromeNs" / "QN.cs"
V2 = ROOT / "src" / "Bot" / "ChromeNs" / "OrderTemplateRequiredFieldsV2.cs"


def source(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_feature_settings_register_selectable_text_enhancement():
    text = source(APP)
    assert "SelectableSettingsText.Initialize();" in text
    assert "typeof(FeatureSettingsWindow)" in text
    assert "DispatcherPriority.ContextIdle" in text
    assert "CollectCandidates(window, candidates)" in text


def test_help_text_becomes_read_only_but_mouse_selectable():
    text = source(APP)
    assert "new TextBox" in text
    assert "IsReadOnly = true" in text
    assert "IsTabStop = false" in text
    assert "BorderThickness = new Thickness(0)" in text
    assert "Background = Brushes.Transparent" in text
    assert "Cursor = Cursors.IBeam" in text
    assert "可用鼠标拖选文字并按 Ctrl+C 复制" in text
    assert "IsHitTestVisible = false" not in text
    assert "Focusable = false" not in text.split("CreateSelectableTextBox", 1)[1].split("CreatePlaceholderButton", 1)[0]


def test_titles_and_field_labels_are_not_globally_replaced():
    text = source(APP)
    assert "source.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight()" in text
    assert "source.Foreground as SolidColorBrush" in text
    assert "IsMutedColor" in text


def test_placeholder_buttons_copy_exact_token_with_feedback():
    text = source(APP)
    assert 'new Regex(@"\\{[^{}\\r\\n]{1,24}\\}"' in text
    assert 'Text = "点击复制："' in text
    assert "CreatePlaceholderButton(placeholder)" in text
    assert "Clipboard.SetText(placeholder ?? string.Empty)" in text
    assert 'button.Content = "已复制"' in text
    assert "TimeSpan.FromMilliseconds(900)" in text


def test_order_help_exposes_canonical_sku_only():
    app = source(APP)
    service = source(ORDER_SERVICE)
    qn = source(QN)
    v2 = source(V2)

    ui_placeholders = (
        "{客服}",
        "{买家}",
        "{订单号}",
        "{时间}",
        "{商品}",
        "{sku}",
        "{数量}",
        "{金额}",
        "{实付}",
        "{订单状态}",
        "{买家备注}",
        "{分段符}",
    )
    for placeholder in ui_placeholders:
        assert f'"{placeholder}"' in app

    runtime_replacements = (
        "{客服}",
        "{买家}",
        "{订单号}",
        "{时间}",
        "{商品}",
        "{sku}",
        "{数量}",
        "{金额}",
        "{实付}",
        "{订单状态}",
        "{买家备注}",
    )
    for placeholder in runtime_replacements:
        assert f'.Replace("{placeholder}"' in service

    old_token = "{" + "规格" + "}"
    assert old_token not in app
    assert old_token not in service
    assert old_token not in v2
    assert 'const string segmentToken = "{分段符}";' in qn
    order_ui_block = app.split("OrderPlaceholders", 1)[1].split("};", 1)[0]
    assert '"{sku}"' in order_ui_block


def test_replacement_preserves_common_wpf_layout_metadata():
    text = source(APP)
    for marker in (
        "Grid.SetRow(target, Grid.GetRow(source))",
        "Grid.SetColumn(target, Grid.GetColumn(source))",
        "DockPanel.SetDock(target, DockPanel.GetDock(source))",
        "Panel.SetZIndex(target, Panel.GetZIndex(source))",
        "panel.Children.Insert(index, replacement)",
        "decorator.Child = replacement",
    ):
        assert marker in text
