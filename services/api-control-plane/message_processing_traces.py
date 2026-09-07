from __future__ import annotations

import csv
import io
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional, Tuple

from fastapi import APIRouter, Depends, HTTPException, Query, Request, Response
from pydantic import BaseModel, Field

import bot_client_shop_binding
import bot_web_console as core


router = APIRouter()
_cp: Any = None
_BEIJING_OFFSET_HOURS = 8
_WINDOW_DELTAS = {
    "2h": timedelta(hours=2),
    "6h": timedelta(hours=6),
    "1d": timedelta(days=1),
    "3d": timedelta(days=3),
    "7d": timedelta(days=7),
    "14d": timedelta(days=14),
}


class TraceEventInput(BaseModel):
    event_id: str = Field(default="", max_length=80)
    trace_id: str = Field(default="", max_length=80)
    seller: str = Field(default="", max_length=160)
    buyer: str = Field(default="", max_length=160)
    stage: str = Field(default="", max_length=80)
    status: str = Field(default="", max_length=40)
    summary: str = Field(default="", max_length=300)
    detail: str = Field(default="", max_length=2000)
    duration_ms: int = Field(default=0, ge=0, le=3_600_000)
    occurred_at: str = Field(default="", max_length=80)


class TraceBatchInput(BaseModel):
    events: List[TraceEventInput] = Field(default_factory=list)


def install(control_plane: Any) -> None:
    global _cp
    _cp = control_plane
    control_plane.app.include_router(router)


def _require_admin(request: Request) -> str:
    # FastAPI injects Request only when the parameter carries the Request annotation.
    # An untyped anonymous request parameter is treated as a required query parameter
    # and makes the console endpoint return 422 before authentication runs.
    return _cp.require_admin(request)


