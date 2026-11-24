from __future__ import annotations

import asyncio
import hashlib
import io
import json
import re
import os
import threading
import time
import uuid
from typing import Any, Dict, Optional

from fastapi import Query

from fastapi import Depends, HTTPException, Request, status
from pydantic import BaseModel, Field
from starlette.concurrency import run_in_threadpool
from PIL import Image


def _env_bool(name: str, default: bool) -> bool:
    value = os.getenv(name)
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


def _env_int(name: str, default: int, minimum: int, maximum: int) -> int:
    try:
        value = int(os.getenv(name, str(default)))
    except (TypeError, ValueError):
        value = default
    return max(minimum, min(maximum, value))


def _env_float(name: str, default: float, minimum: float, maximum: float) -> float:
    try:
        value = float(os.getenv(name, str(default)))
    except (TypeError, ValueError):
        value = default
    return max(minimum, min(maximum, value))


# These environment variables are bootstrap defaults only. The first server startup persists them
# into SQLite; afterwards the control-console values are authoritative until "restore defaults" is
# used. OCR therefore works out of the box and does not require manual environment configuration.
_DEFAULT_ENABLED = _env_bool("OCR_ENABLED", True)
_MAX_IMAGE_BYTES = _env_int("OCR_MAX_IMAGE_BYTES", 8 * 1024 * 1024, 256 * 1024, 64 * 1024 * 1024)
_TIMEOUT_SECONDS = _env_float("OCR_TIMEOUT_SECONDS", 16.0, 1.0, 30.0)
_MAX_CONCURRENCY = _env_int("OCR_MAX_CONCURRENCY", 2, 1, 8)
_MAX_TEXT_CHARS = _env_int("OCR_MAX_TEXT_CHARS", 6000, 256, 12000)
_DEFAULT_SETTINGS: Dict[str, Any] = {
    "enabled": _DEFAULT_ENABLED,
    "max_image_bytes": _MAX_IMAGE_BYTES,
    "timeout_seconds": _TIMEOUT_SECONDS,
    "max_concurrency": _MAX_CONCURRENCY,
    "max_text_chars": _MAX_TEXT_CHARS,
}

_ENGINE_LOCK = threading.Lock()
_ENGINE: Optional[Any] = None
_SETTINGS_LOCK = threading.RLock()
_SETTINGS: Dict[str, Any] = dict(_DEFAULT_SETTINGS)
_ACTIVE_LOCK = threading.Lock()
_ACTIVE_REQUESTS = 0
_SEMAPHORE: Optional[asyncio.Semaphore] = None
_SEMAPHORE_LOOP: Optional[asyncio.AbstractEventLoop] = None
_INSTALLED = False
_CONTROL_PLANE: Optional[Any] = None


class RuntimeOcrSettingsUpdate(BaseModel):
    enabled: bool = True
    max_image_bytes: int = Field(ge=256 * 1024, le=64 * 1024 * 1024)
    timeout_seconds: float = Field(ge=1.0, le=30.0)
    max_concurrency: int = Field(ge=1, le=8)
    max_text_chars: int = Field(ge=256, le=12000)


def _get_semaphore() -> asyncio.Semaphore:
    """Return a loop-local mutex used by the dynamic OCR admission gate.

    The actual concurrency limit is checked against _ACTIVE_REQUESTS on every admission, so a
    control-console max_concurrency change applies to new requests without recreating a fixed-size
    semaphore or requiring a service restart.
    """
    global _SEMAPHORE, _SEMAPHORE_LOOP
    loop = asyncio.get_running_loop()
    if _SEMAPHORE is None or _SEMAPHORE_LOOP is not loop:
        _SEMAPHORE = asyncio.Semaphore(1)
        _SEMAPHORE_LOOP = loop
    return _SEMAPHORE


async def _acquire_ocr_slot(limit: int) -> None:
    global _ACTIVE_REQUESTS
    safe_limit = max(1, min(8, int(limit)))
    mutex = _get_semaphore()
    while True:
        await mutex.acquire()
        admitted = False
        try:
            with _ACTIVE_LOCK:
                if _ACTIVE_REQUESTS < safe_limit:
                    _ACTIVE_REQUESTS += 1
                    admitted = True
        finally:
            mutex.release()
        if admitted:
            return
        await asyncio.sleep(0.025)


