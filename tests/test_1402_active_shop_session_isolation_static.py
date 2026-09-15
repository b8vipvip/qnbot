from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
COORDINATOR = ROOT / "src" / "Bot" / "ChromeNs" / "MultiShopRuntimeSessionCoordinator.cs"
RPA = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.MultiShopDeskBinding.cs"
OPTIONS = ROOT / "src" / "Bot" / "Options" / "WndOption.xaml.cs"
BINDING = ROOT / "src" / "Bot" / "Options" / "ShopBindingOptionsControl.cs"


def text(path):
    return path.read_text(encoding="utf-8-sig")


def test_active_shop_uses_focused_webview_and_stable_identity():
    source = text(COORDINATOR)
    assert "ActiveShopSessionRegistry" in source
    assert "ActiveShopWebViewProbe" in source
    assert "document.hasFocus" in source
    assert "visibilityState" in source
    assert "ShopIdentityResolver.Resolve(qn.Seller).ShopKey" in source
    assert "qn.Seller.TargetId" in source
    assert "CDP会话不一致" in source


def test_background_buyer_switch_cannot_elect_active_seller():
    source = text(COORDINATOR)
    start = source.index("private static void Qn_EvBuyerSwitched")
    end = source.index("private static void Qn_EvRecieveNewMessage", start)
    block = source[start:end]
    assert "ActivateFromFocusedWebView" not in block
    assert "ObserveChatDialogActive" not in block
    assert "ActiveShopSessionRegistry.IsActive(qn)" in block
    assert "ReassertCurrent" in block


def test_native_send_requires_active_seller_shop_and_session():
    source = text(RPA)
    assert "ActiveShopSessionRegistry.ValidateNativeSend(_qn" in source
    assert "多客服发送安全锁已阻止RPA" in source
    assert "DeskSellerBindingRegistry.IsSellerForDesk" in source


def test_settings_open_against_arbitrated_shop_not_stale_curqn():
    source = text(OPTIONS)
    assert "ActiveShopSessionRegistry.GetActiveSellerNick()" in source
    assert "设置窗口已锁定本店" in source
    assert "requestedSeller" in source


def test_each_shop_keeps_independent_token_contract():
    source = text(BINDING)
    assert "每个店铺仍使用独立 ShopKey 和独立 Bot 客户端令牌" in source
    assert "一个令牌只能绑定一个 ShopKey" in source
    assert "ValidateTokenBinding(candidate)" in source
