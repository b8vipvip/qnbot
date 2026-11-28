from __future__ import annotations

import json
import hashlib
import base64
import math
import time
import uuid
from typing import Any

from fastapi import APIRouter, Depends, Header, HTTPException, Request
from pydantic import BaseModel

import bot_web_console

router = APIRouter()
_cp = None
_MAX_RECORDS = 10000
_MAX_BODY_BYTES = 16 * 1024 * 1024

class KnowledgeV2SyncInput(BaseModel):
    revision: int = 0
    records: list[dict[str, Any]] | None = None

class KnowledgeV2FeedbackInput(BaseModel):
    epoch: str = ""
    cursor: int = 0
    events: list[dict[str, Any]] = []
    device_id: str = ""
    local_event_count: int | None = None
    local_event_digest: str = ""

class KnowledgeV2RevisionHistoryInput(BaseModel):
    epoch: str = ""
    cursor: int = 0
    rows: list[dict[str, Any]] = []
    device_id: str = ""
    local_revision_count: int | None = None
    local_revision_digest: str = ""

class KnowledgeV2WebInput(BaseModel):
    records: list[dict[str, Any]]
    expected_revision: int

class KnowledgeV2PackageInput(BaseModel):
    expected_revision: int
    mode: str
    replace_confirmed: bool = False
    package: dict[str, Any]

class KnowledgeV2RestoreInput(BaseModel):
    expected_revision: int
    restore_confirmed: bool = False

class KnowledgeV2OperationInput(BaseModel):
    action: str
    confirmed: bool = False
    candidate_id: str = ""
    knowledge_id: str = ""
    revision_id: str = ""
    fingerprint: str = ""
    fact_key: str = ""
    checkpoint_id: str = ""
    reason: str = ""
    max_contacts: int = 5000
    device_id: str = ""
    learn_knowledge: bool = False

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

        conn.execute("""CREATE TABLE IF NOT EXISTS bot_knowledge_v2_devices (
            client_id INTEGER NOT NULL, shop_key TEXT NOT NULL, device_id TEXT NOT NULL,
            last_seen_at TEXT NOT NULL, PRIMARY KEY(client_id,shop_key,device_id),
            FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE)""")
        conn.execute("""CREATE TABLE IF NOT EXISTS bot_knowledge_v2_feedback_receipts (
            client_id INTEGER NOT NULL, shop_key TEXT NOT NULL, device_id TEXT NOT NULL,
            event_count INTEGER NOT NULL, event_digest TEXT NOT NULL, cursor INTEGER NOT NULL,
            verified INTEGER NOT NULL, received_at TEXT NOT NULL,
            PRIMARY KEY(client_id,shop_key,device_id),
            FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE)""")
        conn.execute("""CREATE TABLE IF NOT EXISTS bot_knowledge_v2_backups (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            client_id INTEGER NOT NULL, revision INTEGER NOT NULL, records_json TEXT NOT NULL,
            created_at TEXT NOT NULL, reason TEXT NOT NULL,
            UNIQUE(client_id,revision), FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
        )""")
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
        conn.execute("""CREATE TABLE IF NOT EXISTS bot_knowledge_v2_revision_history (
            seq INTEGER PRIMARY KEY AUTOINCREMENT, client_id INTEGER NOT NULL,
            shop_key TEXT NOT NULL, revision_id TEXT NOT NULL, row_json TEXT NOT NULL,
            UNIQUE(client_id,shop_key,revision_id),
            FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE)""")
        conn.execute("CREATE INDEX IF NOT EXISTS ix_v2_revision_cursor ON bot_knowledge_v2_revision_history(client_id,shop_key,seq)")
        conn.execute("""CREATE TABLE IF NOT EXISTS bot_knowledge_v2_revision_receipts (
            client_id INTEGER NOT NULL, shop_key TEXT NOT NULL, device_id TEXT NOT NULL,
            row_count INTEGER NOT NULL, digest TEXT NOT NULL, verified INTEGER NOT NULL,
            received_at TEXT NOT NULL, PRIMARY KEY(client_id,shop_key,device_id),
            FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE)""")

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

