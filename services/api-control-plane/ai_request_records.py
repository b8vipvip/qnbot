"""Auditable, bounded AI requests and responses for authenticated Bot clients.

Prompts contain customer messages: keep this behind administrator authentication,
redact obvious secrets/media payloads, and expire records after 14 days.
"""
from __future__ import annotations

import csv
import io
import json
import re
import threading
import time
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, Optional

from fastapi import APIRouter, Depends, HTTPException, Query, Request, Response

router = APIRouter()
_cp: Any = None
_LAST_CLEANUP = float('-inf')
_CLEANUP_LOCK = threading.Lock()
_RETENTION_DAYS = 14
_MAX_PROMPT_CHARS = 48000
_MAX_REPLY_CHARS = 48000
_MAX_ERROR_CHARS = 4000
_SECRET_KEY = re.compile(r"(?:api[_-]?key|access[_-]?token|refresh[_-]?token|authorization|password|passwd|secret|cookie|client_secret|credential)", re.I)
_BEARER = re.compile(r"(?i)(\bBearer\s+)[A-Za-z0-9_./+~=-]+")
_TOKEN = re.compile(r"(?i)\b(?:sk-[A-Za-z0-9_-]{8,}|qnb_[A-Za-z0-9_-]{8,})\b")
_SECRET_INLINE = re.compile(r"(?i)(\b(?:api[_-]?key|access[_-]?token|password|secret)\s*[:=]\s*)[A-Za-z0-9_./+~=-]{6,}")
_DATA_URI = re.compile(r"(?i)data:(?:image|audio|video|application)/[^\s;,]+;base64,[A-Za-z0-9+/=]+")
_MEDIA_URL = re.compile(r"(?i)^https?://[^\s]+(?:\.png|\.jpg|\.jpeg|\.webp|\.gif|\.mp3|\.mp4)(?:[?#].*)?$")


def install(control_plane: Any) -> None:
    global _cp
    _cp = control_plane
    control_plane.app.include_router(router)


def init_db() -> None:
    with _cp.db() as conn:
        conn.executescript("""
        CREATE TABLE IF NOT EXISTS ai_request_records (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            client_id INTEGER NOT NULL,
            client_name TEXT NOT NULL,
            shop_key TEXT NOT NULL DEFAULT '',
            endpoint TEXT NOT NULL,
            request_model TEXT NOT NULL,
            resolved_model TEXT NOT NULL DEFAULT '',
            provider_name TEXT NOT NULL DEFAULT '',
            protocol TEXT NOT NULL DEFAULT '',
            success INTEGER NOT NULL,
            http_status INTEGER NOT NULL,
            duration_ms INTEGER NOT NULL,
            prompt_json TEXT NOT NULL,
            response_text TEXT NOT NULL DEFAULT '',
            attempts_json TEXT NOT NULL DEFAULT '[]',
            error TEXT NOT NULL DEFAULT '',
            truncated INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_ai_request_recent ON ai_request_records(id DESC);
        CREATE INDEX IF NOT EXISTS idx_ai_request_client ON ai_request_records(client_id, id DESC);
        CREATE INDEX IF NOT EXISTS idx_ai_request_shop ON ai_request_records(shop_key, id DESC);
        CREATE INDEX IF NOT EXISTS idx_ai_request_time ON ai_request_records(created_at);
        """)


def _text(value: Any, limit: int) -> str:
    result = str(value if value is not None else "")
    result = _DATA_URI.sub("[媒体数据已省略]", result)
    result = _BEARER.sub(r"\1[REDACTED]", result)
    result = _TOKEN.sub("[REDACTED]", result)
    result = _SECRET_INLINE.sub(r"\1[REDACTED]", result)
    return result[:limit]


def _scrub(value: Any, depth: int = 0) -> Any:
    if depth > 12:
        return "[嵌套内容已省略]"
    if isinstance(value, dict):
        result = {}
        for key, entry in list(value.items())[:256]:
            label = str(key)[:128]
            if _SECRET_KEY.search(label):
                result[label] = "[REDACTED]"
            elif label in {"image_url", "input_image", "image", "audio", "input_audio", "file", "file_data"}:
                result[label] = "[媒体内容已省略]"
            else:
                result[label] = _scrub(entry, depth + 1)
        return result
    if isinstance(value, (list, tuple)):
        return [_scrub(item, depth + 1) for item in value[:256]]
    if isinstance(value, str):
        if _MEDIA_URL.match(value):
            return "[媒体链接已省略]"
        return _text(value, 16000)
    if isinstance(value, (int, float, bool)) or value is None:
        return value
    return _text(value, 400)


