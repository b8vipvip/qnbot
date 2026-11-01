from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RIGHT_PANEL_XAML = ROOT / "src" / "Bot" / "AssistWindow" / "Widget" / "RightPanel.xaml"
RIGHT_PANEL_ENTRY = ROOT / "src" / "Bot" / "AssistWindow" / "Widget" / "RightPanel.SettingsEntry.cs"
SETTINGS_XAML = ROOT / "src" / "Bot" / "Options" / "WndOption.xaml"
SETTINGS_CS = ROOT / "src" / "Bot" / "Options" / "WndOption.xaml.cs"
FEATURE_HOST = ROOT / "src" / "Bot" / "Options" / "FeatureSettingsOptionsControl.cs"
BOT_PROPS = ROOT / "src" / "Bot" / "Directory.Build.props"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_toolbar_settings_button_opens_window_directly_without_visible_dropdown_items():
    xaml = read(RIGHT_PANEL_XAML)
    entry = read(RIGHT_PANEL_ENTRY)

    assert 'Click="btnOpenSettings_Click"' in xaml
    assert "ResolveSellerNameForWindow" in entry
    assert "WndOption.MyShow(seller, Wnd);" in entry
    assert "WndOption.MyShow(Wnd.Desk.WndTitle, Wnd);" not in entry
    assert 'Name="btnDataDesk"' in xaml
    assert 'Content="数据台"' in xaml
    assert 'Click="menuDataDesk_Click"' in xaml
    assert 'Header="API接口"' not in xaml
    assert 'Header="知识库"' not in xaml
    assert 'Header="账号与授权"' not in xaml
    assert 'Header="关于"' not in xaml


def test_settings_window_uses_sidebar_and_constrained_stretch_content_host():
    xaml = read(SETTINGS_XAML)

    assert 'x:Name="navPanel"' in xaml
    assert 'x:Name="contentHost"' in xaml
    assert 'HorizontalContentAlignment="Stretch"' in xaml
    assert 'VerticalContentAlignment="Stretch"' in xaml
    assert 'ClipToBounds="True"' in xaml
    assert 'x:Name="txtPageTitle"' in xaml
    assert 'x:Name="txtShopScope"' in xaml
    assert 'Name="tabMain"' not in xaml
    assert "SplitButton" not in xaml


def test_navigation_is_grouped_by_user_task_and_ai_service_page_is_removed():
    code = read(SETTINGS_CS)

    for group in ("店铺与连接", "回复与通知", "数据与安全", "系统"):
        assert group in code
    for page in (
        "店铺绑定",
        "知识库",
        "自动回复规则",
        "转人工策略",
        "消息策略",
        "数据管理",
        "日志与调试",
        "商业化合规",
        "关于与更新",
    ):
        assert page in code
    assert 'AddFeaturePage("回复与通知", "消息通知"' not in code
    assert 'AddPage("店铺与连接", "AI 服务"' not in code
    assert "aiSettings = new CtlRobotOptions" not in code
    assert "if (showPage == OptionEnum.Robot) showPage = OptionEnum.ShopBinding;" in code
    assert "账号与授权" not in code


def test_feature_pages_are_embedded_and_obsolete_license_page_is_removed():
    code = read(FEATURE_HOST)

    assert 'RemoveMeaninglessLicensePage();' in code
    assert 'string.Equals(Convert.ToString(x.Header), "账号与授权"' in code
    assert '_tabs.Items.Remove(licenseTab);' in code
    assert 'SetPrivateField(_legacyWindow, "_licensee", null);' in code
    assert '_legacyWindow.Content = null;' in code
    assert 'HideLegacyTabHeaders();' in code
    assert "RemoveLegacyKnowledgeGuideTab();" in code
    assert "ReplaceKnowledgePageWithEmbeddedLauncher" not in code
    assert 'KnowledgeCenterHelpWindow.MyShow(Window.GetWindow(this), Seller);' in code
    window = read(SETTINGS_CS)
    assert "KnowledgeCenterWindow.ShowForSeller(this, Seller)" in window


def test_feature_business_data_cannot_be_cleared_by_restore_default():
    code = read(FEATURE_HOST)

    assert "为避免误清空" in code
    assert "暂不提供一键恢复默认" in code


def test_new_partial_sources_are_included_in_main_and_wpf_temporary_builds():
    props = read(BOT_PROPS)

    assert "Options\\FeatureSettingsOptionsControl.cs" in props
    assert "AssistWindow\\Widget\\RightPanel.SettingsEntry.cs" in props
