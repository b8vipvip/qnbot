from __future__ import annotations

import hashlib
import io
import json
import os
import re
import shutil
import threading
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import unquote
from typing import Any, Dict, List, Optional

from fastapi import APIRouter, Depends, File, Header, HTTPException, Request, UploadFile
from fastapi.responses import FileResponse
from pydantic import BaseModel, Field
from starlette.concurrency import run_in_threadpool

import bot_web_console as core
import runtime_ocr

router = APIRouter()
_cp: Any = None
_LOCK = threading.RLock()
_MAX_IMAGE_BYTES = 8 * 1024 * 1024
_MAX_ENTRIES = 500
_MAX_OCR_CHARS = 6000
_MAX_DESCRIPTION_CHARS = 1200


class ImageKnowledgeSaveInput(BaseModel):
    upload_id: str = Field(min_length=16, max_length=80)
    image_name: str = Field(default="", max_length=200)
    ocr_text: str = Field(default="", max_length=_MAX_OCR_CHARS)
    description: str = Field(min_length=1, max_length=_MAX_DESCRIPTION_CHARS)


class ImageKnowledgeUpdateInput(BaseModel):
    image_name: str = Field(default="", max_length=200)
    ocr_text: str = Field(default="", max_length=_MAX_OCR_CHARS)
    description: str = Field(min_length=1, max_length=_MAX_DESCRIPTION_CHARS)
    enabled: bool = True


class ImageKnowledgeSyncInput(BaseModel):
    revision: int = Field(default=0, ge=0)
    records: Optional[List[Dict[str, Any]]] = None


def install(control_plane: Any, console_module=core) -> None:
    global _cp, core
    _cp = control_plane
    core = console_module
    control_plane.app.include_router(router)