def _feedback_digest(conn,client_id,shop_key,cutoff):
    rows=conn.execute("""SELECT event_id,seq,knowledge_id,event_type,utc_ticks FROM bot_knowledge_v2_feedback
        WHERE client_id=? AND shop_key=? AND utc_ticks>=? ORDER BY event_id""",(client_id,shop_key,cutoff)).fetchall()
    canonical=["\n".join((row["event_id"],base64.b64encode(row["knowledge_id"].encode("utf-8")).decode("ascii"),
        row["event_type"],str(row["utc_ticks"]))) for row in rows]
    digest=hashlib.sha256("\n".join(canonical).encode("utf-8")).hexdigest()
    return len(rows),digest,max((int(row["seq"]) for row in rows),default=0)

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
        # A proof is based on the device's already persisted cursor and local ID
        # digest, not just its HTTP success. Require an empty outgoing batch and
        # a registered device, so uploading an event cannot itself prove replay.
        if (payload.device_id and payload.local_event_count is not None and payload.local_event_count>=0
            and len(payload.local_event_digest)==64 and all(c in "0123456789abcdef" for c in payload.local_event_digest)
            and conn.execute("""SELECT 1 FROM bot_knowledge_v2_devices WHERE client_id=?
                AND shop_key=? AND device_id=?""",(client_id,shop_key,payload.device_id)).fetchone()):
            count,digest,highwater=_feedback_digest(conn,client_id,shop_key,cutoff)
            verified=int(not events and payload.epoch==epoch and payload.cursor>=highwater
                         and payload.local_event_count==count and payload.local_event_digest==digest)
            conn.execute("""INSERT INTO bot_knowledge_v2_feedback_receipts
                (client_id,shop_key,device_id,event_count,event_digest,cursor,verified,received_at)
                VALUES(?,?,?,?,?,?,?,?) ON CONFLICT(client_id,shop_key,device_id) DO UPDATE SET
                event_count=excluded.event_count,event_digest=excluded.event_digest,cursor=excluded.cursor,
                verified=excluded.verified,received_at=excluded.received_at""",
                (client_id,shop_key,payload.device_id,payload.local_event_count,payload.local_event_digest,
                 payload.cursor,verified,_cp.iso_now()))
        return {"epoch":epoch,"cursor":int(page[-1]["seq"]) if page else cursor,
            "has_more":len(rows)>200,"ack_ids":[e[0] for e in events],
            "events":[{"Id":row["event_id"],"KnowledgeId":row["knowledge_id"],"EventType":row["event_type"],
                       "UtcTicks":row["utc_ticks"]} for row in page]}

_REVISION_FIELDS = {"Id", "KnowledgeId", "MessageRecordId", "Action", "PreviousJson", "CurrentJson", "CreatedAtTicks"}
_REVISION_ACTIONS = {"apply", "rollback", "buyer_direct_apply", "buyer_direct_rollback", "delete", "enable", "disable"}

