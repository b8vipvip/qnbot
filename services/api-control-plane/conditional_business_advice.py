from __future__ import annotations

import hashlib
import hmac
import json
import re
import secrets
from datetime import datetime, timedelta, timezone
from typing import Any

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel

import bot_client_shop_binding
import bot_web_console

router = APIRouter()
_cp = None

_SCOPE_HASH_RE = re.compile(r"^[a-f0-9]{64}$")
_REQUEST_ID_RE = re.compile(r"^[A-Za-z0-9_-]{16,96}$")
_MAX_PENDING = 500


class ConditionalAdviceClaimInput(BaseModel):
    buyer: str
    order_id: str
    advice_key: str
    request_id: str
    lease_seconds: int = 180


class ConditionalAdviceCompleteInput(BaseModel):
    buyer: str
    order_id: str
    advice_key: str
    lease_token: str
    outcome: str


class ConditionalAdviceResolveInput(BaseModel):
    scope_hash: str
    confirmed_delivered: bool


def install(control_plane: Any, console_module=bot_web_console, binding_module=bot_client_shop_binding) -> None:
    global _cp, bot_web_console, bot_client_shop_binding
    _cp = control_plane
    bot_web_console = console_module
    bot_client_shop_binding = binding_module
    control_plane.app.include_router(router)


