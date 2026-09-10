from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
APP = (ROOT / "src/Bot/App.xaml.cs").read_text(encoding="utf-8-sig")
V2 = (ROOT / "src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs").read_text(encoding="utf-8-sig")
UPDATER = (ROOT / "src/Bot/Update/BotAutoUpdater.ps1").read_text(encoding="utf-8-sig")


def test_order_template_runtime_is_explicitly_initialized_before_generic_settings_rewriter():
    explicit = "OrderTemplateRequiredFieldsV2.InitializeForApp();"
    generic = "SelectableSettingsText.Initialize();"
    assert explicit in APP
    assert APP.index(explicit) < APP.index(generic)
    assert "OrderTemplateSkuUiMigration.Initialize();" in V2


def test_order_template_hint_is_owned_by_clickable_caret_insertion_ui():
    assert "if (IsOrderTemplateHint(source.Text)) return false;" in APP
    for token in (
        "{客服}", "{买家}", "{订单号}", "{时间}", "{商品}", "{sku}",
        "{数量}", "{金额}", "{实付}", "{订单状态}", "{买家备注}", "{分段符}",
    ):
        assert f'"{token}"' in APP
        assert f'"{token}"' in V2
    old_token = "{" + "规格" + "}"
    order_ui_block = APP.split("OrderPlaceholders", 1)[1].split("};", 1)[0]
    assert '"{sku}"' in order_ui_block
    assert old_token not in APP
    assert old_token not in V2
    assert "new Hyperlink(new Run(token))" in V2
    assert "link.Click += delegate { InsertAtCaret(target, token); };" in V2


def test_updater_is_parse_safe_for_label_followed_by_colon_on_windows_powershell_51():
    assert 'Backup validation failed for ${Label}: entry count differs' in UPDATER
    assert 'Backup validation failed for $Label: entry count differs' not in UPDATER