def _revision_rows(rows):
    if len(rows) > 20:
        raise HTTPException(status_code=413, detail="修订历史每批最多20条")
    result = []
    seen = set()
    for row in rows:
        if set(row) != _REVISION_FIELDS:
            raise HTTPException(status_code=400, detail="修订历史字段无效")
        rid, kid, action, ticks = (row[k] for k in ("Id", "KnowledgeId", "Action", "CreatedAtTicks"))
        if (not isinstance(rid,str) or len(rid)!=32 or any(c not in "0123456789abcdef" for c in rid)
            or rid in seen or not isinstance(kid,str) or not kid.strip() or len(kid)>160
            or not isinstance(action,str) or action not in _REVISION_ACTIONS
            or type(ticks) is not int or not 0<ticks<=3155378975999999999
            or not isinstance(row["MessageRecordId"],str) or len(row["MessageRecordId"])>16384):
            raise HTTPException(status_code=400, detail="修订历史内容无效")
        for field in ("PreviousJson","CurrentJson"):
            value=row[field]
            if not isinstance(value,str):
                raise HTTPException(status_code=400, detail="修订快照必须是文本")
            if value:
                try:
                    snapshot=json.loads(value)
                except (ValueError,TypeError):
                    raise HTTPException(status_code=400, detail="修订快照JSON无效")
                if snapshot is not None and (not isinstance(snapshot,dict) or snapshot.get("Id")!=kid):
                    raise HTTPException(status_code=400, detail="修订快照知识身份不匹配")
        encoded=json.dumps(row,ensure_ascii=False,sort_keys=True,separators=(",",":"))
        if len(encoded.encode("utf-8"))>256*1024:
            raise HTTPException(status_code=413, detail="单条修订历史超过256KB")
        seen.add(rid)
        result.append((rid,encoded))
    return result

def _revision_digest(conn,client_id,shop_key):
    rows=conn.execute("""SELECT revision_id,row_json,seq FROM bot_knowledge_v2_revision_history
        WHERE client_id=? AND shop_key=? ORDER BY revision_id""",(client_id,shop_key)).fetchall()
    digest=hashlib.sha256()
    fields=("KnowledgeId","MessageRecordId","Action","PreviousJson","CurrentJson")
    for i,row in enumerate(rows):
        data=json.loads(row["row_json"])
        parts=[row["revision_id"]]+[base64.b64encode(data[f].encode("utf-8")).decode("ascii") for f in fields]+[str(data["CreatedAtTicks"])]
        if i: digest.update(b"\n")
        digest.update("\n".join(parts).encode("utf-8"))
    return len(rows),digest.hexdigest(),max((row["seq"] for row in rows),default=0)

@router.post("/api/runtime/v1/bot-web/knowledge-v2-revision-sync")
def runtime_revision_sync(payload: KnowledgeV2RevisionHistoryInput, request: Request, x_shop_key: str | None = Header(default=None)):
    client_id=int(bot_web_console._runtime_client(request)["id"])
    if not x_shop_key or not 0<=payload.cursor<=9223372036854775807:
        raise HTTPException(status_code=400, detail="修订同步店铺或游标无效")
    incoming=_revision_rows(payload.rows)
    with _cp.db() as conn:
        conn.execute("BEGIN IMMEDIATE")
        shop_key=_feedback_binding(conn,client_id,x_shop_key.strip())
        seed=conn.execute("SELECT epoch FROM bot_knowledge_v2_feedback_epoch WHERE id=1").fetchone()["epoch"]
        epoch=uuid.uuid5(uuid.UUID(hex=seed),f"revision:{client_id}:{shop_key}").hex
        cursor=payload.cursor if payload.epoch==epoch else 0
        for rid,encoded in incoming:
            conn.execute("""INSERT OR IGNORE INTO bot_knowledge_v2_revision_history(client_id,shop_key,revision_id,row_json)
                VALUES(?,?,?,?)""",(client_id,shop_key,rid,encoded))
            prior=conn.execute("""SELECT row_json FROM bot_knowledge_v2_revision_history
                WHERE client_id=? AND shop_key=? AND revision_id=?""",(client_id,shop_key,rid)).fetchone()
            if prior["row_json"]!=encoded:
                raise HTTPException(status_code=409,detail="修订历史ID内容冲突")
        rows=conn.execute("""SELECT seq,row_json FROM bot_knowledge_v2_revision_history
            WHERE client_id=? AND shop_key=? AND seq>? ORDER BY seq LIMIT 21""",(client_id,shop_key,cursor)).fetchall()
        page=rows[:20]
        if (payload.device_id and payload.local_revision_count is not None
            and payload.local_revision_count>=0 and len(payload.local_revision_digest)==64
            and all(c in "0123456789abcdef" for c in payload.local_revision_digest)
            and conn.execute("""SELECT 1 FROM bot_knowledge_v2_devices
                WHERE client_id=? AND shop_key=? AND device_id=?""",
                (client_id,shop_key,payload.device_id)).fetchone()):
            count,digest,highwater=_revision_digest(conn,client_id,shop_key)
            verified=int(not incoming and payload.epoch==epoch and payload.cursor==highwater
                         and payload.local_revision_count==count and payload.local_revision_digest==digest)
            conn.execute("""INSERT INTO bot_knowledge_v2_revision_receipts
                (client_id,shop_key,device_id,row_count,digest,verified,received_at)
                VALUES(?,?,?,?,?,?,?) ON CONFLICT(client_id,shop_key,device_id) DO UPDATE SET
                row_count=excluded.row_count,digest=excluded.digest,verified=excluded.verified,
                received_at=excluded.received_at""",(client_id,shop_key,payload.device_id,
                payload.local_revision_count,payload.local_revision_digest,verified,_cp.iso_now()))
        return {"epoch":epoch,"cursor":int(page[-1]["seq"]) if page else cursor,
                "has_more":len(rows)>20,"ack_ids":[rid for rid,_ in incoming],
                "rows":[json.loads(row["row_json"]) for row in page]}

