from __future__ import annotations

import json
from typing import Any, Dict, List, Optional

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field


router = APIRouter()
_cp: Any = None
_console: Any = None

EDITABLE_KEYS = (
    "id",
    "name",
    "enabled",
    "text_model",
    "vision_model",
    "supports_vision",
    "max_image_size_mb",
    "vision_timeout_seconds",
    "system_prompt",
    "priority",
    "weight",
    "timeout_seconds",
    "retry_count",
)
STATUS_KEYS = ("last_status", "last_latency_ms", "last_test_time")
FORBIDDEN_REMOTE_KEYS = (
    "api_key",
    "apikey",
    "base_url",
    "baseurl",
    "authorization",
    "cookie",
    "token",
    "password",
)


class AiEndpointInput(BaseModel):
    id: str = Field(min_length=1, max_length=80)
    name: str = Field(default="AI接口", max_length=120)
    enabled: bool = True
    text_model: str = Field(default="", max_length=200)
    vision_model: str = Field(default="", max_length=200)
    supports_vision: bool = False
    max_image_size_mb: int = Field(default=5, ge=1, le=20)
    vision_timeout_seconds: int = Field(default=45, ge=10, le=180)
    system_prompt: str = Field(default="", max_length=12000)
    priority: int = Field(default=1, ge=1, le=1000)
    weight: int = Field(default=1, ge=1, le=100)
    timeout_seconds: int = Field(default=35, ge=5, le=300)
    retry_count: int = Field(default=0, ge=0, le=10)


class AiModelSettingsInput(BaseModel):
    endpoints: List[AiEndpointInput] = Field(default_factory=list, max_length=50)


class RuntimeAiModelSettingsSyncInput(BaseModel):
    current_settings: Dict[str, Any] = Field(default_factory=dict)
    last_error: str = Field(default="", max_length=1000)


def install(control_plane: Any, console_module: Any) -> None:
    global _cp, _console
    _cp = control_plane
    _console = console_module
    control_plane.app.include_router(router)


