from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_image_knowledge_is_persisted_with_shop_scope_and_managed_image_files():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    targets = read("src/Directory.Build.targets")

    for field in ("ShopKey", "Seller", "ImageSha256", "ImagePath", "ImageName", "OcrText", "Description"):
        assert field in service
    assert "ResolveRuntimeByShopKey(shopKey)" in service
    assert "string.Equals(shop.ShopKey, shopKey" in service
    assert "ShopKey is the authoritative identity" in service
    assert 'Paths.GetKnowledgeRoot(shop)' in service
    assert "VisualImageKnowledgeEntry" in service
    assert "create unique index if not exists UX_VisualImageKnowledge_ShopSellerImage" in service
    assert "MaxImageBytes = 8 * 1024 * 1024" in service
    assert "VisualImageKnowledgeService.cs" in targets


def test_image_knowledge_matches_ocr_while_ignoring_per_buyer_identifiers():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    match = service.split("public static bool TryMatch", 1)[1].split("private static string BuildContext", 1)[0]
    normalize = service.split("private static string Normalize", 1)[1].split("private static ShopContext ResolveScope", 1)[0]
    assert "TextSimilarity(ocrText, item.OcrText)" in match
    assert "string.Equals(item.ImageSha256, imageSha256" in match
    assert "MatchThreshold = 0.78d" in service
    assert "ocrConfidence < 0.55d" in match
    assert "Ignore numeric/mixed identifiers" in normalize
    assert r"\d{4,}" in normalize


def test_image_description_is_context_not_a_direct_reply_and_detects_wrong_device_page():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    context = service.split("private static string BuildContext", 1)[1].split("private static double TextSimilarity", 1)[0]
    for text in ("不是客服回复", "不代表买家已完成此前要求", "同一客服、同一买家", "另一种设备或页面", "请买家重新发送正确界面"):
        assert text in context


def test_ocr_image_knowledge_match_precedes_knowledge_v2_and_is_context_only_without_approved_answer():
    decision = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    assert decision.index("VisualImageKnowledgeService.TryMatch") < decision.index("KnowledgeEngineV2Service.ResolveWithEvidence")
    assert "(visualMatch == null && imageKnowledgeMatch == null)" in decision
    assert "imageKnowledgeMatch.ContextText" in decision
    assert "imageKnowledgeMatch.Description" in decision
    assert "decision.CanDirectReply" in decision


def test_image_knowledge_context_reaches_multimodal_and_ocr_text_ai():
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")
    for text in ("MatchedImageKnowledgeId", "ApplyImageKnowledgeContext(result, localDecision)",
                 "ApplyImageKnowledgeContext(repaired, localDecision)", "已匹配的人工图片知识说明",
                 "请买家重新发送正确界面"):
        assert text in vision


def test_image_knowledge_editor_uploads_then_automatically_runs_server_ocr_and_saves():
    page = read("src/Bot/Knowledge/KnowledgeV2VisualUnderstandingPage.cs")
    for text in ("上传图片", "图片名称", "图片预览", "OCR 识别结果（可编辑、可删除文字）",
                 "图片说明（用于后续图片匹配和上下文理解）", "await RecognizeImageAsync();",
                 "LocalOcrService.TryRecognizeAsync", "VisualImageKnowledgeService.Save",
                 "保存", "ImportImageFile"):
        assert text in page
    assert "_recognizeButton" not in page
    assert "识别图片" not in page
    for legacy_field in ("规则名称", "应用名称", "页面名称", "必需 OCR 关键词",
                         "可选关键词", "排除关键词", "字段提取规则", "启用此规则"):
        assert legacy_field not in page


def test_buyer_image_message_menu_imports_exact_shop_scoped_cached_original():
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    bridge = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    assert 'Header = "导入图片知识"' in mirror
    assert "TryGetCachedImagePathAsync" in mirror
    assert "TryGetCachedImagePathAsync" in cache
    assert "ImportBuyerImage" in mirror
    assert "shell.OpenImageKnowledge(imagePath)" in bridge