def _release_ocr_slot() -> None:
    global _ACTIVE_REQUESTS
    with _ACTIVE_LOCK:
        if _ACTIVE_REQUESTS > 0:
            _ACTIVE_REQUESTS -= 1


def _active_request_count() -> int:
    with _ACTIVE_LOCK:
        return max(0, int(_ACTIVE_REQUESTS))


def _get_engine() -> Any:
    global _ENGINE
    if _ENGINE is not None:
        return _ENGINE
    with _ENGINE_LOCK:
        if _ENGINE is None:
            from rapidocr import RapidOCR

            _ENGINE = RapidOCR()
    return _ENGINE


def _validate_image(raw: bytes) -> None:
    try:
        with Image.open(io.BytesIO(raw)) as image:
            image.verify()
    except Exception as exc:
        raise ValueError("上传内容不是有效图片") from exc


def _current_settings() -> Dict[str, Any]:
    with _SETTINGS_LOCK:
        return dict(_SETTINGS)


def _apply_settings(settings: Dict[str, Any]) -> Dict[str, Any]:
    normalized = {
        "enabled": bool(settings.get("enabled", True)),
        "max_image_bytes": max(256 * 1024, min(64 * 1024 * 1024, int(settings.get("max_image_bytes", _MAX_IMAGE_BYTES)))),
        "timeout_seconds": max(1.0, min(30.0, float(settings.get("timeout_seconds", _TIMEOUT_SECONDS)))),
        "max_concurrency": max(1, min(8, int(settings.get("max_concurrency", _MAX_CONCURRENCY)))),
        "max_text_chars": max(256, min(12000, int(settings.get("max_text_chars", _MAX_TEXT_CHARS)))),
    }
    with _SETTINGS_LOCK:
        _SETTINGS.clear()
        _SETTINGS.update(normalized)
    return dict(normalized)


def _run_ocr(raw: bytes) -> Dict[str, Any]:
    started = time.perf_counter()
    _validate_image(raw)
    result = _get_engine()(raw)

    texts = list(getattr(result, "txts", None) or [])
    scores = list(getattr(result, "scores", None) or [])
    lines = [str(value).strip() for value in texts if str(value or "").strip()]
    text = "\n".join(lines).strip()
    max_text_chars = int(_current_settings()["max_text_chars"])
    if len(text) > max_text_chars:
        text = text[:max_text_chars] + "…"

    numeric_scores = []
    for score in scores:
        try:
            numeric_scores.append(max(0.0, min(1.0, float(score))))
        except Exception:
            continue
    confidence = sum(numeric_scores) / len(numeric_scores) if numeric_scores else 0.0
    return {
        "ok": True,
        "text": text,
        "confidence": round(confidence, 6),
        "elapsedMs": max(0, int((time.perf_counter() - started) * 1000)),
        "engine": "RapidOCR/ONNXRuntime",
    }


def _release_when_done(task: asyncio.Task) -> None:
    def release(_: asyncio.Task) -> None:
        _release_ocr_slot()

    task.add_done_callback(release)


def _row_to_settings(row: Any) -> Dict[str, Any]:
    return {
        "enabled": bool(row["enabled"]),
        "max_image_bytes": int(row["max_image_bytes"]),
        "timeout_seconds": float(row["timeout_seconds"]),
        "max_concurrency": int(row["max_concurrency"]),
        "max_text_chars": int(row["max_text_chars"]),
    }


def _settings_payload(settings: Dict[str, Any], source: str, updated_at: str = "", updated_by: str = "") -> Dict[str, Any]:
    return {
        **settings,
        "engine": "RapidOCR/ONNXRuntime",
        "engine_loaded": _ENGINE is not None,
        "active_requests": _active_request_count(),
        "source": source,
        "updated_at": updated_at or "",
        "updated_by": updated_by or "",
        "restart_required": False,
        "environment_defaults": dict(_DEFAULT_SETTINGS),
    }



# OCR task ledger keeps no image bytes, buyer identity, API credentials or raw request headers.
# Admin-only, bounded history stores a redacted OCR result so operators can verify recognition.
_TASK_RETENTION = 5000
_TASK_STATES = {"queued", "running", "success", "timeout", "failed", "rejected", "interrupted"}
_TASK_FILTERS = _TASK_STATES | {"all"}
_OCR_TEXT_MAX_CHARS = 12000