@router.get("/api/bot-web/knowledge-v2/revision-sync-status")
def web_revision_sync_status(client=Depends(bot_web_console._web_client)):
    from datetime import datetime,timezone,timedelta
    client_id=int(client["id"])
    with _cp.db() as conn:
        shop_key=_feedback_binding(conn,client_id)
        count,digest,_=_revision_digest(conn,client_id,shop_key)
        rows=conn.execute("""SELECT d.device_id,d.last_seen_at,r.row_count,r.digest,r.verified,r.received_at
            FROM bot_knowledge_v2_devices d LEFT JOIN bot_knowledge_v2_revision_receipts r
            ON d.client_id=r.client_id AND d.shop_key=r.shop_key AND d.device_id=r.device_id
            WHERE d.client_id=? AND d.shop_key=?""",(client_id,shop_key)).fetchall()
    cutoff=datetime.now(timezone.utc)-timedelta(minutes=5)
    def fresh(value):
        try:
            stamp=datetime.fromisoformat(value)
            return (stamp if stamp.tzinfo else stamp.replace(tzinfo=timezone.utc))>=cutoff
        except (ValueError,TypeError):
            return False
    items=[{"id":r["device_id"],"revision_count":r["row_count"],
        "verified":bool(r["verified"] and r["row_count"]==count and r["digest"]==digest
            and fresh(r["received_at"]) and fresh(r["last_seen_at"])),
        "receipt_at":r["received_at"]} for r in rows]
    verified=sum(1 for item in items if item["verified"])
    return {"server_revision_count":count,"verified_devices":verified,
        "two_device_nonempty_convergence":count>0 and verified>=2,"items":items}

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
        raise HTTPException(status_code=400, detail="Knowledge V2 记录最多 10000 条")
    encoded=json.dumps(records,ensure_ascii=False,separators=(",",":"))
    if len(encoded.encode("utf-8")) > _MAX_BODY_BYTES:
        raise HTTPException(status_code=413, detail="Knowledge V2 数据超过 16MB")
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
    if any(not row.get("Id") for row in records):
        raise HTTPException(status_code=400,detail="Knowledge V2 每条记录必须具有稳定ID")
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
        if updated_by == "web":
            # Older Web clients omitted deleted rows. Materialize the same durable
            # tombstone as Windows instead of permitting physical removal.
            by_id={x["Id"]:x for x in records}
            for old in current["records"]:
                incoming=by_id.get(old["Id"])
                if old.get("Status","").lower()=="deleted" and old.get("Enabled") is False:
                    if incoming is not None and (incoming.get("Status","").lower()!="deleted" or incoming.get("Enabled") is not False):
                        raise HTTPException(status_code=409,detail="已删除的知识ID不能复活")
                    by_id[old["Id"]]=old
                elif incoming is None:
                    by_id[old["Id"]]=dict(old,Enabled=False,Status="deleted",UpdatedAt=now)
            records=_validate_records(list(by_id.values()))
            encoded=json.dumps(records,ensure_ascii=False,separators=(",",":"),sort_keys=True)
        current_encoded=json.dumps(current["records"],ensure_ascii=False,separators=(",",":"),sort_keys=True)
        if encoded==current_encoded:
            return current
        revision=current["revision"]+1
        if updated_by=="web":
            conn.execute("""INSERT OR IGNORE INTO bot_knowledge_v2_backups(client_id,revision,records_json,created_at,reason)
                VALUES(?,?,?,?,?)""",(client_id,current["revision"],current_encoded,now,"before_web_write"))
            conn.execute("""DELETE FROM bot_knowledge_v2_backups WHERE client_id=? AND id NOT IN
                (SELECT id FROM bot_knowledge_v2_backups WHERE client_id=? ORDER BY id DESC LIMIT 30)""",(client_id,client_id))
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

