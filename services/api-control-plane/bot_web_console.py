from __future__ import annotations

import base64
import hashlib
import ipaddress
import json
import logging
import os
import secrets
import threading
import time
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional

from fastapi import APIRouter, Depends, HTTPException, Query, Request, status
from fastapi.responses import FileResponse, RedirectResponse
from pydantic import BaseModel, Field


router = APIRouter()
logger = logging.getLogger(__name__)
_cp: Any = None
_LOGIN_LOCK = threading.Lock()
_LOGIN_ATTEMPTS: Dict[str, List[float]] = {}

# The Web inbox requests up to 600 buyer conversations and the desktop mirror
# exports up to 100 turns per buyer. Keep enough rows for that complete window.
MAX_MESSAGES_PER_CLIENT = 600 * 100


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
                is_bot INTEGER NOT NULL DEFAULT 0,
                withdrawn INTEGER NOT NULL DEFAULT 0,
                attachment_mime TEXT,
                attachment_name TEXT,
                attachment_sha256 TEXT,
                attachment_bytes BLOB,
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
        message_columns = {
            row["name"] for row in conn.execute("PRAGMA table_info(bot_messages)").fetchall()
        }
        if "is_bot" not in message_columns:
            conn.execute("ALTER TABLE bot_messages ADD COLUMN is_bot INTEGER NOT NULL DEFAULT 0")
        if "withdrawn" not in message_columns:
            conn.execute("ALTER TABLE bot_messages ADD COLUMN withdrawn INTEGER NOT NULL DEFAULT 0")
        if "attachment_mime" not in message_columns:
            conn.execute("ALTER TABLE bot_messages ADD COLUMN attachment_mime TEXT")
        if "attachment_name" not in message_columns:
            conn.execute("ALTER TABLE bot_messages ADD COLUMN attachment_name TEXT")
        if "attachment_sha256" not in message_columns:
            conn.execute("ALTER TABLE bot_messages ADD COLUMN attachment_sha256 TEXT")
        if "attachment_bytes" not in message_columns:
            conn.execute("ALTER TABLE bot_messages ADD COLUMN attachment_bytes BLOB")


def _bearer(request: Request) -> str:
    header = request.headers.get("authorization", "")
    if not header.lower().startswith("bearer "):
        return ""
    return header.split(" ", 1)[1].strip()


def _client_by_token(
    token: str,
    capture_cipher: bool = True,
    request_path: str = "",
) -> Optional[Dict[str, Any]]:
    if not token:
        return None
    digest = _cp.hash_token(token)
    with _cp.db() as conn:
        row = conn.execute(
            "SELECT * FROM client_tokens WHERE token_hash=?",
            (digest,),
        ).fetchone()
        if not row:
            logger.warning(
                "bot_client_token_rejected reason=unknown_token path=%s token_hash_prefix=%s",
                _safe(request_path, 120),
                digest[:12],
            )
            return None
        if not bool(row["enabled"]):
            logger.warning(
                "bot_client_token_rejected reason=disabled_token path=%s client_id=%s token_hash_prefix=%s",
                _safe(request_path, 120),
                int(row["id"]),
                digest[:12],
            )
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
    client = _client_by_token(
        _bearer(request),
        capture_cipher=True,
        request_path=request.url.path,
    )
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


_LOGIN_ATTEMPT_WINDOW_SECONDS = 600
_LOGIN_ATTEMPT_LIMIT = 12
_LOGIN_ATTEMPT_MAX_KEYS = 4096
_LOGIN_ATTEMPT_KEY_MAX_LENGTH = 128
_LOGIN_ATTEMPT_MAX_FORWARDED_HOPS = 16
_DEFAULT_TRUSTED_PROXY_CIDRS = "127.0.0.0/8,::1/128,172.16.0.0/12"


def _load_trusted_proxy_networks():
    configured = os.environ.get("QNBOT_TRUSTED_PROXY_CIDRS", _DEFAULT_TRUSTED_PROXY_CIDRS)
    networks = []
    for item in configured.split(","):
        item = item.strip()
        if not item:
            continue
        try:
            networks.append(ipaddress.ip_network(item, strict=False))
        except ValueError:
            logger.warning("Ignoring invalid QNBOT_TRUSTED_PROXY_CIDRS entry: %s", item[:128])
    return tuple(networks)


