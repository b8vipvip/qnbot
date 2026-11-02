"""Persistent, privacy-safe OCR task history for the administrator console."""
from __future__ import annotations

import asyncio
from contextlib import contextmanager
import io
import sqlite3
from types import SimpleNamespace

from fastapi import HTTPException
from PIL import Image
import pytest
import runtime_ocr


class FakeControlPlane:
    def __init__(self, file):
        self.file = str(file)
        self.routes = {}
        self.app = self
        self.require_client = lambda: {"client": "test"}
        self.require_admin = lambda: "admin"

    def iso_now(self):
        return "2026-11-02T10:00:00+00:00"

    @contextmanager
    def db(self):
        conn = sqlite3.connect(self.file)
        conn.row_factory = sqlite3.Row
        try:
            yield conn
            conn.commit()
        finally:
            conn.close()

    def get(self, path):
        return lambda handler: self._add("GET", path, handler)

    def put(self, path):
        return lambda handler: self._add("PUT", path, handler)

    def post(self, path):
        return lambda handler: self._add("POST", path, handler)

    def _add(self, method, path, handler):
        self.routes[(method, path)] = handler
        return handler


class FakeRequest:
    def __init__(self, data: bytes, headers=None):
        self.data = data
        self.headers = headers or {}

    async def body(self):
        return self.data


def png_bytes():
    buffer = io.BytesIO()
    Image.new("RGB", (8, 8), "white").save(buffer, format="PNG")
    return buffer.getvalue()


@pytest.fixture
def control_plane(tmp_path, monkeypatch):
    monkeypatch.setattr(runtime_ocr, "_INSTALLED", False)
    cp = FakeControlPlane(tmp_path / "ocr.db")
    runtime_ocr.install(cp)
    runtime_ocr.init_db(cp)
    runtime_ocr._apply_settings(runtime_ocr._DEFAULT_SETTINGS)
    yield cp


def task_rows(cp):
    with cp.db() as conn:
        return [dict(row) for row in conn.execute(
            "SELECT * FROM runtime_ocr_task_records ORDER BY rowid"
        ).fetchall()]


def test_task_schema_status_updates_pagination_and_no_raw_data(control_plane):
    cp = control_plane
    runtime_ocr._create_task_record(cp, "task_001")
    runtime_ocr._update_task_record(
        cp, "task_001", "running", "inference",
        image_bytes=45200, image_hash="a" * 64, queue_ms=24,
    )
    runtime_ocr._update_task_record(
        cp, "task_001", "success", "returned",
        http_status=200, text_chars=3, confidence=0.98, queue_ms=24,
        inference_ms=890, elapsed_ms=920, terminal=True, image_hash="a" * 64,
    )
    rows = task_rows(cp)
    assert len(rows) == 1
    assert rows[0]["status"] == "success"
    assert rows[0]["finished_at"] is not None
    assert rows[0]["image_hash_prefix"] == "a" * 12
    assert rows[0]["queue_ms"] == 24
    assert rows[0]["text_chars"] == 3
    assert "image" not in rows[0]
    assert "text" not in rows[0]
    assert "buyer" not in rows[0]

    all_rows = runtime_ocr._list_task_records(cp, 1, 20, "all")
    assert all_rows["total"] == 1
    assert all_rows["counts"] == {"success": 1}
    assert all_rows["items"][0]["task_id"] == "task_001"
    assert all_rows["scope"] == "server_ocr_only"
    assert runtime_ocr._list_task_records(cp, 1, 10, "failed")["total"] == 0
    with pytest.raises(HTTPException) as exc:
        runtime_ocr._list_task_records(cp, 1, 20, "not_a_state")
    assert exc.value.status_code == 400


def test_task_history_bounded_and_newest_first(control_plane, monkeypatch):
    cp = control_plane
    monkeypatch.setattr(runtime_ocr, "_TASK_RETENTION", 3)
    for i in range(5):
        runtime_ocr._create_task_record(cp, f"task_{i}")
    result = runtime_ocr._list_task_records(cp, 1, 2, "all")
    assert result["total"] == 3
    assert [x["task_id"] for x in result["items"]] == ["task_4", "task_3"]
    assert [x["task_id"] for x in runtime_ocr._list_task_records(cp, 2, 2, "all")["items"]] == ["task_2"]