def _sanitize_ocr_text(value: Any, max_chars: int = _OCR_TEXT_MAX_CHARS):
    text = str(value or "")[:max(0, int(max_chars))]
    redacted = False
    patterns = (
        (re.compile(r"(?<!\d)1[3-9]\d{9}(?!\d)"), "[手机号已脱敏]"),
        (re.compile(r"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", re.IGNORECASE), "[邮箱已脱敏]"),
        (re.compile(r"(?i)(验证码\s*[:：]?\s*)\d{4,8}"), r"\1[已脱敏]"),
        (re.compile(r"(?<!\d)\d{8,}(?!\d)"), "[长数字ID已脱敏]"),
    )
    for pattern, replacement in patterns:
        updated = pattern.sub(replacement, text)
        if updated != text:
            redacted = True
            text = updated
    return text, redacted


def _safe_engine(value: Any) -> str:
    engine = str(value or "RapidOCR/ONNXRuntime").strip()
    engine = re.sub(r"[^A-Za-z0-9._/+ -]", "", engine)
    return engine[:80] or "RapidOCR/ONNXRuntime"


def _append_task_event(conn: Any, cp: Any, task_id: str, stage: str,
                       state: str, message: str,
                       metadata: Optional[Dict[str, Any]] = None) -> None:
    allowed = {
        "image_bytes", "image_hash_prefix", "queue_ms", "inference_ms",
        "elapsed_ms", "text_chars", "confidence", "http_status",
        "engine", "max_concurrency",
    }
    safe_metadata: Dict[str, Any] = {}
    for key, value in (metadata or {}).items():
        if key not in allowed:
            continue
        if key == "engine":
            safe_metadata[key] = _safe_engine(value)
        elif key == "image_hash_prefix":
            candidate = str(value or "").lower()
            safe_metadata[key] = candidate[:12] if re.fullmatch(r"[a-f0-9]{0,64}", candidate) else ""
        elif key == "confidence":
            try:
                safe_metadata[key] = max(0.0, min(1.0, float(value)))
            except (TypeError, ValueError):
                safe_metadata[key] = 0.0
        else:
            try:
                safe_metadata[key] = max(0, int(value))
            except (TypeError, ValueError):
                safe_metadata[key] = 0
    conn.execute(
        """
        INSERT INTO runtime_ocr_task_events(
            task_id, created_at, stage, status, message, metadata_json
        ) VALUES (?, ?, ?, ?, ?, ?)
        """,
        (
            task_id, cp.iso_now(), str(stage or "")[:40], str(state or "")[:24],
            str(message or "")[:240],
            json.dumps(safe_metadata, ensure_ascii=False, separators=(",", ":")),
        ),
    )


def _create_task_record(cp: Any, request_id: str) -> None:
    with cp.db() as conn:
        conn.execute(
            """
            INSERT INTO runtime_ocr_task_records(
                task_id, started_at, status, phase, http_status
            ) VALUES (?, ?, 'queued', 'admission', 0)
            """,
            (request_id, cp.iso_now()),
        )
        _append_task_event(
            conn, cp, request_id, "admission", "queued",
            "已收到服务端 OCR 请求，等待图片校验与排队",
        )
        # Keep the newest 5000 entries and their events across restarts.
        count = int(conn.execute("SELECT COUNT(*) FROM runtime_ocr_task_records").fetchone()[0])
        if count > _TASK_RETENTION:
            expired = conn.execute(
                """SELECT task_id FROM runtime_ocr_task_records
                   ORDER BY started_at ASC, rowid ASC LIMIT ?""",
                (count - _TASK_RETENTION,),
            ).fetchall()
            expired_ids = [str(row["task_id"]) for row in expired]
            if expired_ids:
                conn.executemany(
                    "DELETE FROM runtime_ocr_task_events WHERE task_id = ?",
                    [(task_id,) for task_id in expired_ids],
                )
                conn.executemany(
                    "DELETE FROM runtime_ocr_task_records WHERE task_id = ?",
                    [(task_id,) for task_id in expired_ids],
                )


