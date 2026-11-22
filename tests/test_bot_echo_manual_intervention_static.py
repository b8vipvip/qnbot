from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_explicit_bot_echo_is_checked_before_human_observation():
    source = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    marker_check = source.index("texts.FirstOrDefault(IsExplicitBotAuthoredReply)")
    mark_manual = source.index("ResponseProgressTracker.MarkManualIntervention")

    assert marker_check < mark_manual
    assert "CancelActiveBuyerGeneration" not in source
    assert "ResponseProgressTracker.MarkDeliveryConfirmed" in source
    assert "卖家消息多字段命中Bot署名，未判定人工回复" in source
    assert "人工客服回复已记录为对比学习证据，Bot任务继续" in source
    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in source
    assert "var shopKey = shop.ShopKey;" in source
    assert "ShopSettingsScope.Enter(shop)" in source


def test_bot_marker_fallback_accepts_supported_bracket_styles_only_at_message_end():
    source = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    assert 'EndsWith("[AI]", StringComparison.OrdinalIgnoreCase)' in source
    assert 'EndsWith("【AI】", StringComparison.OrdinalIgnoreCase)' in source
    assert 'EndsWith("［AI］", StringComparison.OrdinalIgnoreCase)' in source
    assert "char.IsWhiteSpace" in source
    assert "compact.EndsWith" in source


def test_seller_echo_checks_original_text_and_summary_before_human_observation():
    source = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    extract = source.index("ExtractMessageTextCandidates(message)")
    delivery = source.index("TryConfirmBotDelivery(seller, buyer, texts, shopKey)")
    marker = source.index("texts.FirstOrDefault(IsExplicitBotAuthoredReply)")
    observe = source.index("ResponseProgressTracker.MarkManualIntervention")

    assert extract < delivery < marker < observe
    assert "message.originalData.text" in source
    assert "message.summary" in source
    assert "Distinct(StringComparer.Ordinal)" in source
    assert "foreach (var candidate in texts)" in source


def test_runtime_monitor_rejects_foreign_seller_target_before_alias_or_manual_reply_learning():
    source = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    observe = source[
        source.index("private static void Qn_EvRecieveNewMessage"):
        source.index("private static void RecordBuyerSourceSort")
    ]

    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in observe
    assert "DirectOrderIdentityResolver.IdentityEquals(qnSeller, seller)" in observe
    assert "DirectOrderIdentityResolver.IsMessageFromShop(message, shop)" in observe
    assert observe.index("DirectOrderIdentityResolver.IsMessageFromShop(message, shop)") < observe.index("BuyerIdentityAliasService.ObserveMessage")
    assert observe.index("if (fromIsSeller == toIsSeller) continue;") < observe.index("BuyerIdentityAliasService.ObserveMessage")
