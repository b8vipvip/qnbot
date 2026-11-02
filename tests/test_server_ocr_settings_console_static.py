from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_server_ocr_has_safe_defaults_and_persisted_admin_settings():
    runtime = read("services/api-control-plane/runtime_ocr.py")
    bootstrap = read("services/api-control-plane/bootstrap.py")
    env = read("services/api-control-plane/.env.example")

    assert '"OCR_ENABLED"' in runtime
    assert '"OCR_MAX_IMAGE_BYTES"' in runtime
    assert '"OCR_TIMEOUT_SECONDS"' in runtime
    assert '"OCR_MAX_CONCURRENCY"' in runtime
    assert '"OCR_MAX_TEXT_CHARS"' in runtime
    assert "runtime_ocr_settings" in runtime
    assert '@app.get("/api/admin/ocr/settings")' in runtime
    assert '@app.put("/api/admin/ocr/settings")' in runtime
    assert '@app.post("/api/admin/ocr/settings/reset")' in runtime
    assert "Depends(require_admin)" in runtime
    assert "restart_required" in runtime
    assert "runtime_ocr.init_db(control_plane)" in bootstrap

    assert "OCR_ENABLED=true" in env
    assert "OCR_MAX_IMAGE_BYTES=8388608" in env
    assert "OCR_TIMEOUT_SECONDS=16" in env
    assert "OCR_MAX_CONCURRENCY=2" in env
    assert "OCR_MAX_TEXT_CHARS=6000" in env
    assert "不配置以下变量也能直接运行" in env


def test_runtime_ocr_applies_live_limits_without_restart():
    runtime = read("services/api-control-plane/runtime_ocr.py")

    assert 'if not settings["enabled"]' in runtime
    assert 'max_image_bytes = int(settings["max_image_bytes"])' in runtime
    assert 'await _acquire_ocr_slot(int(settings["max_concurrency"]))' in runtime
    assert 'timeout=float(settings["timeout_seconds"])' in runtime
    assert 'max_text_chars = int(_current_settings()["max_text_chars"])' in runtime
    assert "_ACTIVE_REQUESTS" in runtime
    assert "asyncio.Semaphore" in runtime
    assert "Keep the concurrency slot until the underlying inference actually exits" in runtime


def test_primary_console_exposes_lazy_loaded_ocr_settings_page():
    sections = read("services/api-control-plane/static/console-sections.js")
    page = read("services/api-control-plane/static/ocr-settings.html")
    script = read("services/api-control-plane/static/ocr-settings.js")

    assert 'ocr:{frameId:"ocrSettingsFrame",src:"/static/ocr-settings.html?embedded=1"}' in sections
    assert 'insertPrimaryButton("ocr","服务端 OCR")' in sections
    assert 'titles.ocr=["服务端 OCR"' in sections
    assert 'frame.dataset.loaded="1"' in sections

    assert "无需人工配置即可使用" in page
    assert 'id="ocrEnabled"' in page
    assert 'id="ocrMaxImageMb"' in page
    assert 'id="ocrTimeoutSeconds"' in page
    assert 'id="ocrMaxConcurrency"' in page
    assert 'id="ocrMaxTextChars"' in page
    assert "/static/ocr-settings.js?v=" in page
    assert "当前没有需要人工选择的模型路径或模型名称" in page

    assert 'ocrApi("/api/admin/ocr/settings")' in script
    assert 'method:"PUT"' in script
    assert 'ocrApi("/api/admin/ocr/settings/reset"' in script
    assert "OCR 参数和视觉理解优先级已保存并立即生效，无需重启服务" in script