def _update_task_record(cp: Any, request_id: str, status_value: str,
                        phase: str, *, http_status: int = 0,
                        error_code: str = "", queue_ms: int = 0,
                        inference_ms: int = 0, elapsed_ms: int = 0,
                        image_bytes: int = 0, image_hash: str = "",
                        text_chars: int = 0, confidence: float = 0.0,
                        engine: Optional[str] = None,
                        result_text: Optional[str] = None,
                        event_message: str = "",
                        event_metadata: Optional[Dict[str, Any]] = None,
                        terminal: bool = False) -> None:
    if status_value not in _TASK_STATES:
        raise ValueError("invalid_ocr_task_state")
    # error_code and event messages are selected from fixed call-site values;
    # raw exception messages, OCR headers, and request bodies are never logged.
    sanitized_text, text_redacted = _sanitize_ocr_text(result_text or "")
    safe_engine = _safe_engine(engine) if engine is not None else ""
    if not event_message:
        event_message = {
            ("running", "validation"): "图片通过基础校验",
            ("running", "admission"): "等待 OCR 推理并发槽位",
            ("running", "inference"): "开始 RapidOCR/ONNXRuntime 推理",
            ("success", "returned"): "OCR 识别完成并返回",
            ("timeout", "inference"): "OCR 推理超时",
            ("interrupted", "server_restarted"): "服务重启，未完成任务已标记中断",
            ("interrupted", "admission"): "客户端断开，请求已中断",
            ("failed", "inference"): "OCR 推理失败",
            ("rejected", "validation"): "图片校验未通过",
        }.get((status_value, phase), "OCR 任务状态已更新")
    with cp.db() as conn:
        conn.execute(
            """
            UPDATE runtime_ocr_task_records SET
                status=?, phase=?, http_status=?, error_code=?, queue_ms=?,
                inference_ms=?, elapsed_ms=?, image_bytes=?, image_hash_prefix=?,
                text_chars=?, confidence=?, engine=?, result_text=?,
                result_text_redacted=?, finished_at=?
            WHERE task_id=?
            """,
            (
                status_value, phase, http_status, error_code,
                max(0, int(queue_ms)), max(0, int(inference_ms)),
                max(0, int(elapsed_ms)), max(0, int(image_bytes)),
                image_hash[:12], max(0, int(text_chars)),
                max(0.0, min(1.0, float(confidence))),
                safe_engine, sanitized_text, 1 if text_redacted else 0,
                cp.iso_now() if terminal else None, request_id,
            ),
        )
        metadata = dict(event_metadata or {})
        metadata.setdefault("http_status", http_status)
        if image_bytes:
            metadata.setdefault("image_bytes", image_bytes)
        if image_hash:
            metadata.setdefault("image_hash_prefix", image_hash[:12])
        if queue_ms:
            metadata.setdefault("queue_ms", queue_ms)
        if inference_ms:
            metadata.setdefault("inference_ms", inference_ms)
        if elapsed_ms:
            metadata.setdefault("elapsed_ms", elapsed_ms)
        if text_chars:
            metadata.setdefault("text_chars", text_chars)
        if confidence:
            metadata.setdefault("confidence", confidence)
        if safe_engine:
            metadata.setdefault("engine", safe_engine)
        _append_task_event(
            conn, cp, request_id, phase, status_value, event_message, metadata
        )


def _list_task_records(cp: Any, page: int, page_size: int,
                       state: str) -> Dict[str, Any]:
    if state not in _TASK_FILTERS:
        raise HTTPException(status_code=400, detail="无效OCR状态过滤器")
    clause = "" if state == "all" else " WHERE status = ?"
    args = () if state == "all" else (state,)
    offset = (page - 1) * page_size
    with cp.db() as conn:
        total = int(conn.execute(
            "SELECT COUNT(*) FROM runtime_ocr_task_records" + clause, args
        ).fetchone()[0])
        rows = conn.execute(
            """SELECT task_id, started_at, finished_at, status, phase,
                      http_status, error_code, queue_ms, inference_ms,
                      elapsed_ms, image_bytes, image_hash_prefix,
                      text_chars, confidence, engine, result_text_redacted
               FROM runtime_ocr_task_records""" + clause
            + " ORDER BY started_at DESC, rowid DESC LIMIT ? OFFSET ?",
            args + (page_size, offset),
        ).fetchall()
        summary_rows = conn.execute(
            "SELECT status, COUNT(*) AS total FROM runtime_ocr_task_records GROUP BY status"
        ).fetchall()
    return {
        "items": [dict(row) for row in rows],
        "total": total, "page": page, "page_size": page_size,
        "status": state,
        "counts": {str(row["status"]): int(row["total"]) for row in summary_rows},
        "retention_limit": _TASK_RETENTION,
        "scope": "server_ocr_only",
    }