def _package_records(package):
    if type(package.get("SchemaVersion")) is not int or package["SchemaVersion"]!=2 or not isinstance(package.get("Records"),list):
        raise HTTPException(status_code=400,detail="请选择 SchemaVersion=2 的结构化知识包")
    if package.get("Settings") is not None or package.get("FeedbackEvents"):
        raise HTTPException(status_code=400,detail="含检索设置或私人反馈的完整包请在原店铺Windows恢复")
    rows=_validate_records(package["Records"])
    if any(not row.get("Id") for row in rows):
        raise HTTPException(status_code=400,detail="知识包记录缺少稳定ID")
    return rows

def _deleted(row):
    return row.get("Enabled") is False and str(row.get("Status") or "").lower()=="deleted"

def _merge_web_package(current, incoming, mode):
    old={r["Id"]:r for r in current}
    if mode=="replace":
        # The ordinary web CAS writer adds missing tombstones; an older backup
        # cannot resurrect an explicitly deleted ID.
        return [old[r["Id"]] if r["Id"] in old and _deleted(old[r["Id"]]) else r for r in incoming]
    result=dict(old)
    metadata={"CreatedAt","UpdatedAt","LastVerifiedAt","UseCount","AcceptedCount","CorrectionCount","WithdrawCount"}
    def content(r):
        return {k:v for k,v in r.items() if k not in metadata}
    for r in incoming:
        rid=r["Id"]
        if rid not in result:
            result[rid]=r
        elif mode=="merge" and not _deleted(result[rid]) and content(result[rid])!=content(r):
            # Preserve an incoming explicit deletion instead of making it an active conflict.
            if _deleted(r):
                result[rid]=r
                continue
            digest=hashlib.sha256(json.dumps(content(r),sort_keys=True,ensure_ascii=False,separators=(",",":")).encode("utf-8")).hexdigest()[:24]
            cid="web-conflict-"+digest
            result.setdefault(cid,dict(r,Id=cid,SourceType="cloud_sync_conflict_candidate",SourceId=rid,Enabled=False,Status="candidate"))
    return list(result.values())

@router.get("/api/bot-web/knowledge-v2/export")
def web_v2_export(client=Depends(bot_web_console._web_client)):
    state=_state(int(client["id"]))
    return {"SchemaVersion":2,"ExportedAt":_cp.iso_now(),"Records":state["records"],"Settings":None,"FeedbackEvents":None}