_TRUSTED_PROXY_NETWORKS = _load_trusted_proxy_networks()


def _is_trusted_proxy(address) -> bool:
    return any(address.version == network.version and address in network
               for network in _TRUSTED_PROXY_NETWORKS)


def _login_attempt_key(request: Request) -> str:
    peer_text = request.client.host if request.client else "unknown"
    try:
        peer = ipaddress.ip_address(str(peer_text).strip())
    except ValueError:
        return str(peer_text)[:_LOGIN_ATTEMPT_KEY_MAX_LENGTH] or "unknown"

    # Never trust forwarding headers from a direct/untrusted peer. In particular,
    # a caller must not be able to rotate X-Forwarded-For to evade the login limit.
    if not _is_trusted_proxy(peer):
        return str(peer)[:_LOGIN_ATTEMPT_KEY_MAX_LENGTH]

    forwarded = request.headers.get("x-forwarded-for", "").strip()
    if not forwarded or len(forwarded) > 2048:
        return str(peer)[:_LOGIN_ATTEMPT_KEY_MAX_LENGTH]

    # Proxies append their observed peer to the right of any incoming chain.
    # Walk from the trusted edge inward and choose the first non-proxy address;
    # never take the leftmost value, which may have been supplied by the caller.
    chain = forwarded.split(",")[-_LOGIN_ATTEMPT_MAX_FORWARDED_HOPS:]
    for item in reversed(chain):
        try:
            address = ipaddress.ip_address(item.strip())
        except ValueError:
            return str(peer)[:_LOGIN_ATTEMPT_KEY_MAX_LENGTH]
        if _is_trusted_proxy(address):
            continue
        return str(address)[:_LOGIN_ATTEMPT_KEY_MAX_LENGTH]

    return str(peer)[:_LOGIN_ATTEMPT_KEY_MAX_LENGTH]


def _rate_limit_login(request: Request) -> None:
    key = _login_attempt_key(request)
    now = time.time()
    cutoff = now - _LOGIN_ATTEMPT_WINDOW_SECONDS
    with _LOGIN_LOCK:
        # Prune every source, not only the current one. Otherwise one-off source
        # addresses accumulate forever in this long-lived process.
        for source, attempts in list(_LOGIN_ATTEMPTS.items()):
            recent_source_attempts = [x for x in attempts if x >= cutoff]
            if recent_source_attempts:
                _LOGIN_ATTEMPTS[source] = recent_source_attempts
            else:
                _LOGIN_ATTEMPTS.pop(source, None)

        recent = _LOGIN_ATTEMPTS.get(key, [])
        if len(recent) >= _LOGIN_ATTEMPT_LIMIT:
            raise HTTPException(status_code=429, detail="登录尝试过于频繁，请稍后再试")

        # X-Forwarded-For is supplied by the proxy chain and may contain many
        # distinct values. Keep the in-memory limiter bounded even if upstream
        # forwarding configuration is misconfigured or a caller rotates values.
        if key not in _LOGIN_ATTEMPTS and len(_LOGIN_ATTEMPTS) >= _LOGIN_ATTEMPT_MAX_KEYS:
            oldest_key = min(
                _LOGIN_ATTEMPTS,
                key=lambda source: _LOGIN_ATTEMPTS[source][-1] if _LOGIN_ATTEMPTS[source] else 0,
            )
            _LOGIN_ATTEMPTS.pop(oldest_key, None)

        recent.append(now)
        _LOGIN_ATTEMPTS[key] = recent


def _clear_login_attempts(request: Request) -> None:
    key = _login_attempt_key(request)
    with _LOGIN_LOCK:
        _LOGIN_ATTEMPTS.pop(key, None)


