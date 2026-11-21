from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_send_confirmation_observes_structured_conversation_alias_before_comparison():
    source = read("src/Bot/ChromeNs/QN.cs")

    start = source.index("private async Task<bool> EnsureActiveBuyerForSendAsync")
    end = source.index("public async void SendImageAsync", start)
    block = source[start:end]
    observe = block.index("BuyerIdentityAliasService.Observe(")
    equivalent = block.index("BuyerIdentityAliasService.AreEquivalent(sellerNick, currentNick, buyer)")

    assert observe < equivalent
    assert "currentConversation.Nick" in block
    assert "currentConversation.Display" in block
    assert "currentConversation.TargetId" in block
    assert "if (currentNick == buyer)" not in block
    assert "ActiveBuyerConfirmDeadlineMs" in block
    assert "无法在会话确认总预算内确认当前会话为目标买家" in block


def test_incoming_messages_learn_buyer_nick_display_aliases_before_dedup():
    source = read("src/Bot/ChromeNs/QN.cs")

    start = source.index("private Task ProcessIncomingMessageAsync")
    end = source.index("private async Task ProcessBuyerBurstAsync", start)
    block = source[start:end]
    guard = block.index("NonBuyerConversationGuard.ShouldBlockMessage")
    observe = block.index("BuyerIdentityAliasService.ObserveMessage")
    dedup = block.index("_incomingMessageDeduplicator.TryAccept")

    # Non-buyer traffic must be rejected before it can teach aliases, while real buyer aliases
    # must still be learned before the ordinary duplicate gate.
    assert guard < observe < dedup


def test_seller_echo_accepts_only_known_equivalent_buyer_aliases():
    source = read("src/Bot/ChromeNs/QN.cs")

    start = source.index("public bool HasRecentSellerEcho")
    block = source[start: start + 1200]

    assert "BuyerIdentityAliasService.AreEquivalent(sellerNick, _lastSellerEchoBuyer, buyerNick)" in block
    assert "_lastSellerEchoText == text" in block


def test_connection_summary_is_not_overwritten_by_last_send_failure():
    source = read("src/Bot/ChromeNs/MyWebSocketServer.cs")

    start = source.index("private static string BuildSummary")
    end = source.index("public static ConnectionDiagnosticsSnapshot GetSnapshot", start)
    block = source[start:end]

    assert "sendOk" not in block
    assert "最近发送失败" not in block
    assert "wsOk && injectionOk && qnOk && sellerOk && uiOk && buttonOk && inputOk" in block
    assert "SendStatus = lastSendStatus" in source
