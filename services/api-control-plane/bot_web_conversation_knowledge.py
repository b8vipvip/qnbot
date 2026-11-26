from __future__ import annotations

import asyncio
import base64
import hashlib
import json
import mimetypes
import threading
import uuid
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional

from fastapi import APIRouter, Depends, HTTPException, Query, Request
from pydantic import BaseModel, Field

import bot_web_console as core


router = APIRouter()
_KNOWLEDGE_LOCK = threading.RLock()


def install(control_plane: Any) -> None:
    control_plane.app.include_router(router)


def _current_image_revision(conn, client_id: int, shop_key: str) -> int:
    state = conn.execute(
        "SELECT revision FROM bot_web_image_knowledge_sync_state WHERE client_id=? AND shop_key=?",
        (client_id, shop_key),
    ).fetchone()
    entries = conn.execute(
        "SELECT COALESCE(MAX(revision),0) AS revision FROM bot_web_image_knowledge WHERE client_id=? AND shop_key=?",
        (client_id, shop_key),
    ).fetchone()
    tombstones = conn.execute(
        "SELECT COALESCE(MAX(revision),0) AS revision FROM bot_web_image_knowledge_tombstones WHERE client_id=? AND shop_key=?",
        (client_id, shop_key),
    ).fetchone()
    return max(
        int(state["revision"] or 0) if state else 0,
        int(entries["revision"] or 0) if entries else 0,
        int(tombstones["revision"] or 0) if tombstones else 0,
    )


