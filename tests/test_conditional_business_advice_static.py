from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_image_knowledge_and_vision_prompt_are_domain_neutral():
    image = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    prompt = read("src/Bot/ChromeNs/VisionRequestService.cs")
    matcher = image[image.index("public static bool TryMatch("):image.index("private sealed class HitInfoRuleSet")]

    assert "HitInfo" in matcher
    assert "TryMatchHitInfo" in matcher
    assert "legacy_ocr_similarity" in matcher
    assert "不预设图片一定属于设备、电视、账号、会员、充值" in prompt
    assert "所有行业与店铺类型" in prompt
    assert "电视应用界面" not in prompt
    assert "电视系统自带" not in prompt


def test_conditional_advice_uses_generic_typed_evidence_requirements():
    source = read("src/Bot/ChromeNs/ConditionalBusinessAdviceService.cs")
    for token in (
        "evidence:image.matches_record",
        "evidence:order.paid",
        "evidence:order.open",
        "evidence:order.product_matches_record",
    ):
        assert token in source
    for domain_specific in ("海信", "聚好看", "NewTV ID", "电视会员", "酷狗"):
        assert domain_specific not in source


def test_knowledge_v2_skips_only_typed_evidence_terms_not_normal_scope_checks():
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")
    applicable = semantics[
        semantics.index("public static bool IsApplicable"):
        semantics.index("public static string FactKey")
    ]
    assert "ConditionalBusinessAdviceService.IsEvidenceRequirement(x)" in applicable
    assert "ScopeTermMatches(x, query)" in applicable
    assert "var exclusions = NormalizeScopeTerms(record.Exclusions);" in applicable
    assert "var conditions = NormalizeScopeTerms(record.Conditions);" in applicable


def test_conditional_refund_advice_requires_paid_open_order_and_current_image():
    source = read("src/Bot/ChromeNs/ConditionalBusinessAdviceService.cs")
    build = source[
        source.index("public static bool TryBuild"):
        source.index("public static string BuildImageEvidenceText")
    ]
    assert "commerce.HasStructuredOrder" in build
    assert "commerce.IsPaid != true" in build
    assert 'commerce.PurchasePhase, "paid"' in build
    assert "commerce.OrderId" in build
    assert "GetLatestImageObservation(shopKey, seller, buyer)" in build
    assert "MatchedImageKnowledgeId" in build
    assert "MatchedImageKnowledgeId" in build
    assert "order.PaidAt" in build
    assert "imageObservedAt < orderEvidenceTime" in build
    assert "VisualImageKnowledgeService.Load(shopKey, seller)" in build
    assert "IsImageKnowledgeMatchStillValid(shopKey, seller, image)" in build
    assert "paidOpenOrders.Count > 1" in build
    assert ".GroupBy(x => x.OrderId.Trim(), StringComparer.Ordinal)" in build


def test_refund_suggestion_never_claims_or_executes_a_refund():
    source = read("src/Bot/ChromeNs/ConditionalBusinessAdviceService.cs")
    assert "SafeRefundSuggestionRegex" in source
    assert "UnsafeRefundActionRegex" in source
    assert "已退款|退款成功|退款已完成" in source
    assert "自动退款|直接退款|立即退款" in source
    assert "建议.{0,24}申请退款" in source
    assert "CompleteDelivery" in source


def test_order_terminal_states_and_fulfilled_orders_block_refund_advice():
    source = read("src/Bot/ChromeNs/ConditionalBusinessAdviceService.cs")
    method = source[
        source.index("private static bool IsPaidAndOpen"):
        source.index("private static DateTime SafeTime")
    ]
    assert "OrderEventType.Closed" in method
    assert "OrderEventType.RefundRequested" in method
    assert "ClosedOrFulfilledRegex.IsMatch(status)" in method
    assert "order.IsPaid != true" in method
    assert "OrderEventType.Paid || PaidRegex.IsMatch(status)" in method


def test_image_match_is_revalidated_against_current_shop_rules():
    source = read("src/Bot/ChromeNs/ConditionalBusinessAdviceService.cs")
    method = source[
        source.index("private static bool IsImageKnowledgeMatchStillValid"):
        source.index("private static bool MatchesImage")
    ]
    assert "VisualImageKnowledgeService.Load(shopKey, seller)" in method
    assert "VisualImageKnowledgeService.TryMatch" in method
    assert "image.OcrImageSha256" in method
    assert "currentMatch.KnowledgeId" in method


def test_newer_unmatched_or_withdrawn_image_supersedes_an_older_match():
    recent = read("src/Bot/ChromeNs/RecentVisualContextService.cs")
    method = recent[
        recent.index("internal static VisualKnowledgeObservationEntity GetLatestImageObservation"):
        recent.index("public static bool TryGetRecentAccountEvidence")
    ]
    assert "LoadRecent(" in method
    assert "OcrImageSha256" in method
    assert "FirstOrDefault()" in method
    assert "IsWithdrawnObservation" in method
    assert "return latest;" in method

def test_conditional_advice_delivery_is_shop_buyer_order_and_record_scoped():
    source = read("src/Bot/ChromeNs/ConditionalBusinessAdviceService.cs")
    key = source[
        source.index("private static string BuildLedgerKey"):
        source.index("private static string GetLedgerPath")
    ]
    assert "shopKey ?? string.Empty" in key
    assert "seller ?? string.Empty" in key
    assert "buyer ?? string.Empty" in key
    assert "orderId ?? string.Empty" in key
    assert "adviceKey ?? string.Empty" in key
    assert "CrossProcessAtomicStateFile.Acquire" in source
    assert "WriteAllTextAtomic" in source
    assert "已经发送" in source
    assert "发送状态不确定" in source
    assert "DateTime.MaxValue" in source
    assert "order.PaidAt" in source
    assert "DeliveryUncertain = true" in source


def test_text_and_image_reply_paths_share_conditional_advice_guard():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    ocr = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    vision = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")
    legacy_vision = read("src/Bot/ChromeNs/QN.cs")

    assert "CommerceReplyAuthorityService.Evaluate" in runtime
    assert "conditionalOnly" in runtime
    assert "TryAuthorizeRefundSuggestion" in runtime
    assert "TryReserveDelivery" in runtime
    assert "CompleteDelivery" in runtime
    assert "CommerceReplyAuthorityService.Evaluate" in ocr
    assert "TryBuild(" in ocr
    assert "TryAuthorizeRefundSuggestion" in ocr
    assert "ConditionalAdviceKey = decision.ConditionalAdviceKey" in ocr
    assert "TryReserveDelivery" in vision
    assert "CompleteDelivery" in vision
    assert "TryReserveDelivery" in legacy_vision
    assert "CompleteDelivery" in legacy_vision


def test_conditional_advice_is_not_a_generic_image_classifier():
    source = read("src/Bot/ChromeNs/ConditionalBusinessAdviceService.cs")
    assert "VisualImageKnowledgeService.Load(shopKey, seller)" in source
    assert "RecentVisualContextService.GetLatestImageObservation" in source
    assert "MatchesImage(record, evidence)" in source
    assert "MatchesOrderProduct(record, evidence)" in source
    assert "ProductIds" in source
    assert "GetSpecificTerms" in source
