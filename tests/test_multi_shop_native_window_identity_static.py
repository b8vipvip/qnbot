from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_qianniu_reception_discovery_does_not_require_one_exact_window_title():
    finder = read("src/Bot/Automation/ChatDeskNs/Automators/QnAccountFinder.cs")
    assert '"Qt5152QWindowIcon",\n                        null,' in finder
    assert "IsReceptionCandidate" in finder
    assert "GetWindowRectangle" in finder
    assert "rect.Width < 560 || rect.Height < 380" in finder
    assert "MatchUniqueSellerFromTitle" in finder
    assert "current Qianniu can host several logged-in sellers in one AliWorkbench process" in finder
    assert "Never guess between two online shops" in finder


def test_shared_hwnd_registry_allows_multiple_sellers_but_one_active_seller():
    registry = read("src/Bot/Automation/ChatDeskNs/DeskSellerBindingRegistry.cs")
    rpa = read("src/Bot/ChromeNs/QNRpa.MultiShopDeskBinding.cs")
    assert "SellerToHwnd" in registry
    assert "ActiveSellerByHwnd" in registry
    assert "同一seller不能绑定两个Desk" in registry
    assert "同一Desk不能绑定两个seller" not in registry
    assert "Desk.Create(new QnChatWnd(seller, hwnd, pid))" not in registry
    assert "BindForegroundSeller" in registry
    assert "MarkActiveSeller" in registry
    assert "共享千牛窗口活动客服已切换" in registry
    assert "DeskSellerBindingRegistry.FindSellerDesk(seller)" in rpa
    assert "desks.Count == 1 && RuntimeSellerCount() <= 1" in rpa
    assert "ActiveShopSessionRegistry.ValidateNativeSend(_qn" in rpa
    assert "DeskSellerBindingRegistry.IsSellerForDesk(desk, seller)" in rpa
    assert "当前可见千牛输入框不属于目标seller" in rpa
    assert "禁止跨客服写入/发送" in rpa


def test_focused_webview_not_background_buyer_switch_owns_shared_desk():
    coordinator = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")
    scanner = read("src/Bot/ControllerNs/DeskScanner.cs")
    settings = read("src/Bot/AssistWindow/Widget/RightPanel.SettingsEntry.cs")

    assert "ActiveShopWebViewProbe" in coordinator
    assert "document.hasFocus" in coordinator
    assert "ActivateFromFocusedWebView" in coordinator
    seller_handler = coordinator.split("private static void Qn_EvSellerSwitched", 1)[1].split(
        "private static void Qn_EvBuyerSwitched", 1
    )[0]
    buyer_handler = coordinator.split("private static void Qn_EvBuyerSwitched", 1)[1].split(
        "private static void Qn_EvRecieveNewMessage", 1
    )[0]
    receive_handler = coordinator.split("private static void Qn_EvRecieveNewMessage", 1)[1].split(
        "private static void EnsureQn", 1
    )[0]
    assert "ObserveChatDialogActive" in seller_handler
    assert "ActiveShopSessionRegistry.IsActive(qn)" in buyer_handler
    assert "ActivateFromFocusedWebView" not in buyer_handler
    assert "BindForegroundSeller" not in receive_handler

    assert "DeskSellerBindingRegistry.BindResolvedSeller" in scanner
    assert "EnsureVisibleForMultiShopAttachedMode" in scanner
    assert "DeskSellerBindingRegistry.GetSeller(desk)" in settings
    assert "同一个千牛接待窗口可以承载多个客服账号" in settings
    assert "只允许当前活动客服使用当前可见输入框" in settings


def test_attached_bot_ui_accepts_only_its_proven_seller():
    robot = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MultiShopSession.cs")
    props = read("src/Bot/Directory.Build.props")
    assert "DeskSellerBindingRegistry.IsSellerForDesk(_desk, seller)" in robot
    assert "AssistWindow\\WndAssist.MultiShopAttached.cs" in props
    assert "Automation\\ChatDeskNs\\DeskSellerBindingRegistry.cs" in props