def _json_bounded(value: Any, limit: int) -> tuple[str, bool]:
    content = json.dumps(_scrub(value), ensure_ascii=False, separators=(",", ":"), default=str)
    if len(content) <= limit:
        return content, False
    # A bounded preview is deliberate: never pretend a truncated string is valid JSON.
    return json.dumps({"truncated": True, "preview": content[:limit - 100]}, ensure_ascii=False), True


def _maybe_cleanup() -> None:
    global _LAST_CLEANUP
    now = time.monotonic()
    if now - _LAST_CLEANUP < 3600 or not _CLEANUP_LOCK.acquire(blocking=False):
        return
    try:
        if now - _LAST_CLEANUP < 3600:
            return
        threshold = (datetime.now(timezone.utc) - timedelta(days=_RETENTION_DAYS)).isoformat(timespec="seconds")
        with _cp.db() as conn:
            conn.execute("DELETE FROM ai_request_records WHERE created_at < ?", (threshold,))
        _LAST_CLEANUP = now
    finally:
        _CLEANUP_LOCK.release()


def record(
    *, client_id: int, client_name: str, shop_key: str, endpoint: str,
    requested_model: str, messages: Any, dispatched: Dict[str, Any],
    duration_ms: int, internal_error: str = "",
) -> None:
    """One row per logical authenticated client request, not per fallback attempt.

    Called best-effort by dispatch_chat: recording must not change API outcome.
    """
    if not client_id:
        return
    _maybe_cleanup()
    attempts = dispatched.get("attempts") or []
    successful = bool(dispatched.get("success")) and not internal_error
    winner = dispatched.get("attempt") if successful else (attempts[-1] if attempts else {})
    winner = winner or {}
    compact_attempts = [{
        key: _scrub(attempt.get(key))
        for key in ("provider_name", "model", "protocol", "latency_ms", "status_code", "success", "error")
        if key in attempt
    } for attempt in attempts[:24] if isinstance(attempt, dict)]
    prompts, truncated_prompt = _json_bounded(messages, _MAX_PROMPT_CHARS)
    attempt_json, truncated_attempts = _json_bounded(compact_attempts, 16000)
    answer = _text(winner.get("answer") if successful else "", _MAX_REPLY_CHARS)
    error = _text(internal_error or ("" if successful else winner.get("error") or "所有上游调用均失败"), _MAX_ERROR_CHARS)
    with _cp.db() as conn:
        conn.execute("""
            INSERT INTO ai_request_records(
              client_id,client_name,shop_key,endpoint,request_model,
              resolved_model,provider_name,protocol,success,http_status,
              duration_ms,prompt_json,response_text,attempts_json,error,truncated,created_at
            ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
        """, (
            int(client_id), _text(client_name, 120), _text(shop_key, 160),
            _text(endpoint, 90), _text(requested_model, 160),
            _text(winner.get("model"), 160), _text(winner.get("provider_name"), 120),
            _text(winner.get("protocol"), 40), int(successful),
            200 if successful else (500 if internal_error else 502),
            max(0, int(duration_ms)), prompts, answer, attempt_json, error,
            int(truncated_prompt or truncated_attempts or len(str(winner.get("answer") or "")) > _MAX_REPLY_CHARS
                or len(attempts) > 24), datetime.now(timezone.utc).isoformat(timespec="seconds")
        ))


def _require_admin(request: Request) -> str:
    return _cp.require_admin(request)


def _where(client_id: Optional[int], shop_key: str, status: str, model: str) -> tuple[str, list[Any]]:
    clauses, args = [], []
    if client_id is not None:
        clauses.append("client_id=?")
        args.append(client_id)
    if shop_key:
        clauses.append("shop_key=?")
        args.append(shop_key)
    if status:
        if status not in {"success", "failed"}:
            raise HTTPException(status_code=422, detail="status 必须是 success 或 failed")
        clauses.append("success=?")
        args.append(int(status == "success"))
    if model:
        clauses.append("request_model=?")
        args.append(model)
    return (" WHERE " + " AND ".join(clauses) if clauses else ""), args


