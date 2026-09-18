from __future__ import annotations

import hashlib
import json
import secrets
import threading
import time
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional

from fastapi import APIRouter, Depends, HTTPException, Query, Request, status
from fastapi.responses import FileResponse, RedirectResponse
from pydantic import BaseModel, Field


router = APIRouter()
_cp: Any = None
_LOGIN_LOCK = threading.Lock()
_LOGIN_ATTEMPTS: Dict[str, List[float]] = {}

DEFAULT_SETTINGS: Dict[str, Any] = {
    "auto_reply_enabled": True,
    "message_sync_enabled": True,
    "allow_web_manual_reply": True,
    "sync_interval_seconds": 3,
    "message_retention_days": 7,
}


def install(control_plane: Any) -> None:
    global _cp
    _cp = control_plane
    control_plane.app.include_router(router)


def _now() -> str:
    return _cp.iso_now()


def _json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _parse(value: Optional[str], default: Any) -> Any:
    if not value:
        return default
    try:
        return json.loads(value)
    except Exception:
        return default


def _safe(value: Any, limit: int = 1000) -> str:
    text = str(value or "").replace("\r", " ").replace("\n", " ").strip()
    while "  " in text:
        text = text.replace("  ", " ")
    return text if len(text) <= limit else text[:limit] + "..."


def _utc_from_iso(value: Optional[str]) -> Optional[datetime]:
    if not value:
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.astimezone(timezone.utc)
    except Exception:
        return None


def _is_online(last_seen_at: Optional[str]) -> bool:
    seen = _utc_from_iso(last_seen_at)
    return bool(seen and seen >= datetime.now(timezone.utc) - timedelta(seconds=20))


