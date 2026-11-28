from __future__ import annotations

import json
import math
import time
import uuid
from typing import Any

from fastapi import APIRouter, Depends, Header, HTTPException, Request
from pydantic import BaseModel

import bot_web_console

router = APIRouter()
_cp = None
_MAX_RECORDS = 5000
_MAX_BODY_BYTES = 4 * 1024 * 1024

class KnowledgeV2SyncInput(BaseModel):
    revision: int = 0
    records: list[dict[str, Any]] | None = None

class KnowledgeV2FeedbackInput(BaseModel):
    epoch: str = ""
    cursor: int = 0
    events: list[dict[str, Any]] = []

class KnowledgeV2WebInput(BaseModel):
    records: list[dict[str, Any]]
    expected_revision: int

class KnowledgeV2SmartImportInput(BaseModel):
    text: str
    timeout_seconds: int = 90

class KnowledgeV2RuntimeSettingsInput(BaseModel):
    enabled: bool
    mode: str = "production"
    direct_threshold: float = 0.82
    min_confidence: float = 0.72

def install(control_plane, console_module=bot_web_console):
    global _cp
    _cp = control_plane
    global bot_web_console
    bot_web_console = console_module
    control_plane.app.include_router(router)

def init_db():
    if _cp is None:
        raise RuntimeError("knowledge_v2_sync is not installed")
    with _cp.db() as conn:
        conn.execute("""
            CREATE TABLE IF NOT EXISTS bot_knowledge_v2_state (
                client_id INTEGER PRIMARY KEY,
                revision INTEGER NOT NULL DEFAULT 0,
                records_json TEXT NOT NULL DEFAULT '[]',
                updated_at TEXT NOT NULL DEFAULT '',
                updated_by TEXT NOT NULL DEFAULT '',
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            )
        """)

        conn.execute("""CREATE TABLE IF NOT EXISTS bot_knowledge_v2_feedback_epoch (
            id INTEGER PRIMARY KEY CHECK(id=1), epoch TEXT NOT NULL)""")
        conn.execute("INSERT OR IGNORE INTO bot_knowledge_v2_feedback_epoch(id,epoch) VALUES(1,?)", (uuid.uuid4().hex,))
        conn.execute("""CREATE TABLE IF NOT EXISTS bot_knowledge_v2_feedback (
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            client_id INTEGER NOT NULL, shop_key TEXT NOT NULL, event_id TEXT NOT NULL,
            knowledge_id TEXT NOT NULL, event_type TEXT NOT NULL, utc_ticks INTEGER NOT NULL,
            UNIQUE(client_id,shop_key,event_id),
            FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
        )""")
        conn.execute("CREATE INDEX IF NOT EXISTS ix_v2_feedback_cursor ON bot_knowledge_v2_feedback(client_id,shop_key,seq)")
        conn.execute("CREATE INDEX IF NOT EXISTS ix_v2_feedback_time ON bot_knowledge_v2_feedback(client_id,shop_key,utc_ticks)")

_FEEDBACK_TYPES = {"sent", "send_failed", "accepted", "correction", "withdrawal", "buyer_negative"}
_TICKS_PER_SECOND = 10_000_000
_UNIX_TICKS = 621355968000000000

def _feedback_now_ticks():
    return _UNIX_TICKS + int(time.time() * _TICKS_PER_SECOND)

def _feedback_binding(conn, client_id, requested=None):
    row = conn.execute("SELECT shop_key FROM bot_client_shop_binding WHERE client_id=?", (client_id,)).fetchone()
    bound = str(row["shop_key"] or "").strip() if row else ""
    if not bound or (requested is not None and requested != bound):
        raise HTTPException(status_code=403, detail="反馈同步令牌未绑定该店铺")
    return bound