def _get_task_detail(cp: Any, task_id: str) -> Dict[str, Any]:
    task_id = (task_id or "").strip().lower()
    if not re.fullmatch(r"[a-f0-9]{32}", task_id):
        raise HTTPException(status_code=400, detail="无效 OCR 任务 ID")
    with cp.db() as conn:
        row = conn.execute(
            "SELECT * FROM runtime_ocr_task_records WHERE task_id = ?",
            (task_id,),
        ).fetchone()
        if row is None:
            raise HTTPException(status_code=404, detail="OCR 任务不存在或已过期")
        event_rows = conn.execute(
            """SELECT created_at, stage, status, message, metadata_json
               FROM runtime_ocr_task_events WHERE task_id = ?
               ORDER BY event_id ASC""",
            (task_id,),
        ).fetchall()
    events = []
    for event in event_rows:
        item = dict(event)
        try:
            item["metadata"] = json.loads(item.pop("metadata_json") or "{}")
        except (TypeError, ValueError):
            item["metadata"] = {}
            item.pop("metadata_json", None)
        events.append(item)
    task = dict(row)
    task["result_text_redacted"] = bool(task.get("result_text_redacted", 0))
    return {
        "task": task,
        "events": events,
        "scope": "server_ocr_only",
        "privacy": "OCR result text is bounded and redacted; source image and buyer identity are not stored.",
    }

