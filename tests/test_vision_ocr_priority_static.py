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
