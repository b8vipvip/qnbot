from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_commerce_services_are_compiled_for_wpf_and_main_project():
    targets = read("src/Directory.Build.targets")
    assert "ChromeNs\\CommerceContextService.cs" in targets
    assert "ChromeNs\\CommerceKnowledgeLearningBridge.cs" in targets


def test_structured_order_context_is_final_conversation_enrichment():
    text = read("src/Bot/ChromeNs/ConversationStateService.cs")
    assert "public CommerceContextSnapshot CommerceContext" in text
    progress = text.index("ConversationProgressGuardService.EnrichState(")
    commerce = text.index("CommerceContextService.EnrichState(")
    assert commerce > progress
    assert "CommerceContextService.BuildPromptAddon(state.CommerceContext)" in text


def test_commerce_context_prefers_verified_order_over_text_fallback():
    text = read("src/Bot/ChromeNs/CommerceContextService.cs")
    structured = text.index("OrderGuidanceDeliveryGuard.TryGetLatestOrderSnapshot")
    fallback = text.index("ApplyConversationFallback(result")
    assert structured < fallback
    assert "ApplyOrder(result, order);\n                return result;" in text
    assert 'SetPhase(target, "paid", "已付款/待履约")' in text
    assert 'SetPhase(target, "unpaid", "已下单/待付款")' in text
    assert 'SetPhase(target, "after_sale", "售后/退款处理中")' in text


def test_scenario_scope_is_atomic_and_excludes_customer_or_order_identity():
    text = read("src/Bot/ChromeNs/CommerceContextService.cs")
    assert "BuildPolicyScopeToken" in text
    assert '"commerce_scope[phase="' in text
    assert '"|fulfillment="' in text
    assert '"|item="' in text
    assert '"|sku="' in text
    assert '"|category="' in text
    method = text[text.index("public static string BuildPolicyScopeToken"):text.index("public static List<string> BuildScopeTerms")]
    assert "OrderId" not in method
    assert "Buyer" not in method


def test_order_context_query_is_read_only_clone_boundary():
    text = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs")
    assert "public static bool TryGetLatestOrderSnapshot" in text
    method = text[text.index("public static bool TryGetLatestOrderSnapshot"):text.index("public static bool CanCreateFollowUp")]
    assert "FindLatestInternal" in method
    assert "CloneSnapshot(record.Snapshot)" in method
    assert "SaveInternal" not in method
    assert "MarkDelivered" not in method


def test_terminal_order_events_can_refresh_commerce_truth_without_reopening_guidance():
    text = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs")
    observe = text[text.index("public static void ObserveOrder"):text.index("public static bool IsExplicitBuyerFollowUp")]
    assert "snapshot.EventType != OrderEventType.Created" not in observe
    assert "MergeSnapshot(record.Snapshot, snapshot)" in observe
    merge = text[text.index("private static void MergeSnapshot"):text.index("private static bool IsGuidanceTerminal")]
    assert "incomingTerminal || !targetTerminal" in merge
    assert "target.EventType = incoming.EventType" in merge
    terminal = text[text.index("private static bool IsGuidanceTerminal"):text.index("private static string Hash")]
    assert "OrderEventType.Closed" in terminal
    assert "OrderEventType.RefundRequested" in terminal
    followup = text[text.index("public static bool CanCreateFollowUp"):text.index("public static bool ShouldSuppressBeforeSend")]
    assert "IsGuidanceTerminal(record.Snapshot)" in followup


def test_post_order_smart_reply_cannot_finish_as_fixed_faq_direct_answer():
    text = read("src/Bot/ChromeNs/ConversationProgressGuardService.cs")
    method = text[text.index("public static bool RequiresContextualHandling"):text.index("public static bool AllowKnowledge")]
    assert "commerce.HasStructuredOrder" in method
    assert 'commerce.PurchasePhase, "post_order_unverified"' in method
    assert "return true;" in method


