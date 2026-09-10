from __future__ import annotations

import json
import re
from typing import Any, Dict, Optional

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field


router = APIRouter()
_cp: Any = None
_console: Any = None

RULE_SCHEMA_VERSION = 2
BASE_RULE_KEYS = (
    "auto_reply_rules_enabled",
    "manual_handoff_keywords",
    "manual_confirm_keywords",
    "work_hours_enabled",
    "work_start_time",
    "work_end_time",
    "off_hours_reply_mode",
    "off_hours_fixed_text",
)
ORDER_RULE_KEYS = (
    "order_placed_reply_enabled",
    "order_placed_reply_mode",
    "order_placed_reply_text",
    "order_placed_api_timeout_seconds",
    "order_placed_reply_delay_seconds",
)
RULE_KEYS = BASE_RULE_KEYS + ORDER_RULE_KEYS

DEFAULT_RULE_SETTINGS: Dict[str, Any] = {
    "auto_reply_rules_enabled": True,
    "manual_handoff_keywords": "退款,退货,投诉,差评,赔偿,发票,税票,订单隐私,身份证,银行卡,法律,维权,平台介入",
    "manual_confirm_keywords": "手机号,地址,隐私,密码,账号,验证码,转账,补偿,客服主管",
    "work_hours_enabled": True,
    "work_start_time": "09:00",
    "work_end_time": "18:00",
    "off_hours_reply_mode": "AI告知下班时间",
    "off_hours_fixed_text": "亲，人工客服当前已下班，工作时间为每天 {工作时间}。您的问题已记录，请在上班时间联系或等待人工处理。",
    # These defaults are only placeholders before the Windows client adopts its real
    # local values. Existing rows are upgraded by field adoption, never by overwriting
    # the client with these values. The timeout placeholder matches the current Windows
    # runtime clamp: raw values <= 3 seconds are effectively 3 seconds.
    "order_placed_reply_enabled": False,
    "order_placed_reply_mode": "固定预设答案",
    "order_placed_reply_text": "",
    "order_placed_api_timeout_seconds": 3,
    "order_placed_reply_delay_seconds": 0,
}

_ALLOWED_OFF_HOURS_MODES = {"AI告知下班时间", "固定预设答案"}
_ALLOWED_ORDER_REPLY_MODES = {"固定预设答案", "调用HTTP接口"}
_TIME_RE = re.compile(r"^(?:[01]\d|2[0-3]):[0-5]\d$")


class AutoReplyRuleSettingsInput(BaseModel):
    auto_reply_rules_enabled: Optional[bool] = None
    manual_handoff_keywords: Optional[str] = Field(default=None, max_length=2000)
    manual_confirm_keywords: Optional[str] = Field(default=None, max_length=2000)
    work_hours_enabled: Optional[bool] = None
    work_start_time: Optional[str] = Field(default=None, max_length=5)
    work_end_time: Optional[str] = Field(default=None, max_length=5)
    off_hours_reply_mode: Optional[str] = Field(default=None, max_length=40)
    off_hours_fixed_text: Optional[str] = Field(default=None, max_length=3000)
    order_placed_reply_enabled: Optional[bool] = None
    order_placed_reply_mode: Optional[str] = Field(default=None, max_length=40)
    order_placed_reply_text: Optional[str] = Field(default=None, max_length=5000)
    order_placed_api_timeout_seconds: Optional[int] = Field(default=None, ge=3, le=60)
    order_placed_reply_delay_seconds: Optional[int] = Field(default=None, ge=0, le=0)


