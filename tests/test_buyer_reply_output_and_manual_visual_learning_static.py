from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_internal_english_reasoning_is_detected_before_qianniu_send():
    guard = read("src/Bot/ChromeNs/BuyerReplyOutputGuard.cs")
    send = read("src/Bot/ChromeNs/QNRpa.ReliableSend.cs")

    for marker in [
        "we\\s+need",
        "respond\\s+(?:in\\s+)?chinese",
        "likely\\s+(?:say|reply)",
        "one\\s+sentence",
        "internal\\s+reasoning",
    ]:
        assert marker in guard
    assert "异常英文文本" in guard
    assert "AllowedShortLatinTokenRegex" in guard

    validation = send.index("BuyerReplyOutputGuard.TryNormalizeForBuyer(expected")
    editor_read = send.index("TryGetEditorText(out text)")
    assert validation < editor_read
    assert "已阻止异常AI内容发送给买家" in send
    assert "发送前内容安全检查" in send


def test_manual_agent_fragments_are_combined_and_attached_to_visual_semantics():
    source = read("src/Bot/ChromeNs/ManualVisualReplyLearningService.cs")
    visual_store = read("src/Bot/ChromeNs/VisualKnowledgeLearningService.cs")

    assert "VisionImageCacheService.TryGetRecentReference" in source
    assert "pending.Fragments.Add" in source
    assert 'string.Join("；", parts)' in source
    assert "VisualKnowledgeObservationEntity" in source
    assert "VisualKnowledgeEntryEntity" in source
    assert 'SourceType = "视觉人工即时学习"' in source
    assert 'current.Status = "已通过人工即时回复学习"' in visual_store
    assert "MergeTags(observation.VisualTags, ExtractManualTags(answer))" in source
    assert "SendDeliveryWatchdog.IsKnownBotAnswer" in source


def test_manual_visual_learning_waits_for_inflight_vision_instead_of_cancelling_it():
    source = read("src/Bot/ChromeNs/ManualVisualReplyLearningService.cs")
    withdrawal = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")

    assert "for (var attempt = 0; attempt < 60; attempt++)" in source
    assert "await Task.Delay(2000)" in source
    assert "LoadObservation(pending)" in source
    poll = source[
        source.index("for (var attempt = 0; attempt < 60; attempt++)"):
        source.index("if (observation != null", source.index("for (var attempt = 0; attempt < 60; attempt++)"))
    ]
    assert "VisionImageCacheService.IsMessageKeyWithdrawn" in poll
    assert "VisualKnowledgeLearningService.IsObservationWithdrawn" in poll
    assert poll.index("IsMessageKeyWithdrawn") < poll.index("LoadObservation(pending)")
    assert "Vision.ExecuteAsync(task, CancellationToken.None)" in withdrawal
    assert "人工视觉回复等待两分钟仍未取得图片语义" in source


def test_non_visual_session_learning_keeps_human_evidence_primary_and_mode_gates_synthesis():
    service = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    bridge = read("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")

    assert "InactivityMinutes = 5" in service
    assert "SellerQuietSeconds = 30" in service
    assert "ConversationSessionLearningService.ObserveLiveMessage" in bridge
    assert 'suggestion.Confidence < 0.86' in service
    for evidence in [
        '"manual_reply"',
        '"manual_correction"',
        '"withdrawn_bot_then_manual"',
        '"repeated_human_pattern"',
    ]:
        assert evidence in service
    assert "人工最终有效回复优先于Bot旧答案" in service

    # AI-first retains the historical human-evidence-only boundary. Local-first adds a
    # separate, stricter conversation_synthesis path rather than reclassifying bot_only.
    assert "当前店铺启用了AI优先模式。保持保守学习" in service
    assert 'suggestion.EvidenceType == "bot_only"' in service
    assert 'suggestion.EvidenceType == "insufficient"' in service
    assert 'localFirst && suggestion.EvidenceType == "conversation_synthesis"' in service
    assert "本地优先会话综合知识置信度低于0.92" in service


