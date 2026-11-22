from __future__ import annotations

import json
import math
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
    if mode not in ("production","shadow"):
        raise HTTPException(status_code=400,detail="运行模式只能是 production 或 shadow")
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