class RuntimeAutoReplyRuleSyncInput(BaseModel):
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
            CREATE TABLE IF NOT EXISTS bot_web_auto_reply_rules (
                client_id INTEGER PRIMARY KEY,
                desired_settings_json TEXT NOT NULL DEFAULT '{}',
                current_settings_json TEXT NOT NULL DEFAULT '{}',
                revision INTEGER NOT NULL DEFAULT 0,
                applied_revision INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NOT NULL DEFAULT '',
                settings_schema_version INTEGER NOT NULL DEFAULT 1,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );
            """
        )
        columns = {str(row[1]) for row in conn.execute("PRAGMA table_info(bot_web_auto_reply_rules)").fetchall()}
        if "settings_schema_version" not in columns:
            conn.execute(
                "ALTER TABLE bot_web_auto_reply_rules "
                "ADD COLUMN settings_schema_version INTEGER NOT NULL DEFAULT 1"
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
    text = str(value or "").replace("\x00", "").replace("\r\n", "\n").replace("\r", "\n").strip()
    return text[:limit]


def _keys_for_version(schema_version: int) -> tuple[str, ...]:
    return RULE_KEYS if int(schema_version or 0) >= 2 else BASE_RULE_KEYS


def _normalize(values: Dict[str, Any], *, fill_defaults: bool, schema_version: int = RULE_SCHEMA_VERSION) -> Dict[str, Any]:
    keys = _keys_for_version(schema_version)
    source = {key: DEFAULT_RULE_SETTINGS[key] for key in keys} if fill_defaults else {}
    if isinstance(values, dict):
        source.update({key: value for key, value in values.items() if key in keys})
    result: Dict[str, Any] = {}
    for key in keys:
        if key not in source:
            continue
        value = source[key]
        if key in {"auto_reply_rules_enabled", "work_hours_enabled", "order_placed_reply_enabled"}:
            result[key] = bool(value)
        elif key in {"manual_handoff_keywords", "manual_confirm_keywords"}:
            result[key] = _clean_text(value, 2000)
        elif key in {"work_start_time", "work_end_time"}:
            text = _clean_text(value, 5)
            if not _TIME_RE.fullmatch(text):
                raise ValueError(f"{key} 必须使用 HH:mm 格式")
            result[key] = text
        elif key == "off_hours_reply_mode":
            text = _clean_text(value, 40)
            if text not in _ALLOWED_OFF_HOURS_MODES:
                raise ValueError("off_hours_reply_mode 不受支持")
            result[key] = text
        elif key == "off_hours_fixed_text":
            result[key] = _clean_text(value, 3000)
        elif key == "order_placed_reply_mode":
            text = _clean_text(value, 40)
            if text not in _ALLOWED_ORDER_REPLY_MODES:
                raise ValueError("order_placed_reply_mode 不受支持")
            result[key] = text
        elif key == "order_placed_reply_text":
            result[key] = _clean_text(value, 5000)
        elif key == "order_placed_api_timeout_seconds":
            try:
                timeout = int(value)
            except (TypeError, ValueError):
                raise ValueError("order_placed_api_timeout_seconds 必须是整数")
            if timeout < 3 or timeout > 60:
                raise ValueError("order_placed_api_timeout_seconds 必须在 3-60 秒之间")
            result[key] = timeout
        elif key == "order_placed_reply_delay_seconds":
            try:
                delay = int(value)
            except (TypeError, ValueError):
                raise ValueError("order_placed_reply_delay_seconds 必须是整数")
            if delay != 0:
                raise ValueError("下单固定回复当前强制立即发送，delay 必须为 0")
            result[key] = 0
    return result


def _validate_for_web(values: Dict[str, Any], schema_version: int) -> Dict[str, Any]:
    try:
        normalized = _normalize(values, fill_defaults=True, schema_version=schema_version)
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))
    if normalized["off_hours_reply_mode"] == "固定预设答案" and not normalized["off_hours_fixed_text"]:
        raise HTTPException(status_code=422, detail="选择固定预设答案时，下班固定回复不能为空")
    if schema_version >= 2:
        if (
            normalized["order_placed_reply_enabled"]
            and normalized["order_placed_reply_mode"] == "固定预设答案"
            and not normalized["order_placed_reply_text"]
        ):
            raise HTTPException(status_code=422, detail="启用下单固定预设回复时，回复内容不能为空")
        if normalized["order_placed_reply_delay_seconds"] != 0:
            raise HTTPException(status_code=422, detail="下单固定回复当前只允许强制立即发送（0 秒）")
    return normalized


def _row(client_id: int) -> Optional[Dict[str, Any]]:
    with _cp.db() as conn:
        row = conn.execute(
            "SELECT * FROM bot_web_auto_reply_rules WHERE client_id=?",
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


def _schema_version(row: Optional[Dict[str, Any]]) -> int:
    if not row:
        return 0
    try:
        return max(1, min(RULE_SCHEMA_VERSION, int(row.get("settings_schema_version") or 1)))
    except Exception:
        return 1


def _snapshot(client_id: int) -> Dict[str, Any]:
    row = _row(client_id)
    if not row:
        return {
            "initialized": False,
            "desired": dict(DEFAULT_RULE_SETTINGS),
            "current": {},
            "revision": 0,
            "applied_revision": 0,
            "last_error": "",
            "online": _online(client_id),
            "settings_schema_version": 0,
            "order_rules_ready": False,
        }
    schema_version = _schema_version(row)
    desired = _normalize(_load(row["desired_settings_json"]), fill_defaults=True, schema_version=schema_version)
    current = _normalize(_load(row["current_settings_json"]), fill_defaults=False, schema_version=schema_version)
    return {
        "initialized": True,
        "desired": desired,
        "current": current,
        "revision": int(row["revision"] or 0),
        "applied_revision": int(row["applied_revision"] or 0),
        "last_error": str(row["last_error"] or ""),
        "online": _online(client_id),
        "updated_at": row["updated_at"],
        "settings_schema_version": schema_version,
        "order_rules_ready": schema_version >= 2,
    }


@router.get("/api/bot-web/auto-reply-rules")
def get_auto_reply_rules(client: Dict[str, Any] = Depends(_web_client)) -> Dict[str, Any]:
    return _snapshot(int(client["id"]))


@router.put("/api/bot-web/auto-reply-rules")
def put_auto_reply_rules(
    data: AutoReplyRuleSettingsInput,
    client: Dict[str, Any] = Depends(_web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    existing = _row(client_id)
    if not existing:
        raise HTTPException(status_code=409, detail="请先等待 Windows Bot 完成首次规则同步")
    schema_version = _schema_version(existing)
    raw_payload = {key: getattr(data, key) for key in RULE_KEYS if getattr(data, key) is not None}
    if schema_version < 2 and any(key in raw_payload for key in ORDER_RULE_KEYS):
        raise HTTPException(status_code=409, detail="请先等待新版 Windows Bot 上报下单回复规则")
    allowed_keys = set(_keys_for_version(schema_version))
    payload = {key: value for key, value in raw_payload.items() if key in allowed_keys}
    base = _load(existing["desired_settings_json"])
    base.update(payload)
    desired = _validate_for_web(base, schema_version)
    old_desired = _normalize(
        _load(existing["desired_settings_json"]),
        fill_defaults=True,
        schema_version=schema_version,
    )
    changed = old_desired != desired
    revision = int(existing["revision"] or 0)
    if changed:
        revision += 1
    applied_revision = int(existing["applied_revision"] or 0)
    now = _cp.iso_now()
    with _cp.db() as conn:
        conn.execute(
            """
            UPDATE bot_web_auto_reply_rules
            SET desired_settings_json=?,revision=?,last_error=?,updated_at=?
            WHERE client_id=?
            """,
            (
                _dump(desired),
                revision,
                "" if changed else str(existing["last_error"] or ""),
                now,
                client_id,
            ),
        )
    return _snapshot(client_id)


@router.post("/api/runtime/v1/bot-web/auto-reply-rules/sync")
def runtime_sync_auto_reply_rules(
    data: RuntimeAutoReplyRuleSyncInput,
    client: Dict[str, Any] = Depends(_runtime_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    raw_current = data.current_settings if isinstance(data.current_settings, dict) else {}
    supports_order_rules = all(key in raw_current for key in ORDER_RULE_KEYS)
    incoming_schema_version = 2 if supports_order_rules else 1
    try:
        current = _normalize(
            raw_current,
            fill_defaults=True,
            schema_version=incoming_schema_version,
        )
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))
    now = _cp.iso_now()
    existing = _row(client_id)

    # First contact adopts the Windows Bot's actual local rules. This is deliberate:
    # upgrading an existing client must never replace its locally curated rules with
    # server defaults before the operator explicitly saves something from Bot Web.
    if not existing:
        with _cp.db() as conn:
            conn.execute(
                """
                INSERT INTO bot_web_auto_reply_rules(
                    client_id,desired_settings_json,current_settings_json,revision,
                    applied_revision,last_error,settings_schema_version,updated_at
                ) VALUES(?,?,?,?,?,?,?,?)
                """,
                (
                    client_id,
                    _dump(current),
                    _dump(current),
                    1,
                    1,
                    "",
                    incoming_schema_version,
                    now,
                ),
            )
        return {
            "ok": True,
            "desired_settings": current,
            "revision": 1,
            "applied_revision": 1,
            "settings_schema_version": incoming_schema_version,
        }

    stored_schema_version = _schema_version(existing)
    revision = int(existing["revision"] or 0)
    applied_revision = int(existing["applied_revision"] or 0)
    last_error = _clean_text(data.last_error, 1000)

    # Schema v2 adds the post-order fields. An existing v1 row must adopt those fields
    # from the first v2 Windows report rather than filling server defaults and pushing
    # them down. Existing desired v1 edits are preserved, including a pending revision.
    if stored_schema_version < 2 and incoming_schema_version >= 2:
        desired = _normalize(
            _load(existing["desired_settings_json"]),
            fill_defaults=True,
            schema_version=1,
        )
        for key in ORDER_RULE_KEYS:
            desired[key] = current[key]
        if current == desired:
            applied_revision = revision
            last_error = ""
        with _cp.db() as conn:
            conn.execute(
                """
                UPDATE bot_web_auto_reply_rules
                SET desired_settings_json=?,current_settings_json=?,applied_revision=?,
                    last_error=?,settings_schema_version=2,updated_at=?
                WHERE client_id=?
                """,
                (
                    _dump(desired),
                    _dump(current),
                    applied_revision,
                    last_error,
                    now,
                    client_id,
                ),
            )
        return {
            "ok": True,
            "desired_settings": desired,
            "revision": revision,
            "applied_revision": applied_revision,
            "settings_schema_version": 2,
        }

    desired = _normalize(
        _load(existing["desired_settings_json"]),
        fill_defaults=True,
        schema_version=stored_schema_version,
    )
    # A downgraded/older client cannot acknowledge a newer schema because it does not
    # report the newly managed fields. It may still receive the v1 subset it understands.
    current_for_store = current
    if incoming_schema_version >= stored_schema_version and current == desired:
        applied_revision = revision
        last_error = ""
    with _cp.db() as conn:
        conn.execute(
            """
            UPDATE bot_web_auto_reply_rules
            SET current_settings_json=?,applied_revision=?,last_error=?,updated_at=?
            WHERE client_id=?
            """,
            (_dump(current_for_store), applied_revision, last_error, now, client_id),
        )
    return {
        "ok": True,
        "desired_settings": desired,
        "revision": revision,
        "applied_revision": applied_revision,
        "settings_schema_version": stored_schema_version,
    }