def test_startup_and_legacy_build_include_new_services():
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    assert "ManualVisualReplyLearningService.Initialize();" in app
    assert "BuyerReplyOutputGuard.cs" in targets
    assert "ManualVisualReplyLearningService.cs" in targets


def test_manual_visual_learning_accepts_canonical_seller_echo_with_stable_shop_identity():
    source = read("src/Bot/ChromeNs/ManualVisualReplyLearningService.cs")
    method = source[
        source.index("private static void ObserveSellerReplyScoped"):
        source.index("private static string ExtractMessageText")
    ]

    assert "ShopSettingsScope.Current" in method
    assert "DirectOrderIdentityResolver.IdentityEquals(from, seller)" in method
    assert "DirectOrderIdentityResolver.IsMessageFromShop(message, shop)" in method

    # Seller login nickname and stable target-ID checks live in the shared
    # authoritative identity helper, not duplicated in this event adapter.
    identity = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    is_message_from_shop = identity[
        identity.index("public static bool IsMessageFromShop"):
        identity.index("private static bool Candidate")
    ]
    assert "IdentityEquals(loginNick, seller)" in is_message_from_shop
    assert "shop.HasStableSellerId" in is_message_from_shop
    assert "BuyerIdentityAliasService.ObserveMessage(seller, message)" in method
    assert "BuyerIdentityAliasService.ResolveInternalNick(seller, buyer)" in method

def test_recent_visual_context_initializes_out_values_when_shop_resolution_fails():
    source = read("src/Bot/ChromeNs/RecentVisualContextService.cs")
    account = source.split(
        "public static bool TryGetRecentKugouAccountEvidence(\n            string seller", 1
    )[1].split(
        "public static bool TryGetRecentKugouAccountEvidence(\n            string shopKey", 1
    )[0]
    order_photo = source.split(
        "public static bool TrySatisfyOrderPhotoRequirement(\n            string seller", 1
    )[1].split(
        "public static bool TrySatisfyOrderPhotoRequirement(\n            string shopKey", 1
    )[0]

    assert account.index("evidence = null;") < account.index("ResolveShop")
    assert order_photo.index("evidence = string.Empty;") < order_photo.index("ResolveShop")


def test_manual_visual_learning_never_binds_a_reply_to_a_different_recent_image():
    source = read("src/Bot/ChromeNs/ManualVisualReplyLearningService.cs")
    loader = source[
        source.index("private static VisualKnowledgeObservationEntity LoadObservation("):
        source.index("private static VisualKnowledgeEntryEntity UpsertVisualKnowledge(")
    ]
    fallback = loader[loader.index("var since ="):]

    assert "pending.MessageKey" in loader
    assert "string.Equals(x.MessageKey, pending.MessageKey, StringComparison.Ordinal)" in fallback
    assert "SameBuyer(pending.Seller, x.Buyer, pending.Buyer)" in fallback


def test_manual_visual_learning_fails_closed_when_bot_echo_provenance_is_unavailable():
    source = read("src/Bot/ChromeNs/ManualVisualReplyLearningService.cs")
    classifier = source[
        source.index("private static bool IsBotReply("):
        source.index("private static double Similarity(")
    ]
    observe = source[
        source.index("private static void ObserveSellerReplyScoped("):
        source.index("private static async Task LearnWhenReadyAsync(")
    ]

    assert "IsBotReply(seller, buyer, text, shopKey, replyAt)" in observe
    assert "BotConversationHistoryStore.TryLoadRangeComplete(" in classifier
    assert "if (!BotConversationHistoryStore.TryLoadRangeComplete(" in classifier
    assert "return true;" in classifier
    assert "NormalizeBotAnswer(card.Answer)" in classifier
    assert "card.AnswerReadyAtTicks" in classifier