@router.post("/api/bot-web/knowledge-v2/import")
def web_v2_import(payload: KnowledgeV2PackageInput,client=Depends(bot_web_console._web_client)):
    if payload.mode not in ("replace","merge","append") or (payload.mode=="replace" and not payload.replace_confirmed):
        raise HTTPException(status_code=400,detail="导入模式无效或未确认替换")
    rows=_package_records(payload.package)
    client_id=int(client["id"])
    current=_state(client_id)
    merged=_merge_web_package(current["records"],rows,payload.mode)
    return _save(client_id,merged,"web",expected_revision=payload.expected_revision,raise_on_conflict=True)

@router.get("/api/bot-web/knowledge-v2/backups")
def web_v2_backups(limit:int=30,client=Depends(bot_web_console._web_client)):
    with _cp.db() as conn:
        rows=conn.execute("""SELECT id,revision,created_at,reason,length(CAST(records_json AS BLOB)) AS bytes
            FROM bot_knowledge_v2_backups WHERE client_id=? ORDER BY id DESC LIMIT ?""",
            (int(client["id"]),max(1,min(30,limit)))).fetchall()
    return {"backups":[dict(row) for row in rows]}

@router.post("/api/bot-web/knowledge-v2/backups/{backup_id}/restore")
def web_v2_restore(backup_id:int,payload:KnowledgeV2RestoreInput,client=Depends(bot_web_console._web_client)):
    if not payload.restore_confirmed:
        raise HTTPException(status_code=400,detail="必须明确确认恢复")
    client_id=int(client["id"])
    with _cp.db() as conn:
        row=conn.execute("SELECT revision,records_json FROM bot_knowledge_v2_backups WHERE id=? AND client_id=?",(backup_id,client_id)).fetchone()
    if not row:
        raise HTTPException(status_code=404,detail="本店知识备份不存在")
    current=_state(client_id)
    merged=_merge_web_package(current["records"],json.loads(row["records_json"]),"replace")
    result=_save(client_id,merged,"web",expected_revision=payload.expected_revision,raise_on_conflict=True)
    return dict(result,restored_from_revision=row["revision"])

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


_OPERATION_ACTIONS = {"history_status","history_scan","history_resume","revision_generate","revision_list",
    "revision_apply","revision_reject","conflict_list","conflict_resolve","rollback_list","rollback"}
_OPERATION_MUTATIONS = {"history_scan","history_resume","revision_generate","revision_apply","revision_reject","conflict_resolve","rollback"}

@router.get("/api/bot-web/knowledge-v2/devices")
def web_v2_devices(client=Depends(bot_web_console._web_client)):
    from datetime import datetime,timezone,timedelta
    with _cp.db() as conn:
        shop_key=_feedback_binding(conn,int(client["id"]))
        client_id=int(client["id"])
        rows=conn.execute("""SELECT d.device_id,d.last_seen_at,r.event_count,r.event_digest,r.verified,r.received_at
            FROM bot_knowledge_v2_devices d LEFT JOIN bot_knowledge_v2_feedback_receipts r
            ON d.client_id=r.client_id AND d.shop_key=r.shop_key AND d.device_id=r.device_id
            WHERE d.client_id=? AND d.shop_key=? ORDER BY d.last_seen_at DESC LIMIT 30""",(client_id,shop_key)).fetchall()
        count,digest,_=_feedback_digest(conn,client_id,shop_key,_feedback_now_ticks()-180*86400*_TICKS_PER_SECOND)
    cutoff=datetime.now(timezone.utc)-timedelta(seconds=90)
    result=[]
    for row in rows:
        try:
            seen=datetime.fromisoformat(row["last_seen_at"])
            if seen.tzinfo is None: seen=seen.replace(tzinfo=timezone.utc)
            online=seen>=cutoff
        except (ValueError,TypeError):
            online=False
        try:
            receipt=datetime.fromisoformat(row["received_at"])
            if receipt.tzinfo is None: receipt=receipt.replace(tzinfo=timezone.utc)
            fresh=receipt>=cutoff
        except (ValueError,TypeError):
            fresh=False
        verified=bool(online and fresh and row["verified"] and row["event_count"]==count and row["event_digest"]==digest)
        result.append({"id":row["device_id"],"last_seen_at":row["last_seen_at"],"online":online,
            "feedback_verified":verified,"feedback_event_count":row["event_count"],"feedback_receipt_at":row["received_at"]})
    return {"items":result,"server_feedback_event_count":count,
        "verified_devices":sum(1 for item in result if item["feedback_verified"]),
        "two_device_nonempty_convergence":count>0 and sum(1 for item in result if item["feedback_verified"])>=2}

