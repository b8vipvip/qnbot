from __future__ import annotations

import asyncio
import hashlib
import io
import os
import threading
import time
from typing import Any, Dict, Optional

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
        settings = _current_settings()
        if not settings["enabled"]:
            raise HTTPException(status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="服务端OCR已被管理员停用")

        max_image_bytes = int(settings["max_image_bytes"])
        content_length = request.headers.get("content-length", "").strip()
        if content_length:
            try:
                if int(content_length) > max_image_bytes:
                    raise HTTPException(status_code=status.HTTP_413_REQUEST_ENTITY_TOO_LARGE, detail="OCR图片超过大小限制")
            except ValueError:
                pass

        raw = await request.body()
        if not raw:
            raise HTTPException(status_code=400, detail="OCR图片不能为空")
        if len(raw) > max_image_bytes:
            raise HTTPException(status_code=status.HTTP_413_REQUEST_ENTITY_TOO_LARGE, detail="OCR图片超过大小限制")

        supplied_hash = request.headers.get("x-image-sha256", "").strip().lower()
        actual_hash = hashlib.sha256(raw).hexdigest()
        if supplied_hash and supplied_hash != actual_hash:
            raise HTTPException(status_code=400, detail="OCR图片哈希校验失败")

        await _acquire_ocr_slot(int(settings["max_concurrency"]))
        task = asyncio.create_task(run_in_threadpool(_run_ocr, raw))
        release_immediately = True
        try:
            result = await asyncio.wait_for(asyncio.shield(task), timeout=float(settings["timeout_seconds"]))
            return {
                **result,
                "imageSha256": actual_hash,
                "cacheable": True,
            }
        except asyncio.TimeoutError as exc:
            # Keep the concurrency slot until the underlying inference actually exits.
            # This prevents timed-out native inference threads from accumulating without bound.
            release_immediately = False
            _release_when_done(task)
            raise HTTPException(status_code=504, detail="服务端OCR推理超时") from exc
        except ValueError as exc:
            raise HTTPException(status_code=400, detail=str(exc)) from exc
        except HTTPException:
            raise
        except Exception as exc:
            # Never include OCR text or image bytes in logs/errors.
            raise HTTPException(status_code=503, detail="服务端OCR暂不可用") from exc
        finally:
            if release_immediately:
                _release_ocr_slot()