def init_bot_web_db() -> None:
    with _cp.db() as conn:
        columns = {row["name"] for row in conn.execute("PRAGMA table_info(client_tokens)").fetchall()}
        if "token_cipher" not in columns:
            conn.execute("ALTER TABLE client_tokens ADD COLUMN token_cipher TEXT NOT NULL DEFAULT ''")
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bot_client_state (
                client_id INTEGER PRIMARY KEY,
                status_json TEXT NOT NULL DEFAULT '{}',
                current_settings_json TEXT NOT NULL DEFAULT '{}',
                desired_settings_json TEXT NOT NULL DEFAULT '{}',
                app_version TEXT NOT NULL DEFAULT '',
                seller_nicks_json TEXT NOT NULL DEFAULT '[]',
                last_seen_at TEXT,
                updated_at TEXT NOT NULL,
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS bot_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_id INTEGER NOT NULL,
                message_key TEXT NOT NULL,
                seller TEXT NOT NULL DEFAULT '',
                buyer TEXT NOT NULL DEFAULT '',
                role TEXT NOT NULL DEFAULT 'system',
                text TEXT NOT NULL DEFAULT '',
                message_type TEXT NOT NULL DEFAULT 'text',
                occurred_at TEXT NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(client_id, message_key),
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS bot_commands (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_id INTEGER NOT NULL,
                command_type TEXT NOT NULL,
                payload_json TEXT NOT NULL DEFAULT '{}',
                status TEXT NOT NULL DEFAULT 'pending',
                result_json TEXT NOT NULL DEFAULT '{}',
                error TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                completed_at TEXT,
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_bot_messages_client_id ON bot_messages(client_id, id DESC);
            CREATE INDEX IF NOT EXISTS idx_bot_messages_conversation ON bot_messages(client_id, seller, buyer, id DESC);
            CREATE INDEX IF NOT EXISTS idx_bot_commands_pending ON bot_commands(client_id, status, id ASC);
            """
        )


def _bearer(request: Request) -> str:
    header = request.headers.get("authorization", "")
    if not header.lower().startswith("bearer "):
        return ""
    return header.split(" ", 1)[1].strip()


def _client_by_token(token: str, capture_cipher: bool = True) -> Optional[Dict[str, Any]]:
    if not token:
        return None
    digest = _cp.hash_token(token)
    with _cp.db() as conn:
        row = conn.execute(
            "SELECT * FROM client_tokens WHERE token_hash=? AND enabled=1",
            (digest,),
        ).fetchone()
        if not row:
            return None
        item = dict(row)
        updates = ["last_used_at=?"]
        values: List[Any] = [_now()]
        if capture_cipher and not item.get("token_cipher"):
            updates.append("token_cipher=?")
            values.append(_cp.encrypt_secret(token))
        values.append(item["id"])
        conn.execute("UPDATE client_tokens SET " + ",".join(updates) + " WHERE id=?", tuple(values))
    return item


def _runtime_client(request: Request) -> Dict[str, Any]:
    client = _client_by_token(_bearer(request), capture_cipher=True)
    if not client:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="客户端令牌无效")
    return client


def _web_client(request: Request) -> Dict[str, Any]:
    client_id = int(request.session.get("bot_web_client_id") or 0)
    if client_id < 1:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="请先使用 Bot 客户端令牌登录")
    with _cp.db() as conn:
        row = conn.execute("SELECT * FROM client_tokens WHERE id=? AND enabled=1", (client_id,)).fetchone()
    if not row:
        request.session.pop("bot_web_client_id", None)
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="客户端令牌已停用或删除")
    return dict(row)


def _settings_for(client_id: int) -> Dict[str, Any]:
    with _cp.db() as conn:
        row = conn.execute(
            "SELECT desired_settings_json,current_settings_json FROM bot_client_state WHERE client_id=?",
            (client_id,),
        ).fetchone()
    desired = dict(DEFAULT_SETTINGS)
    current: Dict[str, Any] = {}
    if row:
        desired.update(_parse(row["desired_settings_json"], {}))
        current = _parse(row["current_settings_json"], {})
    return {"desired": desired, "current": current}


def _ensure_state(client_id: int) -> None:
    with _cp.db() as conn:
        conn.execute(
            """
            INSERT OR IGNORE INTO bot_client_state(
                client_id,status_json,current_settings_json,desired_settings_json,
                app_version,seller_nicks_json,last_seen_at,updated_at
            ) VALUES(?,?,?,?,?,?,?,?)
            """,
            (client_id, "{}", "{}", _json(DEFAULT_SETTINGS), "", "[]", None, _now()),
        )


def _rate_limit_login(request: Request) -> None:
    forwarded = request.headers.get("x-forwarded-for", "").split(",", 1)[0].strip()
    key = forwarded or (request.client.host if request.client else "unknown")
    now = time.time()
    with _LOGIN_LOCK:
        recent = [x for x in _LOGIN_ATTEMPTS.get(key, []) if x >= now - 600]
        if len(recent) >= 12:
            raise HTTPException(status_code=429, detail="登录尝试过于频繁，请稍后再试")
        recent.append(now)
        _LOGIN_ATTEMPTS[key] = recent


def _clear_login_attempts(request: Request) -> None:
    forwarded = request.headers.get("x-forwarded-for", "").split(",", 1)[0].strip()
    key = forwarded or (request.client.host if request.client else "unknown")
    with _LOGIN_LOCK:
        _LOGIN_ATTEMPTS.pop(key, None)


def _command_rows(client_id: int) -> List[Dict[str, Any]]:
    with _cp.db() as conn:
        rows = conn.execute(
            """
            SELECT id,command_type,payload_json,created_at
            FROM bot_commands
            WHERE client_id=? AND status='pending'
            ORDER BY id ASC LIMIT 30
            """,
            (client_id,),
        ).fetchall()
    return [
        {
            "id": int(row["id"]),
            "type": row["command_type"],
            "payload": _parse(row["payload_json"], {}),
            "created_at": row["created_at"],
        }
        for row in rows
    ]


def _save_message(client_id: int, item: Dict[str, Any]) -> None:
    seller = _safe(item.get("seller"), 120)
    buyer = _safe(item.get("buyer"), 120)
    role = str(item.get("role") or "system").lower()
    if role not in {"user", "assistant", "system"}:
        role = "system"
    text = str(item.get("text") or "").replace("\x00", "").strip()
    if not text:
        return
    text = text[:6000]
    occurred = str(item.get("occurred_at") or _now())[:64]
    message_type = _safe(item.get("message_type") or "text", 50)
    message_key = _safe(item.get("message_key"), 200)
    if not message_key:
        seed = f"{seller}|{buyer}|{role}|{occurred}|{text}"
        message_key = hashlib.sha256(seed.encode("utf-8")).hexdigest()
    with _cp.db() as conn:
        conn.execute(
            """
            INSERT OR IGNORE INTO bot_messages(
                client_id,message_key,seller,buyer,role,text,message_type,occurred_at,created_at
            ) VALUES(?,?,?,?,?,?,?,?,?)
            """,
            (client_id, message_key, seller, buyer, role, text, message_type, occurred, _now()),
        )


def _apply_command_results(client_id: int, results: List[Dict[str, Any]]) -> None:
    for result in results[:100]:
        try:
            command_id = int(result.get("id") or 0)
        except Exception:
            continue
        if command_id < 1:
            continue
        success = bool(result.get("success"))
        error = _safe(result.get("error"), 1000)
        result_json = result.get("result") if isinstance(result.get("result"), dict) else {}
        with _cp.db() as conn:
            row = conn.execute(
                "SELECT command_type,payload_json FROM bot_commands WHERE id=? AND client_id=?",
                (command_id, client_id),
            ).fetchone()
            if not row:
                continue
            conn.execute(
                """
                UPDATE bot_commands
                SET status=?,result_json=?,error=?,completed_at=?
                WHERE id=? AND client_id=?
                """,
                ("completed" if success else "failed", _json(result_json), error, _now(), command_id, client_id),
            )
            if row["command_type"] == "send_text":
                conn.execute(
                    "UPDATE bot_messages SET message_type=? WHERE client_id=? AND message_key=?",
                    ("web_sent" if success else "web_failed", client_id, f"command:{command_id}"),
                )


def _cleanup_messages(client_id: int, retention_days: int) -> None:
    retention_days = max(1, min(30, int(retention_days or 7)))
    threshold = (datetime.now(timezone.utc) - timedelta(days=retention_days)).isoformat(timespec="seconds")
    with _cp.db() as conn:
        conn.execute("DELETE FROM bot_messages WHERE client_id=? AND occurred_at<?", (client_id, threshold))
        count = conn.execute("SELECT COUNT(*) c FROM bot_messages WHERE client_id=?", (client_id,)).fetchone()["c"]
        if count > 20000:
            conn.execute(
                """
                DELETE FROM bot_messages WHERE client_id=? AND id NOT IN (
                    SELECT id FROM bot_messages WHERE client_id=? ORDER BY id DESC LIMIT 20000
                )
                """,
                (client_id, client_id),
            )


class TokenLoginInput(BaseModel):
    token: str = Field(min_length=8, max_length=500)


class AdminClientInput(BaseModel):
    name: str = Field(min_length=1, max_length=100)


class SyncInput(BaseModel):
    status: Dict[str, Any] = Field(default_factory=dict)
    current_settings: Dict[str, Any] = Field(default_factory=dict)
    messages: List[Dict[str, Any]] = Field(default_factory=list)
    command_results: List[Dict[str, Any]] = Field(default_factory=list)


class SettingsInput(BaseModel):
    auto_reply_enabled: Optional[bool] = None
    message_sync_enabled: Optional[bool] = None
    allow_web_manual_reply: Optional[bool] = None
    sync_interval_seconds: Optional[int] = None
    message_retention_days: Optional[int] = None


class WebSendInput(BaseModel):
    seller: str = Field(min_length=1, max_length=120)
    buyer: str = Field(min_length=1, max_length=120)
    text: str = Field(min_length=1, max_length=2000)


class DiagnosticsInput(BaseModel):
    kind: str = Field(min_length=1, max_length=40)


@router.get("/bot")
def bot_web_redirect() -> RedirectResponse:
    return RedirectResponse(url="/bot/", status_code=307)


@router.get("/bot/")
def bot_web_page() -> FileResponse:
    return FileResponse(_cp.STATIC_DIR / "bot-web.html")


@router.post("/api/bot-web/login")
def bot_web_login(data: TokenLoginInput, request: Request) -> Dict[str, Any]:
    _rate_limit_login(request)
    token = data.token.strip()
    client = _client_by_token(token, capture_cipher=True)
    if not client:
        raise HTTPException(status_code=401, detail="Bot 客户端令牌无效")
    _clear_login_attempts(request)
    request.session["bot_web_client_id"] = int(client["id"])
    request.session["bot_web_client_name"] = str(client["name"])
    _ensure_state(int(client["id"]))
    return {"ok": True, "client_id": int(client["id"]), "client_name": client["name"]}


@router.post("/api/bot-web/logout")
def bot_web_logout(request: Request) -> Dict[str, Any]:
    request.session.pop("bot_web_client_id", None)
    request.session.pop("bot_web_client_name", None)
    return {"ok": True}


@router.get("/api/bot-web/me")
def bot_web_me(client: Dict[str, Any] = Depends(_web_client)) -> Dict[str, Any]:
    return {"client_id": int(client["id"]), "client_name": client["name"]}


@router.get("/api/bot-web/snapshot")
def bot_web_snapshot(
    after_id: int = Query(0, ge=0),
    client: Dict[str, Any] = Depends(_web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    _ensure_state(client_id)
    with _cp.db() as conn:
        state_row = conn.execute("SELECT * FROM bot_client_state WHERE client_id=?", (client_id,)).fetchone()
        if after_id > 0:
            rows = conn.execute(
                "SELECT * FROM bot_messages WHERE client_id=? AND id>? ORDER BY id ASC LIMIT 500",
                (client_id, after_id),
            ).fetchall()
        else:
            rows = conn.execute(
                "SELECT * FROM bot_messages WHERE client_id=? ORDER BY id DESC LIMIT 300",
                (client_id,),
            ).fetchall()
            rows = list(reversed(rows))
        command_rows = conn.execute(
            """
            SELECT id,command_type,status,error,result_json,created_at,completed_at
            FROM bot_commands WHERE client_id=? ORDER BY id DESC LIMIT 20
            """,
            (client_id,),
        ).fetchall()
    status_data = _parse(state_row["status_json"], {}) if state_row else {}
    last_seen = state_row["last_seen_at"] if state_row else None
    settings = _settings_for(client_id)
    return {
        "client": {
            "id": client_id,
            "name": client["name"],
            "online": _is_online(last_seen),
            "last_seen_at": last_seen,
            "app_version": state_row["app_version"] if state_row else "",
            "seller_nicks": _parse(state_row["seller_nicks_json"], []) if state_row else [],
        },
        "status": status_data,
        "settings": settings,
        "messages": [dict(row) for row in rows],
        "commands": [
            {
                **{k: v for k, v in dict(row).items() if k != "result_json"},
                "result": _parse(row["result_json"], {}),
            }
            for row in command_rows
        ],
        "server_time": _now(),
    }


@router.put("/api/bot-web/settings")
def bot_web_update_settings(
    data: SettingsInput,
    client: Dict[str, Any] = Depends(_web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    _ensure_state(client_id)
    current = _settings_for(client_id)["desired"]
    updates = data.model_dump(exclude_none=True)
    if "sync_interval_seconds" in updates:
        updates["sync_interval_seconds"] = max(2, min(60, int(updates["sync_interval_seconds"])))
    if "message_retention_days" in updates:
        updates["message_retention_days"] = max(1, min(30, int(updates["message_retention_days"])))
    current.update(updates)
    with _cp.db() as conn:
        conn.execute(
            "UPDATE bot_client_state SET desired_settings_json=?,updated_at=? WHERE client_id=?",
            (_json(current), _now(), client_id),
        )
    return {"ok": True, "desired": current}


@router.post("/api/bot-web/diagnostics")
def bot_web_run_diagnostics(
    data: DiagnosticsInput,
    client: Dict[str, Any] = Depends(_web_client),
) -> Dict[str, Any]:
    kind = (data.kind or "").strip().lower()
    command_types = {
        "snapshot": "diagnostics_snapshot",
        "flow_probe": "diagnostics_flow_probe",
    }
    command_type = command_types.get(kind)
    if not command_type:
        raise HTTPException(status_code=422, detail="不支持的诊断类型")
    client_id = int(client["id"])
    with _cp.db() as conn:
        cursor = conn.execute(
            """
            INSERT INTO bot_commands(client_id,command_type,payload_json,status,result_json,error,created_at)
            VALUES(?,?,?,'pending','{}','',?)
            """,
            (client_id, command_type, "{}", _now()),
        )
        command_id = int(cursor.lastrowid)
    return {"ok": True, "command_id": command_id, "status": "pending", "kind": kind}


@router.post("/api/bot-web/messages/send")
def bot_web_send_message(
    data: WebSendInput,
    client: Dict[str, Any] = Depends(_web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    settings = _settings_for(client_id)["desired"]
    if not bool(settings.get("allow_web_manual_reply", True)):
        raise HTTPException(status_code=403, detail="该 Bot 已关闭 Web 端人工回复")
    text = data.text.strip()
    with _cp.db() as conn:
        cursor = conn.execute(
            """
            INSERT INTO bot_commands(client_id,command_type,payload_json,status,result_json,error,created_at)
            VALUES(?,?,?,'pending','{}','',?)
            """,
            (
                client_id,
                "send_text",
                _json({"seller": data.seller.strip(), "buyer": data.buyer.strip(), "text": text}),
                _now(),
            ),
        )
        command_id = int(cursor.lastrowid)
        conn.execute(
            """
            INSERT INTO bot_messages(
                client_id,message_key,seller,buyer,role,text,message_type,occurred_at,created_at
            ) VALUES(?,?,?,?,?,?,?,?,?)
            """,
            (
                client_id,
                f"command:{command_id}",
                data.seller.strip(),
                data.buyer.strip(),
                "assistant",
                text,
                "web_pending",
                _now(),
                _now(),
            ),
        )
    return {"ok": True, "command_id": command_id, "status": "pending"}


@router.post("/api/runtime/v1/bot-web/sync")
def runtime_bot_web_sync(
    data: SyncInput,
    client: Dict[str, Any] = Depends(_runtime_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    _ensure_state(client_id)
    status_data = dict(data.status or {})
    current_settings = dict(data.current_settings or {})
    app_version = _safe(status_data.get("app_version"), 100)
    sellers = status_data.get("seller_nicks") if isinstance(status_data.get("seller_nicks"), list) else []
    sellers = [_safe(x, 120) for x in sellers[:30] if _safe(x, 120)]
    with _cp.db() as conn:
        conn.execute(
            """
            UPDATE bot_client_state SET
                status_json=?,current_settings_json=?,app_version=?,seller_nicks_json=?,
                last_seen_at=?,updated_at=?
            WHERE client_id=?
            """,
            (_json(status_data), _json(current_settings), app_version, _json(sellers), _now(), _now(), client_id),
        )
    settings = _settings_for(client_id)["desired"]
    if bool(settings.get("message_sync_enabled", True)):
        for item in data.messages[:500]:
            if isinstance(item, dict):
                _save_message(client_id, item)
    _apply_command_results(client_id, data.command_results or [])
    _cleanup_messages(client_id, int(settings.get("message_retention_days", 7)))
    return {
        "ok": True,
        "server_time": _now(),
        "desired_settings": settings,
        "commands": _command_rows(client_id),
    }


@router.get("/api/admin/bot-web/clients")
def admin_bot_web_clients(_: str = Depends(lambda request: _cp.require_admin(request))) -> List[Dict[str, Any]]:
    with _cp.db() as conn:
        rows = conn.execute(
            """
            SELECT c.id,c.name,c.token_prefix,c.token_cipher,c.enabled,c.created_at,c.last_used_at,
                   s.last_seen_at,s.app_version,s.seller_nicks_json
            FROM client_tokens c
            LEFT JOIN bot_client_state s ON s.client_id=c.id
            ORDER BY c.id DESC
            """
        ).fetchall()
    return [
        {
            "id": int(row["id"]),
            "name": row["name"],
            "token_prefix": row["token_prefix"],
            "token_available": bool(row["token_cipher"]),
            "enabled": bool(row["enabled"]),
            "created_at": row["created_at"],
            "last_used_at": row["last_used_at"],
            "last_seen_at": row["last_seen_at"],
            "online": _is_online(row["last_seen_at"]),
            "app_version": row["app_version"] or "",
            "seller_nicks": _parse(row["seller_nicks_json"], []),
        }
        for row in rows
    ]


@router.post("/api/admin/bot-web/clients")
def admin_bot_web_create_client(
    data: AdminClientInput,
    _: str = Depends(lambda request: _cp.require_admin(request)),
) -> Dict[str, Any]:
    token = "qnb_" + secrets.token_urlsafe(32)
    with _cp.db() as conn:
        cursor = conn.execute(
            """
            INSERT INTO client_tokens(name,token_hash,token_prefix,token_cipher,enabled,created_at)
            VALUES(?,?,?,?,1,?)
            """,
            (data.name.strip(), _cp.hash_token(token), token[:12], _cp.encrypt_secret(token), _now()),
        )
        client_id = int(cursor.lastrowid)
    _ensure_state(client_id)
    return {"id": client_id, "name": data.name.strip(), "token": token}


@router.get("/api/admin/bot-web/clients/{client_id}/token")
def admin_bot_web_reveal_token(
    client_id: int,
    _: str = Depends(lambda request: _cp.require_admin(request)),
) -> Dict[str, Any]:
    with _cp.db() as conn:
        row = conn.execute("SELECT token_cipher FROM client_tokens WHERE id=?", (client_id,)).fetchone()
    if not row:
        raise HTTPException(status_code=404, detail="客户端不存在")
    if not row["token_cipher"]:
        raise HTTPException(status_code=409, detail="旧令牌尚未加密留存；请让新版 Bot 在线同步一次，或重新生成令牌")
    return {"token": _cp.decrypt_secret(row["token_cipher"])}


@router.post("/api/admin/bot-web/clients/{client_id}/rotate")
def admin_bot_web_rotate_token(
    client_id: int,
    _: str = Depends(lambda request: _cp.require_admin(request)),
) -> Dict[str, Any]:
    token = "qnb_" + secrets.token_urlsafe(32)
    with _cp.db() as conn:
        row = conn.execute("SELECT id FROM client_tokens WHERE id=?", (client_id,)).fetchone()
        if not row:
            raise HTTPException(status_code=404, detail="客户端不存在")
        conn.execute(
            """
            UPDATE client_tokens
            SET token_hash=?,token_prefix=?,token_cipher=?,enabled=1,last_used_at=NULL
            WHERE id=?
            """,
            (_cp.hash_token(token), token[:12], _cp.encrypt_secret(token), client_id),
        )
    return {"ok": True, "token": token, "warning": "旧令牌已立即失效，请同步更新 Windows Bot。"}