def _validate_feedback(events, now):
    if len(events) > 200:
        raise HTTPException(status_code=413, detail="反馈同步每批最多200条")
    seen = set()
    clean = []
    for row in events:
        if set(row) != {"Id", "KnowledgeId", "EventType", "UtcTicks"}:
            raise HTTPException(status_code=400, detail="反馈事件字段无效")
        eid, kid, typ, ticks = (row[k] for k in ("Id", "KnowledgeId", "EventType", "UtcTicks"))
        try:
            valid_id = isinstance(eid, str) and len(eid) == 32 and uuid.UUID(hex=eid).hex == eid and int(eid,16) != 0
        except (ValueError, AttributeError, TypeError):
            valid_id = False
        if (not valid_id or eid in seen or not isinstance(kid, str) or not kid.strip()
            or len(kid) > 200 or not isinstance(typ, str) or typ not in _FEEDBACK_TYPES
            or type(ticks) is not int or ticks <= 0 or ticks > now + 300 * _TICKS_PER_SECOND):
            raise HTTPException(status_code=400, detail="反馈事件内容无效或ID重复")
        seen.add(eid)
        clean.append((eid, kid, typ, ticks))
    return clean

@router.post("/api/runtime/v1/bot-web/knowledge-v2-feedback-sync")
def runtime_feedback_sync(payload: KnowledgeV2FeedbackInput, request: Request, x_shop_key: str | None = Header(default=None)):
    client_id = int(bot_web_console._runtime_client(request)["id"])
    if not x_shop_key or payload.cursor < 0 or payload.cursor > 9223372036854775807:
        raise HTTPException(status_code=400, detail="反馈同步店铺或游标无效")
    now = _feedback_now_ticks()
    cutoff = now - 180 * 86400 * _TICKS_PER_SECOND
    events = _validate_feedback(payload.events, now)
    with _cp.db() as conn:
        conn.execute("BEGIN IMMEDIATE")
        shop_key = _feedback_binding(conn, client_id, x_shop_key.strip())
        server_epoch = conn.execute("SELECT epoch FROM bot_knowledge_v2_feedback_epoch WHERE id=1").fetchone()["epoch"]
        epoch = uuid.uuid5(uuid.UUID(hex=server_epoch), f"{client_id}:{shop_key}").hex
        cursor = payload.cursor if payload.epoch == epoch else 0
        conn.execute("DELETE FROM bot_knowledge_v2_feedback WHERE client_id=? AND shop_key=? AND utc_ticks<?", (client_id,shop_key,cutoff))
        for eid,kid,typ,ticks in events:
            if ticks < cutoff:
                continue
            conn.execute("""INSERT OR IGNORE INTO bot_knowledge_v2_feedback(client_id,shop_key,event_id,knowledge_id,event_type,utc_ticks)
                VALUES(?,?,?,?,?,?)""", (client_id,shop_key,eid,kid,typ,ticks))
            prior = conn.execute("""SELECT knowledge_id,event_type,utc_ticks FROM bot_knowledge_v2_feedback
                WHERE client_id=? AND shop_key=? AND event_id=?""", (client_id,shop_key,eid)).fetchone()
            if (prior["knowledge_id"],prior["event_type"],prior["utc_ticks"]) != (kid,typ,ticks):
                raise HTTPException(status_code=409,detail="反馈事件ID内容冲突")
        rows = conn.execute("""SELECT seq,event_id,knowledge_id,event_type,utc_ticks FROM bot_knowledge_v2_feedback
            WHERE client_id=? AND shop_key=? AND seq>? ORDER BY seq LIMIT 201""", (client_id,shop_key,cursor)).fetchall()
        page = rows[:200]
        return {"epoch":epoch,"cursor":int(page[-1]["seq"]) if page else cursor,
            "has_more":len(rows)>200,"ack_ids":[e[0] for e in events],
            "events":[{"Id":row["event_id"],"KnowledgeId":row["knowledge_id"],"EventType":row["event_type"],
                       "UtcTicks":row["utc_ticks"]} for row in page]}

@router.get("/api/bot-web/knowledge-v2/feedback")
def web_feedback(client=Depends(bot_web_console._web_client)):
    client_id = int(client["id"])
    cutoff = _feedback_now_ticks() - 180 * 86400 * _TICKS_PER_SECOND
    with _cp.db() as conn:
        shop_key = _feedback_binding(conn,client_id)
        rows = conn.execute("""SELECT knowledge_id,event_type,COUNT(*) AS count,MAX(utc_ticks) AS last_ticks
            FROM bot_knowledge_v2_feedback WHERE client_id=? AND shop_key=? AND utc_ticks>=?
            GROUP BY knowledge_id,event_type""",(client_id,shop_key,cutoff)).fetchall()
    counts = {}
    for row in rows:
        item=counts.setdefault(row["knowledge_id"],{})
        item[row["event_type"]]=row["count"]
        if row["event_type"]=="sent":
            item["last_used_utc_ticks"]=row["last_ticks"]
    return {"items":counts,"window_days":180}

