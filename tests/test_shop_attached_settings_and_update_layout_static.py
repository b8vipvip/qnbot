from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_attached_bot_is_the_only_runtime_workbench():
    scanner = read("src/Bot/ControllerNs/DeskScanner.cs")
    desk = read("src/Bot/Automation/ChatDeskNs/Desk.cs")
    tray = read("src/Bot/AssistWindow/NotifyIcon/MenuCreator/HelpMenuCreator.cs")

    assert "GetOpenChatWnds()" in scanner
    assert "Desk.FindExistingByHwnd" in scanner
    assert "EnsureVisibleForMultiShopAttachedMode" in scanner
    assert "WndAssist.CreateAndAttachToDesk(desk);" in desk
    assert "打开Bot工作台" not in tray
    assert not (ROOT / "src/Bot/AssistWindow/BotDesktopWindow.cs").exists()


def test_each_attached_settings_window_uses_its_bound_desk_seller_shopkey():
    entry = read("src/Bot/AssistWindow/Widget/RightPanel.SettingsEntry.cs")
    window = read("src/Bot/Options/WndOption.xaml.cs")
    assert "DeskSellerBindingRegistry.GetSeller(desk)" in entry
    assert "ResolveSellerNameForWindow" in entry
    assert "BindResolvedSeller" in entry
    assert "WndOption.MyShow(seller, Wnd);" in entry
    assert "WndOption.MyShow(Wnd.Desk.WndTitle, Wnd);" not in entry
    assert "ShopContextLocator.ResolveBySellerNick(seller)" in window
    assert "ShopKey：" in window
    assert "Application.Current.Windows.OfType<WndOption>()" in window
    assert "ShowSameNickOneInstance<WndOption>" not in window
    assert "using (ShopSettingsScope.Enter(_shop))" in window


def test_update_page_has_no_fixed_minheight_and_primary_actions_are_before_preferences():
    ui = read("src/Bot/Options/BotUpdateOptionsControl.cs")
    xaml = read("src/Bot/Options/WndOption.xaml")

    assert "MinHeight = 580" not in ui
    assert "MinWidth = 650" not in ui
    assert "CanContentScroll = false" in ui
    assert "PanningMode = PanningMode.VerticalOnly" in ui
    assert ui.index('CreateTitle("检查与安装")') < ui.index('CreateTitle("自动检查与更新设置")')
    assert 'HorizontalContentAlignment="Stretch"' in xaml
    assert 'VerticalContentAlignment="Stretch"' in xaml
    assert 'ClipToBounds="True"' in xaml


def test_shop_binding_page_replaces_ai_service_navigation_and_can_pull_cloud_knowledge():
    window = read("src/Bot/Options/WndOption.xaml.cs")
    binding = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    sync = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")

    assert 'AddPage("店铺与连接", "AI 服务"' not in window
    assert "if (showPage == OptionEnum.Robot) showPage = OptionEnum.ShopBinding;" in window
    assert "Bot 服务端（程序内置）" in binding
    assert "本店 Bot 服务端地址" not in binding
    assert "本店 Bot 客户端令牌" in binding
    assert "管理令牌" in binding
    assert "导出全店信息" in binding and "导入全店信息" in binding
    assert "立即同步知识库" in binding
    assert "保存令牌并立即同步知识库" not in binding
    assert "await KnowledgeV2CloudSyncService.SyncNowAsync(_shop)" in binding
    assert "internal static async Task<bool> SyncNowAsync(ShopContext shop)" in sync


def test_attached_window_tracking_no_longer_periodically_uses_topmost():
    perf = read("src/Bot/AssistWindow/WndAssist.AttachedPerformance.cs")
    props = read("src/Bot/Directory.Build.props")
    assert "SafePeriodicTrack" in perf
    assert "5000" in perf
    assert "SwpNoActivate" in perf
    assert "HwndNotTopmost" in perf
    assert "Topmost = true" not in perf
    assert "BringTop()" not in perf
    assert "WndAssist.AttachedPerformance.cs" in props


def test_status_bar_uses_shop_control_plane_status_not_legacy_local_ai_endpoint_count():
    status = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.ControlPlaneStatus.cs")
    legacy = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.DataDesk.cs")
    props = read("src/Bot/Directory.Build.props")
    assert 'txtStatusApi.Text = "服务端：" + status' in status
    assert "ShopTokenBindingService.GetStatusText" in status
    assert "ShopScopedUiBridge.Get(Window.GetWindow(this))" in status
    assert "ShopContextLocator.ResolveRuntimeByShopKey(activeShopKey)" in status
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in status
    assert "_diagnosticsTimer.Interval = TimeSpan.FromSeconds(5)" in status
    assert "_statsTimer.Interval = TimeSpan.FromSeconds(10)" in status
    assert "AiEndpointStore.GetEnabledEndpoints" in legacy  # old data-desk metric remains compatibility-only
    assert "CtlRobot.ControlPlaneStatus.cs" in props



def test_settings_optional_feature_failure_cannot_block_shop_binding_page():
    window = read("src/Bot/Options/WndOption.xaml.cs")
    build = window[window.index("private void BuildSettingsPages()"):window.index("private void AddPage")]
    assert "shopBinding = new ShopBindingOptionsControl(Seller);" in build
    assert "_featureSettings = null;" in build
    assert "if (_featureSettings != null)" in build
    assert "店铺绑定" in build

def test_shop_product_catalog_uses_the_bound_scope_and_stable_shopkey_outside_ui_scope():
    catalog = read("src/Bot/ChromeNs/ShopProductCatalogService.cs")
    locator = read("src/Bot/ShopScope/ShopContextLocator.cs")
    window = read("src/Bot/Options/WndOption.xaml.cs")

    resolver = catalog.split("private static ShopContext ResolveShopForSeller", 1)[1].split(
        "private static string CachePath", 1
    )[0]
    cache_path = catalog.split("private static string CachePath", 1)[1].split(
        "private static ShopScopedSettingsStore RuleStore", 1
    )[0]
    rule_store = catalog.split("private static ShopScopedSettingsStore RuleStore", 1)[1].split(
        "internal static ShopCatalogSnapshot Load", 1
    )[0]

    assert "ResolveShopForSeller(seller)" in cache_path
    assert "ResolveShopForSeller(seller)" in rule_store
    assert "ShopSettingsScope.Current" in resolver
    assert "current.ShopKey" in resolver
    assert "ResolveCurrentOrBySellerNick(seller)" in resolver
    assert "ResolveRuntimeByShopKey(sellerNick)" in locator
    assert "IsQianniuShopKey(sellerNick)" in locator
    assert "shopStrategy = new ShopStrategyOptionsControl(_shop.ShopKey);" in window


def test_offline_product_reply_rules_are_optional_during_settings_initialization():
    feature = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")
    start = feature.index("try\n            {\n                _orderProductRulesUi = new ShopOrderProductRulesUi")
    end = feature.index("if (legacyStack != null)", start)
    optional_rules = feature[start:end]
    assert "catch (Exception ex)" in optional_rules
    assert "_orderProductRulesUi = null;" in optional_rules
    assert "Product-specific rules are optional" in optional_rules
    assert "Log.Exception(ex);" in optional_rules