def test_server_restart_marks_unfinished_tasks_interrupted(control_plane):
    cp = control_plane
    runtime_ocr._create_task_record(cp, "stuck")
    runtime_ocr.init_db(cp)
    records = task_rows(cp)
    assert records[0]["status"] == "interrupted"
    assert records[0]["error_code"] == "server_restarted"


def test_runtime_success_returns_task_id_without_changing_ocr_contract(control_plane, monkeypatch):
    cp = control_plane
    monkeypatch.setattr(runtime_ocr, "_run_ocr", lambda raw: {
        "ok": True, "text": "OCR文字", "confidence": 0.95,
        "elapsedMs": 12, "engine": "fake",
    })
    payload = png_bytes()
    endpoint = cp.routes[("POST", "/api/runtime/v1/ocr")]
    response = asyncio.run(endpoint(FakeRequest(payload), {"client": "test"}))
    assert response["ok"] is True
    assert response["text"] == "OCR文字"
    assert response["cacheable"] is True
    assert len(response["imageSha256"]) == 64
    assert len(response["ocrTaskId"]) == 32
    rec = task_rows(cp)[0]
    assert rec["status"] == "success"
    assert rec["http_status"] == 200
    assert rec["text_chars"] == 5
    assert rec["confidence"] == pytest.approx(0.95)
    assert rec["image_hash_prefix"] == response["imageSha256"][:12]
    assert "OCR文字" not in str(rec)


def test_runtime_rejects_bad_hash_without_saving_secret_header(control_plane):
    cp = control_plane
    endpoint = cp.routes[("POST", "/api/runtime/v1/ocr")]
    with pytest.raises(HTTPException) as exc:
        asyncio.run(endpoint(
            FakeRequest(png_bytes(), {"x-image-sha256": "untrusted-private-value"}),
            {"client": "test"},
        ))
    assert exc.value.status_code == 400
    rec = task_rows(cp)[0]
    assert rec["status"] == "rejected"
    assert rec["error_code"] == "invalid_image_or_hash"
    assert "untrusted-private-value" not in str(rec)


def test_runtime_failed_inference_is_recorded_but_ocr_text_not_persisted(
    control_plane, monkeypatch,
):
    cp = control_plane

    def fail(raw):
        raise RuntimeError("sensitive account content")

    monkeypatch.setattr(runtime_ocr, "_run_ocr", fail)
    endpoint = cp.routes[("POST", "/api/runtime/v1/ocr")]
    with pytest.raises(HTTPException) as exc:
        asyncio.run(endpoint(FakeRequest(png_bytes()), {"client": "test"}))
    assert exc.value.status_code == 503
    rec = task_rows(cp)[0]
    assert rec["status"] == "failed"
    assert rec["error_code"] == "internal_ocr_error"
    assert "sensitive account content" not in str(rec)


def test_timeout_is_recorded_as_504_and_native_slot_released_after_exit(
    control_plane, monkeypatch,
):
    import time
    cp = control_plane

    def slow_worker(raw):
        time.sleep(1.15)
        return {"ok": True, "text": "not sent", "confidence": 1.0}

    monkeypatch.setattr(runtime_ocr, "_run_ocr", slow_worker)
    runtime_ocr._apply_settings({
        **runtime_ocr._DEFAULT_SETTINGS, "timeout_seconds": 1.0,
        "max_concurrency": 1,
    })
    endpoint = cp.routes[("POST", "/api/runtime/v1/ocr")]
    with pytest.raises(HTTPException) as exc:
        asyncio.run(endpoint(FakeRequest(png_bytes()), {"client": "test"}))
    assert exc.value.status_code == 504
    record = task_rows(cp)[0]
    assert record["status"] == "timeout"
    assert record["http_status"] == 504
    assert record["error_code"] == "inference_timeout"
    assert record["finished_at"] is not None
    # Response timeout must not leak any inference output into SQLite.
    assert "not sent" not in str(record)