def _validate_records(records):
    if not isinstance(records, list):
        raise HTTPException(status_code=400, detail="records 必须是数组")
    if len(records) > _MAX_RECORDS:
        raise HTTPException(status_code=400, detail="Knowledge V2 记录最多 5000 条")
    encoded=json.dumps(records,ensure_ascii=False,separators=(",",":"))
    if len(encoded.encode("utf-8")) > _MAX_BODY_BYTES:
        raise HTTPException(status_code=413, detail="Knowledge V2 数据超过 4MB")
    allowed={"Id","Type","Title","Intent","Subject","Predicate","Entities","Aliases","Answer","ShortAnswer","Conditions","Exclusions","RequiredContext","ProductIds","RiskLevel","SourceType","SourceId","Authority","Confidence","UseCount","AcceptedCount","CorrectionCount","WithdrawCount","Enabled","Status","CreatedAt","UpdatedAt","LastVerifiedAt"}
    clean=[]
    seen_ids=set()
    for item in records:
        if not isinstance(item,dict):
            raise HTTPException(status_code=400,detail="Knowledge V2 记录格式无效")
        row={k:v for k,v in item.items() if k in allowed}
        text_fields=("Type","Title","Intent","Subject","Predicate","Answer","ShortAnswer",
            "RiskLevel","SourceType","SourceId","Status","CreatedAt","UpdatedAt","LastVerifiedAt")
        for field in text_fields:
            value=row.get(field)
            if value is not None and not isinstance(value,str):
                raise HTTPException(status_code=400,detail=f"Knowledge V2 {field} 必须是文本")
        list_fields=("Entities","Aliases","Conditions","Exclusions","RequiredContext","ProductIds")
        for field in list_fields:
            value=row.get(field)
            if value is not None and (not isinstance(value,list) or any(not isinstance(x,str) for x in value)):
                raise HTTPException(status_code=400,detail=f"Knowledge V2 {field} 必须是文本数组")
        if "Enabled" in row and row["Enabled"] is not None and not isinstance(row["Enabled"],bool):
            raise HTTPException(status_code=400,detail="Knowledge V2 Enabled 必须是布尔值")
        title=row.get("Title")
        answer=row.get("Answer")
        if not isinstance(title,str) or not title.strip() or not isinstance(answer,str) or not answer.strip():
            raise HTTPException(status_code=400,detail="Knowledge V2 标题和答案必须是非空文本")
        if "Id" in row and row["Id"] is not None:
            record_id=row["Id"]
            if not isinstance(record_id,str) or not record_id.strip() or len(record_id)>160:
                raise HTTPException(status_code=400,detail="Knowledge V2 记录 ID 无效")
            record_id=record_id.strip()
            if record_id in seen_ids:
                raise HTTPException(status_code=400,detail="Knowledge V2 记录 ID 不允许重复")
            seen_ids.add(record_id)
            row["Id"]=record_id
        for field in ("Authority","Confidence"):
            value=row.get(field)
            if value is None:
                continue
            if isinstance(value,bool) or not isinstance(value,(int,float)):
                raise HTTPException(status_code=400,detail=f"Knowledge V2 {field} 必须是 0 到 1 之间的有限数值")
            # Range-check integers before float conversion: a legal JSON integer
            # with hundreds of digits otherwise raises OverflowError (HTTP 500).
            if value<0 or value>1:
                raise HTTPException(status_code=400,detail=f"Knowledge V2 {field} 必须是 0 到 1 之间的有限数值")
            number=float(value)
            if not math.isfinite(number) or number<0 or number>1:
                raise HTTPException(status_code=400,detail=f"Knowledge V2 {field} 必须是 0 到 1 之间的有限数值")
            row[field]=number
        for field in ("UseCount","AcceptedCount","CorrectionCount","WithdrawCount"):
            value=row.get(field)
            if value is None:
                continue
            if isinstance(value,bool) or not isinstance(value,int) or value<0 or value>2147483647:
                raise HTTPException(status_code=400,detail=f"Knowledge V2 {field} 必须是非负整数")
        clean.append(row)
    return clean

