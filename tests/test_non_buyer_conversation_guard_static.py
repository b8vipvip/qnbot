from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_shared_non_buyer_guard_uses_identity_and_structured_source_not_urls():
    code = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    guard = code[code.index("internal static class NonBuyerConversationGuard"):code.index("internal static class IncomingMessageSafety")]
    assert "self_identity" in guard
    assert "行业小二" in guard
    assert "服务商" in guard
    assert "cnalichn" in guard
    assert "1688" in guard
    assert "group" in guard
    assert "chatroom" in guard
    assert "platform_system_card" in guard
    assert "http://" not in guard
    assert "https://" not in guard
    assert "Regex" not in guard


def test_conversation_model_preserves_qianniu_source_metadata():
    code = read("src/DbEntity/Response/LocalUser.cs")
    for field in ("targetType", "conversationType", "scene", "category", "source", "channel"):
        assert f'JsonProperty("{field}")' in code


def test_foreground_guard_runs_before_alias_order_and_smart_reply():
    code = read("src/Bot/ChromeNs/QN.cs")
    start = code.index("private Task ProcessIncomingMessageAsync")
    end = code.index("private async Task ProcessBuyerBurstAsync")
    block = code[start:end]
    guard = block.index("NonBuyerConversationGuard.ShouldBlockMessage")
    alias = block.index("BuyerIdentityAliasService.ObserveMessage")
    order = block.index("OrderPlacedAutoReplyService.TryCreatePlan")
    safety = block.index("IncomingMessageSafety.Evaluate")
    assert guard < alias < order < safety


def test_conversation_switches_and_background_notifications_do_not_poison_current_buyer():
    code = read("src/Bot/ChromeNs/QN.cs")
    buyer_switch = code[code.index("private void Cdp_EvBuyerSwitched"):code.index("public static QN GetByNick")]
    assert buyer_switch.index("ShouldBlockConversation") < buyer_switch.index("Buyer = e.Buyer")
    seller_switch = code[code.index("private void Cdp_EvSellerSwitched"):code.index("private Task ProcessIncomingMessageAsync")]
    assert seller_switch.index("ShouldBlockConversation") < seller_switch.index("Buyer = e.Buyer")
    background = code[code.index("private void Cdp_EvShopRobotReceriveNewMessage"):code.index("private void Cdp_EvSellerSwitched")]
    assert background.index("ShouldBlockConversation") < background.index("ScheduleBackgroundMessageRecovery")
    active = code[code.index("public void SetActiveConversationByNick"):code.index("private void Cdp_EvShopRobotReceriveNewMessage")]
    assert "ShouldBlockIdentity" in active


def test_first_inquiry_and_returning_buyer_fast_paths_share_guard():
    fast = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")
    assert "ShouldBlockConversation(active.LoginID, active.Conversation" in fast
    assert "ShouldBlockIdentity(seller, buyer" in fast
    assert "IsReplyableFirstInquiryCandidate" in fast
    assert "ShouldBlockMessage(message, seller" in fast
    returning = read("src/Bot/ChromeNs/ReturningBuyerFirstReplyBridge.Messages.cs")
    assert "NonBuyerConversationGuard.ShouldBlockMessage" in returning


def test_recovery_guard_runs_before_recovered_order_card_and_buyer_dedupe():
    code = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    dispatch = code[code.index("private async Task ProcessRecoveredMessageWithKnownBuyerAsync"):code.index("private Task ProcessRecoveredBuyerMessageAfterMissAsync")]
    assert dispatch.index("ShouldBlockMessage") < dispatch.index("IsPotentialRecoveredOrderCard")
    buyer = code[code.index("private Task ProcessRecoveredBuyerMessageAfterMissAsync"):code.index("private static bool IsPotentialRecoveredOrderCard")]
    assert buyer.index("ShouldBlockMessage") < buyer.index("_handledBuyerMessageDeduplicator.TryAccept")
    assert "ShouldBlockConversation(e.Seller, e.Buyer" in code


def test_non_buyer_events_do_not_pollute_buyer_session_learning():
    code = read("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")
    assert "BuyerSessionAgent忽略非买家后台通知" in code
    observe = code[code.index("private static void ObserveMessage"):code.index("private static BuyerSessionEventKind ClassifyBuyerEvent")]
    assert observe.index("ShouldBlockMessage") < observe.index("OrderCardParser.TryParse")


def test_real_buyer_product_links_remain_supported_after_source_guard():
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")
    safety = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    assert "ConversationContextStore.IsProductLink(message, messageText)" in safety
    assert "RegisterProductLinkReply" in safety
    assert "https?://" in context


def test_qn_ingress_and_shared_non_buyer_guard_normalize_canonical_seller_identity():
    qn = read("src/Bot/ChromeNs/QN.cs")
    buyer = qn[qn.index("private bool IsBuyerMessage"):qn.index("private bool IsCurrentSellerEndpoint")]
    endpoint = qn[qn.index("private bool IsCurrentSellerEndpoint"):qn.index("public void SetActiveConversationByNick")]
    guard = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    non_buyer = guard[
        guard.index("internal static class NonBuyerConversationGuard"):
        guard.index("internal static class IncomingMessageSafety")
    ]

    assert "DirectOrderIdentityResolver.IdentityEquals(m.fromid.nick, _seller.Nick)" in buyer
    assert "DirectOrderIdentityResolver.IdentityEquals(m.toid.nick, _seller.Nick)" in buyer
    assert "DirectOrderIdentityResolver.IdentityEquals(endpointNick, sellerNick)" in endpoint
    assert "DirectOrderIdentityResolver.IdentityEquals(loginNick, sellerNick)" in endpoint
    assert "DirectOrderIdentityResolver.IdentityEquals(sellerNick, fromNick)" in non_buyer
    assert "DirectOrderIdentityResolver.IdentityEquals(sellerNick, candidateNick)" in non_buyer
