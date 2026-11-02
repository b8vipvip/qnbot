"""OCR task history is administrator-scoped, bounded and privacy-safe."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_admin_only_task_list_uses_server_sqlite():
    runtime = read("services/api-control-plane/runtime_ocr.py")
    assert "CREATE TABLE IF NOT EXISTS runtime_ocr_task_records" in runtime
    assert "@app.get(\"/api/admin/ocr/tasks\")" in runtime
    assert "Depends(require_admin)" in runtime
    assert "page_size: int = Query(default=20, ge=1, le=100)" in runtime
    assert "retention_limit" in runtime
    assert "_TASK_RETENTION = 5000" in runtime
    assert "runtime_ocr_task_records" in runtime
    assert "server_ocr_only" in runtime


def test_every_attempt_transitions_and_timeouts_keep_native_slot():
    runtime = read("services/api-control-plane/runtime_ocr.py")
    assert "task_id = uuid.uuid4().hex" in runtime
    assert "_create_task_record(cp, task_id)" in runtime
    for status in ('"running"', '"success"', '"timeout"', '"failed"', '"rejected"', '"interrupted"'):
        assert status in runtime
    assert "worker_task = asyncio.create_task(run_in_threadpool(_run_ocr, raw))" in runtime
    assert "await asyncio.wait_for(" in runtime
    assert "_release_when_done(worker_task)" in runtime
    assert 'error_code="request_cancelled"' in runtime
    assert '"ocrTaskId": task_id' in runtime


def test_no_image_ocr_text_or_buyer_data_in_diagnostics():
    runtime = read("services/api-control-plane/runtime_ocr.py")
    schema = runtime.split("CREATE TABLE IF NOT EXISTS runtime_ocr_task_records", 1)[1].split('"""', 1)[0]
    for secret_col in ("image_data", "ocr_text", "buyer_nick", "api_key", "image_url"):
        assert secret_col not in schema
    assert "image_hash[:12]" in runtime
    assert "error_code = {" in runtime
    assert "result.get(\"text\", \"\")" in runtime
    assert "source" not in schema


def test_console_status_filter_pagination_and_no_unsafe_html():
    html = read("services/api-control-plane/static/ocr-settings.html")
    js = read("services/api-control-plane/static/ocr-settings.js")
    assert 'id="ocrTaskRows"' in html
    assert 'id="ocrTaskFilter"' in html
    assert 'id="ocrTaskPrev"' in html
    assert 'id="ocrTaskNext"' in html
    assert 'id="ocrTaskSummary"' in html
    assert 'id="ocrTaskRefreshBtn"' in html
    assert '/api/admin/ocr/tasks?' in js
    assert "setInterval(" in js
    assert "tbody.replaceChildren()" in js
    assert "td.textContent=" in js
    assert ".innerHTML" not in js
    assert "服务端 OCR" in html
    assert "客户端知识库命中与向买家发送消息的状态不在本列表内" in html