def _state(client_id):
    with _cp.db() as conn:
        row=conn.execute("SELECT revision,records_json,updated_at,updated_by FROM bot_knowledge_v2_state WHERE client_id=?",(client_id,)).fetchone()
    if not row:
        return {"revision":0,"records":[],"updated_at":"","updated_by":""}
    return {"revision":int(row["revision"] or 0),"records":json.loads(row["records_json"] or "[]"),"updated_at":row["updated_at"] or "","updated_by":row["updated_by"] or ""}

def _save(client_id, records, updated_by, expected_revision=None, raise_on_conflict=False):
    records=_validate_records(records)
    encoded=json.dumps(records,ensure_ascii=False,separators=(",",":"),sort_keys=True)
    now=_cp.iso_now()
    with _cp.db() as conn:
        # Serialize read/compare/write. A runtime request can otherwise read revision N,
        # race with a web edit to N+1, then overwrite that newer state using its stale N.
        conn.execute("BEGIN IMMEDIATE")
        row=conn.execute(
            "SELECT revision,records_json,updated_at,updated_by FROM bot_knowledge_v2_state WHERE client_id=?",
            (client_id,)
        ).fetchone()
        if not row:
            current={"revision":0,"records":[],"updated_at":"","updated_by":""}
        else:
            current={
                "revision":int(row["revision"] or 0),
                "records":json.loads(row["records_json"] or "[]"),
                "updated_at":row["updated_at"] or "",
                "updated_by":row["updated_by"] or ""
            }
        if expected_revision is not None and current["revision"] != expected_revision:
            if raise_on_conflict:
                raise HTTPException(
                    status_code=409,
                    detail={
                        "code": "revision_conflict",
                        "message": "Knowledge V2 云端版本已被另一设备修改，请刷新后重试",
                        "current_revision": current["revision"],
                    },
                )
            return current
        current_encoded=json.dumps(current["records"],ensure_ascii=False,separators=(",",":"),sort_keys=True)
        if encoded==current_encoded:
            return current
        revision=current["revision"]+1
        conn.execute("""INSERT INTO bot_knowledge_v2_state(client_id,revision,records_json,updated_at,updated_by)
          VALUES(?,?,?,?,?) ON CONFLICT(client_id) DO UPDATE SET revision=excluded.revision,records_json=excluded.records_json,updated_at=excluded.updated_at,updated_by=excluded.updated_by""",
          (client_id,revision,encoded,now,updated_by))
        return {"revision":revision,"records":records,"updated_at":now,"updated_by":updated_by}

@router.get("/api/bot-web/knowledge-v2")
def web_get(client=Depends(bot_web_console._web_client)):
    return _state(int(client["id"]))

@router.put("/api/bot-web/knowledge-v2")
def web_put(payload: KnowledgeV2WebInput, client=Depends(bot_web_console._web_client)):
    return _save(
        int(client["id"]),
        payload.records,
        "web",
        expected_revision=payload.expected_revision,
        raise_on_conflict=True,
    )

