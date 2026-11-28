from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_object_message_center_payload_is_preserved_as_json_text():
    code = read("src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    assert "FlexibleJsonStringConverter" in code
    assert "objectType == typeof(string)" in code
    assert "token.ToString(Formatting.None)" in code
    assert "JsonConvert.DefaultSettings" in code
    assert "QianniuWebSocketJsonCompatibility.Initialize();" in app
    assert "ChromeNs\\OrderPaymentNotificationFallback.cs" in targets


def test_paid_order_fallback_expands_nested_json_and_never_guesses_buyer():
    code = read("src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs")
    assert "messageCenterNotify统一兼容解析" in code
    assert "JToken.Parse(nested)" in code
    assert "customernick" in code
    assert "oppositenick" in code
    assert "sendernick" in code
    assert "conversationnick" in code
    assert "EventCueRegex" in code
    assert "LabeledOrderRegex" in code
    assert "OrderAutomationCoordinator.ObserveGenericPaymentSignal" in code
    generic = code[code.index("internal static void ObserveGenericPaymentSignal"):code.index("private static void OnShopRobotNewMessage")]
    assert "GetCurrentConversationID" not in generic
    assert "OpenChat" not in generic
    assert "ProcessDirectOrderMessageAsync" in code


def test_knowledge_enabled_checkbox_is_single_click_two_way_and_persisted():
    assert not (ROOT / "src/Bot/Knowledge/KnowledgeManagerControl.cs").exists()
    code = read("src/Bot/Knowledge/KnowledgeCenterV2RecordsPage.cs")
    assert '_enabled = new CheckBox { Content = "启用"' in code
    assert "_enabled.IsChecked = item.Enabled" in code
    assert "record.Enabled = _enabled.IsChecked == true" in code
    assert "TrySaveEditorRecord" in code
    assert "BotFeatureStore.SaveKnowledgeBase" not in code


def test_legacy_about_menu_opens_full_about_and_update_center():
    menu = read("src/Bot/AssistWindow/NotifyIcon/MenuCreator/HelpMenuCreator.cs")
    ui = read("src/Bot/Options/BotUpdateOptionsControl.cs")
    assert 'CreateItem("关于与版本更新", OnAboutClicked)' in menu
    assert "BotAboutUpdateLauncher.Show();" in menu
    assert "internal static class BotAboutUpdateLauncher" in ui
    assert "OptionEnum.AboutUpdate" in ui
    assert "WndOption.MyShow" in ui
    assert 'Title = "关于与版本更新"' in ui


def test_about_page_shows_build_metadata_and_manual_update_actions():
    ui = read("src/Bot/Options/BotUpdateOptionsControl.cs")
    assert "release-info.json" in ui
    assert 'AddLabel(versionGrid, 1, "构建提交")' in ui
    assert 'AddLabel(versionGrid, 2, "发布时间/构建时间")' in ui
    assert 'AddLabel(versionGrid, 3, "更新通道")' in ui
    assert 'AddLabel(versionGrid, 4, "构建任务")' in ui
    assert 'AddLabel(versionGrid, 5, "安装目录")' in ui
    assert 'CreateButton("手动检查更新", true)' in ui
    assert 'CreateButton("下载并安装", false)' in ui
    assert "BotUpdateService.CheckNowAsync(true)" in ui
    assert "BotUpdateService.ShowUpdatePrompt" in ui
