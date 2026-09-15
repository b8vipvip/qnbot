from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BRIDGE = ROOT / "src" / "Bot" / "ShopScope" / "ShopScopedRuntimeBridge.cs"
BINDING = ROOT / "services" / "api-control-plane" / "bot_client_shop_binding.py"


def text(path):
    return path.read_text(encoding="utf-8-sig")


def test_authoritative_conversation_change_recovers_active_seller_without_forwarded_duplicates():
    source = text(BRIDGE)
    assert "qn.EvBuyerSwitched += Qn_EvBuyerSwitched" in source
    assert 'GetField("ForwardedInboundSourceSession"' in source
    assert "GetForwardedInboundSourceSession()" in source
    assert "重复页面onConversationChange仅更新本店买家，不切换活动客服" in source
    assert 'ObserveChatDialogActive(qn, "authoritative-onConversationChange")' in source
    assert 'ReassertCurrent("authoritative-onConversationChange")' in source


def test_shop_runtime_logs_are_mirrored_by_shop_key_or_stable_seller_ref():
    source = text(BRIDGE)
    assert "ShopByKey" in source
    assert "ShopBySellerRef" in source
    assert 'Path.Combine(Paths.GetLogRoot(shop), "runtime.txt")' in source
    assert 'text.IndexOf("shop=" + pair.Key' in source
    assert 'text.IndexOf("shopKey=" + pair.Key' in source
    assert 'return "seller#" + hash.ToString("x16").Substring(0, 10)' in source


def test_control_plane_rejects_known_token_for_different_runtime_seller_even_on_force():
    source = text(BINDING)
    assert "def _known_runtime_sellers" in source
    assert "seller_nicks_json" in source
    assert "def _validate_runtime_seller_identity" in source
    assert '"code": "token_seller_mismatch"' in source
    assert "_validate_runtime_seller_identity(conn, client_id, seller)" in source
    validate_pos = source.index("_validate_runtime_seller_identity(conn, client_id, seller)")
    rebound_pos = source.index("if bound and bound != shop_key and not force")
    assert validate_pos < rebound_pos