@router.post("/api/runtime/v1/bot-web/knowledge-v2-sync")
def runtime_sync(payload: KnowledgeV2SyncInput, request: Request, x_shop_key: str | None = Header(default=None)):
    client=bot_web_console._runtime_client(request)
    client_id=int(client["id"])
    shop_key=(x_shop_key or "").strip()
    if not shop_key:
        raise HTTPException(status_code=400,detail="缺少 X-Shop-Key")
    if payload.revision < 0:
        raise HTTPException(status_code=400,detail="revision 无效")
    current=_state(client_id)
    # First Windows report seeds cloud state. Later, a newer cloud revision wins and is
    # returned to Windows; an equal revision with changed local data is treated as a
    # Windows-originated update. This keeps the existing local repository authoritative
    # during first adoption without allowing stale clients to overwrite Web changes.
    if current["revision"] == 0 and payload.records is not None:
        return _save(client_id,payload.records,"windows",expected_revision=0)
    if current["revision"] > payload.revision:
        return current
    if payload.records is not None:
        # Runtime snapshots are full-corpus replacements. A physical disappearance
        # of an existing ID is not a valid delete: Windows deletion writes a tombstone
        # with the same ID (Enabled=false, Status=deleted). Reject an incomplete local
        # snapshot rather than letting a damaged/partially restored SQLite file erase
        # the cloud corpus. New records and explicit tombstones remain valid.
        if current["revision"] == payload.revision:
            current_ids = {
                str(item.get("Id") or "").strip()
                for item in current["records"]
                if isinstance(item, dict) and str(item.get("Id") or "").strip()
            }
            incoming_ids = {
                str(item.get("Id") or "").strip()
                for item in payload.records
                if isinstance(item, dict) and str(item.get("Id") or "").strip()
            }
            missing_ids = current_ids - incoming_ids
            if missing_ids:
                return {
                    **current,
                    "sync_warning": {
                        "code": "missing_tombstones",
                        "missing_count": len(missing_ids),
                    },
                }

            # A cloud delete tombstone is monotonic. A restored/older Windows
            # snapshot may still contain the same ID as an enabled record even
            # though its revision checkpoint was restored independently. Never
            # let that stale copy resurrect the deleted answer; the client will
            # merge the cloud tombstone and preserve the stale copy as a disabled
            # conflict candidate for operator review.
            cloud_deleted_ids = {
                str(item.get("Id") or "").strip()
                for item in current["records"]
                if isinstance(item, dict)
                and str(item.get("Id") or "").strip()
                and item.get("Enabled") is False
                and str(item.get("Status") or "").strip().lower() == "deleted"
            }
            incoming_by_id = {
                str(item.get("Id") or "").strip(): item
                for item in payload.records
                if isinstance(item, dict) and str(item.get("Id") or "").strip()
            }
            resurrected_ids = {
                record_id
                for record_id in cloud_deleted_ids
                if record_id in incoming_by_id
                and not (
                    incoming_by_id[record_id].get("Enabled") is False
                    and str(incoming_by_id[record_id].get("Status") or "").strip().lower() == "deleted"
                )
            }
            if resurrected_ids:
                return {
                    **current,
                    "sync_warning": {
                        "code": "cloud_delete_wins",
                        "conflict_count": len(resurrected_ids),
                    },
                }
        return _save(client_id,payload.records,"windows",expected_revision=payload.revision)
    return current


@router.post("/api/bot-web/knowledge-v2/smart-import")
def web_smart_import(payload: KnowledgeV2SmartImportInput, client=Depends(bot_web_console._web_client)):
    text=(payload.text or "").strip()
    if not text:
        raise HTTPException(status_code=400,detail="智能导入资料不能为空")
    if len(text)>20000:
        raise HTTPException(status_code=413,detail="智能导入文字最多 20000 字")
    timeout=max(15,min(180,int(payload.timeout_seconds or 90)))
    client_id=int(client["id"])
    with _cp.db() as conn:
        cursor=conn.execute("""INSERT INTO bot_commands(client_id,command_type,payload_json,status,result_json,error,created_at)
          VALUES(?,?,?,'pending','{}','',?)""",
          (client_id,"knowledge_v2_smart_import",json.dumps({"text":text,"timeout_seconds":timeout},ensure_ascii=False,separators=(",",":")),_cp.iso_now()))
        command_id=int(cursor.lastrowid)
    return {"ok":True,"command_id":command_id,"status":"pending"}

@router.get("/api/bot-web/knowledge-v2/smart-import/{command_id}")
def web_smart_import_status(command_id: int, client=Depends(bot_web_console._web_client)):
    with _cp.db() as conn:
        row=conn.execute("""SELECT status,result_json,error,created_at,completed_at FROM bot_commands
          WHERE id=? AND client_id=? AND command_type='knowledge_v2_smart_import'""",(command_id,int(client["id"]))).fetchone()
    if not row:
        raise HTTPException(status_code=404,detail="智能导入任务不存在")
    return {"command_id":command_id,"status":row["status"],"result":json.loads(row["result_json"] or "{}"),"error":row["error"] or "","created_at":row["created_at"],"completed_at":row["completed_at"]}


