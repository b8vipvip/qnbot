from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BRIDGE = ROOT / "src" / "Bot" / "ShopScope" / "ShopScopedRuntimeBridge.cs"
SETTINGS = ROOT / "src" / "Bot" / "AssistWindow" / "Widget" / "RightPanel.SettingsEntry.cs"
BINDING = ROOT / "services" / "api-control-plane" / "bot_client_shop_binding.py"


def text(path):
    return path.read_text(encoding="utf-8-sig")


def test_conversation_change_recovers_active_seller_from_authoritative_or_duplicate_webview():
    source = text(BRIDGE)
    assert "qn.EvBuyerSwitched += Qn_EvBuyerSwitched" in source
    assert 'GetField("ForwardedInboundSourceSession"' in source
    assert "GetForwardedInboundSourceSession()" in source
    assert '"forwarded-onConversationChange:" + forwardedSource' in source
    assert 'ActivateFromVerifiedConversationChange(' in source
    assert 'ReassertCurrent("verified-onConversationChange")' in source
    assert "真实onConversationChange已晋升活动客服" in source
    assert "ObserveChatDialogActive(qn, \"authoritative-onConversationChange\")" not in source
    assert "重复页面onConversationChange仅更新本店买家，不切换活动客服" not in source

    registry = text(ROOT / "src" / "Bot" / "ChromeNs" / "MultiShopRuntimeSessionCoordinator.cs")
    activate = registry[
        registry.index("internal static bool ActivateFromVerifiedConversationChange"):
        registry.index("internal static bool ValidateNativeSend")
    ]
    assert "eventSellerNick" in activate
    assert "eventTargetId" in activate
    assert "expectedTargetId" in activate
    assert "if (buyer.Length == 0) return false;" in activate
    assert "return Activate(qn, sessionId, eventTargetId, true," in activate


def test_settings_click_promotes_visible_desk_seller_before_opening_window():
    source = text(SETTINGS)
    assert "var seller = DeskSellerBindingRegistry.GetSeller(desk)" in source
    assert "QN.FindExistingBySellerNick(seller)" in source
    assert "ActiveShopSessionRegistry.ActivateFromFocusedWebView(" in source
    assert '"settings-visible-desk"' in source
    assert 'ReassertCurrent("settings-visible-desk")' in source
    assert source.index("ActiveShopSessionRegistry.ActivateFromFocusedWebView(") < source.index("WndOption.MyShow(seller, Wnd)")


def test_shop_runtime_logs_are_materialized_and_mirrored_by_live_seller_identity():
    source = text(BRIDGE)
    assert "ShopByKey" in source
    assert "ShopBySellerRef" in source
    assert "LogReadyMarkers" in source
    assert "EnsureShopLogWriters()" in source
    assert 'Path.Combine(Paths.GetLogRoot(shop), "runtime.txt")' in source
    assert "店铺独立运行日志已就绪" in source
    assert 'text.IndexOf("shop=" + pair.Key' in source
    assert 'text.IndexOf("shopKey=" + pair.Key' in source
    assert "ShopIdentityResolver.Resolve(qn.Seller)" in source
    assert "StableSellerRef(seller)" in source
    assert 'return "seller#" + hash.ToString("x16").Substring(0, 10)' in source


def test_control_plane_normal_claim_rejects_mismatch_but_confirmed_force_can_repair_old_contamination():
    source = text(BINDING)
    assert "def _known_runtime_sellers" in source
    assert "seller_nicks_json" in source
    assert "def _validate_runtime_seller_identity" in source
    assert "allow_confirmed_correction: bool = False" in source
    assert '"code": "token_seller_mismatch"' in source
    assert "if allow_confirmed_correction:" in source
    assert "return True" in source
    assert "seller_identity_corrected = _validate_runtime_seller_identity(" in source
    assert "allow_confirmed_correction=bool(force)" in source
    assert "if rebound or seller_identity_corrected:" in source
    assert '"seller_identity_corrected": seller_identity_corrected' in source
    assert '"server_state_reset": rebound or seller_identity_corrected' in source