def init_db() -> None:
    with _cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bot_web_ai_model_settings (
                client_id INTEGER PRIMARY KEY,
                desired_settings_json TEXT NOT NULL DEFAULT '{}',
                current_settings_json TEXT NOT NULL DEFAULT '{}',
                revision INTEGER NOT NULL DEFAULT 0,
                applied_revision INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL,
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );
            """
        )


def _web_client(request: Request) -> Dict[str, Any]:
    return _console._web_client(request)


def _runtime_client(request: Request) -> Dict[str, Any]:
    return _console._runtime_client(request)


def _dump(value: Dict[str, Any]) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _load(value: str) -> Dict[str, Any]:
    try:
        parsed = json.loads(value or "{}")
        return parsed if isinstance(parsed, dict) else {}
    except Exception:
        return {}


def _clean_text(value: Any, limit: int) -> str:
    return str(value or "").replace("\x00", "").replace("\r\n", "\n").replace("\r", "\n").strip()[:limit]


def _clean_id(value: Any) -> str:
    value = _clean_text(value, 80)
    if not value:
        raise ValueError("AI endpoint id 不能为空")
    return value


def _bounded_int(value: Any, low: int, high: int, field: str) -> int:
    try:
        number = int(value)
    except (TypeError, ValueError):
        raise ValueError(f"{field} 必须是整数")
    if number < low or number > high:
        raise ValueError(f"{field} 必须在 {low}-{high} 之间")
    return number


def _normalize_endpoint(value: Any, *, include_status: bool) -> Dict[str, Any]:
    if not isinstance(value, dict):
        raise ValueError("AI endpoint 必须是对象")
    endpoint = {
        "id": _clean_id(value.get("id")),
        "name": _clean_text(value.get("name") or "AI接口", 120),
        "enabled": bool(value.get("enabled", True)),
        "text_model": _clean_text(value.get("text_model"), 200),
        "vision_model": _clean_text(value.get("vision_model"), 200),
        "supports_vision": bool(value.get("supports_vision", False)),
        "max_image_size_mb": _bounded_int(value.get("max_image_size_mb", 5), 1, 20, "max_image_size_mb"),
        "vision_timeout_seconds": _bounded_int(value.get("vision_timeout_seconds", 45), 10, 180, "vision_timeout_seconds"),
        "system_prompt": _clean_text(value.get("system_prompt"), 12000),
        "priority": _bounded_int(value.get("priority", 1), 1, 1000, "priority"),
        "weight": _bounded_int(value.get("weight", 1), 1, 100, "weight"),
        "timeout_seconds": _bounded_int(value.get("timeout_seconds", 35), 5, 300, "timeout_seconds"),
        "retry_count": _bounded_int(value.get("retry_count", 0), 0, 10, "retry_count"),
    }
    if include_status:
        endpoint["last_status"] = _clean_text(value.get("last_status"), 500)
        endpoint["last_latency_ms"] = max(0, min(86_400_000, _bounded_int(value.get("last_latency_ms", 0), 0, 86_400_000, "last_latency_ms")))
        endpoint["last_test_time"] = _clean_text(value.get("last_test_time"), 80)
    return endpoint


def _normalize_settings(values: Dict[str, Any], *, include_status: bool) -> Dict[str, Any]:
    raw = values if isinstance(values, dict) else {}
    endpoints = raw.get("endpoints")
    if endpoints is None:
        endpoints = []
    if not isinstance(endpoints, list):
        raise ValueError("endpoints 必须是数组")
    if len(endpoints) > 50:
        raise ValueError("AI endpoint 最多允许 50 个")
    normalized = [_normalize_endpoint(item, include_status=include_status) for item in endpoints]
    ids = [item["id"] for item in normalized]
    if len(ids) != len(set(ids)):
        raise ValueError("AI endpoint id 不允许重复")
    normalized.sort(key=lambda item: (item["priority"], item["name"], item["id"]))
    return {"endpoints": normalized}


def _editable(values: Dict[str, Any]) -> Dict[str, Any]:
    normalized = _normalize_settings(values, include_status=False)
    return normalized


def _reconcile_desired(stored: Dict[str, Any], current: Dict[str, Any]) -> Dict[str, Any]:
    current_editable = _editable(current)
    try:
        stored_editable = _editable(stored)
    except ValueError:
        stored_editable = {"endpoints": []}
    stored_by_id = {item["id"]: item for item in stored_editable["endpoints"]}
    result: List[Dict[str, Any]] = []
    for local in current_editable["endpoints"]:
        remote = stored_by_id.get(local["id"])
        result.append(dict(remote) if remote is not None else dict(local))
    result.sort(key=lambda item: (item["priority"], item["name"], item["id"]))
    return {"endpoints": result}


def _row(client_id: int) -> Optional[Dict[str, Any]]:
    with _cp.db() as conn:
        row = conn.execute(
            "SELECT * FROM bot_web_ai_model_settings WHERE client_id=?",
            (client_id,),
        ).fetchone()
    return dict(row) if row else None


def _online(client_id: int) -> bool:
    with _cp.db() as conn:
        row = conn.execute(
            "SELECT last_seen_at FROM bot_client_state WHERE client_id=?",
            (client_id,),
        ).fetchone()
    return bool(row and _console._is_online(row["last_seen_at"]))


def _snapshot(client_id: int) -> Dict[str, Any]:
    row = _row(client_id)
    if not row:
        return {
            "initialized": False,
            "desired": {"endpoints": []},
            "current": {"endpoints": []},
            "revision": 0,
            "applied_revision": 0,
            "last_error": "",
            "online": _online(client_id),
        }
    try:
        desired = _editable(_load(row["desired_settings_json"]))
    except ValueError:
        desired = {"endpoints": []}
    try:
        current = _normalize_settings(_load(row["current_settings_json"]), include_status=True)
    except ValueError:
        current = {"endpoints": []}
    return {
        "initialized": True,
        "desired": desired,
        "current": current,
        "revision": int(row["revision"] or 0),
        "applied_revision": int(row["applied_revision"] or 0),
        "last_error": _clean_text(row["last_error"], 1000),
        "online": _online(client_id),
        "updated_at": row["updated_at"],
    }


@router.get("/api/bot-web/ai-model-settings")
def get_ai_model_settings(client: Dict[str, Any] = Depends(_web_client)) -> Dict[str, Any]:
    return _snapshot(int(client["id"]))


@router.put("/api/bot-web/ai-model-settings")
def put_ai_model_settings(
    data: AiModelSettingsInput,
    client: Dict[str, Any] = Depends(_web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    existing = _row(client_id)
    if not existing:
        raise HTTPException(status_code=409, detail="请先等待 Windows Bot 完成首次 AI 模型设置同步")

    try:
        desired = _normalize_settings(
            {"endpoints": [item.model_dump() for item in data.endpoints]},
            include_status=False,
        )
        current = _normalize_settings(_load(existing["current_settings_json"]), include_status=True)
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))

    desired_ids = {item["id"] for item in desired["endpoints"]}
    current_ids = {item["id"] for item in current["endpoints"]}
    if desired_ids != current_ids:
        raise HTTPException(
            status_code=409,
            detail="AI 接口列表已在 Windows 端变化，请刷新后重试；Web 端不允许新增或删除接口",
        )

    old_desired = _editable(_load(existing["desired_settings_json"]))
    changed = old_desired != desired
    revision = int(existing["revision"] or 0) + (1 if changed else 0)
    now = _cp.iso_now()
    with _cp.db() as conn:
        conn.execute(
            """
            UPDATE bot_web_ai_model_settings
            SET desired_settings_json=?,revision=?,last_error=?,updated_at=?
            WHERE client_id=?
            """,
            (
                _dump(desired),
                revision,
                "" if changed else _clean_text(existing["last_error"], 1000),
                now,
                client_id,
            ),
        )
    return _snapshot(client_id)


@router.post("/api/runtime/v1/bot-web/ai-model-settings/sync")
def runtime_sync_ai_model_settings(
    data: RuntimeAiModelSettingsSyncInput,
    client: Dict[str, Any] = Depends(_runtime_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    try:
        current = _normalize_settings(data.current_settings, include_status=True)
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))

    # Only the explicit safe schema above is persisted. BaseUrl, ApiKey, Authorization,
    # Cookie, tokens and passwords are intentionally absent even if a compromised or
    # future client accidentally includes them in current_settings.
    current_editable = _editable(current)
    now = _cp.iso_now()
    existing = _row(client_id)
    if not existing:
        with _cp.db() as conn:
            conn.execute(
                """
                INSERT INTO bot_web_ai_model_settings(
                    client_id,desired_settings_json,current_settings_json,revision,
                    applied_revision,last_error,updated_at
                ) VALUES(?,?,?,?,?,?,?)
                """,
                (client_id, _dump(current_editable), _dump(current), 1, 1, "", now),
            )
        return {
            "ok": True,
            "desired_settings": current_editable,
            "revision": 1,
            "applied_revision": 1,
        }

    revision = int(existing["revision"] or 0)
    applied_revision = int(existing["applied_revision"] or 0)
    last_error = _clean_text(data.last_error, 1000)
    desired = _reconcile_desired(_load(existing["desired_settings_json"]), current)
    if current_editable == desired:
        applied_revision = revision
        last_error = ""

    with _cp.db() as conn:
        conn.execute(
            """
            UPDATE bot_web_ai_model_settings
            SET desired_settings_json=?,current_settings_json=?,applied_revision=?,
                last_error=?,updated_at=?
            WHERE client_id=?
            """,
            (_dump(desired), _dump(current), applied_revision, last_error, now, client_id),
        )

    return {
        "ok": True,
        "desired_settings": desired,
        "revision": revision,
        "applied_revision": applied_revision,
    }