def _enqueue_v2_settings_command(client_id: int, command_type: str, payload: dict[str, Any] | None = None):
    with _cp.db() as conn:
        cursor=conn.execute("""INSERT INTO bot_commands(client_id,command_type,payload_json,status,result_json,error,created_at)
          VALUES(?,?,?,'pending','{}','',?)""",
          (client_id,command_type,json.dumps(payload or {},ensure_ascii=False,separators=(",",":")),_cp.iso_now()))
        return int(cursor.lastrowid)

@router.post("/api/bot-web/knowledge-v2/runtime-settings/read")
def web_v2_runtime_settings_read(client=Depends(bot_web_console._web_client)):
    command_id=_enqueue_v2_settings_command(int(client["id"]),"knowledge_v2_settings_get")
    return {"ok":True,"command_id":command_id,"status":"pending"}

@router.post("/api/bot-web/knowledge-v2/runtime-settings")
def web_v2_runtime_settings_save(payload: KnowledgeV2RuntimeSettingsInput, client=Depends(bot_web_console._web_client)):
    mode=(payload.mode or "").strip().lower()
    if mode != "production" or not payload.enabled:
        raise HTTPException(status_code=400,detail="Knowledge V2 是唯一知识库，始终启用且仅支持 production 模式")
    threshold=float(payload.direct_threshold)
    confidence=float(payload.min_confidence)
    if not math.isfinite(threshold) or not math.isfinite(confidence):
        raise HTTPException(status_code=400,detail="运行阈值必须是有限数值")
    if threshold<0.70 or threshold>0.96:
        raise HTTPException(status_code=400,detail="本地直答匹配阈值必须在 0.70~0.96")
    if confidence<0.50 or confidence>0.95:
        raise HTTPException(status_code=400,detail="最低知识可信度必须在 0.50~0.95")
    command_id=_enqueue_v2_settings_command(int(client["id"]),"knowledge_v2_settings_set",{
        "enabled":bool(payload.enabled),"mode":mode,
        "direct_threshold":round(threshold,3),"min_confidence":round(confidence,3)
    })
    return {"ok":True,"command_id":command_id,"status":"pending"}

@router.get("/api/bot-web/knowledge-v2/runtime-settings/{command_id}")
def web_v2_runtime_settings_status(command_id: int, client=Depends(bot_web_console._web_client)):
    with _cp.db() as conn:
        row=conn.execute("""SELECT command_type,status,result_json,error,created_at,completed_at FROM bot_commands
          WHERE id=? AND client_id=? AND command_type IN ('knowledge_v2_settings_get','knowledge_v2_settings_set')""",
          (command_id,int(client["id"]))).fetchone()
    if not row:
        raise HTTPException(status_code=404,detail="V2 运行设置任务不存在")
    return {"command_id":command_id,"command_type":row["command_type"],"status":row["status"],
      "result":json.loads(row["result_json"] or "{}"),"error":row["error"] or "",
      "created_at":row["created_at"],"completed_at":row["completed_at"]}


@router.get("/api/bot-web/knowledge-v2/runtime-settings/audit")
def web_v2_runtime_settings_audit(client=Depends(bot_web_console._web_client)):
    with _cp.db() as conn:
        rows=conn.execute("""SELECT id,command_type,status,payload_json,result_json,error,created_at,completed_at
          FROM bot_commands WHERE client_id=? AND command_type IN ('knowledge_v2_settings_get','knowledge_v2_settings_set')
          ORDER BY id DESC LIMIT 30""",(int(client["id"]),)).fetchall()
    items=[]
    for row in rows:
        payload=json.loads(row["payload_json"] or "{}")
        result=json.loads(row["result_json"] or "{}")
        safe={}
        source=result if row["status"]=="completed" else payload
        for key in ("enabled","mode","direct_threshold","min_confidence","shop_key"):
            if key in source: safe[key]=source[key]
        items.append({"id":int(row["id"]),"action":"保存设置" if row["command_type"]=="knowledge_v2_settings_set" else "读取设置",
          "status":row["status"],"settings":safe,"error":row["error"] or "",
          "created_at":row["created_at"],"completed_at":row["completed_at"]})
    return {"items":items}
