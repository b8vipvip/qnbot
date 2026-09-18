from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RPA = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.MultiShopDeskBinding.cs"


def test_inactive_seller_send_requests_authoritative_conversation_switch_without_weakening_gate():
    text = RPA.read_text(encoding="utf-8-sig")

    assert "RequestSellerActivationForPendingSend" in text
    assert "ActiveShopSessionRegistry.ValidateNativeSend(_qn, out isolationReason)" in text
    assert "RequestSellerActivationForPendingSend(seller, isolationReason);" in text
    assert "_qn.OpenChat(buyer);" in text
    assert "多客服发送检测到buyer已匹配但活动seller未切换" in text

    # Recovery must remain fail-closed for the current attempt. The existing retry path is
    # responsible for retrying only after onConversationChange has promoted the target seller.
    guard = text.index("if (!ActiveShopSessionRegistry.ValidateNativeSend(_qn, out isolationReason))")
    request = text.index("RequestSellerActivationForPendingSend(seller, isolationReason);", guard)
    rejection = text.index("return false;", request)
    assert guard < request < rejection


def test_readiness_probe_does_not_switch_visible_seller():
    text = RPA.read_text(encoding="utf-8-sig")
    start = text.index("internal bool IsSellerDeskBindingReady")
    readiness = text[start:]
    assert "ValidateNativeSend" in readiness
    assert "RequestSellerActivationForPendingSend" not in readiness
    assert "OpenChat(" not in readiness
