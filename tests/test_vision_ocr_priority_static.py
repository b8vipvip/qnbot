from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(rel: str) -> str:
    return (ROOT / rel).read_text(encoding="utf-8-sig")


def test_server_persists_and_exposes_ocr_vision_priority():
    text = read("services/api-control-plane/runtime_ocr_priority.py")
    assert 'OCR_FIRST = "ocr_first"' in text
    assert 'AI_FIRST = "ai_first"' in text
    assert 'OCR_VISION_PRIORITY' in text
    assert "runtime_ocr_vision_priority" in text
    assert '@app.get("/api/admin/ocr/vision-priority")' in text
    assert '@app.put("/api/admin/ocr/vision-priority")' in text
    assert '@app.post("/api/admin/ocr/vision-priority/reset")' in text
    assert '@app.get("/api/runtime/v1/ocr/vision-priority")' in text
    assert "Depends(require_admin)" in text
    assert "Depends(require_client)" in text


def test_control_plane_bootstrap_and_container_package_priority_runtime():
    bootstrap = read("services/api-control-plane/bootstrap.py")
    dockerfile = read("services/api-control-plane/Dockerfile")
    workflow = read(".github/workflows/api-control-plane-ci.yml")
    env = read("services/api-control-plane/.env.example")

    assert "import runtime_ocr_priority" in bootstrap
    assert "runtime_ocr_priority.install(control_plane)" in bootstrap
    assert "runtime_ocr_priority.init_db(control_plane)" in bootstrap
    assert "runtime_ocr_priority.py" in dockerfile
    assert "runtime_ocr_priority.py" in workflow
    assert "node --check services/api-control-plane/static/ocr-settings.js" in workflow
    assert "OCR_VISION_PRIORITY=ocr_first" in env


def test_server_ocr_console_exposes_two_visual_priority_modes():
    html = read("services/api-control-plane/static/ocr-settings.html")
    js = read("services/api-control-plane/static/ocr-settings.js")

    assert "无需人工配置即可使用" in html
    assert 'id="ocrVisionPriority"' in html
    assert 'value="ocr_first"' in html
    assert 'value="ai_first"' in html
    assert "OCR 优先" in html
    assert "AI 视觉接口优先" in html
    assert "/api/admin/ocr/vision-priority" in js
    assert "/api/admin/ocr/vision-priority/reset" in js
    assert "vision_priority:visionPriority" in js


def test_bot_reads_priority_from_authenticated_shop_control_plane():
    props = read("src/Directory.Build.props")
    service = read("src/Bot/ChromeNs/VisionOcrPriorityService.cs")

    assert "VisionOcrPriorityService.cs" in props
    assert "ShopControlPlaneConnectionStore" in service
    assert "ShopContextLocator.ResolveRuntimeBySellerNick" in service
    assert 'new AuthenticationHeaderValue("Bearer", endpoint.ApiKey)' in service
    assert '"/api/runtime/v1/ocr/vision-priority"' in service
    assert 'public const string OcrFirst = "ocr_first"' in service
    assert 'public const string AiFirst = "ai_first"' in service
    assert "return OcrFirst;" in service  # fail-safe keeps previous behavior


def test_ai_first_yields_before_ocr_direct_reply_but_keeps_no_ai_fallback():
    decision = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")

    priority_lookup = decision.index("VisionOcrPriorityService.ResolveAsync")
    image_resolution = decision.index("new VisionImageResolver().ResolveAsync")
    assert priority_lookup < image_resolution
    assert "VisionOcrPriorityService.IsAiFirst(priority)" in decision
    assert "AiEndpointStore.GetVisionEnabledEndpoints()" in decision
    assert "跳过OCR+知识库提前直答" in decision
    assert "未配置可用视觉模型，允许OCR+知识库兜底" in decision