def init_db() -> None:
    if _cp is None:
        raise RuntimeError("conditional_business_advice is not installed")
    with _cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bot_conditional_business_advice (
                shop_key TEXT NOT NULL,
                scope_hash TEXT NOT NULL,
                state TEXT NOT NULL,
                request_id TEXT NOT NULL DEFAULT '',
                lease_token_hash TEXT NOT NULL DEFAULT '',
                lease_token_cipher TEXT NOT NULL DEFAULT '',
                lease_expires_at TEXT NOT NULL DEFAULT '',
                buyer_cipher TEXT NOT NULL DEFAULT '',
                order_id_cipher TEXT NOT NULL DEFAULT '',
                advice_key_cipher TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                delivered_at TEXT NOT NULL DEFAULT '',
                reason_code TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(shop_key, scope_hash)
            );
            CREATE INDEX IF NOT EXISTS ix_conditional_advice_pending
                ON bot_conditional_business_advice(shop_key, state, updated_at);
            CREATE TABLE IF NOT EXISTS bot_conditional_business_advice_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                shop_key TEXT NOT NULL,
                scope_hash TEXT NOT NULL,
                client_id INTEGER NOT NULL,
                event_type TEXT NOT NULL,
                outcome TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_conditional_advice_events_shop_time
                ON bot_conditional_business_advice_events(shop_key, created_at);
            """
        )


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _iso(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat(timespec="milliseconds")


def _parse_iso(value: str) -> datetime | None:
    value = (value or "").strip()
    if not value:
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.astimezone(timezone.utc)
    except (TypeError, ValueError, OverflowError):
        return None


def _clean(value: Any, label: str, maximum: int) -> str:
    if not isinstance(value, str):
        raise HTTPException(status_code=400, detail=f"{label} 必须是文本")
    value = value.strip()
    if not value or len(value) > maximum or "\r" in value or "\n" in value:
        raise HTTPException(status_code=400, detail=f"{label} 无效")
    return value


def _scope_hash(shop_key: str, buyer: str, order_id: str, advice_key: str) -> str:
    # The stable control-plane encryption key is shared by all API replicas and
    # devices. Raw buyer/order identifiers never enter the ledger key or audit log.
    try:
        key = _cp.derive_fernet_key()
    except Exception as exc:
        raise HTTPException(status_code=503, detail="条件性建议幂等密钥不可用") from exc
    if not isinstance(key, bytes) or len(key) < 32:
        raise HTTPException(status_code=503, detail="条件性建议幂等密钥配置无效")
    canonical = json.dumps(
        ["conditional-advice-v1", shop_key, buyer.casefold(), order_id, advice_key],
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    return hmac.new(key, canonical, hashlib.sha256).hexdigest()


def _encrypt(value: str) -> str:
    try:
        return str(_cp.encrypt_secret(value))
    except Exception as exc:
        raise HTTPException(status_code=503, detail="条件性建议记录加密失败") from exc


def _decrypt(value: str) -> str:
    try:
        return str(_cp.decrypt_secret(value))
    except Exception as exc:
        raise HTTPException(status_code=503, detail="条件性建议待核对记录无法解密") from exc


def _runtime_scope(request: Request, x_shop_key: str) -> tuple[int, str]:
    client = bot_web_console._runtime_client(request)
    client_id = int(client["id"])
    shop_key = _clean(x_shop_key, "X-Shop-Key", 160)
    # Each client token remains bound to one ShopKey, while multiple client tokens
    # may legitimately bind to the same ShopKey on separate Windows devices.
    bot_client_shop_binding.ensure_binding(client_id, shop_key, False, "")
    return client_id, shop_key


def _append_event(conn, shop_key: str, scope_hash: str, client_id: int, event_type: str, outcome: str, now: str) -> None:
    conn.execute(
        """INSERT INTO bot_conditional_business_advice_events
           (shop_key,scope_hash,client_id,event_type,outcome,created_at)
           VALUES(?,?,?,?,?,?)""",
        (shop_key, scope_hash, int(client_id), event_type[:64], outcome[:96], now),
    )
    # Events are diagnostic history; delivery barriers are never aged out here.
    conn.execute(
        """DELETE FROM bot_conditional_business_advice_events
           WHERE shop_key=? AND id NOT IN (
             SELECT id FROM bot_conditional_business_advice_events
             WHERE shop_key=? ORDER BY id DESC LIMIT 2000
           )""",
        (shop_key, shop_key),
    )


def _mark_expired_reservations_uncertain(conn, shop_key: str, now_dt: datetime, client_id: int) -> None:
    now = _iso(now_dt)
    rows = conn.execute(
        """SELECT scope_hash FROM bot_conditional_business_advice
           WHERE shop_key=? AND state='reserved' AND lease_expires_at<>'' AND lease_expires_at<=?""",
        (shop_key, now),
    ).fetchall()
    for row in rows:
        scope_hash = str(row["scope_hash"])
        conn.execute(
            """UPDATE bot_conditional_business_advice
               SET state='uncertain',lease_expires_at='',updated_at=?,reason_code='lease_expired'
               WHERE shop_key=? AND scope_hash=? AND state='reserved'""",
            (now, shop_key, scope_hash),
        )
        _append_event(conn, shop_key, scope_hash, client_id, "lease_expired", "manual_review_required", now)


def _scope_for_payload(shop_key: str, buyer: str, order_id: str, advice_key: str) -> tuple[str, str, str, str]:
    buyer = _clean(buyer, "buyer", 160)
    order_id = _clean(order_id, "order_id", 160)
    advice_key = _clean(advice_key, "advice_key", 160)
    return _scope_hash(shop_key, buyer, order_id, advice_key), buyer, order_id, advice_key


@router.post("/api/runtime/v1/bot-web/conditional-business-advice/claim")
def runtime_claim(
    payload: ConditionalAdviceClaimInput,
    request: Request,
    x_shop_key: str = "",
) -> dict[str, Any]:
    client_id, shop_key = _runtime_scope(request, x_shop_key)
    scope_hash, buyer, order_id, advice_key = _scope_for_payload(
        shop_key, payload.buyer, payload.order_id, payload.advice_key
    )
    request_id = _clean(payload.request_id, "request_id", 96)
    if not _REQUEST_ID_RE.fullmatch(request_id):
        raise HTTPException(status_code=400, detail="request_id 格式无效")
    lease_seconds = int(payload.lease_seconds)
    if lease_seconds < 30 or lease_seconds > 600:
        raise HTTPException(status_code=400, detail="lease_seconds 必须在 30 到 600 秒之间")

    now_dt = _utc_now()
    now = _iso(now_dt)
    expires = _iso(now_dt + timedelta(seconds=lease_seconds))
    with _cp.db() as conn:
        conn.execute("BEGIN IMMEDIATE")
        _mark_expired_reservations_uncertain(conn, shop_key, now_dt, client_id)
        row = conn.execute(
            """SELECT state,request_id,lease_token_cipher,lease_expires_at
               FROM bot_conditional_business_advice WHERE shop_key=? AND scope_hash=?""",
            (shop_key, scope_hash),
        ).fetchone()
        if row is not None:
            state = str(row["state"] or "")
            if state == "delivered":
                _append_event(conn, shop_key, scope_hash, client_id, "claim_denied", "already_delivered", now)
                return {"ok": True, "claimed": False, "state": "delivered", "reason": "already_delivered"}
            if state == "uncertain":
                _append_event(conn, shop_key, scope_hash, client_id, "claim_denied", "manual_review_required", now)
                return {"ok": True, "claimed": False, "state": "uncertain", "reason": "manual_review_required"}
            if state == "reserved":
                if str(row["request_id"] or "") == request_id:
                    token_cipher = str(row["lease_token_cipher"] or "")
                    if token_cipher:
                        token = _decrypt(token_cipher)
                        return {
                            "ok": True, "claimed": True, "state": "reserved",
                            "scope_hash": scope_hash, "lease_token": token,
                            "lease_expires_at": str(row["lease_expires_at"] or ""),
                            "replayed": True,
                        }
                    raise HTTPException(status_code=503, detail="原发送租约无法恢复，已保持阻止状态")
                _append_event(conn, shop_key, scope_hash, client_id, "claim_denied", "lease_held", now)
                return {"ok": True, "claimed": False, "state": "reserved", "reason": "lease_held"}
            if state != "released":
                raise HTTPException(status_code=409, detail="条件性建议幂等状态无效")

        lease_token = secrets.token_urlsafe(32)
        token_hash = hashlib.sha256(lease_token.encode("utf-8")).hexdigest()
        token_cipher = _encrypt(lease_token)
        values = (
            request_id, token_hash, token_cipher, expires,
            _encrypt(buyer), _encrypt(order_id), _encrypt(advice_key), now, now,
        )
        if row is None:
            conn.execute(
                """INSERT INTO bot_conditional_business_advice
                   (shop_key,scope_hash,state,request_id,lease_token_hash,lease_token_cipher,
                    lease_expires_at,buyer_cipher,order_id_cipher,advice_key_cipher,
                    created_at,updated_at,reason_code)
                   VALUES(?,?,'reserved',?,?,?,?,?,?,?,?,?,?,'')""",
                (shop_key, scope_hash) + values,
            )
        else:
            conn.execute(
                """UPDATE bot_conditional_business_advice
                   SET state='reserved',request_id=?,lease_token_hash=?,lease_token_cipher=?,
                       lease_expires_at=?,buyer_cipher=?,order_id_cipher=?,advice_key_cipher=?,
                       updated_at=?,delivered_at='',reason_code=''
                   WHERE shop_key=? AND scope_hash=?""",
                values[:7] + (now, shop_key, scope_hash),
            )
        _append_event(conn, shop_key, scope_hash, client_id, "lease_claimed", "reserved", now)
        return {
            "ok": True, "claimed": True, "state": "reserved",
            "scope_hash": scope_hash, "lease_token": lease_token,
            "lease_expires_at": expires, "replayed": False,
        }


@router.post("/api/runtime/v1/bot-web/conditional-business-advice/complete")
def runtime_complete(
    payload: ConditionalAdviceCompleteInput,
    request: Request,
    x_shop_key: str = "",
) -> dict[str, Any]:
    client_id, shop_key = _runtime_scope(request, x_shop_key)
    scope_hash, _, _, _ = _scope_for_payload(shop_key, payload.buyer, payload.order_id, payload.advice_key)
    lease_token = _clean(payload.lease_token, "lease_token", 256)
    outcome = (payload.outcome or "").strip().lower()
    if outcome not in {"delivered", "uncertain", "cancelled_before_send"}:
        raise HTTPException(status_code=400, detail="outcome 无效")
    now_dt = _utc_now()
    now = _iso(now_dt)
    token_hash = hashlib.sha256(lease_token.encode("utf-8")).hexdigest()
    with _cp.db() as conn:
        conn.execute("BEGIN IMMEDIATE")
        _mark_expired_reservations_uncertain(conn, shop_key, now_dt, client_id)
        row = conn.execute(
            """SELECT state,lease_token_hash FROM bot_conditional_business_advice
               WHERE shop_key=? AND scope_hash=?""",
            (shop_key, scope_hash),
        ).fetchone()
        if row is None:
            raise HTTPException(status_code=404, detail="条件性建议租约不存在")
        if str(row["state"] or "") == "delivered" and outcome == "delivered":
            return {"ok": True, "state": "delivered", "replayed": True}
        stored_hash = str(row["lease_token_hash"] or "")
        if not stored_hash or not hmac.compare_digest(stored_hash, token_hash):
            raise HTTPException(status_code=409, detail="条件性建议租约已过期或不属于当前发送实例")
        if outcome == "delivered":
            state = "delivered"
            delivered_at = now
            reason = "send_confirmed"
            clear_token = True
        elif outcome == "cancelled_before_send":
            state = "released"
            delivered_at = ""
            reason = "cancelled_before_send"
            clear_token = True
        else:
            state = "uncertain"
            delivered_at = ""
            reason = "send_outcome_uncertain"
            clear_token = False
        conn.execute(
            """UPDATE bot_conditional_business_advice
               SET state=?,lease_token_hash=?,lease_token_cipher=?,lease_expires_at='',
                   delivered_at=?,updated_at=?,reason_code=?
               WHERE shop_key=? AND scope_hash=?""",
            (
                state,
                "" if clear_token else stored_hash,
                "" if clear_token else "",
                delivered_at,
                now,
                reason,
                shop_key,
                scope_hash,
            ),
        )
        _append_event(conn, shop_key, scope_hash, client_id, "delivery_completed", reason, now)
        return {"ok": True, "state": state, "replayed": False}


@router.get("/api/runtime/v1/bot-web/conditional-business-advice/pending")
def runtime_pending(request: Request, x_shop_key: str = "") -> dict[str, Any]:
    client_id, shop_key = _runtime_scope(request, x_shop_key)
    now_dt = _utc_now()
    now = _iso(now_dt)
    with _cp.db() as conn:
        conn.execute("BEGIN IMMEDIATE")
        _mark_expired_reservations_uncertain(conn, shop_key, now_dt, client_id)
        rows = conn.execute(
            """SELECT scope_hash,buyer_cipher,order_id_cipher,advice_key_cipher,updated_at
               FROM bot_conditional_business_advice
               WHERE shop_key=? AND state='uncertain'
               ORDER BY updated_at DESC LIMIT ?""",
            (shop_key, _MAX_PENDING),
        ).fetchall()
        items = []
        for row in rows:
            items.append({
                "scope_hash": str(row["scope_hash"]),
                "buyer": _decrypt(str(row["buyer_cipher"] or "")),
                "order_id": _decrypt(str(row["order_id_cipher"] or "")),
                "advice_key": _decrypt(str(row["advice_key_cipher"] or "")),
                "updated_at": str(row["updated_at"] or ""),
            })
        return {"ok": True, "items": items}


@router.post("/api/runtime/v1/bot-web/conditional-business-advice/resolve")
def runtime_resolve(
    payload: ConditionalAdviceResolveInput,
    request: Request,
    x_shop_key: str = "",
) -> dict[str, Any]:
    client_id, shop_key = _runtime_scope(request, x_shop_key)
    scope_hash = _clean(payload.scope_hash, "scope_hash", 64).lower()
    if not _SCOPE_HASH_RE.fullmatch(scope_hash):
        raise HTTPException(status_code=400, detail="scope_hash 格式无效")
    now_dt = _utc_now()
    now = _iso(now_dt)
    with _cp.db() as conn:
        conn.execute("BEGIN IMMEDIATE")
        _mark_expired_reservations_uncertain(conn, shop_key, now_dt, client_id)
        row = conn.execute(
            "SELECT state FROM bot_conditional_business_advice WHERE shop_key=? AND scope_hash=?",
            (shop_key, scope_hash),
        ).fetchone()
        if row is None or str(row["state"] or "") != "uncertain":
            raise HTTPException(status_code=409, detail="记录不存在、仍在发送或已完成核对")
        if payload.confirmed_delivered:
            state = "delivered"
            delivered_at = now
            outcome = "operator_confirmed_delivered"
        else:
            state = "released"
            delivered_at = ""
            outcome = "operator_confirmed_not_delivered"
        conn.execute(
            """UPDATE bot_conditional_business_advice
               SET state=?,request_id='',lease_token_hash='',lease_token_cipher='',
                   lease_expires_at='',delivered_at=?,updated_at=?,reason_code=?
               WHERE shop_key=? AND scope_hash=?""",
            (state, delivered_at, now, outcome, shop_key, scope_hash),
        )
        _append_event(conn, shop_key, scope_hash, client_id, "manual_reconciliation", outcome, now)
        return {"ok": True, "state": state, "reason": outcome}