@router.post("/api/bot-web/knowledge-v2/operations")
def web_v2_operation(payload: KnowledgeV2OperationInput,client=Depends(bot_web_console._web_client)):
    action=payload.action
    if action not in _OPERATION_ACTIONS or (action in _OPERATION_MUTATIONS and not payload.confirmed):
        raise HTTPException(status_code=400,detail="操作无效或未明确确认")
    for value in (payload.candidate_id,payload.knowledge_id,payload.revision_id,payload.checkpoint_id,payload.reason,payload.fact_key,payload.fingerprint):
        if len(value)>1000:
            raise HTTPException(status_code=400,detail="操作参数过长")
    if not 1<=payload.max_contacts<=5000:
        raise HTTPException(status_code=400,detail="扫描联系人范围无效")
    if action in {"revision_apply","revision_reject"} and not payload.candidate_id.strip():
        raise HTTPException(status_code=400,detail="缺少修订候选ID")
    if action=="history_resume" and not payload.checkpoint_id.strip():
        raise HTTPException(status_code=400,detail="缺少历史扫描断点ID")
    if action in {"conflict_resolve","rollback"} and (not payload.knowledge_id.strip() or not payload.fingerprint.strip()):
        raise HTTPException(status_code=400,detail="缺少选中知识或版本指纹")
    if action=="conflict_resolve" and not payload.fact_key.strip():
        raise HTTPException(status_code=400,detail="缺少冲突事实键")
    if action=="rollback" and not payload.revision_id.strip():
        raise HTTPException(status_code=400,detail="缺少目标修订ID")
    client_id=int(client["id"])
    with _cp.db() as conn:
        conn.execute("BEGIN IMMEDIATE")
        shop_key=_feedback_binding(conn,client_id)
        if not payload.device_id or not conn.execute("""SELECT 1 FROM bot_knowledge_v2_devices WHERE client_id=?
            AND shop_key=? AND device_id=?""",(client_id,shop_key,payload.device_id)).fetchone():
            raise HTTPException(status_code=400,detail="请选择本店已注册的Windows设备")
        active=conn.execute("""SELECT id FROM bot_commands WHERE client_id=? AND command_type='knowledge_v2_operation'
            AND status IN ('pending','running','cancel_requested') ORDER BY id DESC LIMIT 1""",(client_id,)).fetchone()
        if active:
            raise HTTPException(status_code=409,detail={"message":"本店已有知识操作正在处理","command_id":active["id"]})
        data=payload.model_dump() if hasattr(payload,"model_dump") else payload.dict()
        data["shop_key"]=shop_key
        row=conn.execute("""INSERT INTO bot_commands(client_id,command_type,payload_json,status,result_json,error,created_at)
            VALUES(?, 'knowledge_v2_operation', ?, 'pending', '{}', '', ?)""",
            (client_id,json.dumps(data,ensure_ascii=False),_cp.iso_now()))
        return {"command_id":int(row.lastrowid),"status":"pending"}

@router.get("/api/bot-web/knowledge-v2/operations")
def web_v2_operations(client=Depends(bot_web_console._web_client)):
    with _cp.db() as conn:
        rows=conn.execute("""SELECT id,status,payload_json,created_at,completed_at FROM bot_commands WHERE client_id=?
            AND command_type='knowledge_v2_operation' ORDER BY id DESC LIMIT 30""",(int(client["id"]),)).fetchall()
    return {"items":[{"command_id":r["id"],"status":r["status"],"action":json.loads(r["payload_json"] or "{}").get("action"),
        "device_id":json.loads(r["payload_json"] or "{}").get("device_id"),
        "created_at":r["created_at"],"updated_at":r["completed_at"]} for r in rows]}