_SUMMARY_FIELDS = (
    "id,client_id,client_name,shop_key,endpoint,request_model,"
    "resolved_model,provider_name,protocol,success,http_status,"
    "duration_ms,truncated,created_at,error"
)


@router.get("/api/admin/ai-requests")
def list_ai_requests(
    response: Response,
    client_id: Optional[int] = Query(None, ge=1),
    shop_key: str = Query("", max_length=160),
    status: str = Query("", max_length=12),
    model: str = Query("", max_length=160),
    limit: int = Query(50, ge=1, le=200),
    offset: int = Query(0, ge=0, le=100000),
    _: str = Depends(_require_admin),
) -> Dict[str, Any]:
    response.headers["Cache-Control"] = "no-store"
    where, args = _where(client_id, shop_key.strip(), status, model.strip())
    with _cp.db() as conn:
        total = int(conn.execute("SELECT COUNT(*) FROM ai_request_records" + where, args).fetchone()[0])
        rows = conn.execute(
            "SELECT " + _SUMMARY_FIELDS + " FROM ai_request_records" + where
            + " ORDER BY id DESC LIMIT ? OFFSET ?", [*args, limit, offset]
        ).fetchall()
    return {"total": total, "limit": limit, "offset": offset, "items": [dict(row) for row in rows]}


@router.get("/api/admin/ai-requests/export")
def export_ai_requests(
    count: int = Query(100, ge=1, le=5000),
    format: str = Query("json", pattern="^(json|csv)$"),
    client_id: Optional[int] = Query(None, ge=1),
    shop_key: str = Query("", max_length=160),
    status: str = Query("", max_length=12),
    model: str = Query("", max_length=160),
    _: str = Depends(_require_admin),
) -> Response:
    where, args = _where(client_id, shop_key.strip(), status, model.strip())
    with _cp.db() as conn:
        rows = [dict(r) for r in conn.execute(
            "SELECT * FROM ai_request_records" + where + " ORDER BY id DESC LIMIT ?",
            [*args, count]
        ).fetchall()]
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    headers = {"Cache-Control": "no-store", "Content-Disposition": f'attachment; filename="ai-requests-{stamp}.{format}"'}
    for row in rows:
        row["prompt"] = json.loads(row.pop("prompt_json"))
        row["attempts"] = json.loads(row.pop("attempts_json"))
    if format == "json":
        return Response(
            json.dumps({"exported_count": len(rows), "records": rows}, ensure_ascii=False, indent=2),
            media_type="application/json; charset=utf-8", headers=headers
        )
    buffer = io.StringIO()
    fields = ["id", "created_at", "client_id", "client_name", "shop_key", "endpoint",
              "request_model", "resolved_model", "provider_name", "protocol", "success",
              "http_status", "duration_ms", "truncated", "prompt", "response_text", "error", "attempts"]
    writer = csv.DictWriter(buffer, fieldnames=fields)
    writer.writeheader()
    for row in rows:
        formatted = {}
        for field in fields:
            val = row.get(field, "")
            if isinstance(val, (list, dict)):
                val = json.dumps(val, ensure_ascii=False)
            val = str(val if val is not None else "")
            # Escape Excel/Calc formula interpretation, including whitespace prefixes.
            if val.lstrip().startswith(("=", "+", "-", "@", "\t", "\r", "\n")):
                val = "'" + val
            formatted[field] = val
        writer.writerow(formatted)
    return Response("\ufeff" + buffer.getvalue(), media_type="text/csv; charset=utf-8", headers=headers)


@router.get("/api/admin/ai-requests/{record_id}")
def ai_request_detail(
    record_id: int,
    response: Response,
    _: str = Depends(_require_admin),
) -> Dict[str, Any]:
    response.headers["Cache-Control"] = "no-store"
    with _cp.db() as conn:
        row = conn.execute("SELECT * FROM ai_request_records WHERE id=?", (record_id,)).fetchone()
    if row is None:
        raise HTTPException(status_code=404, detail="记录不存在或已过期")
    result = dict(row)
    result["prompt"] = json.loads(result.pop("prompt_json"))
    result["attempts"] = json.loads(result.pop("attempts_json"))
    return result