def test_commerce_scoped_knowledge_has_one_central_policy_authority():
    policy = read("src/Bot/ChromeNs/KnowledgePolicyProfileService.cs")
    evaluate = policy[policy.index("public static KnowledgePolicyEvaluation Evaluate"):policy.index("public static void RecordRouteSelection")]
    assert "ReadAtomicCommerceScope(profile.RequiredContext)" in evaluate
    assert "CommerceContextService.BuildPolicyScopeToken" in evaluate
    assert "Excluded = true" in evaluate
    assert "电商场景键不匹配" in evaluate
    assert evaluate.index("ReadAtomicCommerceScope") < evaluate.index("if (!IsEnabled())")

    progress = read("src/Bot/ChromeNs/ConversationProgressGuardService.cs")
    allow = progress[progress.index("public static bool AllowKnowledge"):progress.index("public static void AddValidationIssues")]
    assert "ReadAtomicCommerceScope" not in allow
    assert "BuildPolicyScopeToken" not in allow


def test_v2_direct_reply_yields_to_commerce_agent_when_verified_order_exists():
    text = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    gate = text.index("OrderGuidanceDeliveryGuard.TryGetLatestOrderSnapshot")
    resolve = text.index("KnowledgeEngineV2Service.Resolve")
    assert gate < resolve
    assert "Knowledge Engine V2已让出终态直答权" in text
    assert "await inner(lease);" in text[gate:resolve]


def test_validator_only_promotes_verified_order_context_to_authoritative_evidence():
    text = read("src/Bot/ChromeNs/PreSendAnswerValidator.cs")
    validate = text[text.index("public static AnswerValidationResult Validate"):text.index("public static string BuildEvidenceText")]
    assert validate.index("BuildState(seller, buyer, question)") < validate.index("BuildAuthoritativeEvidence(knowledge, state)")
    evidence = text[text.index("private static string BuildAuthoritativeEvidence"):text.index("private static ConversationStateSnapshot BuildState")]
    assert "commerce != null && commerce.HasStructuredOrder" in evidence
    assert "购买阶段" in evidence
    assert "订单状态" in evidence
    assert "付款状态" in evidence
    assert "conversation" not in evidence.lower()


def test_human_correction_preserves_old_answer_and_scopes_new_answer():
    text = read("src/Bot/ChromeNs/CommerceKnowledgeLearningBridge.cs")
    assert "ConversationSessionLearningService.ReportsChanged += OnReportsChanged" in text
    assert "BuildPolicyScopeToken" in text
    assert 'SourceType = "场景分流保留-纠错前答案"' in text
    assert "oldProfile.DoNotApplyWhen = MergeCondition(oldProfile.DoNotApplyWhen, scopeToken)" in text
    assert "currentProfile.ApplyWhen = scopeToken" in text
    assert "currentProfile.RequiredContext = scopeToken" in text
    assert "currentProfile.AnswerMode = KnowledgeAnswerModes.Contextual" in text
    assert "currentProfile.Confidence = Math.Max(currentProfile.Confidence, 0.94)" in text


def test_style_and_business_facts_remain_separate_learning_concerns():
    session = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    commerce = read("src/Bot/ChromeNs/CommerceKnowledgeLearningBridge.cs")
    assert "SaveStyleProfile" in session
    assert "reply_style_profile" in session
    assert "人工客服最终有效回复优先级最高" in session
    assert "StoreReplyStyleProfileEntity" not in commerce


def test_commerce_change_does_not_touch_reliable_send_implementation():
    # Architectural guard: new commerce services consume facts and governance only.
    context = read("src/Bot/ChromeNs/CommerceContextService.cs")
    learning = read("src/Bot/ChromeNs/CommerceKnowledgeLearningBridge.cs")
    joined = context + learning
    assert "SendTextWithRetryAsync" not in joined
    assert "QNRpa" not in joined
    assert "CDPClient" not in joined