@router.get("/api/bot-web/knowledge-v2/operations/{command_id}")
def web_v2_operation_status(command_id:int,client=Depends(bot_web_console._web_client)):
    with _cp.db() as conn:
        row=conn.execute("""SELECT status,result_json,error,payload_json,created_at,completed_at FROM bot_commands
            WHERE id=? AND client_id=? AND command_type='knowledge_v2_operation'""",(command_id,int(client["id"]))).fetchone()
    if not row:
        raise HTTPException(status_code=404,detail="知识操作不存在")
    return {"command_id":command_id,"status":row["status"],"result":json.loads(row["result_json"] or "{}"),
        "error":row["error"] or "","action":json.loads(row["payload_json"] or "{}").get("action"),
        "device_id":json.loads(row["payload_json"] or "{}").get("device_id"),
        "created_at":row["created_at"],"updated_at":row["completed_at"]}

@router.post("/api/bot-web/knowledge-v2/operations/{command_id}/cancel")
def web_v2_operation_cancel(command_id:int,client=Depends(bot_web_console._web_client)):
    with _cp.db() as conn:
        conn.execute("BEGIN IMMEDIATE")
        row=conn.execute("""SELECT status,payload_json FROM bot_commands WHERE id=? AND client_id=?
            AND command_type='knowledge_v2_operation'""",(command_id,int(client["id"]))).fetchone()
        if not row:
            raise HTTPException(status_code=404,detail="知识操作不存在")
        if row["status"]=="pending":
            status="cancelled"
        elif row["status"] in ("running","cancel_requested") and json.loads(row["payload_json"]).get("action") in ("history_scan","history_resume","revision_generate"):
            status="cancel_requested"
        else:
            raise HTTPException(status_code=409,detail="操作已结束或不可中断")
        conn.execute("UPDATE bot_commands SET status=?,completed_at=? WHERE id=? AND client_id=?",
            (status,_cp.iso_now(),command_id,int(client["id"])))
    return {"command_id":command_id,"status":status}

@router.post("/api/bot-web/knowledge-v2/operations/{command_id}/abandon")
def web_v2_operation_abandon(command_id:int,client=Depends(bot_web_console._web_client)):
    # A lost dispatch/desktop restart is never automatically replayed: a mutation
    # might have committed. After a long heartbeat gap the operator may abandon
    # the unknown result and inspect current state before starting a new operation.
    from datetime import datetime,timezone,timedelta
    with _cp.db() as conn:
        conn.execute("BEGIN IMMEDIATE")
        row=conn.execute("""SELECT status,completed_at FROM bot_commands WHERE id=? AND client_id=?
            AND command_type='knowledge_v2_operation'""",(command_id,int(client["id"]))).fetchone()
        if not row or row["status"] not in ("running","cancel_requested"):
            raise HTTPException(status_code=409,detail="没有可放弃的执行中任务")
        try:
            last=datetime.fromisoformat(row["completed_at"])
            if last.tzinfo is None:
                last=last.replace(tzinfo=timezone.utc)
        except (TypeError,ValueError):
            raise HTTPException(status_code=409,detail="无法确定任务最后心跳")
        if datetime.now(timezone.utc)-last<timedelta(minutes=30):
            raise HTTPException(status_code=409,detail="最近30分钟内仍有领取或执行心跳，禁止放弃")
        conn.execute("UPDATE bot_commands SET status='abandoned',error=?,completed_at=? WHERE id=? AND client_id=?",
            ("执行结果未知，请检查当前知识或原设备断点",_cp.iso_now(),command_id,int(client["id"])))
    return {"command_id":command_id,"status":"abandoned"}

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
