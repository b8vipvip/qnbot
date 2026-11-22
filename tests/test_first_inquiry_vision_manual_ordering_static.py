from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_off_hours_suppresses_first_inquiry_before_any_reservation_or_resolution():
    source = read("src/Bot/ChromeNs/QN.RuntimeSafety.cs")

    assert "ShouldSuppressForOffHours(seller, buyer, shopKey)" in source
    assert "当前处于下班自动回复时段，由下班回复独占本轮" in source
    assert source.index("ShouldSuppressForOffHours(seller, buyer, shopKey)") < source.index("var resolved = RunInShopScope")


def test_first_inquiry_image_keeps_vision_route_after_greeting_reservation():
    source = read("src/Bot/ChromeNs/VisionMessageDecision.cs")

    assert "var firstPrepared = FirstInquiryFixedReplyService.TryPrepare" in source
    assert 'var isImage = string.Equals(safetyDecision.MessageLabel, "[图片]"' in source
    assert "safetyDecision.ShouldCallAi = true;" in source
    assert "Kind = VisionDecisionKind.Vision" in source
    assert "首条咨询固定回复先发送，随后继续图片视觉理解" in source
    assert source.index("if (isImage)") < source.index("if (safetyDecision.ShouldCallAi)")


def test_order_auto_reply_is_classified_before_first_inquiry_and_vision_pipeline():
    source = read("src/Bot/ChromeNs/QN.cs")

    order_index = source.index("OrderPlacedAutoReplyService.TryCreatePlan")
    safety_index = source.index("IncomingMessageSafety.Evaluate", order_index)
    vision_index = source.index("VisionMessageDecision.Decide", safety_index)
    enqueue_index = source.index("_buyerMessageBurstCoordinator.Enqueue", vision_index)

    assert order_index < safety_index < vision_index < enqueue_index
    assert "return orderPlan == null" in source[order_index:safety_index]
    assert "ProcessOrderPlacedReplyAsync(orderPlan)" in source[order_index:safety_index]


def test_stale_manual_reply_is_detected_before_nonblocking_learning_observation():
    source = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    assert "LatestBuyerSourceSort" in source
    assert "var incomingBuyer = BuyerIdentityAliasService.ResolveInternalNick(seller, from);" in source
    assert "RecordBuyerSourceSort(seller, incomingBuyer, message, shopKey);" in source
    assert "if (fromIsSeller == toIsSeller) continue;" in source
    assert "IsSellerReplyOlderThanLatestBuyerTurn(seller, buyer, message, shopKey)" in source
    assert "return buyerSort > sellerSort;" in source
    guard_index = source.index("if (IsSellerReplyOlderThanLatestBuyerTurn")
    observe_index = source.index("ResponseProgressTracker.MarkManualIntervention", guard_index)
    assert guard_index < observe_index
    assert "CancelActiveBuyerGeneration" not in source


def test_system_tips_do_not_advance_manual_takeover_buyer_ordering():
    source = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    assert "ConversationContextStore.IsPlatformSystemTip(message, text)" in source
    assert "ConversationContextStore.IsWithdrawalNotice(message, text)" in source