def init_db(control_plane: Optional[Any] = None) -> None:
    cp = control_plane or _CONTROL_PLANE
    if cp is None:
        raise RuntimeError("runtime_ocr 尚未安装到控制面")

    now = cp.iso_now()
    with cp.db() as conn:
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS runtime_ocr_settings (
                id INTEGER PRIMARY KEY CHECK(id = 1),
                enabled INTEGER NOT NULL DEFAULT 1,
                max_image_bytes INTEGER NOT NULL,
                timeout_seconds REAL NOT NULL,
                max_concurrency INTEGER NOT NULL,
                max_text_chars INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL DEFAULT ''
            )
            """
        )
        conn.execute(
            """
            INSERT OR IGNORE INTO runtime_ocr_settings(
                id, enabled, max_image_bytes, timeout_seconds, max_concurrency,
                max_text_chars, updated_at, updated_by
            ) VALUES(1, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                1 if _DEFAULT_SETTINGS["enabled"] else 0,
                _DEFAULT_SETTINGS["max_image_bytes"],
                _DEFAULT_SETTINGS["timeout_seconds"],
                _DEFAULT_SETTINGS["max_concurrency"],
                _DEFAULT_SETTINGS["max_text_chars"],
                now,
                "environment-defaults",
            ),
        )
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS runtime_ocr_task_records (
                task_id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                status TEXT NOT NULL,
                phase TEXT NOT NULL,
                http_status INTEGER NOT NULL DEFAULT 0,
                error_code TEXT NOT NULL DEFAULT '',
                queue_ms INTEGER NOT NULL DEFAULT 0,
                inference_ms INTEGER NOT NULL DEFAULT 0,
                elapsed_ms INTEGER NOT NULL DEFAULT 0,
                image_bytes INTEGER NOT NULL DEFAULT 0,
                image_hash_prefix TEXT NOT NULL DEFAULT '',
                text_chars INTEGER NOT NULL DEFAULT 0,
                confidence REAL NOT NULL DEFAULT 0,
                engine TEXT NOT NULL DEFAULT '',
                result_text TEXT NOT NULL DEFAULT '',
                result_text_redacted INTEGER NOT NULL DEFAULT 0
            )
            """
        )
        # Add detail columns to existing installations without rebuilding task history.
        task_columns = {
            str(row["name"])
            for row in conn.execute("PRAGMA table_info(runtime_ocr_task_records)").fetchall()
        }
        for column, definition in (
            ("engine", "TEXT NOT NULL DEFAULT ''"),
            ("result_text", "TEXT NOT NULL DEFAULT ''"),
            ("result_text_redacted", "INTEGER NOT NULL DEFAULT 0"),
        ):
            if column not in task_columns:
                conn.execute(
                    "ALTER TABLE runtime_ocr_task_records ADD COLUMN "
                    + column + " " + definition
                )
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS runtime_ocr_task_events (
                event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                stage TEXT NOT NULL,
                status TEXT NOT NULL,
                message TEXT NOT NULL,
                metadata_json TEXT NOT NULL DEFAULT '{}'
            )
            """
        )
        conn.execute(
            "CREATE INDEX IF NOT EXISTS idx_ocr_task_events_task "
            "ON runtime_ocr_task_events(task_id, event_id)"
        )
        conn.execute(
            "CREATE INDEX IF NOT EXISTS idx_ocr_task_started "
            "ON runtime_ocr_task_records(started_at DESC)"
        )
        conn.execute(
            "CREATE INDEX IF NOT EXISTS idx_ocr_task_status_started "
            "ON runtime_ocr_task_records(status, started_at DESC)"
        )
        # A service restart interrupts in-flight tasks from the previous process
        # and adds an auditable event to each affected task.
        unfinished = conn.execute(
            "SELECT task_id FROM runtime_ocr_task_records WHERE status IN ('queued','running')"
        ).fetchall()
        conn.execute(
            """UPDATE runtime_ocr_task_records SET status='interrupted',
               phase='server_restarted', error_code='server_restarted',
               finished_at=? WHERE status IN ('queued','running')""",
            (now,),
        )
        for unfinished_row in unfinished:
            _append_task_event(
                conn, cp, str(unfinished_row["task_id"]), "server_restarted",
                "interrupted", "服务重启，未完成任务已标记中断",
                {"http_status": 503},
            )
        row = conn.execute("SELECT * FROM runtime_ocr_settings WHERE id = 1").fetchone()
        # Upgrade only the untouched old 8s bootstrap default. Never override
        # an administrator-defined timeout, even when it equals eight seconds.
        # The 16s budget covers observed 8s inference 504s on cold/contended
        # machines; in-flight ONNX workers remain bounded by the admission gate.
        if (row is not None
            and os.getenv("OCR_TIMEOUT_SECONDS") is None
            and row["updated_by"] == "environment-defaults"
            and float(row["timeout_seconds"]) == 8.0):
            conn.execute(
                "UPDATE runtime_ocr_settings SET timeout_seconds = ?, "
                "updated_at = ?, updated_by = ? WHERE id = 1",
                (16.0, now, "bootstrap-timeout-migration"),
            )
            row = conn.execute("SELECT * FROM runtime_ocr_settings WHERE id = 1").fetchone()
    if row is not None:
        _apply_settings(_row_to_settings(row))


