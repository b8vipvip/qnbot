from __future__ import annotations

import hashlib
import json
import re
import threading
from typing import Any, Dict, Optional

from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field

import bot_web_console as core


router = APIRouter()
_STORE_RULE_LOCK = threading.RLock()
_HASH_RE = re.compile(r"^[0-9a-fA-F]{64}$")
_MAX_PROFILE_BYTES = 512 * 1024


def install(control_plane: Any) -> None:
    control_plane.app.include_router(router)


def init_db() -> None:
    cp = core._cp
    with cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bot_store_rule_state (
                client_id INTEGER PRIMARY KEY,
                revision INTEGER NOT NULL DEFAULT 0,
                profile_json TEXT NOT NULL DEFAULT '{}',
                content_hash TEXT NOT NULL DEFAULT '',
                updated_by TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL,
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );
            """
        )


def _now() -> str:
    return core._now()


def _json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _state(client_id: int) -> Dict[str, Any]:
    cp = core._cp
    with cp.db() as conn:
        row = conn.execute(
            "SELECT revision,profile_json,content_hash,updated_by,updated_at "
            "FROM bot_store_rule_state WHERE client_id=?",
            (client_id,),
        ).fetchone()
    if not row:
        return {
            "revision": 0,
            "profile": None,
            "content_hash": "",
            "updated_by": "",
            "updated_at": None,
        }
    try:
        profile = json.loads(row["profile_json"] or "{}")
    except Exception:
        profile = None
    if not isinstance(profile, dict):
        profile = None
    return {
        "revision": int(row["revision"] or 0),
        "profile": profile,
        "content_hash": row["content_hash"] or "",
        "updated_by": row["updated_by"] or "",
        "updated_at": row["updated_at"],
    }


def _require_string(value: Any, field: str, limit: int) -> None:
    if value is None:
        return
    if not isinstance(value, str):
        raise HTTPException(status_code=400, detail=f"店铺规则字段 {field} 必须是字符串")
    if len(value) > limit:
        raise HTTPException(status_code=400, detail=f"店铺规则字段 {field} 超过长度限制")


def _validate_profile(profile: Dict[str, Any]) -> Dict[str, Any]:
    if not isinstance(profile, dict):
        raise HTTPException(status_code=400, detail="店铺规则 profile 必须是对象")
    encoded = _json(profile).encode("utf-8")
    if len(encoded) > _MAX_PROFILE_BYTES:
        raise HTTPException(status_code=413, detail="店铺规则配置超过512KB限制")

    _require_string(profile.get("rawInput"), "rawInput", 50000)
    _require_string(profile.get("standardPrompt"), "standardPrompt", 12000)
    _require_string(profile.get("corePrompt"), "corePrompt", 2500)

    rules = profile.get("rules", [])
    if rules is None:
        rules = []
    if not isinstance(rules, list):
        raise HTTPException(status_code=400, detail="店铺规则 rules 必须是数组")
    if len(rules) > 80:
        raise HTTPException(status_code=400, detail="店铺规则最多80条")
    for index, rule in enumerate(rules):
        if not isinstance(rule, dict):
            raise HTTPException(status_code=400, detail=f"店铺规则第{index + 1}条必须是对象")
        _require_string(rule.get("Id"), f"rules[{index}].Id", 80)
        _require_string(rule.get("Title"), f"rules[{index}].Title", 160)
        _require_string(rule.get("Category"), f"rules[{index}].Category", 80)
        _require_string(rule.get("Scope"), f"rules[{index}].Scope", 20)
        _require_string(rule.get("Content"), f"rules[{index}].Content", 2200)
        triggers = rule.get("Triggers", [])
        if triggers is None:
            triggers = []
        if not isinstance(triggers, list) or len(triggers) > 20:
            raise HTTPException(status_code=400, detail=f"店铺规则第{index + 1}条 triggers 无效")
        for trigger in triggers:
            _require_string(trigger, f"rules[{index}].Triggers", 60)
    return profile


def _save_state(
    client_id: int,
    profile: Dict[str, Any],
    content_hash: str,
    updated_by: str,
) -> Dict[str, Any]:
    cp = core._cp
    clean = _validate_profile(profile)
    digest = (content_hash or "").strip().lower()
    if not _HASH_RE.fullmatch(digest):
        raise HTTPException(status_code=400, detail="店铺规则 content_hash 必须是64位 SHA-256")
    now = _now()
    with _STORE_RULE_LOCK, cp.db() as conn:
        current = conn.execute(
            "SELECT revision FROM bot_store_rule_state WHERE client_id=?",
            (client_id,),
        ).fetchone()
        revision = int(current["revision"] or 0) + 1 if current else 1
        conn.execute(
            """
            INSERT INTO bot_store_rule_state(
                client_id,revision,profile_json,content_hash,updated_by,updated_at
            ) VALUES(?,?,?,?,?,?)
            ON CONFLICT(client_id) DO UPDATE SET
                revision=excluded.revision,
                profile_json=excluded.profile_json,
                content_hash=excluded.content_hash,
                updated_by=excluded.updated_by,
                updated_at=excluded.updated_at
            """,
            (client_id, revision, _json(clean), digest, updated_by, now),
        )
    return {
        "revision": revision,
        "profile": clean,
        "content_hash": digest,
        "updated_by": updated_by,
        "updated_at": now,
    }


class StoreRuleWebInput(BaseModel):
    profile: Dict[str, Any]


def _profile_hash(profile: Dict[str, Any]) -> str:
    return hashlib.sha256(_json(profile).encode("utf-8")).hexdigest()


@router.get("/api/bot-web/store-rules")
def web_store_rules(
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    state = _state(int(client["id"]))
    return {
        "profile": state["profile"] or {},
        "revision": state["revision"],
        "content_hash": state["content_hash"],
        "updated_by": state["updated_by"],
        "updated_at": state["updated_at"],
    }


@router.put("/api/bot-web/store-rules")
def web_store_rules_update(
    data: StoreRuleWebInput,
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    clean = _validate_profile(data.profile)
    digest = _profile_hash(clean)
    with _STORE_RULE_LOCK:
        current = _state(client_id)
        if current["profile"] is not None and current["content_hash"] == digest:
            return {"ok": True, **current}
        state = _save_state(client_id, clean, digest, "web")
    return {"ok": True, **state}


class StoreRuleSyncInput(BaseModel):
    enabled: bool = False
    revision: int = Field(default=0, ge=0)
    content_hash: str = Field(default="", max_length=128)
    profile: Optional[Dict[str, Any]] = None


@router.post("/api/runtime/v1/bot-web/store-rule-sync")
def runtime_store_rule_sync(
    data: StoreRuleSyncInput,
    request: Request,
    client: Dict[str, Any] = Depends(core._runtime_client),
) -> Dict[str, Any]:
    shop_key = (request.headers.get("x-shop-key") or "").strip()
    if not shop_key:
        raise HTTPException(status_code=400, detail="缺少 X-Shop-Key")

    client_id = int(client["id"])
    if not data.enabled:
        return {"ok": True, "enabled": False, "revision": _state(client_id)["revision"]}

    with _STORE_RULE_LOCK:
        state = _state(client_id)
        incoming = data.profile if isinstance(data.profile, dict) else None
        incoming_hash = (data.content_hash or "").strip().lower()

        if state["revision"] == 0 and incoming is not None:
            state = _save_state(client_id, incoming, incoming_hash, "windows")
        elif data.revision == state["revision"] and incoming is not None:
            if incoming_hash != state["content_hash"]:
                state = _save_state(client_id, incoming, incoming_hash, "windows")
        elif data.revision > state["revision"] and incoming is not None:
            state = _save_state(client_id, incoming, incoming_hash, "windows-recovery")

        response: Dict[str, Any] = {
            "ok": True,
            "enabled": True,
            "revision": state["revision"],
            "content_hash": state["content_hash"],
            "updated_at": state["updated_at"],
            "updated_by": state["updated_by"],
        }
        if (
            state["profile"] is not None
            and (data.revision < state["revision"] or incoming_hash != state["content_hash"])
        ):
            response["profile"] = state["profile"]
        return response