def _command_rows(client_id: int, shop_key: str = "") -> List[Dict[str, Any]]:
    commands=[]
    with _cp.db() as conn:
        conn.execute("BEGIN IMMEDIATE")
        rows = conn.execute(
            """
            SELECT id,command_type,payload_json,created_at,status
            FROM bot_commands
            WHERE client_id=? AND (status='pending' OR
                (command_type='knowledge_v2_operation' AND status='cancel_requested'))
            ORDER BY id ASC LIMIT 30
            """,
            (client_id,),
        ).fetchall()
        for row in rows:
            payload=_parse(row["payload_json"],{})
            kind=row["command_type"]
            if kind=="knowledge_v2_operation":
                if not shop_key or payload.get("shop_key")!=shop_key:
                    continue
                bound=conn.execute("SELECT shop_key FROM bot_client_shop_binding WHERE client_id=?",(client_id,)).fetchone()
                if not bound or bound["shop_key"]!=shop_key:
                    continue
                if row["status"]=="cancel_requested":
                    kind="knowledge_v2_operation_cancel"
                else:
                    # Atomically claim exactly once before returning the command.
                    payload["claim"]=secrets.token_hex(24)
                    conn.execute("""UPDATE bot_commands SET status='running',payload_json=?,completed_at=?
                        WHERE id=? AND client_id=? AND status='pending'""",
                        (_json(payload),_now(),row["id"],client_id))
            commands.append({"id":int(row["id"]),"type":kind,"payload":payload,"created_at":row["created_at"]})
    return commands


def _decode_message_attachment(item: Dict[str, Any], withdrawn: bool):
    if withdrawn:
        return None, None, None, None
    attachment = item.get("attachment")
    if attachment is None:
        return None, None, None, None
    if not isinstance(attachment, dict):
        raise HTTPException(status_code=400, detail="消息图片附件结构无效")
    encoded = str(attachment.get("data_base64") or "")
    if not encoded or len(encoded) > 12 * 1024 * 1024:
        raise HTTPException(status_code=413, detail="消息图片附件大小无效")
    try:
        raw = base64.b64decode(encoded, validate=True)
    except Exception:
        raise HTTPException(status_code=400, detail="消息图片附件Base64编码无效")
    if not raw or len(raw) > 8 * 1024 * 1024:
        raise HTTPException(status_code=413, detail="消息图片附件必须为 1 字节至 8 MB")
    mime = str(attachment.get("mime_type") or "").lower()
    allowed = {"image/jpeg", "image/png", "image/webp", "image/gif"}
    if mime not in allowed:
        raise HTTPException(status_code=415, detail="消息图片附件MIME类型不受支持")
    digest = hashlib.sha256(raw).hexdigest()
    if digest != str(attachment.get("sha256") or "").lower():
        raise HTTPException(status_code=409, detail="消息图片附件SHA-256校验失败")
    try:
        import io
        from PIL import Image
        with Image.open(io.BytesIO(raw)) as image:
            image.verify()
            detected = Image.MIME.get(image.format)
        if detected != mime:
            raise HTTPException(status_code=415, detail="消息图片附件MIME与文件内容不匹配")
    except HTTPException:
        raise
    except Exception:
        raise HTTPException(status_code=422, detail="消息图片附件格式无效或文件损坏")
    name = _safe(attachment.get("file_name") or ("image-" + digest[:12]), 180)
    return mime, name, digest, raw