def init_db() -> None:
    if _cp is None:
        raise RuntimeError("bot_web_image_knowledge is not installed")
    with _cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bot_message_images (
                client_id INTEGER NOT NULL,
                message_key TEXT NOT NULL,
                seller TEXT NOT NULL,
                buyer TEXT NOT NULL,
                image_sha256 TEXT NOT NULL,
                mime_type TEXT NOT NULL,
                image_name TEXT NOT NULL DEFAULT '',
                file_path TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY(client_id,message_key),
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS bot_image_knowledge_state (
                client_id INTEGER NOT NULL,
                shop_key TEXT NOT NULL,
                revision INTEGER NOT NULL DEFAULT 0,
                records_json TEXT NOT NULL DEFAULT '[]',
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(client_id,shop_key),
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS bot_image_knowledge_uploads (
                upload_id TEXT PRIMARY KEY,
                client_id INTEGER NOT NULL,
                shop_key TEXT NOT NULL,
                image_name TEXT NOT NULL DEFAULT '',
                image_sha256 TEXT NOT NULL,
                mime_type TEXT NOT NULL,
                file_path TEXT NOT NULL,
                ocr_text TEXT NOT NULL DEFAULT '',
                confidence REAL NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_bot_image_knowledge_uploads_owner
                ON bot_image_knowledge_uploads(client_id,shop_key,created_at);
            """
        )


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _storage_root() -> Path:
    root = Path(getattr(_cp, "DATA_DIR", "/data")).resolve() / "bot-web-image-knowledge"
    root.mkdir(parents=True, exist_ok=True)
    return root


def _shop_component(shop_key: str) -> str:
    return hashlib.sha256((shop_key or "").encode("utf-8")).hexdigest()[:20]


def _shop_root(client_id: int, shop_key: str) -> Path:
    root = _storage_root() / str(int(client_id)) / _shop_component(shop_key)
    root.mkdir(parents=True, exist_ok=True)
    return root


def _safe_image_name(value: Any, fallback: str = "") -> str:
    name = os.path.basename(unquote(str(value or "")).replace("\\", "/")).strip()
    name = re.sub(r"[\x00-\x1f<>:\"|?*]", "_", name)
    name = name[:180].strip(" .")
    return name or fallback


def _mime_extension(mime: str) -> str:
    return {
        "image/jpeg": ".jpg",
        "image/png": ".png",
        "image/webp": ".webp",
        "image/gif": ".gif",
    }.get((mime or "").lower(), "")


def _detect_mime(raw: bytes) -> str:
    try:
        from PIL import Image
        with Image.open(io.BytesIO(raw)) as image:
            fmt = (image.format or "").upper()
        return {"JPEG": "image/jpeg", "JPG": "image/jpeg", "PNG": "image/png",
                "WEBP": "image/webp", "GIF": "image/gif"}.get(fmt, "")
    except Exception as exc:
        raise HTTPException(status_code=400, detail="上传内容不是有效图片") from exc


def _validate_and_ocr(raw: bytes) -> Dict[str, Any]:
    if not raw:
        raise HTTPException(status_code=400, detail="图片不能为空")
    if len(raw) > _MAX_IMAGE_BYTES:
        raise HTTPException(status_code=413, detail="图片大小不能超过 8 MB")
    try:
        runtime_ocr._validate_image(raw)
    except Exception as exc:
        raise HTTPException(status_code=400, detail="上传内容不是有效图片") from exc
    mime = _detect_mime(raw)
    if mime not in {"image/jpeg", "image/png", "image/webp", "image/gif"}:
        raise HTTPException(status_code=415, detail="仅支持 JPG、PNG、WEBP、GIF 图片")
    settings = runtime_ocr._current_settings()
    if not settings.get("enabled", True):
        raise HTTPException(status_code=503, detail="服务端 OCR 已被管理员停用")
    try:
        result = run_in_threadpool(runtime_ocr._run_ocr, raw)
    except Exception as exc:
        raise HTTPException(status_code=502, detail="服务端 OCR 执行失败：" + str(exc)[:240]) from exc
    return {"mime_type": mime, "ocr": result}


def _atomic_write(path: Path, raw: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with temp.open("xb") as stream:
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(str(temp), str(path))
    finally:
        try:
            if temp.exists():
                temp.unlink()
        except OSError:
            pass


def _bound_shop(client_id: int) -> Dict[str, str]:
    with _cp.db() as conn:
        row = conn.execute(
            "SELECT shop_key,seller FROM bot_client_shop_binding WHERE client_id=?",
            (int(client_id),),
        ).fetchone()
    shop_key = str(row["shop_key"] or "").strip() if row else ""
    seller = str(row["seller"] or "").strip() if row else ""
    if not shop_key:
        raise HTTPException(status_code=409, detail="Windows Bot 尚未完成店铺绑定，暂时无法使用云端图片知识")
    if not seller:
        with _cp.db() as conn:
            state = conn.execute(
                "SELECT status_json,seller_nicks_json FROM bot_client_state WHERE client_id=?",
                (int(client_id),),
            ).fetchone()
        if state:
            try:
                status = json.loads(state["status_json"] or "{}")
                seller = str(status.get("current_seller") or "").strip()
                if not seller:
                    sellers = json.loads(state["seller_nicks_json"] or "[]")
                    seller = str(sellers[0] or "").strip() if sellers else ""
            except Exception:
                seller = ""
    return {"shop_key": shop_key, "seller": seller}


def _require_runtime_shop(client_id: int, shop_key: str) -> Dict[str, str]:
    scope = _bound_shop(client_id)
    if not shop_key or shop_key != scope["shop_key"]:
        raise HTTPException(status_code=409, detail="ShopKey 与当前客户端绑定不一致，已阻止跨店图片访问")
    return scope


def _state(client_id: int, shop_key: str) -> Dict[str, Any]:
    with _cp.db() as conn:
        row = conn.execute(
            "SELECT revision,records_json,updated_at,updated_by FROM bot_image_knowledge_state WHERE client_id=? AND shop_key=?",
            (int(client_id), shop_key),
        ).fetchone()
    if not row:
        return {"revision": 0, "records": [], "updated_at": "", "updated_by": ""}
    try:
        records = json.loads(row["records_json"] or "[]")
    except Exception:
        records = []
    if not isinstance(records, list):
        records = []
    return {
        "revision": int(row["revision"] or 0),
        "records": [x for x in records if isinstance(x, dict)],
        "updated_at": row["updated_at"] or "",
        "updated_by": row["updated_by"] or "",
    }


def _normalise_records(records: Any) -> List[Dict[str, Any]]:
    if not isinstance(records, list) or len(records) > _MAX_ENTRIES:
        raise HTTPException(status_code=400, detail="图片知识记录必须是最多 500 条的数组")
    allowed = {"Id", "ImageName", "ImageSha256", "ImageMimeType", "OcrText", "Description",
               "Enabled", "Deleted", "CreatedAt", "UpdatedAt"}
    result: List[Dict[str, Any]] = []
    seen = set()
    for raw in records:
        if not isinstance(raw, dict):
            raise HTTPException(status_code=400, detail="图片知识记录格式无效")
        item = {key: raw[key] for key in allowed if key in raw}
        record_id = str(item.get("Id") or "").strip()
        sha = str(item.get("ImageSha256") or "").strip().lower()
        mime = str(item.get("ImageMimeType") or "").strip().lower()
        if not record_id or len(record_id) > 80 or record_id in seen:
            raise HTTPException(status_code=400, detail="图片知识 ID 无效或重复")
        if not re.fullmatch(r"[a-f0-9]{64}", sha):
            raise HTTPException(status_code=400, detail="图片知识 SHA-256 无效")
        if mime not in {"image/jpeg", "image/png", "image/webp", "image/gif"}:
            raise HTTPException(status_code=400, detail="图片知识 MIME 类型无效")
        seen.add(record_id)
        item["Id"] = record_id
        item["ImageSha256"] = sha
        item["ImageMimeType"] = mime
        item["ImageName"] = _safe_image_name(item.get("ImageName"), "图片知识" + _mime_extension(mime))
        item["OcrText"] = str(item.get("OcrText") or "")[:_MAX_OCR_CHARS]
        item["Description"] = str(item.get("Description") or "")[:_MAX_DESCRIPTION_CHARS]
        item["Enabled"] = bool(item.get("Enabled", True))
        item["Deleted"] = bool(item.get("Deleted", False))
        item["CreatedAt"] = str(item.get("CreatedAt") or "")[:64]
        item["UpdatedAt"] = str(item.get("UpdatedAt") or "")[:64]
        result.append(item)
    return result


def _save_state(client_id: int, shop_key: str, records: List[Dict[str, Any]], updated_by: str) -> Dict[str, Any]:
    clean = _normalise_records(records)
    encoded = json.dumps(clean, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
    now = _now()
    with _LOCK, _cp.db() as conn:
        conn.execute("BEGIN IMMEDIATE")
        row = conn.execute(
            "SELECT revision,records_json FROM bot_image_knowledge_state WHERE client_id=? AND shop_key=?",
            (int(client_id), shop_key),
        ).fetchone()
        current_revision = int(row["revision"] or 0) if row else 0
        current_encoded = json.dumps(
            json.loads(row["records_json"] or "[]") if row else [],
            ensure_ascii=False, separators=(",", ":"), sort_keys=True
        )
        if encoded == current_encoded:
            return _state(client_id, shop_key)
        revision = current_revision + 1
        conn.execute(
            """
            INSERT INTO bot_image_knowledge_state(client_id,shop_key,revision,records_json,updated_at,updated_by)
            VALUES(?,?,?,?,?,?)
            ON CONFLICT(client_id,shop_key) DO UPDATE SET
                revision=excluded.revision,records_json=excluded.records_json,
                updated_at=excluded.updated_at,updated_by=excluded.updated_by
            """,
            (int(client_id), shop_key, revision, encoded, now, str(updated_by or "")[:80]),
        )
    return {"revision": revision, "records": clean, "updated_at": now, "updated_by": updated_by}


def _entry_path(client_id: int, shop_key: str, record: Dict[str, Any]) -> Path:
    extension = _mime_extension(str(record.get("ImageMimeType") or ""))
    name = str(record.get("Id") or "") + "-" + str(record.get("ImageSha256") or "")[:12] + extension
    return _shop_root(client_id, shop_key) / "entries" / name


def _record_with_image_flag(client_id: int, shop_key: str, record: Dict[str, Any]) -> Dict[str, Any]:
    item = dict(record)
    path = _entry_path(client_id, shop_key, item)
    item["ImageAvailable"] = path.is_file()
    item["ImageUrl"] = "/api/bot-web/image-knowledge/" + str(item.get("Id") or "") + "/image"
    return item


def _stage_upload(client_id: int, shop_key: str, image_name: str, raw: bytes,
                  mime: str, ocr: Dict[str, Any]) -> Dict[str, Any]:
    upload_id = uuid.uuid4().hex
    sha = hashlib.sha256(raw).hexdigest()
    name = _safe_image_name(image_name, "图片知识_" + datetime.now().strftime("%Y%m%d_%H%M%S") + _mime_extension(mime))
    path = _shop_root(client_id, shop_key) / "staging" / (upload_id + _mime_extension(mime))
    _atomic_write(path, raw)
    result = ocr or {}
    ocr_result = result.get("ocr") if isinstance(result.get("ocr"), dict) else result
    text = str(ocr_result.get("text") or "")[:_MAX_OCR_CHARS]
    confidence = float(ocr_result.get("confidence") or 0)
    with _cp.db() as conn:
        conn.execute(
            """INSERT INTO bot_image_knowledge_uploads(
                upload_id,client_id,shop_key,image_name,image_sha256,mime_type,file_path,ocr_text,confidence,created_at
            ) VALUES(?,?,?,?,?,?,?,?,?,?)""",
            (upload_id, int(client_id), shop_key, name, sha, mime, str(path), text, confidence, _now()),
        )
    return {
        "upload_id": upload_id,
        "image_name": name,
        "image_sha256": sha,
        "mime_type": mime,
        "ocr_text": text,
        "confidence": confidence,
        "engine": str(ocr_result.get("engine") or "RapidOCR/ONNXRuntime"),
        "elapsed_ms": int(ocr_result.get("elapsedMs") or 0),
        "image_url": "/api/bot-web/image-knowledge/uploads/" + upload_id + "/image",
    }


async def _read_and_stage(client_id: int, shop_key: str, filename: str, raw: bytes) -> Dict[str, Any]:
    if len(raw) > _MAX_IMAGE_BYTES:
        raise HTTPException(status_code=413, detail="图片大小不能超过 8 MB")
    ocr = await _run_ocr(raw)
    mime = ocr["mime_type"]
    return _stage_upload(client_id, shop_key, filename, raw, mime, ocr)


async def _run_ocr(raw: bytes) -> Dict[str, Any]:
    validated = _validate_and_ocr(raw)
    if hasattr(validated["ocr"], "__await__"):
        validated["ocr"] = await validated["ocr"]
    return validated


@router.post("/api/bot-web/image-knowledge/upload")
async def web_upload_image_knowledge(
    file: UploadFile = File(...),
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    scope = _bound_shop(client_id)
    raw = await file.read(_MAX_IMAGE_BYTES + 1)
    if len(raw) > _MAX_IMAGE_BYTES:
        raise HTTPException(status_code=413, detail="图片大小不能超过 8 MB")
    return await _read_and_stage(client_id, scope["shop_key"], file.filename or "", raw)


@router.get("/api/bot-web/image-knowledge/uploads/{upload_id}/image")
def web_get_staged_image(
    upload_id: str,
    client: Dict[str, Any] = Depends(core._web_client),
):
    client_id = int(client["id"])
    scope = _bound_shop(client_id)
    with _cp.db() as conn:
        row = conn.execute(
            "SELECT file_path,mime_type FROM bot_image_knowledge_uploads WHERE upload_id=? AND client_id=? AND shop_key=?",
            (upload_id, client_id, scope["shop_key"]),
        ).fetchone()
    if not row or not Path(row["file_path"]).is_file():
        raise HTTPException(status_code=404, detail="待保存图片不存在或已过期")
    return FileResponse(row["file_path"], media_type=row["mime_type"])


@router.get("/api/bot-web/image-knowledge")
def web_list_image_knowledge(client: Dict[str, Any] = Depends(core._web_client)) -> Dict[str, Any]:
    client_id = int(client["id"])
    scope = _bound_shop(client_id)
    state = _state(client_id, scope["shop_key"])
    records = [x for x in state["records"] if not bool(x.get("Deleted"))]
    records.sort(key=lambda x: str(x.get("UpdatedAt") or x.get("CreatedAt") or ""), reverse=True)
    return {
        "shop_key": scope["shop_key"],
        "seller": scope["seller"],
        "revision": state["revision"],
        "updated_at": state["updated_at"],
        "records": [_record_with_image_flag(client_id, scope["shop_key"], x) for x in records],
    }


@router.post("/api/bot-web/image-knowledge")
def web_save_image_knowledge(
    data: ImageKnowledgeSaveInput,
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    scope = _bound_shop(client_id)
    with _cp.db() as conn:
        upload = conn.execute(
            """SELECT * FROM bot_image_knowledge_uploads
               WHERE upload_id=? AND client_id=? AND shop_key=?""",
            (data.upload_id, client_id, scope["shop_key"]),
        ).fetchone()
    if not upload or not Path(upload["file_path"]).is_file():
        raise HTTPException(status_code=404, detail="上传图片已过期，请重新上传")
    description = str(data.description or "").strip()
    if not description:
        raise HTTPException(status_code=400, detail="请填写图片说明")
    image_name = _safe_image_name(data.image_name, upload["image_name"])
    record_id = uuid.uuid4().hex
    record = {
        "Id": record_id,
        "ImageName": image_name,
        "ImageSha256": upload["image_sha256"],
        "ImageMimeType": upload["mime_type"],
        "OcrText": str(data.ocr_text or upload["ocr_text"] or "")[:_MAX_OCR_CHARS],
        "Description": description[:_MAX_DESCRIPTION_CHARS],
        "Enabled": True,
        "Deleted": False,
        "CreatedAt": _now(),
        "UpdatedAt": _now(),
    }
    target = _entry_path(client_id, scope["shop_key"], record)
    target.parent.mkdir(parents=True, exist_ok=True)
    os.replace(upload["file_path"], target)
    state = _state(client_id, scope["shop_key"])
    if len([x for x in state["records"] if not bool(x.get("Deleted"))]) >= _MAX_ENTRIES:
        try:
            target.unlink()
        except OSError:
            pass
        raise HTTPException(status_code=409, detail="当前店铺图片知识已达到 500 条上限")
    result = _save_state(client_id, scope["shop_key"], state["records"] + [record], "web")
    with _cp.db() as conn:
        conn.execute("DELETE FROM bot_image_knowledge_uploads WHERE upload_id=? AND client_id=?",
                     (data.upload_id, client_id))
    return {"ok": True, "record": _record_with_image_flag(client_id, scope["shop_key"], record),
            "revision": result["revision"]}


@router.put("/api/bot-web/image-knowledge/{record_id}")
def web_update_image_knowledge(
    record_id: str,
    data: ImageKnowledgeUpdateInput,
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    scope = _bound_shop(client_id)
    state = _state(client_id, scope["shop_key"])
    records = list(state["records"])
    index = next((i for i, x in enumerate(records)
                  if str(x.get("Id") or "") == record_id and not bool(x.get("Deleted"))), -1)
    if index < 0:
        raise HTTPException(status_code=404, detail="图片知识不存在")
    record = dict(records[index])
    record.update({
        "ImageName": _safe_image_name(data.image_name, str(record.get("ImageName") or "图片知识")),
        "OcrText": str(data.ocr_text or "")[:_MAX_OCR_CHARS],
        "Description": str(data.description or "").strip()[:_MAX_DESCRIPTION_CHARS],
        "Enabled": bool(data.enabled),
        "UpdatedAt": _now(),
    })
    if not record["Description"]:
        raise HTTPException(status_code=400, detail="图片说明不能为空")
    records[index] = record
    result = _save_state(client_id, scope["shop_key"], records, "web")
    return {"ok": True, "record": _record_with_image_flag(client_id, scope["shop_key"], record),
            "revision": result["revision"]}


@router.delete("/api/bot-web/image-knowledge/{record_id}")
def web_delete_image_knowledge(
    record_id: str,
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    scope = _bound_shop(client_id)
    state = _state(client_id, scope["shop_key"])
    records = list(state["records"])
    index = next((i for i, x in enumerate(records)
                  if str(x.get("Id") or "") == record_id and not bool(x.get("Deleted"))), -1)
    if index < 0:
        raise HTTPException(status_code=404, detail="图片知识不存在")
    records[index] = dict(records[index])
    records[index]["Deleted"] = True
    records[index]["Enabled"] = False
    records[index]["UpdatedAt"] = _now()
    result = _save_state(client_id, scope["shop_key"], records, "web-delete")
    return {"ok": True, "revision": result["revision"]}


@router.get("/api/bot-web/image-knowledge/{record_id}/image")
def web_get_image_knowledge_image(
    record_id: str,
    client: Dict[str, Any] = Depends(core._web_client),
):
    client_id = int(client["id"])
    scope = _bound_shop(client_id)
    state = _state(client_id, scope["shop_key"])
    record = next((x for x in state["records"]
                   if str(x.get("Id") or "") == record_id and not bool(x.get("Deleted"))), None)
    if not record:
        raise HTTPException(status_code=404, detail="图片知识不存在")
    path = _entry_path(client_id, scope["shop_key"], record)
    if not path.is_file():
        raise HTTPException(status_code=404, detail="图片文件不存在")
    return FileResponse(path, media_type=str(record.get("ImageMimeType") or "application/octet-stream"))


@router.post("/api/bot-web/image-knowledge/from-message/{message_id}")
async def web_import_image_message(
    message_id: int,
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    scope = _bound_shop(client_id)
    with _cp.db() as conn:
        message = conn.execute(
            """SELECT id,message_key,seller,buyer,role,text,message_type,withdrawn,occurred_at
               FROM bot_messages WHERE id=? AND client_id=?""",
            (message_id, client_id),
        ).fetchone()
        if not message:
            raise HTTPException(status_code=404, detail="消息不存在")
        if message["role"] != "user" or bool(message["withdrawn"]):
            raise HTTPException(status_code=400, detail="只能从未撤回的买家图片消息导入")
        image = conn.execute(
            """SELECT image_sha256,mime_type,image_name,file_path FROM bot_message_images
               WHERE client_id=? AND message_key=? AND seller=? AND buyer=?""",
            (client_id, message["message_key"], message["seller"], message["buyer"]),
        ).fetchone()
    if not image or not Path(image["file_path"]).is_file():
        raise HTTPException(status_code=404, detail="该消息的原始图片尚未同步到服务端")
    raw = Path(image["file_path"]).read_bytes()
    fallback = "买家图片_" + str(message["occurred_at"] or datetime.now().strftime("%Y%m%d_%H%M%S"))
    return await _read_and_stage(client_id, scope["shop_key"], image["image_name"] or fallback, raw)


@router.post("/api/runtime/v1/bot-web/message-image")
async def runtime_upload_message_image(
    request: Request,
    x_shop_key: str = Header(default=""),
    x_message_key: str = Header(default=""),
    x_seller: str = Header(default=""),
    x_buyer: str = Header(default=""),
    x_image_sha256: str = Header(default=""),
    x_image_name: str = Header(default=""),
    client: Dict[str, Any] = Depends(core._runtime_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    scope = _require_runtime_shop(client_id, x_shop_key.strip())
    message_key = str(x_message_key or "").strip()
    seller = unquote(str(x_seller or "")).strip()
    buyer = unquote(str(x_buyer or "")).strip()
    if not message_key or not seller or not buyer:
        raise HTTPException(status_code=400, detail="图片消息缺少消息身份")
    with _cp.db() as conn:
        message = conn.execute(
            """SELECT role,withdrawn FROM bot_messages
               WHERE client_id=? AND message_key=? AND seller=? AND buyer=?""",
            (client_id, message_key, seller, buyer),
        ).fetchone()
    if not message or message["role"] != "user" or bool(message["withdrawn"]):
        raise HTTPException(status_code=409, detail="找不到对应的有效买家图片消息，拒绝绑定图片")
    raw = await request.body()
    if not raw or len(raw) > _MAX_IMAGE_BYTES:
        raise HTTPException(status_code=413, detail="消息图片不能为空且不能超过 8 MB")
    mime = _detect_mime(raw)
    sha = hashlib.sha256(raw).hexdigest()
    if x_image_sha256 and x_image_sha256.strip().lower() != sha:
        raise HTTPException(status_code=400, detail="图片 SHA-256 校验失败")
    extension = _mime_extension(mime)
    name = _safe_image_name(x_image_name, "买家图片_" + sha[:12] + extension)
    path = _shop_root(client_id, scope["shop_key"]) / "messages" / (sha + extension)
    if not path.is_file():
        _atomic_write(path, raw)
    with _cp.db() as conn:
        conn.execute(
            """INSERT INTO bot_message_images(
                client_id,message_key,seller,buyer,image_sha256,mime_type,image_name,file_path,created_at
            ) VALUES(?,?,?,?,?,?,?,?,?)
            ON CONFLICT(client_id,message_key) DO UPDATE SET
                seller=excluded.seller,buyer=excluded.buyer,image_sha256=excluded.image_sha256,
                mime_type=excluded.mime_type,image_name=excluded.image_name,file_path=excluded.file_path,
                created_at=excluded.created_at""",
            (client_id, message_key, seller, buyer, sha, mime, name, str(path), _now()),
        )
    return {"ok": True, "image_available": True, "image_sha256": sha}


@router.get("/api/bot-web/conversation/messages/{message_id}/image")
def web_get_conversation_message_image(
    message_id: int,
    client: Dict[str, Any] = Depends(core._web_client),
):
    client_id = int(client["id"])
    with _cp.db() as conn:
        row = conn.execute(
            """SELECT i.file_path,i.mime_type
               FROM bot_messages m JOIN bot_message_images i
                 ON i.client_id=m.client_id AND i.message_key=m.message_key
               WHERE m.id=? AND m.client_id=? AND m.role='user' AND m.withdrawn=0
                 AND i.seller=m.seller AND i.buyer=m.buyer""",
            (message_id, client_id),
        ).fetchone()
    if not row or not Path(row["file_path"]).is_file():
        raise HTTPException(status_code=404, detail="消息图片不存在")
    return FileResponse(row["file_path"], media_type=row["mime_type"])


@router.post("/api/runtime/v1/bot-web/image-knowledge-sync")
def runtime_image_knowledge_sync(
    data: ImageKnowledgeSyncInput,
    request: Request,
    x_shop_key: str = Header(default=""),
):
    client = core._runtime_client(request)
    client_id = int(client["id"])
    scope = _require_runtime_shop(client_id, x_shop_key.strip())
    current = _state(client_id, scope["shop_key"])
    incoming = _normalise_records(data.records) if data.records is not None else None

    with _LOCK:
        current = _state(client_id, scope["shop_key"])
        if current["revision"] == 0 and incoming is not None:
            current = _save_state(client_id, scope["shop_key"], incoming, "windows-seed")
        elif current["revision"] > data.revision:
            pass
        elif incoming is not None:
            encoded_incoming = json.dumps(incoming, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
            encoded_current = json.dumps(current["records"], ensure_ascii=False, separators=(",", ":"), sort_keys=True)
            if encoded_incoming != encoded_current:
                current = _save_state(client_id, scope["shop_key"], incoming, "windows")

    records = []
    missing_uploads = []
    for record in current["records"]:
        item = _record_with_image_flag(client_id, scope["shop_key"], record)
        records.append(item)
        if not bool(record.get("Deleted")) and not item["ImageAvailable"]:
            missing_uploads.append({"id": record["Id"], "sha256": record["ImageSha256"]})
    return {"revision": current["revision"], "records": records, "missing_uploads": missing_uploads,
            "updated_at": current["updated_at"]}


@router.post("/api/runtime/v1/bot-web/image-knowledge/{record_id}/image")
async def runtime_upload_image_knowledge_file(
    record_id: str,
    request: Request,
    x_shop_key: str = Header(default=""),
    x_image_sha256: str = Header(default=""),
):
    client = core._runtime_client(request)
    client_id = int(client["id"])
    scope = _require_runtime_shop(client_id, x_shop_key.strip())
    state = _state(client_id, scope["shop_key"])
    record = next((x for x in state["records"]
                   if str(x.get("Id") or "") == record_id and not bool(x.get("Deleted"))), None)
    if not record:
        raise HTTPException(status_code=404, detail="待同步图片知识不存在")
    raw = await request.body()
    if not raw or len(raw) > _MAX_IMAGE_BYTES:
        raise HTTPException(status_code=413, detail="图片不能为空且不能超过 8 MB")
    sha = hashlib.sha256(raw).hexdigest()
    if sha != str(record.get("ImageSha256") or "") or (x_image_sha256 and x_image_sha256.strip().lower() != sha):
        raise HTTPException(status_code=400, detail="图片知识文件 SHA-256 不匹配")
    mime = _detect_mime(raw)
    if mime != str(record.get("ImageMimeType") or ""):
        raise HTTPException(status_code=400, detail="图片知识文件 MIME 类型不匹配")
    path = _entry_path(client_id, scope["shop_key"], record)
    _atomic_write(path, raw)
    return {"ok": True, "image_available": True, "sha256": sha}


@router.get("/api/runtime/v1/bot-web/image-knowledge/{record_id}/image")
def runtime_get_image_knowledge_file(
    record_id: str,
    request: Request,
    x_shop_key: str = Header(default=""),
):
    client = core._runtime_client(request)
    client_id = int(client["id"])
    scope = _require_runtime_shop(client_id, x_shop_key.strip())
    state = _state(client_id, scope["shop_key"])
    record = next((x for x in state["records"]
                   if str(x.get("Id") or "") == record_id and not bool(x.get("Deleted"))), None)
    if not record:
        raise HTTPException(status_code=404, detail="图片知识不存在")
    path = _entry_path(client_id, scope["shop_key"], record)
    if not path.is_file():
        raise HTTPException(status_code=404, detail="图片知识文件不存在")
    return FileResponse(path, media_type=str(record.get("ImageMimeType") or "application/octet-stream"))