def init_db() -> None:
    with _cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS bot_message_processing_traces (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_id INTEGER NOT NULL,
                shop_key TEXT NOT NULL,
                event_id TEXT NOT NULL,
                trace_id TEXT NOT NULL,
                seller TEXT NOT NULL DEFAULT '',
                buyer TEXT NOT NULL DEFAULT '',
                stage TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT '',
                summary TEXT NOT NULL DEFAULT '',
                detail TEXT NOT NULL DEFAULT '',
                duration_ms INTEGER NOT NULL DEFAULT 0,
                occurred_at TEXT NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(client_id, event_id),
                FOREIGN KEY(client_id) REFERENCES client_tokens(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_bot_processing_trace_client
            ON bot_message_processing_traces(client_id, id DESC);

            CREATE INDEX IF NOT EXISTS idx_bot_processing_trace_shop
            ON bot_message_processing_traces(shop_key, id DESC);

            CREATE INDEX IF NOT EXISTS idx_bot_processing_trace_conversation
            ON bot_message_processing_traces(client_id, shop_key, seller, buyer, id DESC);

            CREATE INDEX IF NOT EXISTS idx_bot_processing_trace_trace_id
            ON bot_message_processing_traces(client_id, trace_id, id ASC);
            """
        )


def _safe(value: Any, limit: int) -> str:
    text = str(value or "").replace("\x00", "").replace("\r", " ").replace("\n", " ").strip()
    while "  " in text:
        text = text.replace("  ", " ")
    return text if len(text) <= limit else text[:limit] + "..."


def _binding_shop_key(client_id: int) -> str:
    with _cp.db() as conn:
        row = conn.execute(
            "SELECT shop_key FROM bot_client_shop_binding WHERE client_id=?",
            (client_id,),
        ).fetchone()
    return _safe(row["shop_key"] if row else "", 160)


def _cleanup(client_id: int) -> None:
    threshold = (datetime.now(timezone.utc) - timedelta(days=14)).isoformat(timespec="seconds")
    with _cp.db() as conn:
        conn.execute(
            "DELETE FROM bot_message_processing_traces WHERE client_id=? AND occurred_at<?",
            (client_id, threshold),
        )
        row = conn.execute(
            "SELECT COUNT(*) c FROM bot_message_processing_traces WHERE client_id=?",
            (client_id,),
        ).fetchone()
        count = int(row["c"] if row else 0)
        if count > 20_000:
            conn.execute(
                """
                DELETE FROM bot_message_processing_traces
                WHERE client_id=? AND id NOT IN (
                    SELECT id FROM bot_message_processing_traces
                    WHERE client_id=? ORDER BY id DESC LIMIT 20000
                )
                """,
                (client_id, client_id),
            )


def _trace_filters(
    client_id: int = 0,
    shop_key: str = "",
    seller: str = "",
    buyer: str = "",
    status: str = "",
    trace_id: str = "",
    recent_since: Optional[str] = None,
) -> Tuple[List[str], List[Any]]:
    where: List[str] = []
    values: List[Any] = []
    if client_id > 0:
        where.append("t.client_id=?")
        values.append(client_id)
    if shop_key.strip():
        where.append("t.shop_key=?")
        values.append(shop_key.strip())
    if seller.strip():
        where.append("t.seller LIKE ?")
        values.append("%" + seller.strip() + "%")
    if buyer.strip():
        where.append("t.buyer LIKE ?")
        values.append("%" + buyer.strip() + "%")
    if status.strip():
        where.append("t.status=?")
        values.append(status.strip())
    if trace_id.strip():
        where.append("t.trace_id=?")
        values.append(trace_id.strip())
    if recent_since:
        where.append("datetime(t.occurred_at) >= datetime(?)")
        values.append(recent_since)
    return where, values


def _window_threshold(window: str) -> str:
    key = (window or "").strip().lower()
    delta = _WINDOW_DELTAS.get(key)
    if delta is None:
        raise HTTPException(
            status_code=400,
            detail="时间范围仅支持 2h、6h、1d、3d、7d、14d",
        )
    return (datetime.now(timezone.utc) - delta).isoformat(timespec="seconds")


def _beijing_day_expr(alias: str = "t") -> str:
    return (
        "date(COALESCE(datetime(" + alias + ".occurred_at), "
        "datetime(" + alias + ".created_at)), '+8 hours')"
    )


def _beijing_time(value: str) -> str:
    text = str(value or "").strip()
    if not text:
        return ""
    try:
        parsed = datetime.fromisoformat(text.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        target = parsed.astimezone(timezone(timedelta(hours=_BEIJING_OFFSET_HOURS)))
        return target.strftime("%Y-%m-%d %H:%M:%S")
    except Exception:
        return text


@router.post("/api/runtime/v1/message-processing-traces/batch")
def runtime_trace_batch(data: TraceBatchInput, request: Request) -> Dict[str, Any]:
    client = core._runtime_client(request)
    client_id = int(client["id"])
    shop_key = _safe(request.headers.get("x-shop-key") or "", 160)
    if not shop_key:
        raise HTTPException(status_code=400, detail="缺少 X-Shop-Key")
    bot_client_shop_binding.ensure_binding(client_id, shop_key, False, "")

    saved = 0
    now = _cp.iso_now()
    with _cp.db() as conn:
        for event in data.events[:500]:
            event_id = _safe(event.event_id, 80)
            trace_id = _safe(event.trace_id, 80)
            if not event_id or not trace_id:
                continue
            occurred_at = _safe(event.occurred_at or now, 80)
            cursor = conn.execute(
                """
                INSERT OR IGNORE INTO bot_message_processing_traces(
                    client_id,shop_key,event_id,trace_id,seller,buyer,stage,status,
                    summary,detail,duration_ms,occurred_at,created_at
                ) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)
                """,
                (
                    client_id,
                    shop_key,
                    event_id,
                    trace_id,
                    _safe(event.seller, 160),
                    _safe(event.buyer, 160),
                    _safe(event.stage, 80),
                    _safe(event.status, 40),
                    _safe(event.summary, 300),
                    _safe(event.detail, 2000),
                    max(0, int(event.duration_ms or 0)),
                    occurred_at,
                    now,
                ),
            )
            if cursor.rowcount > 0:
                saved += 1
    _cleanup(client_id)
    return {"ok": True, "saved": saved, "shop_key": shop_key}


@router.get("/api/admin/message-processing-traces")
def admin_message_processing_traces(
    client_id: int = Query(0, ge=0),
    shop_key: str = Query("", max_length=160),
    seller: str = Query("", max_length=160),
    buyer: str = Query("", max_length=160),
    status: str = Query("", max_length=40),
    trace_id: str = Query("", max_length=80),
    limit: int = Query(300, ge=1, le=1000),
    _: str = Depends(_require_admin),
) -> List[Dict[str, Any]]:
    # Compatibility/raw audit endpoint. The console now uses the grouped conversation endpoint,
    # but retaining this route preserves diagnostics and integrations that need every stage event.
    where, values = _trace_filters(client_id, shop_key, seller, buyer, status, trace_id)
    sql = """
        SELECT t.*, c.name client_name
        FROM bot_message_processing_traces t
        JOIN client_tokens c ON c.id=t.client_id
    """
    if where:
        sql += " WHERE " + " AND ".join(where)
    sql += " ORDER BY t.id DESC LIMIT ?"
    values.append(limit)
    with _cp.db() as conn:
        rows = conn.execute(sql, tuple(values)).fetchall()
    return [dict(row) for row in rows]


@router.get("/api/admin/message-processing-conversations")
def admin_message_processing_conversations(
    client_id: int = Query(0, ge=0),
    shop_key: str = Query("", max_length=160),
    seller: str = Query("", max_length=160),
    buyer: str = Query("", max_length=160),
    status: str = Query("", max_length=40),
    trace_id: str = Query("", max_length=80),
    limit: int = Query(300, ge=1, le=1000),
    _: str = Depends(_require_admin),
) -> List[Dict[str, Any]]:
    where, values = _trace_filters(client_id, shop_key, seller, buyer, status, trace_id)
    day_expr = _beijing_day_expr("t")
    filtered_where = " WHERE " + " AND ".join(where) if where else ""
    sql = f"""
        WITH grouped AS (
            SELECT
                t.client_id,
                t.shop_key,
                t.seller,
                t.buyer,
                {day_expr} AS conversation_date,
                MIN(t.occurred_at) AS first_at,
                MAX(t.occurred_at) AS last_at,
                COUNT(*) AS event_count,
                SUM(CASE WHEN t.status='failed' THEN 1 ELSE 0 END) AS failed_count,
                SUM(CASE WHEN t.status='success' THEN 1 ELSE 0 END) AS success_count,
                COUNT(DISTINCT t.trace_id) AS trace_count,
                MAX(t.id) AS latest_id
            FROM bot_message_processing_traces t
            {filtered_where}
            GROUP BY t.client_id, t.shop_key, t.seller, t.buyer, conversation_date
        )
        SELECT
            g.*,
            c.name AS client_name,
            latest.trace_id AS latest_trace_id,
            latest.stage AS latest_stage,
            latest.status AS latest_status,
            latest.summary AS latest_summary,
            latest.detail AS latest_detail
        FROM grouped g
        JOIN client_tokens c ON c.id=g.client_id
        JOIN bot_message_processing_traces latest ON latest.id=g.latest_id
        WHERE g.conversation_date IS NOT NULL
        ORDER BY g.latest_id DESC
        LIMIT ?
    """
    values.append(limit)
    with _cp.db() as conn:
        rows = conn.execute(sql, tuple(values)).fetchall()
    return [dict(row) for row in rows]


@router.get("/api/admin/message-processing-conversations/detail")
def admin_message_processing_conversation_detail(
    client_id: int = Query(..., ge=1),
    shop_key: str = Query(..., min_length=1, max_length=160),
    seller: str = Query("", max_length=160),
    buyer: str = Query(..., min_length=1, max_length=160),
    conversation_date: str = Query(..., min_length=10, max_length=10),
    _: str = Depends(_require_admin),
) -> Dict[str, Any]:
    try:
        datetime.strptime(conversation_date, "%Y-%m-%d")
    except ValueError:
        raise HTTPException(status_code=400, detail="conversation_date 必须为 YYYY-MM-DD")

    day_expr = _beijing_day_expr("t")
    with _cp.db() as conn:
        rows = conn.execute(
            f"""
            SELECT t.*, c.name client_name
            FROM bot_message_processing_traces t
            JOIN client_tokens c ON c.id=t.client_id
            WHERE t.client_id=? AND t.shop_key=? AND t.seller=? AND t.buyer=?
              AND {day_expr}=?
            ORDER BY datetime(t.occurred_at) ASC, t.id ASC
            """,
            (client_id, shop_key.strip(), seller.strip(), buyer.strip(), conversation_date),
        ).fetchall()
    return {
        "client_id": client_id,
        "shop_key": shop_key.strip(),
        "seller": seller.strip(),
        "buyer": buyer.strip(),
        "conversation_date": conversation_date,
        "events": [dict(row) for row in rows],
    }


@router.get("/api/admin/message-processing-conversations/export")
def admin_message_processing_conversation_export(
    window: str = Query("1d", max_length=8),
    client_id: int = Query(0, ge=0),
    shop_key: str = Query("", max_length=160),
    seller: str = Query("", max_length=160),
    buyer: str = Query("", max_length=160),
    status: str = Query("", max_length=40),
    _: str = Depends(_require_admin),
) -> Response:
    since = _window_threshold(window)
    where, values = _trace_filters(
        client_id, shop_key, seller, buyer, status, "", recent_since=since
    )
    sql = """
        SELECT t.*, c.name client_name
        FROM bot_message_processing_traces t
        JOIN client_tokens c ON c.id=t.client_id
    """
    if where:
        sql += " WHERE " + " AND ".join(where)
    sql += " ORDER BY t.buyer ASC, datetime(t.occurred_at) ASC, t.id ASC"
    with _cp.db() as conn:
        rows = [dict(row) for row in conn.execute(sql, tuple(values)).fetchall()]

    output = io.StringIO()
    writer = csv.writer(output, lineterminator="\n")
    writer.writerow(
        [
            "北京时间",
            "客户端ID",
            "客户端名称",
            "ShopKey",
            "客服",
            "买家",
            "链路ID",
            "阶段",
            "状态",
            "摘要",
            "详情",
            "耗时ms",
        ]
    )
    for row in rows:
        writer.writerow(
            [
                _beijing_time(row.get("occurred_at") or row.get("created_at") or ""),
                row.get("client_id", ""),
                row.get("client_name", ""),
                row.get("shop_key", ""),
                row.get("seller", ""),
                row.get("buyer", ""),
                row.get("trace_id", ""),
                row.get("stage", ""),
                row.get("status", ""),
                row.get("summary", ""),
                row.get("detail", ""),
                row.get("duration_ms", 0),
            ]
        )

    filename = "message-processing-conversations-" + window.lower() + ".csv"
    return Response(
        content="\ufeff" + output.getvalue(),
        media_type="text/csv; charset=utf-8",
        headers={"Content-Disposition": 'attachment; filename="' + filename + '"'},
    )
