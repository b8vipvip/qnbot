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


def test_task_details_store_only_bounded_redacted_text_and_no_original_image_or_buyer_data():
    runtime = read("services/api-control-plane/runtime_ocr.py")
    schema = runtime.split("CREATE TABLE IF NOT EXISTS runtime_ocr_task_records", 1)[1].split('"""', 1)[0]
    for secret_col in ("image_data", "buyer_nick", "api_key", "image_url"):
        assert secret_col not in schema
    assert "result_text TEXT NOT NULL DEFAULT ''" in schema
    assert "result_text_redacted INTEGER NOT NULL DEFAULT 0" in schema
    assert "_sanitize_ocr_text" in runtime
    assert "_append_task_event" in runtime
    assert "runtime_ocr_task_events" in runtime
    assert "image_hash[:12]" in runtime
    assert "error_code = {" in runtime
    assert "result.get(\"text\", \"\")" in runtime
    assert "source" not in schema


def test_admin_task_detail_api_is_separate_from_paginated_task_list():
    runtime = read("services/api-control-plane/runtime_ocr.py")
    assert '@app.get("/api/admin/ocr/tasks/{task_id}")' in runtime
    assert "return _get_task_detail(control_plane, task_id)" in runtime
    assert "def _get_task_detail(" in runtime
    assert "Depends(require_admin)" in runtime


def test_console_status_filter_pagination_and_no_unsafe_html():
    html = read("services/api-control-plane/static/ocr-settings.html")
    js = read("services/api-control-plane/static/ocr-settings.js")
    assert 'id="ocrTaskRows"' in html
    assert 'id="ocrTaskFilter"' in html
    assert 'id="ocrTaskPrev"' in html
    assert 'id="ocrTaskNext"' in html
    assert 'id="ocrTaskSummary"' in html
    assert 'id="ocrTaskRefreshBtn"' in html
    assert '<th>详情</th>' in html
    assert 'id="ocrTaskDetailDialog"' in html
    assert 'id="ocrTaskDetailEvents"' in html
    assert 'id="ocrTaskDetailResult"' in html
    assert "导出 JSON" in html
    assert '/api/admin/ocr/tasks?' in js
    assert '/api/admin/ocr/tasks/"+encodeURIComponent(taskId)' in js
    assert "viewOcrTask" in js
    assert "exportOcrTask" in js
    assert "ocrTaskDetailEvents" in js
    assert "ocrTaskDetailResult" in js
    assert "setInterval(" in js
    assert "tbody.replaceChildren()" in js
    assert "td.textContent=" in js
    assert ".innerHTML" not in js
    assert "服务端 OCR" in html
    assert "客户端知识库命中与向买家发送消息的状态不在本列表内" in html
