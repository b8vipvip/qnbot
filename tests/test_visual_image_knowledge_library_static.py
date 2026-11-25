from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_image_knowledge_is_persisted_with_shop_scope_and_managed_image_files():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    targets = read("src/Directory.Build.targets")

    for field in ("ShopKey", "Seller", "ImageSha256", "ImagePath", "OcrText", "Description"):
        assert field in service
    assert "ResolveRuntimeByShopKey(shopKey)" in service
    assert "string.Equals((shop.SellerNick ?? string.Empty).Trim(), seller" in service
    assert 'Paths.GetKnowledgeRoot(shop)' in service
    assert "VisualImageKnowledgeEntry" in service
    assert "create unique index if not exists UX_VisualImageKnowledge_ShopSellerImage" in service
    assert "delete from VisualImageKnowledgeEntry where EntityId = ? and ShopKey = ? and Seller = ?" in service
    assert "MaxImageBytes = 8 * 1024 * 1024" in service
    assert "VisualImageKnowledgeService.cs" in targets


def test_image_knowledge_matches_ocr_while_ignoring_per_buyer_identifiers():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    match = service.split("public static bool TryMatch", 1)[1].split(
        "private static string BuildContext", 1
    )[0]
    normalize = service.split("private static string Normalize", 1)[1].split(
        "private static ShopContext ResolveScope", 1
    )[0]

    assert "TextSimilarity(ocrText, item.OcrText)" in match
    assert "string.Equals(item.ImageSha256, imageSha256" in match
    assert "MatchThreshold = 0.78d" in service
    assert "ocrConfidence < 0.55d" in match
    assert "numeric/mixed identifiers vary between" in normalize
    assert r"\d{4,}" in normalize


def test_image_description_is_context_not_a_direct_reply_and_detects_wrong_device_page():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    context = service.split("private static string BuildContext", 1)[1].split(
        "private static double TextSimilarity", 1
    )[0]

    assert "不是客服回复" in context
    assert "不代表买家已完成此前要求" in context
    assert "同一客服、同一买家" in context
    assert "另一种设备或页面" in context
    assert "请买家重新发送正确界面" in context


def test_ocr_image_knowledge_match_precedes_knowledge_v2_and_is_context_only_without_approved_answer():
    decision = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")

    assert decision.index("VisualImageKnowledgeService.TryMatch") < decision.index(
        "KnowledgeEngineV2Service.ResolveWithEvidence"
    )
    assert "(visualMatch == null && imageKnowledgeMatch == null)" in decision
    assert "imageKnowledgeMatch.ContextText" in decision
    assert "imageKnowledgeMatch.Description" in decision
    assert "decision.CanDirectReply" in decision
    assert "BuildVisualFallback(null, ocr, \"OCR置信度不足以进行Knowledge V2直答\", imageKnowledgeMatch)" in decision


def test_image_knowledge_is_matched_even_when_vision_priority_is_ai_first():
    decision = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")

    assert "VisualOcrPriorityService.IsAiFirst(priority)" in decision
    assert "VisualImageKnowledgeService.HasEntries(task.ShopKey)" in decision
    assert "if (!hasImageKnowledge)" in decision
    assert "先执行服务端OCR匹配再交给视觉AI" in decision


def test_image_knowledge_context_reaches_multimodal_and_ocr_text_ai():
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")

    assert "MatchedImageKnowledgeId" in vision
    assert "ApplyImageKnowledgeContext(result, localDecision)" in vision
    assert "ApplyImageKnowledgeContext(repaired, localDecision)" in vision
    assert "TryOcrTextAiFallbackAsync(task, cancellationToken, knowledgeV2ContextAddon, localDecision)" in vision
    assert "localDecision.VisualSummary" in vision
    assert "localDecision.ImageKnowledgeScore" in vision
    assert "image-knowledge" in vision


def test_image_knowledge_editor_has_only_image_ocr_and_description_workflow():
    page = read("src/Bot/Knowledge/KnowledgeV2VisualUnderstandingPage.cs")

    for control in (
        "上传图片", "识别图片", "保存图片知识", "删除图片知识",
        "图片预览", "OCR 识别结果（可编辑、可删除文字）",
        "图片说明（用于后续图片匹配和上下文理解）",
        "LocalOcrService.TryRecognizeAsync",
        "VisualImageKnowledgeService.Save",
        "VisualImageKnowledgeService.Delete",
    ):
        assert control in page

    for legacy_field in (
        "规则名称", "应用名称", "页面名称", "必需 OCR 关键词",
        "可选关键词", "排除关键词", "字段提取规则", "启用此规则",
    ):
        assert legacy_field not in page
