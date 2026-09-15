from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_qianniu_one_hwnd_can_host_multiple_authenticated_sellers():
    source = (ROOT / "src" / "Bot" / "Automation" / "ChatDeskNs" / "DeskSellerBindingRegistry.cs").read_text(
        encoding="utf-8-sig"
    )

    assert "ActiveSellerByHwnd" in source
    assert "SellerToHwnd[seller] = desk.Hwnd.Handle;" in source
    assert "同一Desk不能绑定两个seller" not in source
    assert "desk.Dispose();" not in source
    assert "new QnChatWnd(seller, hwnd, pid)" not in source
    assert "共享千牛窗口活动客服已切换" in source


def test_native_rpa_is_gated_by_active_seller_not_only_shared_hwnd():
    source = (ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.MultiShopDeskBinding.cs").read_text(
        encoding="utf-8-sig"
    )

    assert "DeskSellerBindingRegistry.IsSellerForDesk(desk, seller)" in source
    assert "当前可见千牛输入框不属于目标seller" in source
    assert "禁止跨客服写入/发送" in source
    assert "RPA已绑定当前活动客服的千牛窗口" in source


def test_attached_bot_ui_only_follows_active_seller_on_shared_desk():
    source = (ROOT / "src" / "Bot" / "AssistWindow" / "Widget" / "Robot" / "CtlRobot.MultiShopSession.cs").read_text(
        encoding="utf-8-sig"
    )

    assert "DeskSellerBindingRegistry.IsSellerForDesk(_desk, seller)" in source
    assert "txtSeller.Text = seller;" in source
    assert "txtBuyer.Text = buyer.Length == 0" in source