def install(control_plane: Any) -> None:
    global _INSTALLED, _CONTROL_PLANE
    _CONTROL_PLANE = control_plane
    if _INSTALLED:
        return
    _INSTALLED = True

    app = control_plane.app
    require_client = control_plane.require_client
    require_admin = control_plane.require_admin

    @app.get("/api/admin/ocr/settings")
    async def get_runtime_ocr_settings(
        admin_username: str = Depends(require_admin),
    ) -> Dict[str, Any]:
        with control_plane.db() as conn:
            row = conn.execute("SELECT * FROM runtime_ocr_settings WHERE id = 1").fetchone()
        if row is None:
            init_db(control_plane)
            return _settings_payload(_current_settings(), "environment-defaults")
        settings = _apply_settings(_row_to_settings(row))
        return _settings_payload(settings, "database", row["updated_at"], row["updated_by"])

    @app.get("/api/admin/ocr/tasks")
    async def get_runtime_ocr_tasks(
        page: int = Query(default=1, ge=1),
        page_size: int = Query(default=20, ge=1, le=100),
        status_filter: str = Query(default="all", alias="status"),
        admin_username: str = Depends(require_admin),
    ) -> Dict[str, Any]:
        return _list_task_records(control_plane, page, page_size, status_filter)

    @app.get("/api/admin/ocr/tasks/{task_id}")
    async def get_runtime_ocr_task_detail(
        task_id: str,
        admin_username: str = Depends(require_admin),
    ) -> Dict[str, Any]:
        return _get_task_detail(control_plane, task_id)

    @app.put("/api/admin/ocr/settings")
    async def update_runtime_ocr_settings(
        payload: RuntimeOcrSettingsUpdate,
        admin_username: str = Depends(require_admin),
    ) -> Dict[str, Any]:
        settings = _apply_settings(payload.model_dump())
        now = control_plane.iso_now()
        with control_plane.db() as conn:
            conn.execute(
                """
                INSERT INTO runtime_ocr_settings(
                    id, enabled, max_image_bytes, timeout_seconds, max_concurrency,
                    max_text_chars, updated_at, updated_by
                ) VALUES(1, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(id) DO UPDATE SET
                    enabled=excluded.enabled,
                    max_image_bytes=excluded.max_image_bytes,
                    timeout_seconds=excluded.timeout_seconds,
                    max_concurrency=excluded.max_concurrency,
                    max_text_chars=excluded.max_text_chars,
                    updated_at=excluded.updated_at,
                    updated_by=excluded.updated_by
                """,
                (
                    1 if settings["enabled"] else 0,
                    settings["max_image_bytes"],
                    settings["timeout_seconds"],
                    settings["max_concurrency"],
                    settings["max_text_chars"],
                    now,
                    admin_username,
                ),
            )
        return _settings_payload(settings, "database", now, admin_username)

    @app.post("/api/admin/ocr/settings/reset")
    async def reset_runtime_ocr_settings(
        admin_username: str = Depends(require_admin),
    ) -> Dict[str, Any]:
        settings = _apply_settings(_DEFAULT_SETTINGS)
        now = control_plane.iso_now()
        with control_plane.db() as conn:
            conn.execute(
                """
                INSERT INTO runtime_ocr_settings(
                    id, enabled, max_image_bytes, timeout_seconds, max_concurrency,
                    max_text_chars, updated_at, updated_by
                ) VALUES(1, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(id) DO UPDATE SET
                    enabled=excluded.enabled,
                    max_image_bytes=excluded.max_image_bytes,
                    timeout_seconds=excluded.timeout_seconds,
                    max_concurrency=excluded.max_concurrency,
                    max_text_chars=excluded.max_text_chars,
                    updated_at=excluded.updated_at,
                    updated_by=excluded.updated_by
                """,
                (
                    1 if settings["enabled"] else 0,
                    settings["max_image_bytes"],
                    settings["timeout_seconds"],
                    settings["max_concurrency"],
                    settings["max_text_chars"],
                    now,
                    admin_username,
                ),
            )
        return _settings_payload(settings, "environment-defaults", now, admin_username)

    @app.post("/api/runtime/v1/ocr")
    async def runtime_ocr(
        request: Request,
        client: Dict[str, Any] = Depends(require_client),
    ) -> Dict[str, Any]:
        task_id = uuid.uuid4().hex
        started = time.perf_counter()
        cp = control_plane
        _create_task_record(cp, task_id)
        stage = "validation"
        queue_ms = 0
        image_size = 0
        image_hash = ""
        worker_task: Optional[asyncio.Task] = None
        inference_started = 0.0
        slot_owned = False
        deferred_release = False
        terminal = False
        try:
            settings = _current_settings()
            if not settings["enabled"]:
                raise HTTPException(status_code=503, detail="服务端OCR已被管理员停用")

            max_image_bytes = int(settings["max_image_bytes"])
            content_length = request.headers.get("content-length", "").strip()
            if content_length:
                try:
                    if int(content_length) > max_image_bytes:
                        raise HTTPException(status_code=413, detail="OCR图片超过大小限制")
                except ValueError:
                    pass

            raw = await request.body()
            image_size = len(raw)
            if not raw:
                raise HTTPException(status_code=400, detail="OCR图片不能为空")
            if image_size > max_image_bytes:
                raise HTTPException(status_code=413, detail="OCR图片超过大小限制")

            supplied_hash = request.headers.get("x-image-sha256", "").strip().lower()
            image_hash = hashlib.sha256(raw).hexdigest()
            if supplied_hash and supplied_hash != image_hash:
                raise HTTPException(status_code=400, detail="OCR图片哈希校验失败")

            stage = "admission"
            queued_at = time.perf_counter()
            await _acquire_ocr_slot(int(settings["max_concurrency"]))
            slot_owned = True
            queue_ms = int((time.perf_counter() - queued_at) * 1000)
            stage = "inference"
            inference_started = time.perf_counter()
            _update_task_record(cp, task_id, "running", "inference",
                                queue_ms=queue_ms, image_bytes=image_size,
                                image_hash=image_hash)
            worker_task = asyncio.create_task(run_in_threadpool(_run_ocr, raw))
            try:
                result = await asyncio.wait_for(
                    asyncio.shield(worker_task), timeout=float(settings["timeout_seconds"])
                )
            except asyncio.TimeoutError as exc:
                # Keep the concurrency slot until the underlying inference actually exits.
                # Native ONNX inference can continue after HTTP 504.
                deferred_release = True
                _release_when_done(worker_task)
                raise HTTPException(status_code=504, detail="服务端OCR推理超时") from exc

            inference_ms = int((time.perf_counter() - inference_started) * 1000)
            elapsed_ms = int((time.perf_counter() - started) * 1000)
            _update_task_record(
                cp, task_id, "success", "returned", http_status=200,
                queue_ms=queue_ms, inference_ms=inference_ms, elapsed_ms=elapsed_ms,
                image_bytes=image_size, image_hash=image_hash,
                text_chars=len(result.get("text", "")),
                confidence=float(result.get("confidence", 0.0)), terminal=True,
            )
            terminal = True
            return {
                **result,
                "imageSha256": image_hash,
                "cacheable": True,
                "ocrTaskId": task_id,
            }
        except HTTPException as exc:
            if not terminal:
                elapsed_ms = int((time.perf_counter() - started) * 1000)
                inference_ms = (int((time.perf_counter() - inference_started) * 1000)
                                if inference_started else 0)
                # Error codes are static identifiers; never persist image text.
                error_code = {
                    400: "invalid_image_or_hash",
                    413: "image_too_large",
                    503: "ocr_disabled_or_unavailable",
                    504: "inference_timeout",
                }.get(exc.status_code, "http_error")
                state = "timeout" if exc.status_code == 504 else (
                    "rejected" if stage == "validation" else "failed")
                _update_task_record(
                    cp, task_id, state, stage, http_status=exc.status_code,
                    error_code=error_code, queue_ms=queue_ms,
                    inference_ms=inference_ms, elapsed_ms=elapsed_ms,
                    image_bytes=image_size, image_hash=image_hash, terminal=True,
                )
                terminal = True
            raise
        except ValueError as exc:
            _update_task_record(
                cp, task_id, "rejected", stage, http_status=400,
                error_code="invalid_image", queue_ms=queue_ms,
                inference_ms=int((time.perf_counter() - inference_started) * 1000)
                if inference_started else 0,
                elapsed_ms=int((time.perf_counter() - started) * 1000),
                image_bytes=image_size, image_hash=image_hash, terminal=True,
            )
            terminal = True
            raise HTTPException(status_code=400, detail=str(exc)) from exc
        except asyncio.CancelledError:
            # Requests disconnected during admission or inference must not
            # remain in a misleading running state.
            _update_task_record(
                cp, task_id, "interrupted", stage, http_status=499,
                error_code="request_cancelled", queue_ms=queue_ms,
                inference_ms=int((time.perf_counter() - inference_started) * 1000)
                if inference_started else 0,
                elapsed_ms=int((time.perf_counter() - started) * 1000),
                image_bytes=image_size, image_hash=image_hash, terminal=True,
            )
            terminal = True
            raise
        except Exception as exc:
            _update_task_record(
                cp, task_id, "failed", stage, http_status=503,
                error_code="internal_ocr_error", queue_ms=queue_ms,
                inference_ms=int((time.perf_counter() - inference_started) * 1000)
                if inference_started else 0,
                elapsed_ms=int((time.perf_counter() - started) * 1000),
                image_bytes=image_size, image_hash=image_hash, terminal=True,
            )
            terminal = True
            raise HTTPException(status_code=503, detail="服务端OCR暂不可用") from exc
        finally:
            if slot_owned:
                if worker_task is not None and not worker_task.done() and not deferred_release:
                    # A cancelled request must not free its native inference slot.
                    _release_when_done(worker_task)
                elif not deferred_release:
                    _release_ocr_slot()
