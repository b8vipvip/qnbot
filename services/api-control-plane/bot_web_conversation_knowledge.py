from __future__ import annotations

import hashlib
import json
import threading
import uuid
from datetime import datetime
from typing import Any, Dict, List, Optional

from fastapi import APIRouter, Depends, HTTPException, Query
from pydantic import BaseModel, Field

import bot_web_console as core


router = APIRouter()
_KNOWLEDGE_LOCK = threading.RLock()


def install(control_plane: Any) -> None:
    control_plane.app.include_router(router)


def init_db() -> None:
    cp = core._cp
    with cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bot_conversation_reads (
                client_id INTEGER NOT NULL,
                seller TEXT NOT NULL,
                buyer TEXT NOT NULL,
                last_read_message_id INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(client_id, seller, buyer),
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS bot_knowledge_state (
                client_id INTEGER PRIMARY KEY,
                revision INTEGER NOT NULL DEFAULT 0,
                items_json TEXT NOT NULL DEFAULT '[]',
                content_hash TEXT NOT NULL DEFAULT '',
                updated_by TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL,
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS bot_knowledge_backups (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_id INTEGER NOT NULL,
                revision INTEGER NOT NULL,
                items_json TEXT NOT NULL DEFAULT '[]',
                content_hash TEXT NOT NULL DEFAULT '',
                reason TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_bot_knowledge_backups_client
            ON bot_knowledge_backups(client_id,id DESC);

            CREATE INDEX IF NOT EXISTS idx_bot_conversation_reads
            ON bot_conversation_reads(client_id, seller, buyer);
            """
        )


def _now() -> str:
    return core._now()


def _json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _hash_items(items: List[Dict[str, Any]]) -> str:
    canonical = json.dumps(items, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def _safe_text(value: Any, limit: int) -> str:
    text = str(value or "").replace("\x00", "").strip()
    return text if len(text) <= limit else text[:limit]


def _normalize_item(item: Dict[str, Any], existing_id: str = "") -> Dict[str, Any]:
    now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    knowledge_id = _safe_text(item.get("Id") or item.get("id") or existing_id, 80)
    if not knowledge_id:
        knowledge_id = uuid.uuid4().hex
    title = _safe_text(item.get("Title") or item.get("title"), 1000)
    answer = _safe_text(item.get("Answer") or item.get("answer"), 10000)
    if not title or not answer:
        raise HTTPException(status_code=400, detail="问题和答案不能为空")
    return {
        "Id": knowledge_id,
        "Category": _safe_text(item.get("Category") or item.get("category") or "通用", 200) or "通用",
        "Title": title,
        "Answer": answer,
        "Keywords": _safe_text(item.get("Keywords") or item.get("keywords"), 2000),
        "Enabled": bool(item.get("Enabled") if "Enabled" in item else item.get("enabled", True)),
        "SourceType": _safe_text(item.get("SourceType") or item.get("sourceType") or item.get("source_type") or "Web端维护", 200),
        "CreatedAt": _safe_text(item.get("CreatedAt") or item.get("createdAt") or item.get("created_at") or now, 100),
        "UpdatedAt": now,
    }


def _knowledge_row(client_id: int) -> Dict[str, Any]:
    cp = core._cp
    with cp.db() as conn:
        row = conn.execute(
            "SELECT revision,items_json,content_hash,updated_by,updated_at FROM bot_knowledge_state WHERE client_id=?",
            (client_id,),
        ).fetchone()
    if not row:
        return {"revision": 0, "items": [], "content_hash": "", "updated_by": "", "updated_at": None}
    try:
        items = json.loads(row["items_json"] or "[]")
    except Exception:
        items = []
    if not isinstance(items, list):
        items = []
    return {
        "revision": int(row["revision"] or 0),
        "items": [x for x in items if isinstance(x, dict)],
        "content_hash": row["content_hash"] or "",
        "updated_by": row["updated_by"] or "",
        "updated_at": row["updated_at"],
    }


def _save_knowledge(client_id: int, items: List[Dict[str, Any]], updated_by: str) -> Dict[str, Any]:
    cp = core._cp
    clean = [x for x in items if isinstance(x, dict)][:20000]
    digest = _hash_items(clean)
    now = _now()
    with _KNOWLEDGE_LOCK, cp.db() as conn:
        current = conn.execute(
            "SELECT revision,items_json,content_hash FROM bot_knowledge_state WHERE client_id=?",
            (client_id,),
        ).fetchone()
        if current and str(current["content_hash"] or "") != digest:
            conn.execute(
                """
                INSERT INTO bot_knowledge_backups(
                    client_id,revision,items_json,content_hash,reason,created_at
                ) VALUES(?,?,?,?,?,?)
                """,
                (
                    client_id,
                    int(current["revision"] or 0),
                    str(current["items_json"] or "[]"),
                    str(current["content_hash"] or ""),
                    _safe_text(updated_by, 80),
                    now,
                ),
            )
            conn.execute(
                """
                DELETE FROM bot_knowledge_backups
                WHERE client_id=? AND id NOT IN (
                    SELECT id FROM bot_knowledge_backups
                    WHERE client_id=? ORDER BY id DESC LIMIT 30
                )
                """,
                (client_id, client_id),
            )
        revision = int(current["revision"] or 0) + 1 if current else 1
        conn.execute(
            """
            INSERT INTO bot_knowledge_state(client_id,revision,items_json,content_hash,updated_by,updated_at)
            VALUES(?,?,?,?,?,?)
            ON CONFLICT(client_id) DO UPDATE SET
                revision=excluded.revision,
                items_json=excluded.items_json,
                content_hash=excluded.content_hash,
                updated_by=excluded.updated_by,
                updated_at=excluded.updated_at
            """,
            (client_id, revision, _json(clean), digest, updated_by, now),
        )
    return {"revision": revision, "items": clean, "content_hash": digest, "updated_by": updated_by, "updated_at": now}


def _cloud_sync_enabled(client_id: int) -> bool:
    settings = core._settings_for(client_id).get("current") or {}
    return bool(settings.get("knowledge_cloud_sync_enabled", False))


class ConversationReadInput(BaseModel):
    seller: str = Field(min_length=1, max_length=120)
    buyer: str = Field(min_length=1, max_length=120)
    message_id: int = Field(default=0, ge=0)


class KnowledgeItemInput(BaseModel):
    category: str = Field(default="通用", max_length=200)
    title: str = Field(min_length=1, max_length=1000)
    answer: str = Field(min_length=1, max_length=10000)
    keywords: str = Field(default="", max_length=2000)
    enabled: bool = True


class KnowledgeImportInput(BaseModel):
    mode: str = Field(default="merge", max_length=20)
    replace_confirmed: bool = False
    items: List[Dict[str, Any]] = Field(default_factory=list, max_length=20000)


class KnowledgeBackupRestoreInput(BaseModel):
    restore_confirmed: bool = False


class KnowledgeSyncInput(BaseModel):
    enabled: bool = False
    revision: int = Field(default=0, ge=0)
    content_hash: str = Field(default="", max_length=128)
    items: Optional[List[Dict[str, Any]]] = None


@router.get("/api/bot-web/conversations")
def web_conversations(
    query: str = Query("", max_length=120),
    limit: int = Query(300, ge=1, le=1000),
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    cp = core._cp
    inner_params: List[Any] = [client_id]
    where = "client_id=? AND buyer<>''"
    search = query.strip()
    if search:
        where += " AND (buyer LIKE ? OR seller LIKE ? OR text LIKE ?)"
        like = "%" + search + "%"
        inner_params.extend([like, like, like])
    sql = f"""
        SELECT m.id,m.seller,m.buyer,m.role,m.text,m.message_type,m.occurred_at,
               COALESCE(r.last_read_message_id,0) AS last_read_message_id,
               (SELECT COUNT(*) FROM bot_messages u
                WHERE u.client_id=m.client_id AND u.seller=m.seller AND u.buyer=m.buyer
                  AND u.role='user' AND u.id>COALESCE(r.last_read_message_id,0)) AS unread_count
        FROM bot_messages m
        JOIN (
            SELECT seller,buyer,MAX(id) AS max_id
            FROM bot_messages
            WHERE {where}
            GROUP BY seller,buyer
        ) latest ON latest.max_id=m.id
        LEFT JOIN bot_conversation_reads r
          ON r.client_id=? AND r.seller=m.seller AND r.buyer=m.buyer
        ORDER BY m.occurred_at DESC,m.id DESC
        LIMIT ?
    """
    params = inner_params + [client_id, limit]
    with cp.db() as conn:
        rows = conn.execute(sql, tuple(params)).fetchall()
    return {"conversations": [dict(row) for row in rows], "server_time": _now()}


@router.get("/api/bot-web/conversation/messages")
def web_conversation_messages(
    seller: str = Query(..., min_length=1, max_length=120),
    buyer: str = Query(..., min_length=1, max_length=120),
    before_id: int = Query(0, ge=0),
    limit: int = Query(100, ge=1, le=300),
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    cp = core._cp
    if before_id > 0:
        sql = "SELECT * FROM bot_messages WHERE client_id=? AND seller=? AND buyer=? AND id<? ORDER BY id DESC LIMIT ?"
        args = (client_id, seller.strip(), buyer.strip(), before_id, limit)
    else:
        sql = "SELECT * FROM bot_messages WHERE client_id=? AND seller=? AND buyer=? ORDER BY id DESC LIMIT ?"
        args = (client_id, seller.strip(), buyer.strip(), limit)
    with cp.db() as conn:
        rows = conn.execute(sql, args).fetchall()
    messages = list(reversed([dict(row) for row in rows]))
    return {"messages": messages, "has_more": len(messages) >= limit}


@router.post("/api/bot-web/conversation/read")
def web_mark_conversation_read(
    data: ConversationReadInput,
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    cp = core._cp
    message_id = int(data.message_id or 0)
    if message_id < 1:
        with cp.db() as conn:
            row = conn.execute(
                "SELECT MAX(id) id FROM bot_messages WHERE client_id=? AND seller=? AND buyer=?",
                (client_id, data.seller.strip(), data.buyer.strip()),
            ).fetchone()
        message_id = int(row["id"] or 0) if row else 0
    with cp.db() as conn:
        conn.execute(
            """
            INSERT INTO bot_conversation_reads(client_id,seller,buyer,last_read_message_id,updated_at)
            VALUES(?,?,?,?,?)
            ON CONFLICT(client_id,seller,buyer) DO UPDATE SET
                last_read_message_id=MAX(last_read_message_id,excluded.last_read_message_id),
                updated_at=excluded.updated_at
            """,
            (client_id, data.seller.strip(), data.buyer.strip(), message_id, _now()),
        )
    return {"ok": True, "last_read_message_id": message_id}


@router.get("/api/bot-web/knowledge")
def web_knowledge_list(
    query: str = Query("", max_length=300),
    category: str = Query("", max_length=200),
    offset: int = Query(0, ge=0),
    limit: int = Query(100, ge=1, le=500),
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    state = _knowledge_row(client_id)
    items = state["items"]
    needle = query.strip().lower()
    cat = category.strip()
    if needle:
        items = [x for x in items if needle in " ".join(str(x.get(k) or "") for k in ("Category", "Title", "Answer", "Keywords", "SourceType")).lower()]
    if cat:
        items = [x for x in items if str(x.get("Category") or "") == cat]
    items = sorted(items, key=lambda x: str(x.get("UpdatedAt") or x.get("CreatedAt") or ""), reverse=True)
    categories = sorted({str(x.get("Category") or "通用") for x in state["items"] if str(x.get("Category") or "").strip()})
    return {
        "items": items[offset:offset + limit],
        "total": len(items),
        "categories": categories,
        "revision": state["revision"],
        "updated_at": state["updated_at"],
        "updated_by": state["updated_by"],
        "cloud_sync_enabled": _cloud_sync_enabled(client_id),
    }


@router.get("/api/bot-web/knowledge/export")
def web_knowledge_export(
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    state = _knowledge_row(client_id)
    return {
        "format": "qianniu-bot-knowledge",
        "format_version": 1,
        "exported_at": _now(),
        "revision": state["revision"],
        "items": state["items"],
    }


@router.get("/api/bot-web/knowledge/backups")
def web_knowledge_backups(
    limit: int = Query(20, ge=1, le=30),
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    with core._cp.db() as conn:
        rows = conn.execute(
            """
            SELECT id,revision,content_hash,reason,created_at,
                   LENGTH(items_json) AS bytes
            FROM bot_knowledge_backups
            WHERE client_id=? ORDER BY id DESC LIMIT ?
            """,
            (client_id, limit),
        ).fetchall()
    return {"backups": [dict(row) for row in rows]}


@router.post("/api/bot-web/knowledge/backups/{backup_id}/restore")
def web_knowledge_backup_restore(
    backup_id: int,
    data: KnowledgeBackupRestoreInput,
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    if not data.restore_confirmed:
        raise HTTPException(status_code=409, detail="恢复知识备份会覆盖当前知识，需要二次确认")

    with _KNOWLEDGE_LOCK:
        with core._cp.db() as conn:
            row = conn.execute(
                """
                SELECT id,revision,items_json,content_hash,reason,created_at
                FROM bot_knowledge_backups
                WHERE client_id=? AND id=?
                """,
                (client_id, backup_id),
            ).fetchone()
        if not row:
            raise HTTPException(status_code=404, detail="知识备份不存在")
        try:
            items = json.loads(row["items_json"] or "[]")
        except Exception:
            raise HTTPException(status_code=422, detail="知识备份内容损坏，无法恢复")
        if not isinstance(items, list) or any(not isinstance(item, dict) for item in items):
            raise HTTPException(status_code=422, detail="知识备份格式无效，无法恢复")
        result = _save_knowledge(client_id, items, "web-backup-restore")
    return {
        "ok": True,
        "restored_backup_id": int(row["id"]),
        "restored_from_revision": int(row["revision"] or 0),
        "revision": result["revision"],
        "items_count": len(result["items"]),
    }


@router.post("/api/bot-web/knowledge/import")
def web_knowledge_import(
    data: KnowledgeImportInput,
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    mode = (data.mode or "merge").strip().lower()
    if mode not in {"merge", "replace"}:
        raise HTTPException(status_code=422, detail="导入模式只支持 merge 或 replace")
    if mode == "replace" and not data.replace_confirmed:
        raise HTTPException(status_code=409, detail="替换全部知识需要二次确认")

    normalized: List[Dict[str, Any]] = []
    seen_ids = set()
    seen_titles = set()
    for raw in data.items:
        item = _normalize_item(raw)
        key = item["Title"].strip().lower()
        if item["Id"] in seen_ids or key in seen_titles:
            raise HTTPException(status_code=422, detail="导入文件包含重复的知识 ID 或问题标题")
        seen_ids.add(item["Id"])
        seen_titles.add(key)
        normalized.append(item)

    with _KNOWLEDGE_LOCK:
        state = _knowledge_row(client_id)
        if mode == "replace":
            result = _save_knowledge(client_id, normalized, "web-import-replace")
            return {
                "ok": True,
                "mode": mode,
                "added": len(normalized),
                "updated": 0,
                "skipped": 0,
                "revision": result["revision"],
            }

        items = list(state["items"])
        by_id = {str(item.get("Id") or ""): index for index, item in enumerate(items)}
        title_to_id = {
            str(item.get("Title") or "").strip().lower(): str(item.get("Id") or "")
            for item in items
        }
        added = updated = skipped = 0
        for item in normalized:
            item_id = item["Id"]
            title_key = item["Title"].strip().lower()
            if item_id in by_id:
                index = by_id[item_id]
                item["CreatedAt"] = str(items[index].get("CreatedAt") or item["CreatedAt"])
                items[index] = item
                title_to_id[title_key] = item_id
                updated += 1
                continue
            if title_key in title_to_id:
                skipped += 1
                continue
            by_id[item_id] = len(items)
            title_to_id[title_key] = item_id
            items.append(item)
            added += 1

        result = _save_knowledge(client_id, items, "web-import-merge")
        return {
            "ok": True,
            "mode": mode,
            "added": added,
            "updated": updated,
            "skipped": skipped,
            "revision": result["revision"],
        }


@router.post("/api/bot-web/knowledge")
def web_knowledge_create(
    data: KnowledgeItemInput,
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    with _KNOWLEDGE_LOCK:
        state = _knowledge_row(client_id)
        item = _normalize_item(data.model_dump())
        duplicate = next((x for x in state["items"] if str(x.get("Title") or "").strip().lower() == item["Title"].lower()), None)
        if duplicate:
            raise HTTPException(status_code=409, detail="已有相同问题的知识，请编辑原条目")
        result = _save_knowledge(client_id, state["items"] + [item], "web")
    return {"ok": True, "item": item, "revision": result["revision"]}


@router.put("/api/bot-web/knowledge/{knowledge_id}")
def web_knowledge_update(
    knowledge_id: str,
    data: KnowledgeItemInput,
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    with _KNOWLEDGE_LOCK:
        state = _knowledge_row(client_id)
        index = next((i for i, x in enumerate(state["items"]) if str(x.get("Id") or "") == knowledge_id), -1)
        if index < 0:
            raise HTTPException(status_code=404, detail="知识条目不存在")
        original = state["items"][index]
        item = _normalize_item(data.model_dump(), knowledge_id)
        item["CreatedAt"] = str(original.get("CreatedAt") or item["CreatedAt"])
        items = list(state["items"])
        items[index] = item
        result = _save_knowledge(client_id, items, "web")
    return {"ok": True, "item": item, "revision": result["revision"]}


@router.delete("/api/bot-web/knowledge/{knowledge_id}")
def web_knowledge_delete(
    knowledge_id: str,
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    with _KNOWLEDGE_LOCK:
        state = _knowledge_row(client_id)
        items = [x for x in state["items"] if str(x.get("Id") or "") != knowledge_id]
        if len(items) == len(state["items"]):
            raise HTTPException(status_code=404, detail="知识条目不存在")
        result = _save_knowledge(client_id, items, "web")
    return {"ok": True, "revision": result["revision"]}


@router.post("/api/runtime/v1/bot-web/knowledge-sync")
def runtime_knowledge_sync(
    data: KnowledgeSyncInput,
    client: Dict[str, Any] = Depends(core._runtime_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    if not data.enabled:
        return {"ok": True, "enabled": False, "revision": _knowledge_row(client_id)["revision"]}

    with _KNOWLEDGE_LOCK:
        state = _knowledge_row(client_id)
        incoming = [x for x in (data.items or []) if isinstance(x, dict)]
        if state["revision"] == 0 and incoming:
            state = _save_knowledge(client_id, incoming, "windows")
        elif data.revision == state["revision"] and incoming:
            incoming_hash = _hash_items(incoming)
            if incoming_hash != state["content_hash"]:
                state = _save_knowledge(client_id, incoming, "windows")
        elif data.revision > state["revision"] and incoming:
            state = _save_knowledge(client_id, incoming, "windows-recovery")

        response: Dict[str, Any] = {
            "ok": True,
            "enabled": True,
            "revision": state["revision"],
            "content_hash": state["content_hash"],
            "updated_at": state["updated_at"],
            "updated_by": state["updated_by"],
        }
        if data.revision < state["revision"] or data.content_hash != state["content_hash"]:
            response["items"] = state["items"]
        return response
