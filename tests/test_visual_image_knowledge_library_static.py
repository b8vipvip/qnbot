from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_image_knowledge_is_persisted_with_shop_scope_and_managed_image_files():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    targets = read("src/Directory.Build.targets")

    for field in ("ShopKey", "Seller", "ImageSha256", "ImagePath", "ImageName", "OcrText", "HitInfo", "Description"):
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
                 "ApplyImageKnowledgeContext(repaired, localDecision)", "ApprovedV2KnowledgeEvidence",
                 "result.ApprovedV2KnowledgeEvidence = localDecision.ApprovedV2KnowledgeEvidence",
                 "已匹配的人工图片知识说明", "请买家重新发送正确界面"):
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



def test_image_hit_info_rules_are_persisted_synced_and_authoritative():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    page = read("src/Bot/Knowledge/KnowledgeV2VisualUnderstandingPage.cs")
    api = read("services/api-control-plane/bot_web_conversation_knowledge.py")
    web_html = read("services/api-control-plane/static/bot-web.html")
    web_js = read("services/api-control-plane/static/bot-web-v2.js")

    assert "TryParseHitInfoRules" in service
    assert "TryMatchHitInfo" in service
    assert "rules.Required" in service and "rules.Any" in service and "rules.Excluded" in service
    assert "An explicitly configured hit rule is authoritative" in service
    assert '["hit_info"] = pending.HitInfo' in service
    assert 'data["hit_info"]' in service
    assert "HitInfo = cloudHitInfo" in service
    assert "add column HitInfo text not null default ''" in service
    assert 'Header = "命中信息"' in page
    assert 'HitInfo = _hitInfo.Text' in page
    assert "_hitInfo.Text = item == null ? string.Empty : item.HitInfo ?? string.Empty" in page
    assert 'hit_info TEXT NOT NULL DEFAULT' in api
    assert 'hit_info: str = Field(default="", max_length=2400)' in api
    assert '"hit_info": row["hit_info"] or ""' in api
    assert 'id="imageKnowledgeHitInfo"' in web_html
    assert 'hit_info:$("imageKnowledgeHitInfo").value' in web_js
    assert '$("imageKnowledgeHitInfo").value=item.hit_info||""' in web_js



def test_buyer_image_message_menu_imports_exact_shop_scoped_cached_original():
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    cache = read("src/Bot/ChromeNs/VisionImageCacheService.cs")
    bridge = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    assert 'Header = "导入图片知识"' in mirror
    assert "TryGetCachedImagePathAsync" in mirror
    assert "TryGetCachedImagePathAsync" in cache
    assert "ImportBuyerImage" in mirror
    assert "shell.OpenImageKnowledge(imagePath)" in bridge


def test_recent_visual_account_evidence_requires_real_image_flag_or_exact_placeholder():
    context = read("src/Bot/ChromeNs/RecentVisualContextService.cs")
    method = context.split("public static bool TryGetRecentAccountEvidence", 1)[1].split(
        "private static bool IsBotMarked", 1
    )[0]
    assert "turns[i].HasImage" in method
    assert 'string.Equals(text, "[图片]"' in method
    assert 'string.Equals(text, "【图片】"' in method
    assert 'string.Equals(text, "图片消息"' in method
    assert 'text.Contains("[图片]")' not in method
    assert 'text.Contains("【图片】")' not in method


def test_ocr_result_and_curated_image_knowledge_are_persisted_against_exact_buyer_message():
    learning = read("src/Bot/ChromeNs/VisualKnowledgeLearningService.cs")
    ocr_first = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")
    for field in ("OcrImageSha256", "OcrText", "OcrConfidence", "OcrEngine",
                  "MatchedImageKnowledgeId", "ImageKnowledgeScore"):
        assert f"public {'double' if field in ('OcrConfidence', 'ImageKnowledgeScore') else 'string'} {field}" in learning
    assert "RecordOcrEvidence(" in learning
    assert 'where ShopKey=? and Seller=? and Buyer=? and MessageKey=?' in learning
    assert "VisualKnowledgeLearningService.RecordOcrEvidence(" in ocr_first
    assert "VisualImageKnowledgeService.TryMatch(" in ocr_first
    assert "VisualKnowledgeLearningService.RecordOcrEvidence(" in vision
    assert "alter table VisualKnowledgeObservationEntity add column OcrText text" in learning
    assert "if (!string.IsNullOrWhiteSpace(matchedImageKnowledgeId))" in learning
    assert "Empty input must not erase the already persisted message-to-image" in learning
    assert 'Status = withdrawn ? "图片已撤回" : "仅OCR证据"' in learning
    assert 'old.Status = "等待人工接待结束"' in learning


def test_visual_learning_restart_recovers_all_pending_observations_within_retention():
    learning = read("src/Bot/ChromeNs/VisualKnowledgeLearningService.cs")
    init = learning.split("private static void EnsureInitialized()", 1)[1].split(
        "private static void Schedule", 1
    )[0]
    resume = learning.split("private static bool ResumePending()", 1)[1].split(
        "private static void EnsureSchema", 1
    )[0]
    assert "InitSync" in init
    assert "Volatile.Read(ref _initialized)" in init
    assert "if (!ResumePending()) return;" in init
    assert "Volatile.Write(ref _initialized, 1)" in init
    assert "Cleanup();" in resume
    assert "DateTime.Now.AddDays(-ObservationRetentionDays).Ticks" in resume
    assert "limit \" + MaxObservations" in resume
    assert "ObservedAtTicks >= ?" not in resume
    assert "AddDays(-1)" not in resume
    assert "观察时间无效，需人工复核" in resume


def test_image_save_verifies_existing_and_temporary_files_before_atomic_commit():
    service = read("src/Bot/ChromeNs/VisualImageKnowledgeService.cs")
    save = service.split("public static VisualImageKnowledgeEntry Save", 1)[1].split(
        "public static bool Delete", 1
    )[0]
    assert "ComputeSha256(targetPath)" in save
    assert "ComputeSha256(tempPath)" in save
    assert "原图在复制期间发生变化" in save
    assert "File.Replace(tempPath, targetPath, null)" in save
    assert "var replacedImage = false;" in save
    assert "现有图片记录引用了内容哈希不一致的文件" in save
    assert "(createdImage || replacedImage)" in save
    assert "old == null || !SamePath(old.ImagePath, targetPath)" in save