def _save_message(client_id: int, item: Dict[str, Any]) -> None:
    seller = _safe(item.get("seller"), 120)
    buyer = _safe(item.get("buyer"), 120)
    role = str(item.get("role") or "system").lower()
    if role not in {"user", "assistant", "system"}:
        role = "system"
    text = str(item.get("text") or "").replace("\x00", "").strip()
    withdrawn = 1 if bool(item.get("withdrawn")) else 0
    is_bot = 1 if bool(item.get("is_bot")) else 0
    if not text and not withdrawn:
        return
    if not text and withdrawn:
        text = "已撤回一条消息"
    text = text[:6000]
    occurred = str(item.get("occurred_at") or _now())[:64]
    message_type = _safe(item.get("message_type") or "text", 50)
    message_key = _safe(item.get("message_key"), 200)
    if not message_key:
        seed = f"{seller}|{buyer}|{role}|{occurred}|{text}"
        message_key = hashlib.sha256(seed.encode("utf-8")).hexdigest()
    attachment_mime, attachment_name, attachment_sha, attachment_bytes = _decode_message_attachment(item, bool(withdrawn))
    if attachment_bytes is not None:
        message_type = "image"
    elif withdrawn:
        attachment_mime = attachment_name = attachment_sha = attachment_bytes = None
    with _cp.db() as conn:
        conn.execute(
            """
            INSERT INTO bot_messages(
                client_id,message_key,seller,buyer,role,text,message_type,is_bot,withdrawn,
                attachment_mime,attachment_name,attachment_sha256,attachment_bytes,occurred_at,created_at
            ) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(client_id,message_key) DO UPDATE SET
                seller=excluded.seller,
                buyer=excluded.buyer,
                role=excluded.role,
                text=excluded.text,
                message_type=excluded.message_type,
                is_bot=excluded.is_bot,
                withdrawn=excluded.withdrawn,
                attachment_mime=CASE WHEN excluded.withdrawn=1 THEN NULL ELSE COALESCE(excluded.attachment_mime,bot_messages.attachment_mime) END,
                attachment_name=CASE WHEN excluded.withdrawn=1 THEN NULL ELSE COALESCE(excluded.attachment_name,bot_messages.attachment_name) END,
                attachment_sha256=CASE WHEN excluded.withdrawn=1 THEN NULL ELSE COALESCE(excluded.attachment_sha256,bot_messages.attachment_sha256) END,
                attachment_bytes=CASE WHEN excluded.withdrawn=1 THEN NULL ELSE COALESCE(excluded.attachment_bytes,bot_messages.attachment_bytes) END,
                occurred_at=excluded.occurred_at
            """,
            (
                client_id, message_key, seller, buyer, role, text, message_type,
                is_bot, withdrawn, attachment_mime, attachment_name, attachment_sha,
                attachment_bytes, occurred, _now(),
            ),
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
                "SELECT command_type,payload_json,status FROM bot_commands WHERE id=? AND client_id=?",
                (command_id, client_id),
            ).fetchone()
            if not row:
                continue
            new_status="completed" if success else "failed"
            if row["command_type"]=="knowledge_v2_operation":
                expected=_parse(row["payload_json"],{}).get("claim","")
                supplied=result.get("claim","")
                if row["status"] not in ("running","cancel_requested") or not expected or not isinstance(supplied,str) or not secrets.compare_digest(expected,supplied):
                    continue
                phase=result.get("phase","")
                if phase=="running":
                    new_status=row["status"]
                elif phase=="cancelled":
                    new_status="cancelled"
            conn.execute(
                """
                UPDATE bot_commands
                SET status=?,result_json=?,error=?,completed_at=?
                WHERE id=? AND client_id=?
                """,
                (new_status, _json(result_json), error, _now(), command_id, client_id),
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
        if count > MAX_MESSAGES_PER_CLIENT:
            conn.execute(
                """
                DELETE FROM bot_messages WHERE client_id=? AND id NOT IN (
                    SELECT id FROM bot_messages WHERE client_id=? ORDER BY id DESC LIMIT ?
                )
                """,
                (client_id, client_id, MAX_MESSAGES_PER_CLIENT),
            )


class TokenLoginInput(BaseModel):
    token: str = Field(min_length=8, max_length=500)


class AdminClientInput(BaseModel):
    name: str = Field(min_length=1, max_length=100)


class SyncInput(BaseModel):
    shop_key: str = ""
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


class ConfirmedCommandInput(BaseModel):
    confirmed: bool = False


def _queue_safe_command(client_id: int, command_type: str, payload: Optional[Dict[str, Any]] = None) -> int:
    allowed = {"diagnostics_snapshot", "update_check", "update_install"}
    if command_type not in allowed:
        raise HTTPException(status_code=400, detail="不支持的 Bot Web 命令")
    with _cp.db() as conn:
        cursor = conn.execute(
            """INSERT INTO bot_commands(client_id,command_type,payload_json,status,result_json,error,created_at)
               VALUES(?,?,?,'pending','{}','',?)""",
            (client_id, command_type, _json(payload or {}), _now()),
        )
        return int(cursor.lastrowid)


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
    client = _client_by_token(token, capture_cipher=True, request_path=request.url.path)
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
                "SELECT id,client_id,message_key,seller,buyer,role,text,message_type,is_bot,withdrawn,attachment_mime,attachment_name,attachment_sha256,occurred_at,created_at FROM bot_messages WHERE client_id=? AND id>? ORDER BY id ASC LIMIT 500",
                (client_id, after_id),
            ).fetchall()
        else:
            rows = conn.execute(
                "SELECT id,client_id,message_key,seller,buyer,role,text,message_type,is_bot,withdrawn,attachment_mime,attachment_name,attachment_sha256,occurred_at,created_at FROM bot_messages WHERE client_id=? ORDER BY id DESC LIMIT 300",
                (client_id,),
            ).fetchall()
            rows = list(reversed(rows))
        command_rows = conn.execute(
            """
            SELECT id,command_type,status,error,created_at,completed_at
            FROM bot_commands WHERE client_id=? ORDER BY id DESC LIMIT 20
            """,
            (client_id,),
        ).fetchall()
    status_data = _parse(state_row["status_json"], {}) if state_row else {}
    try:
        with _cp.db() as conn:
            binding_row = conn.execute(
                "SELECT shop_key,seller FROM bot_client_shop_binding WHERE client_id=?",
                (client_id,),
            ).fetchone()
    except Exception:
        binding_row = None
    if binding_row:
        # The binding is the authoritative tenant boundary; telemetry is only a
        # presentation fallback and must never switch the selected ShopKey.
        status_data["shop_key"] = str(binding_row["shop_key"] or "")
        if not str(status_data.get("current_seller") or "").strip():
            status_data["current_seller"] = str(binding_row["seller"] or "")
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
        "commands": [dict(row) for row in command_rows],
        "server_time": _now(),
    }


