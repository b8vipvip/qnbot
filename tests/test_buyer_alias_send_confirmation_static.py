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

    assert "BuyerIdentityAliasService.AreEquivalent(sellerNick, echo.Buyer, buyerNick)" in block
    assert "echo.Text == text" in block
    assert "string.Equals(echo.ShopKey, shopKey, StringComparison.Ordinal)" in block


def test_connection_summary_is_not_overwritten_by_last_send_failure():
    source = read("src/Bot/ChromeNs/MyWebSocketServer.cs")

    start = source.index("private static string BuildSummary")
    end = source.index("public static ConnectionDiagnosticsSnapshot GetSnapshot", start)
    block = source[start:end]

    assert "sendOk" not in block
    assert "最近发送失败" not in block
    assert "wsOk && injectionOk && qnOk && sellerOk && uiOk && buttonOk && inputOk" in block
    assert "SendStatus = lastSendStatus" in source


def test_recent_seller_echo_history_is_bounded_and_does_not_lose_other_buyer_confirmations():
    source = read("src/Bot/ChromeNs/QN.cs")
    record = source.split("private void RecordSellerEcho", 1)[1].split("public bool HasRecentSellerEcho", 1)[0]
    lookup = source.split("public bool HasRecentSellerEcho", 1)[1].split("private bool IsBuyerMessage", 1)[0]
    assert "Queue<SellerEchoEntry>" in source
    assert "MaxRecentSellerEchoes = 512" in source
    assert "_recentSellerEchoes.Dequeue()" in record
    assert "_recentSellerEchoes.Enqueue" in record
    assert "ShopKey = shopKey" in record
    assert "_recentSellerEchoes.Any" in lookup
    assert "echo.ReceivedAt >= cutoff" in lookup
    assert "scopedShop.ShopKey, shopKey" in lookup
    assert "_lastSellerEcho" not in source


def test_learning_and_order_focus_read_scoped_echo_history_instead_of_old_private_fields():
    source = read("src/Bot/ChromeNs/QN.cs")
    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    focus = read("src/Bot/ChromeNs/NewOrderAttentionQueue.cs")
    assert "LastSellerEchoTimeForShop(snapshot.ShopKey)" in focus
    assert "TryGetRecentSellerEchoForLearning(shopKey, buyer, questionTime" in learning
    assert 'GetField("_lastSellerEcho' not in learning
    method = source.split("internal bool TryGetRecentSellerEchoForLearning", 1)[1].split("private bool IsBuyerMessage", 1)[0]
    assert "string.Equals(x.ShopKey, shopKey, StringComparison.Ordinal)" in method
    assert 'string.Equals(x.Buyer, (buyer ?? string.Empty).Trim(), StringComparison.Ordinal)' in method
    assert "LastOrDefault" in method