def init_db() -> None:
    cp = core._cp
    with cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bot_web_image_knowledge (
                id TEXT PRIMARY KEY NOT NULL,
                client_id INTEGER NOT NULL,
                shop_key TEXT NOT NULL DEFAULT '',
                seller TEXT NOT NULL DEFAULT '',
                image_name TEXT NOT NULL,
                mime_type TEXT NOT NULL,
                image_sha256 TEXT NOT NULL,
                image_bytes BLOB NOT NULL,
                ocr_text TEXT NOT NULL DEFAULT '',
                description TEXT NOT NULL DEFAULT '',
                enabled INTEGER NOT NULL DEFAULT 1,
                revision INTEGER NOT NULL DEFAULT 0,
                updated_by TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(client_id, shop_key, image_sha256),
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );

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

        columns = {row["name"] for row in conn.execute("PRAGMA table_info(bot_web_image_knowledge)").fetchall()}
        for name, declaration in (
            ("shop_key", "TEXT NOT NULL DEFAULT ''"),
            ("seller", "TEXT NOT NULL DEFAULT ''"),
            ("revision", "INTEGER NOT NULL DEFAULT 0"),
            ("updated_by", "TEXT NOT NULL DEFAULT ''"),
        ):
            if name not in columns:
                conn.execute(f"ALTER TABLE bot_web_image_knowledge ADD COLUMN {name} {declaration}")
        table_info = conn.execute(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='bot_web_image_knowledge'"
        ).fetchone()
        table_sql = " ".join(str(table_info["sql"] or "").lower().split()) if table_info else ""
        if "unique(client_id, image_sha256)" in table_sql:
            # Replace the earlier client_id-only uniqueness constraint. The old
            # rows stay intact (including quarantined ShopKey-less rows), but a
            # digest in one ShopKey no longer blocks the same image in another.
            conn.execute("ALTER TABLE bot_web_image_knowledge RENAME TO bot_web_image_knowledge_legacy_unique")
            conn.execute(
                """CREATE TABLE bot_web_image_knowledge (
                    id TEXT PRIMARY KEY NOT NULL,
                    client_id INTEGER NOT NULL,
                    shop_key TEXT NOT NULL DEFAULT '',
                    seller TEXT NOT NULL DEFAULT '',
                    image_name TEXT NOT NULL,
                    mime_type TEXT NOT NULL,
                    image_sha256 TEXT NOT NULL,
                    image_bytes BLOB NOT NULL,
                    ocr_text TEXT NOT NULL DEFAULT '',
                    description TEXT NOT NULL DEFAULT '',
                    enabled INTEGER NOT NULL DEFAULT 1,
                    revision INTEGER NOT NULL DEFAULT 0,
                    updated_by TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(client_id,shop_key,image_sha256),
                    FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
                )"""
            )
            conn.execute(
                """INSERT INTO bot_web_image_knowledge
                   (id,client_id,shop_key,seller,image_name,mime_type,image_sha256,image_bytes,ocr_text,
                    description,enabled,revision,updated_by,created_at,updated_at)
                   SELECT id,client_id,shop_key,seller,image_name,mime_type,image_sha256,image_bytes,ocr_text,
                          description,enabled,revision,updated_by,created_at,updated_at
                   FROM bot_web_image_knowledge_legacy_unique"""
            )
            conn.execute("DROP TABLE bot_web_image_knowledge_legacy_unique")

        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bot_web_image_knowledge_sync_state (
                client_id INTEGER NOT NULL,
                shop_key TEXT NOT NULL,
                revision INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(client_id,shop_key),
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS bot_web_image_knowledge_tombstones (
                client_id INTEGER NOT NULL,
                shop_key TEXT NOT NULL,
                id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                deleted_at TEXT NOT NULL,
                updated_by TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(client_id,shop_key,id),
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_bot_web_image_knowledge_scope
            ON bot_web_image_knowledge(client_id,shop_key,updated_at);
            CREATE INDEX IF NOT EXISTS idx_bot_web_image_knowledge_tombstones_scope
            ON bot_web_image_knowledge_tombstones(client_id,shop_key,revision);
            """
        )

        # Recover the revision stream from durable rows if an older backup omitted
        # the cursor table or left shop-scoped rows at revision 0. Assign unique
        # revisions in timestamp order so paged clients cannot skip same-revision rows.
        legacy_events = []
        for row in conn.execute(
            """SELECT id,client_id,shop_key,updated_at AS event_at
               FROM bot_web_image_knowledge
               WHERE shop_key<>'' AND revision<=0"""
        ).fetchall():
            legacy_events.append((str(row["event_at"] or ""), "upsert", row))
        for row in conn.execute(
            """SELECT id,client_id,shop_key,deleted_at AS event_at
               FROM bot_web_image_knowledge_tombstones
               WHERE shop_key<>'' AND revision<=0"""
        ).fetchall():
            legacy_events.append((str(row["event_at"] or ""), "delete", row))
        for _, kind, row in sorted(legacy_events, key=lambda item: (item[0], item[1], str(item[2]["id"]))):
            client_id = int(row["client_id"])
            shop_key = str(row["shop_key"])
            revision = _current_image_revision(conn, client_id, shop_key) + 1
            if kind == "upsert":
                conn.execute(
                    "UPDATE bot_web_image_knowledge SET revision=? WHERE client_id=? AND shop_key=? AND id=? AND revision<=0",
                    (revision, client_id, shop_key, row["id"]),
                )
            else:
                conn.execute(
                    "UPDATE bot_web_image_knowledge_tombstones SET revision=? WHERE client_id=? AND shop_key=? AND id=? AND revision<=0",
                    (revision, client_id, shop_key, row["id"]),
                )
            conn.execute(
                """INSERT INTO bot_web_image_knowledge_sync_state(client_id,shop_key,revision,updated_at)
                   VALUES(?,?,?,?) ON CONFLICT(client_id,shop_key) DO UPDATE SET
                   revision=MAX(bot_web_image_knowledge_sync_state.revision,excluded.revision),
                   updated_at=excluded.updated_at""",
                (client_id, shop_key, revision, _now()),
            )
        # Legacy rows from the earlier client_id-only schema have no provable ShopKey.
        # Do not attach them to the token's current binding: the token may have been
        # rebound before this migration. Keep them quarantined and preserve the bytes.
        try:
            legacy_count = conn.execute(
                "SELECT COUNT(*) AS count FROM bot_web_image_knowledge WHERE shop_key=''"
            ).fetchone()["count"]
            if legacy_count:
                core.logger.warning(
                    "bot_web_image_knowledge_legacy_quarantined client_id_scoped_rows=%s",
                    int(legacy_count),
                )
        except Exception:
            pass


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


def _sync_revision(value: Any, field: str) -> int:
    try:
        revision = int(value if value is not None else 0)
    except (TypeError, ValueError, OverflowError):
        raise HTTPException(status_code=400, detail=f"图片知识{field}无效")
    if revision < 0:
        raise HTTPException(status_code=400, detail=f"图片知识{field}不能为负数")
    return revision


def _sync_timestamp(value: Any, field: str, fallback: str) -> str:
    raw = _safe_text(value, 64)
    if not raw:
        return fallback
    try:
        normalized = raw[:-1] + "+00:00" if raw.endswith(("Z", "z")) else raw
        parsed = datetime.fromisoformat(normalized)
        if parsed.tzinfo is None or parsed.utcoffset() is None:
            raise ValueError("timezone is required")
        return parsed.astimezone(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")
    except (TypeError, ValueError, OverflowError):
        raise HTTPException(status_code=400, detail=f"图片知识{field}必须是带时区的ISO-8601时间")


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


class ImageKnowledgeSaveInput(BaseModel):
    id: str = Field(default="", max_length=80)
    shop_key: str = Field(min_length=1, max_length=64)
    seller: str = Field(min_length=1, max_length=160)
    base_revision: int = Field(default=0, ge=0)
    image_name: str = Field(default="", max_length=180)
    mime_type: str = Field(min_length=1, max_length=40)
    image_sha256: str = Field(min_length=64, max_length=64)
    image_base64: str = Field(min_length=16, max_length=12 * 1024 * 1024)
    ocr_text: str = Field(default="", max_length=12000)
    description: str = Field(min_length=1, max_length=1200)
    enabled: bool = True


class ImageKnowledgeLegacyMigrationInput(BaseModel):
    shop_key: str = Field(min_length=1, max_length=64)
    seller: str = Field(min_length=1, max_length=160)
    confirmed: bool = False


class ImageKnowledgeSyncInput(BaseModel):
    shop_key: str = Field(min_length=1, max_length=64)
    seller: str = Field(min_length=1, max_length=160)
    since_revision: int = Field(default=0, ge=0)
    upserts: List[Dict[str, Any]] = Field(default_factory=list, max_length=5)
    deletes: List[Dict[str, Any]] = Field(default_factory=list, max_length=50)


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


def _require_image_shop_scope(client_id: int, shop_key: str, seller: str) -> str:
    import bot_client_shop_binding as shop_binding
    shop_key = _safe_text(shop_key, 64)
    seller = _safe_text(seller, 160)
    if not shop_key or not seller:
        raise HTTPException(status_code=400, detail="图片知识必须指定ShopKey和客服账号")
    with core._cp.db() as conn:
        binding = conn.execute(
            "SELECT shop_key,seller FROM bot_client_shop_binding WHERE client_id=?",
            (client_id,),
        ).fetchone()
        if not binding or not str(binding["shop_key"] or "").strip():
            raise HTTPException(status_code=409, detail="Bot客户端尚未绑定权威ShopKey")
        if str(binding["shop_key"]).strip() != shop_key:
            raise HTTPException(status_code=409, detail={"code":"shop_scope_mismatch","message":"图片知识请求的ShopKey与当前客户端令牌绑定不一致"})
        canonical = str(binding["seller"] or "").strip()
        known = []
        state = conn.execute(
            "SELECT seller_nicks_json FROM bot_client_state WHERE client_id=?",
            (client_id,),
        ).fetchone()
        if state:
            try:
                values = json.loads(state["seller_nicks_json"] or "[]")
                if isinstance(values, list):
                    known = [str(x or "").strip() for x in values if str(x or "").strip()]
            except Exception:
                known = []
        matches = (canonical and shop_binding._seller_identity_matches(seller, canonical)) or any(
            shop_binding._seller_identity_matches(seller, value) for value in known
        )
        if not matches:
            raise HTTPException(status_code=409, detail={"code":"seller_scope_mismatch","message":"客服账号不属于当前Bot令牌绑定的店铺"})
    return canonical or seller


def _next_image_revision(conn, client_id: int, shop_key: str) -> int:
    revision = _current_image_revision(conn, client_id, shop_key) + 1
    conn.execute(
        """INSERT INTO bot_web_image_knowledge_sync_state(client_id,shop_key,revision,updated_at)
           VALUES(?,?,?,?) ON CONFLICT(client_id,shop_key) DO UPDATE SET
           revision=excluded.revision,updated_at=excluded.updated_at""",
        (client_id, shop_key, revision, _now()),
    )
    return revision


def _decode_image_payload(image_base64: str, mime_type: str, expected_sha: str):
    try:
        raw = base64.b64decode(image_base64, validate=True)
    except Exception:
        raise HTTPException(status_code=400, detail="图片数据编码无效")
    if not raw or len(raw) > 8 * 1024 * 1024:
        raise HTTPException(status_code=413, detail="图片必须为 1 字节至 8 MB")
    digest = hashlib.sha256(raw).hexdigest()
    if digest.lower() != str(expected_sha or "").lower():
        raise HTTPException(status_code=409, detail="图片文件与 OCR 识别结果不一致，请重新上传识别")
    from PIL import Image
    import io
    try:
        with Image.open(io.BytesIO(raw)) as image:
            image.verify()
            detected = Image.MIME.get(image.format)
    except Exception:
        raise HTTPException(status_code=422, detail="图片格式无效或文件损坏")
    allowed = {"image/jpeg", "image/png", "image/webp", "image/gif"}
    mime = str(mime_type or "").lower()
    if mime not in allowed or detected not in allowed or detected != mime:
        raise HTTPException(status_code=415, detail="图片 MIME 类型与文件内容不匹配")
    return raw, digest, mime


def _image_row_payload(row, include_bytes: bool = True) -> Dict[str, Any]:
    item = {
        "id": row["id"],
        "image_name": row["image_name"],
        "mime_type": row["mime_type"],
        "image_sha256": row["image_sha256"],
        "ocr_text": row["ocr_text"] or "",
        "description": row["description"] or "",
        "enabled": bool(row["enabled"]),
        "revision": int(row["revision"] or 0),
        "updated_at": row["updated_at"],
    }
    if include_bytes:
        item["image_base64"] = base64.b64encode(bytes(row["image_bytes"])).decode("ascii")
    return item


def _image_tombstone_payload(row) -> Dict[str, Any]:
    return {"id": row["id"], "revision": int(row["revision"] or 0), "deleted_at": row["deleted_at"]}


@router.post("/api/bot-web/image-knowledge/recognize")
async def web_image_knowledge_recognize(request: Request, client: Dict[str, Any] = Depends(core._web_client)):
    raw = await request.body()
    if not raw or len(raw) > 8 * 1024 * 1024:
        raise HTTPException(status_code=413, detail="图片必须为 1 字节至 8 MB")
    import runtime_ocr
    settings = runtime_ocr._current_settings()
    if not settings.get("enabled", True):
        raise HTTPException(status_code=503, detail="服务端 OCR 当前已关闭")
    try:
        await runtime_ocr._acquire_ocr_slot(int(settings.get("max_concurrency", 2)))
        try:
            result = await asyncio.to_thread(runtime_ocr._run_ocr, raw)
        finally:
            runtime_ocr._release_ocr_slot()
    except Exception as exc:
        raise HTTPException(status_code=422, detail="服务端 OCR 识别失败：" + str(exc)[:240])
    return {
        "ok": True,
        "text": str(result.get("text") or ""),
        "confidence": float(result.get("confidence") or 0),
        "elapsed_ms": int(result.get("elapsedMs") or 0),
        "engine": str(result.get("engine") or "RapidOCR/ONNXRuntime"),
        "image_sha256": hashlib.sha256(raw).hexdigest(),
    }


@router.get("/api/bot-web/image-knowledge")
def web_image_knowledge_list(
    shop_key: str = Query(..., min_length=1, max_length=64),
    seller: str = Query(..., min_length=1, max_length=160),
    client: Dict[str, Any] = Depends(core._web_client),
):
    client_id = int(client["id"])
    canonical = _require_image_shop_scope(client_id, shop_key, seller)
    with core._cp.db() as conn:
        rows = conn.execute(
            """SELECT id,shop_key,seller,image_name,mime_type,image_sha256,ocr_text,description,
                      enabled,revision,created_at,updated_at
               FROM bot_web_image_knowledge WHERE client_id=? AND shop_key=? AND seller=?
               ORDER BY updated_at DESC LIMIT 500""",
            (client_id, shop_key, canonical),
        ).fetchall()
        state = conn.execute(
            "SELECT revision FROM bot_web_image_knowledge_sync_state WHERE client_id=? AND shop_key=?",
            (client_id, shop_key),
        ).fetchone()
        legacy_unbound_count = conn.execute(
            "SELECT COUNT(*) AS count FROM bot_web_image_knowledge WHERE client_id=? AND shop_key=''",
            (client_id,),
        ).fetchone()["count"]
    return {"shop_key": shop_key, "seller": canonical, "revision": int(state["revision"] or 0) if state else 0,
            "legacy_unbound_count": int(legacy_unbound_count or 0), "items": [dict(row) for row in rows]}


@router.get("/api/bot-web/image-knowledge/{image_id}/image")
def web_image_knowledge_image(
    image_id: str,
    shop_key: str = Query(..., min_length=1, max_length=64),
    seller: str = Query(..., min_length=1, max_length=160),
    client: Dict[str, Any] = Depends(core._web_client),
):
    client_id = int(client["id"])
    canonical = _require_image_shop_scope(client_id, shop_key, seller)
    with core._cp.db() as conn:
        row = conn.execute(
            "SELECT mime_type,image_bytes FROM bot_web_image_knowledge WHERE client_id=? AND shop_key=? AND seller=? AND id=?",
            (client_id, shop_key, canonical, image_id),
        ).fetchone()
    if not row:
        raise HTTPException(status_code=404, detail="图片知识不存在")
    return {"mime_type": row["mime_type"], "image_base64": base64.b64encode(bytes(row["image_bytes"])).decode("ascii")}


@router.post("/api/bot-web/image-knowledge")
def web_image_knowledge_save(data: ImageKnowledgeSaveInput, client: Dict[str, Any] = Depends(core._web_client)):
    client_id = int(client["id"])
    canonical = _require_image_shop_scope(client_id, data.shop_key, data.seller)
    raw, digest, mime = _decode_image_payload(data.image_base64, data.mime_type, data.image_sha256)
    name = _safe_text(data.image_name, 180) or "图片-" + digest[:12] + (mimetypes.guess_extension(mime) or ".img")
    description = _safe_text(data.description, 1200)
    if not description:
        raise HTTPException(status_code=400, detail="图片说明不能为空")
    image_id = _safe_text(data.id, 80) or uuid.uuid4().hex
    now = _now()
    with _KNOWLEDGE_LOCK, core._cp.db() as conn:
        global_id = conn.execute(
            "SELECT client_id,shop_key FROM bot_web_image_knowledge WHERE id=?",
            (image_id,),
        ).fetchone()
        if global_id and (int(global_id["client_id"]) != client_id or str(global_id["shop_key"] or "") != data.shop_key):
            raise HTTPException(status_code=409, detail="图片知识ID已属于其他客户端或店铺")
        current = conn.execute(
            "SELECT revision FROM bot_web_image_knowledge WHERE client_id=? AND shop_key=? AND seller=? AND id=?",
            (client_id, data.shop_key, canonical, image_id),
        ).fetchone()
        current_revision = int(current["revision"] or 0) if current else 0
        if current_revision != data.base_revision:
            raise HTTPException(status_code=409, detail={"code":"image_knowledge_revision_conflict","message":"图片知识已在另一端更新，请刷新后重新编辑","current_revision":current_revision})
        duplicate = conn.execute(
            "SELECT id FROM bot_web_image_knowledge WHERE client_id=? AND shop_key=? AND image_sha256=? AND id<>?",
            (client_id, data.shop_key, digest, image_id),
        ).fetchone()
        if duplicate:
            raise HTTPException(status_code=409, detail="当前店铺已存在相同图片，请编辑已有图片知识")
        revision = _next_image_revision(conn, client_id, data.shop_key)
        conn.execute(
            """INSERT INTO bot_web_image_knowledge
               (id,client_id,shop_key,seller,image_name,mime_type,image_sha256,image_bytes,ocr_text,description,
                enabled,revision,updated_by,created_at,updated_at)
               VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
               ON CONFLICT(id) DO UPDATE SET
                 shop_key=excluded.shop_key,seller=excluded.seller,image_name=excluded.image_name,
                 mime_type=excluded.mime_type,image_sha256=excluded.image_sha256,image_bytes=excluded.image_bytes,
                 ocr_text=excluded.ocr_text,description=excluded.description,enabled=excluded.enabled,
                 revision=excluded.revision,updated_by=excluded.updated_by,updated_at=excluded.updated_at
               WHERE bot_web_image_knowledge.client_id=excluded.client_id
                 AND bot_web_image_knowledge.shop_key=excluded.shop_key""",
            (image_id, client_id, data.shop_key, canonical, name, mime, digest, raw, data.ocr_text.strip(),
             description, 1 if data.enabled else 0, revision, "web", now, now),
        )
        conn.execute(
            "DELETE FROM bot_web_image_knowledge_tombstones WHERE client_id=? AND shop_key=? AND id=?",
            (client_id, data.shop_key, image_id),
        )
    return {"ok": True, "id": image_id, "shop_key": data.shop_key, "seller": canonical,
            "image_name": name, "image_sha256": digest, "revision": revision, "updated_at": now}


@router.delete("/api/bot-web/image-knowledge/{image_id}")
def web_image_knowledge_delete(
    image_id: str,
    shop_key: str = Query(..., min_length=1, max_length=64),
    seller: str = Query(..., min_length=1, max_length=160),
    expected_revision: int = Query(0, ge=0),
    client: Dict[str, Any] = Depends(core._web_client),
):
    client_id = int(client["id"])
    canonical = _require_image_shop_scope(client_id, shop_key, seller)
    now = _now()
    with _KNOWLEDGE_LOCK, core._cp.db() as conn:
        row = conn.execute(
            "SELECT revision FROM bot_web_image_knowledge WHERE client_id=? AND shop_key=? AND seller=? AND id=?",
            (client_id, shop_key, canonical, image_id),
        ).fetchone()
        if not row:
            raise HTTPException(status_code=404, detail="图片知识不存在")
        current_revision = int(row["revision"] or 0)
        if current_revision != expected_revision:
            raise HTTPException(status_code=409, detail={"code":"image_knowledge_revision_conflict","message":"图片知识已在另一端更新，请刷新后重试","current_revision":current_revision})
        revision = _next_image_revision(conn, client_id, shop_key)
        conn.execute(
            "DELETE FROM bot_web_image_knowledge WHERE client_id=? AND shop_key=? AND seller=? AND id=?",
            (client_id, shop_key, canonical, image_id),
        )
        conn.execute(
            """INSERT INTO bot_web_image_knowledge_tombstones(client_id,shop_key,id,revision,deleted_at,updated_by)
               VALUES(?,?,?,?,?,'web')
               ON CONFLICT(client_id,shop_key,id) DO UPDATE SET
                 revision=excluded.revision,deleted_at=excluded.deleted_at,updated_by=excluded.updated_by""",
            (client_id, shop_key, image_id, revision, now),
        )
    return {"ok": True, "revision": revision}


@router.post("/api/bot-web/image-knowledge/migrate-legacy")
def web_image_knowledge_migrate_legacy(
    data: ImageKnowledgeLegacyMigrationInput,
    client: Dict[str, Any] = Depends(core._web_client),
):
    if not data.confirmed:
        raise HTTPException(status_code=400, detail="迁移旧版图片知识必须明确确认归属当前店铺")
    client_id = int(client["id"])
    canonical = _require_image_shop_scope(client_id, data.shop_key, data.seller)
    migrated = []
    skipped_duplicates = []
    now = _now()
    with _KNOWLEDGE_LOCK, core._cp.db() as conn:
        legacy_rows = conn.execute(
            """SELECT id,image_sha256 FROM bot_web_image_knowledge
               WHERE client_id=? AND shop_key='' ORDER BY created_at ASC""",
            (client_id,),
        ).fetchall()
        for row in legacy_rows:
            duplicate = conn.execute(
                """SELECT id FROM bot_web_image_knowledge
                   WHERE client_id=? AND shop_key=? AND image_sha256=?""",
                (client_id, data.shop_key, row["image_sha256"]),
            ).fetchone()
            if duplicate:
                skipped_duplicates.append({"legacy_id": row["id"], "existing_id": duplicate["id"]})
                continue
            revision = _next_image_revision(conn, client_id, data.shop_key)
            cursor = conn.execute(
                """UPDATE bot_web_image_knowledge
                   SET shop_key=?,seller=?,revision=?,updated_by='manual-legacy-migration',updated_at=?
                   WHERE client_id=? AND id=? AND shop_key=''""",
                (data.shop_key, canonical, revision, now, client_id, row["id"]),
            )
            if cursor.rowcount:
                conn.execute(
                    "DELETE FROM bot_web_image_knowledge_tombstones WHERE client_id=? AND shop_key=? AND id=?",
                    (client_id, data.shop_key, row["id"]),
                )
                migrated.append(row["id"])
    return {
        "ok": True,
        "shop_key": data.shop_key,
        "migrated_count": len(migrated),
        "migrated_ids": migrated,
        "skipped_duplicates": skipped_duplicates,
    }


@router.post("/api/runtime/v1/bot-web/image-knowledge/sync")
def runtime_image_knowledge_sync(
    data: ImageKnowledgeSyncInput,
    request: Request,
    client: Dict[str, Any] = Depends(core._runtime_client),
):
    client_id = int(client["id"])
    header_shop_key = (request.headers.get("x-shop-key") or "").strip()
    if header_shop_key != data.shop_key:
        raise HTTPException(status_code=409, detail="X-Shop-Key 与图片知识同步载荷不一致")
    canonical = _require_image_shop_scope(client_id, data.shop_key, data.seller)
    conflicts: List[Dict[str, Any]] = []
    with _KNOWLEDGE_LOCK, core._cp.db() as conn:
        initial_revision = _current_image_revision(conn, client_id, data.shop_key)
        for item in data.upserts:
            image_id = _safe_text(item.get("id"), 80)
            if not image_id:
                raise HTTPException(status_code=400, detail="图片知识缺少稳定ID")
            raw, digest, mime = _decode_image_payload(
                str(item.get("image_base64") or ""),
                str(item.get("mime_type") or ""),
                str(item.get("image_sha256") or ""),
            )
            base_revision = _sync_revision(item.get("base_revision"), "base_revision")
            global_id = conn.execute(
                "SELECT client_id,shop_key FROM bot_web_image_knowledge WHERE id=?",
                (image_id,),
            ).fetchone()
            if global_id and (int(global_id["client_id"]) != client_id or str(global_id["shop_key"] or "") != data.shop_key):
                raise HTTPException(status_code=409, detail="图片知识ID已属于其他客户端或店铺")
            current = conn.execute(
                "SELECT * FROM bot_web_image_knowledge WHERE client_id=? AND shop_key=? AND seller=? AND id=?",
                (client_id, data.shop_key, canonical, image_id),
            ).fetchone()
            tombstone = conn.execute(
                "SELECT * FROM bot_web_image_knowledge_tombstones WHERE client_id=? AND shop_key=? AND id=?",
                (client_id, data.shop_key, image_id),
            ).fetchone()
            current_revision = int(current["revision"] or 0) if current else int(tombstone["revision"] or 0) if tombstone else 0
            if current_revision != base_revision:
                if current:
                    conflicts.append({"kind":"upsert","item":_image_row_payload(current, True)})
                elif tombstone:
                    conflicts.append({"kind":"delete","item":_image_tombstone_payload(tombstone)})
                else:
                    # The client may have an older non-zero base revision even though
                    # its local ID no longer exists in this restored cloud database.
                    # Return the duplicate's canonical ID mapping so the Windows client
                    # can re-key by content hash instead of hitting its unique index.
                    duplicate = conn.execute(
                        "SELECT * FROM bot_web_image_knowledge WHERE client_id=? AND shop_key=? AND image_sha256=? AND id<>?",
                        (client_id, data.shop_key, digest, image_id),
                    ).fetchone()
                    if duplicate:
                        conflicts.append({"kind":"upsert","local_id":image_id,"item":_image_row_payload(duplicate, True)})
                continue
            duplicate = conn.execute(
                "SELECT * FROM bot_web_image_knowledge WHERE client_id=? AND shop_key=? AND image_sha256=? AND id<>?",
                (client_id, data.shop_key, digest, image_id),
            ).fetchone()
            if duplicate:
                conflicts.append({"kind":"upsert","local_id":image_id,"item":_image_row_payload(duplicate, True)})
                continue
            now = _now()
            revision = _next_image_revision(conn, client_id, data.shop_key)
            updated_at = _sync_timestamp(item.get("updated_at"), "updated_at", now)
            enabled_value = item.get("enabled", True)
            if not isinstance(enabled_value, bool):
                raise HTTPException(status_code=400, detail="图片知识enabled必须是布尔值")
            image_name = _safe_text(item.get("image_name"), 180) or "图片-" + digest[:12]
            description = _safe_text(item.get("description"), 1200)
            if not description:
                raise HTTPException(status_code=400, detail="图片说明不能为空")
            conn.execute(
                """INSERT INTO bot_web_image_knowledge
                   (id,client_id,shop_key,seller,image_name,mime_type,image_sha256,image_bytes,ocr_text,description,
                    enabled,revision,updated_by,created_at,updated_at)
                   VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
                   ON CONFLICT(id) DO UPDATE SET
                     image_name=excluded.image_name,mime_type=excluded.mime_type,image_sha256=excluded.image_sha256,
                     image_bytes=excluded.image_bytes,ocr_text=excluded.ocr_text,description=excluded.description,
                     enabled=excluded.enabled,revision=excluded.revision,updated_by=excluded.updated_by,
                     updated_at=excluded.updated_at
                   WHERE bot_web_image_knowledge.client_id=excluded.client_id
                     AND bot_web_image_knowledge.shop_key=excluded.shop_key""",
                (image_id, client_id, data.shop_key, canonical, image_name, mime, digest, raw,
                 _safe_text(item.get("ocr_text"), 12000), description, 1 if enabled_value else 0,
                 revision, "windows", now, updated_at),
            )
            conn.execute(
                "DELETE FROM bot_web_image_knowledge_tombstones WHERE client_id=? AND shop_key=? AND id=?",
                (client_id, data.shop_key, image_id),
            )

        for item in data.deletes:
            image_id = _safe_text(item.get("id"), 80)
            if not image_id:
                continue
            base_revision = _sync_revision(item.get("base_revision"), "base_revision")
            current = conn.execute(
                "SELECT * FROM bot_web_image_knowledge WHERE client_id=? AND shop_key=? AND seller=? AND id=?",
                (client_id, data.shop_key, canonical, image_id),
            ).fetchone()
            tombstone = conn.execute(
                "SELECT * FROM bot_web_image_knowledge_tombstones WHERE client_id=? AND shop_key=? AND id=?",
                (client_id, data.shop_key, image_id),
            ).fetchone()
            current_revision = int(current["revision"] or 0) if current else int(tombstone["revision"] or 0) if tombstone else 0
            if current_revision != base_revision:
                if current:
                    conflicts.append({"kind":"upsert","item":_image_row_payload(current, True)})
                elif tombstone:
                    conflicts.append({"kind":"delete","item":_image_tombstone_payload(tombstone)})
                continue
            revision = _next_image_revision(conn, client_id, data.shop_key)
            deleted_at = _sync_timestamp(item.get("deleted_at"), "deleted_at", _now())
            conn.execute(
                "DELETE FROM bot_web_image_knowledge WHERE client_id=? AND shop_key=? AND seller=? AND id=?",
                (client_id, data.shop_key, canonical, image_id),
            )
            conn.execute(
                """INSERT INTO bot_web_image_knowledge_tombstones(client_id,shop_key,id,revision,deleted_at,updated_by)
                   VALUES(?,?,?,?,?,'windows')
                   ON CONFLICT(client_id,shop_key,id) DO UPDATE SET
                     revision=excluded.revision,deleted_at=excluded.deleted_at,updated_by=excluded.updated_by""",
                (client_id, data.shop_key, image_id, revision, deleted_at),
            )

        current_revision = _current_image_revision(conn, client_id, data.shop_key)
        if data.since_revision > initial_revision:
            # A restored/rebuilt cloud database can legitimately have a lower revision
            # than a Windows cursor. Tell the client to re-stage local rows and perform
            # a full reconciliation instead of pinning it forever at a future cursor.
            return {
                "ok": True,
                "shop_key": data.shop_key,
                "seller": canonical,
                "revision": 0,
                "current_revision": current_revision,
                "reset_cursor": True,
                "has_more": True,
                "upserts": [],
                "deletes": [],
                "conflicts": conflicts,
            }
        event_rows = conn.execute(
            "SELECT * FROM bot_web_image_knowledge WHERE client_id=? AND shop_key=? AND revision>? ORDER BY revision ASC",
            (client_id, data.shop_key, data.since_revision),
        ).fetchall()
        delete_rows = conn.execute(
            "SELECT id,revision,deleted_at FROM bot_web_image_knowledge_tombstones WHERE client_id=? AND shop_key=? AND revision>? ORDER BY revision ASC",
            (client_id, data.shop_key, data.since_revision),
        ).fetchall()
        events = [(int(row["revision"]), "upsert", row) for row in event_rows]
        events.extend((int(row["revision"]), "delete", row) for row in delete_rows)
        events.sort(key=lambda x: x[0])
        upserts: List[Dict[str, Any]] = []
        deletes: List[Dict[str, Any]] = []
        response_bytes = 0
        page_revision = data.since_revision
        for revision, kind, row in events:
            if kind == "upsert":
                raw_size = len(bytes(row["image_bytes"] or b""))
                if response_bytes and response_bytes + raw_size > 8 * 1024 * 1024:
                    break
                if raw_size > 8 * 1024 * 1024:
                    raise HTTPException(status_code=500, detail="云端图片知识数据超过同步大小限制")
                upserts.append(_image_row_payload(row, True))
                response_bytes += raw_size
            else:
                deletes.append(_image_tombstone_payload(row))
            page_revision = revision
            if len(upserts) >= 5 or len(deletes) >= 50:
                break
        if not events or page_revision == data.since_revision:
            page_revision = current_revision
        has_more = page_revision < current_revision

    return {
        "ok": True,
        "shop_key": data.shop_key,
        "seller": canonical,
        "revision": page_revision,
        "current_revision": current_revision,
        "has_more": has_more,
        "reset_cursor": False,
        "upserts": upserts,
        "deletes": deletes,
        "conflicts": conflicts,
    }


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
        sql = """SELECT id,client_id,message_key,seller,buyer,role,text,message_type,is_bot,withdrawn,
                     attachment_mime,attachment_name,attachment_sha256,occurred_at,created_at
              FROM bot_messages WHERE client_id=? AND seller=? AND buyer=? AND id<? ORDER BY id DESC LIMIT ?"""
        args = (client_id, seller.strip(), buyer.strip(), before_id, limit)
    else:
        sql = """SELECT id,client_id,message_key,seller,buyer,role,text,message_type,is_bot,withdrawn,
                     attachment_mime,attachment_name,attachment_sha256,occurred_at,created_at
              FROM bot_messages WHERE client_id=? AND seller=? AND buyer=? ORDER BY id DESC LIMIT ?"""
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