def test_server_ocr_runs_before_knowledge_readiness_and_uses_shopkey():
    decision = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    vision_gate = read("src/Bot/ChromeNs/VisionMessageDecision.cs")
    ocr_call = decision.index("LocalOcrService.TryRecognizeAsync")
    knowledge_gate = decision.index("KnowledgeEngineV2Service.IsEnabled")
    assert ocr_call < knowledge_gate
    assert "task.ShopKey," in decision
    assert "task.Message, null, cancellationToken, task.ShopKey, task.BrowserClient" in decision
    assert "ShopContextLocator.ResolveRuntimeByShopKey(task.ShopKey)" in decision
    assert "LocalOcrService.IsServerOcrConfigured(shop.SellerNick, shopKey)" in vision_gate
    assert "CanUseServerOcr(shopKey)" in vision_gate
    assert "HasAuthoritativeShopScope(shopKey)" in vision_gate
    assert "if (!shopScopeUsable)" in vision_gate
    assert "policy=attempt_and_record" in vision_gate
    assert "if (!usable)" not in vision_gate
    assert "ResolveShopVisionEndpoints(shopKey)" in vision_gate


def test_ai_first_multimodal_path_still_attaches_shop_scoped_server_ocr():
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")
    resolver = read("src/Bot/ChromeNs/VisionImageResolver.cs")
    assert "image.LocalCachePath, task.SellerNick, task.ShopKey, cts.Token" in vision
    assert "ResolveAsync(message, endpoint, cancellationToken, shopKey, browserClient)" in resolver



def test_server_ocr_result_is_visible_in_question_card_without_mutating_source_question():
    pipeline = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")
    assert "ApplyOcrTextToQuestionCard(ctl, burst.CombinedQuestion, result, detectedAt)" in pipeline
    assert "服务端OCR缓存命中" in pipeline
    assert "服务端OCR识别" in pipeline
    assert "control.SetQuestion(question + Environment.NewLine + label + \"：\" + recognizedText, detectedAt)" in pipeline
    assert "UI-only annotation" in pipeline



def test_knowledge_source_label_identifies_server_ocr_not_local_ocr():
    decision = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    assert '"服务端OCR+知识库V2"' in decision
    assert '"本地OCR+知识库V2"' not in decision



def test_image_messages_are_not_silently_skipped_by_ocr_preflight():
    decision = read("src/Bot/ChromeNs/VisionMessageDecision.cs")
    assert "var shopScopeUsable = HasAuthoritativeShopScope(shopKey)" in decision
    assert "if (!localUsable && !externalUsable)" in decision
    assert "仍进入视觉处理链路" in decision
    assert "由实际OCR请求记录连接失败" in decision
    helper = decision.split("private static bool CanUseServerOcr", 1)[1].split(
        "private static IEnumerable<AiEndpointConfig> ResolveShopVisionEndpoints", 1
    )[0]
    assert "var sellerNick = message" not in helper
    assert "LocalOcrService.IsServerOcrConfigured(shop.SellerNick, shopKey)" in helper


def test_ocr_route_decision_is_logged_with_shop_scoped_capability_state():
    decision = read("src/Bot/ChromeNs/VisionMessageDecision.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")
    assert "图片消息路由探测" in decision
    assert "serverOcrConfigured=" in decision
    assert "visionEndpoints=" in decision
    assert "买家消息路由决策" in qn
    assert "visionDecision.Kind" in qn



def test_ocr_and_priority_connections_use_authoritative_shopkey_not_ambient_nickname():
    local_ocr = read("src/Bot/ChromeNs/LocalOcrService.cs")
    priority = read("src/Bot/ChromeNs/VisionOcrPriorityService.cs")
    for source in (local_ocr, priority):
        assert "ShopContextLocator.ResolveRuntimeByShopKey(shopKey)" in source
        assert "shopKey.Length == 0 && sellerNick.Length > 0" in source
        assert "current != null && !string.Equals(current.ShopKey, shopKey" not in source


def test_ocr_knowledge_lookup_uses_authoritative_shop_seller_and_image_alias_is_allowed():
    decision = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    burst = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "var knowledgeSeller = shop == null ? string.Empty" in decision
    assert "KnowledgeEngineV2Service.Resolve(knowledgeSeller, task.BuyerNick, question)" in decision
    assert "BotMessageSuffixService.Apply(knowledgeSeller, decision.Answer)" in decision
    assert "var isVisionItem = item.VisionDecision != null" in burst
    assert "(!isVisionItem && !sellerMatchesShop)" in burst
    assert "图片消息使用权威ShopKey店铺作用域继续处理" in burst