@router.get("/api/bot-web/conversation/messages/{message_id}/attachment")
def bot_web_conversation_message_attachment(
    message_id: int,
    seller: str = Query(..., min_length=1, max_length=120),
    buyer: str = Query(..., min_length=1, max_length=120),
    client: Dict[str, Any] = Depends(_web_client),
) -> Dict[str, Any]:
    with _cp.db() as conn:
        row = conn.execute(
            """SELECT attachment_mime,attachment_name,attachment_sha256,attachment_bytes
               FROM bot_messages WHERE client_id=? AND id=? AND seller=? AND buyer=? AND withdrawn=0""",
            (int(client["id"]), message_id, seller.strip(), buyer.strip()),
        ).fetchone()
    if not row or not row["attachment_bytes"]:
        raise HTTPException(status_code=404, detail="该消息的原始图片附件尚未同步或已撤回")
    return {
        "mime_type": row["attachment_mime"],
        "file_name": row["attachment_name"],
        "sha256": row["attachment_sha256"],
        "image_base64": base64.b64encode(bytes(row["attachment_bytes"])).decode("ascii"),
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


@router.post("/api/bot-web/diagnostics/run")
def bot_web_run_diagnostics(client: Dict[str, Any] = Depends(_web_client)) -> Dict[str, Any]:
    command_id = _queue_safe_command(int(client["id"]), "diagnostics_snapshot")
    return {"ok": True, "command_id": command_id, "status": "pending"}


@router.post("/api/bot-web/update/check")
def bot_web_update_check(client: Dict[str, Any] = Depends(_web_client)) -> Dict[str, Any]:
    command_id = _queue_safe_command(int(client["id"]), "update_check")
    return {"ok": True, "command_id": command_id, "status": "pending"}


@router.post("/api/bot-web/update/install")
def bot_web_update_install(
    data: ConfirmedCommandInput,
    client: Dict[str, Any] = Depends(_web_client),
) -> Dict[str, Any]:
    if not data.confirmed:
        raise HTTPException(status_code=400, detail="远程安装必须明确二次确认")
    command_id = _queue_safe_command(int(client["id"]), "update_install", {"confirmed": True})
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
        "commands": _command_rows(client_id, data.shop_key),
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
