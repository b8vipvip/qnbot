from __future__ import annotations

from typing import Any, Dict, List, Tuple

from fastapi import APIRouter, Depends, Query
from pydantic import BaseModel, Field

import bot_web_console as core


router = APIRouter()


def install(control_plane: Any) -> None:
    # Register the mobile inbox routes before the legacy knowledge conversation
    # routes. The inbox is a projection of the desktop Bot message mirror, not a
    # Bot-Q&A-only feed.
    control_plane.app.include_router(router)


def _public_message(row: Any) -> Dict[str, Any]:
    item = dict(row)
    text = str(item.get("text") or "")
    role = str(item.get("role") or "").lower()
    message_type = str(item.get("message_type") or "").lower()
    withdrawn = bool(item.get("withdrawn"))
    is_bot = bool(item.get("is_bot")) or message_type in {"bot_answer", "ai_answer", "bot_reply"}

    if not withdrawn and role == "assistant":
        for marker in ("[A]", "[AI]"):
            if text.rstrip().lower().endswith(marker.lower()):
                text = text.rstrip()[:-len(marker)].rstrip()
                is_bot = True
                break

    if withdrawn:
        is_bot = False
    if withdrawn and not text.strip():
        text = "已撤回一条消息"

    item["text"] = text
    item["is_bot"] = is_bot
    item["withdrawn"] = withdrawn
    return item


def _group_all_messages(client_id: int) -> Dict[Tuple[str, str], List[Dict[str, Any]]]:
    cp = core._cp
    with cp.db() as conn:
        rows = conn.execute(
            """
            SELECT id,client_id,message_key,seller,buyer,role,text,message_type,
                   is_bot,withdrawn,occurred_at,created_at
            FROM bot_messages
            WHERE client_id=? AND buyer<>''
            ORDER BY seller ASC,buyer ASC,occurred_at ASC,id ASC
            """,
            (client_id,),
        ).fetchall()
    grouped: Dict[Tuple[str, str], List[Dict[str, Any]]] = {}
    for row in rows:
        item = _public_message(row)
        key = (str(item.get("seller") or ""), str(item.get("buyer") or ""))
        grouped.setdefault(key, []).append(item)
    return grouped


def _last_read_map(client_id: int) -> Dict[Tuple[str, str], int]:
    cp = core._cp
    with cp.db() as conn:
        rows = conn.execute(
            "SELECT seller,buyer,last_read_message_id FROM bot_conversation_reads WHERE client_id=?",
            (client_id,),
        ).fetchall()
    return {
        (str(row["seller"] or ""), str(row["buyer"] or "")): int(row["last_read_message_id"] or 0)
        for row in rows
    }


class ConversationReadInput(BaseModel):
    seller: str = Field(min_length=1, max_length=120)
    buyer: str = Field(min_length=1, max_length=120)
    message_id: int = Field(default=0, ge=0)


@router.get("/api/bot-web/conversations")
def web_bot_qa_conversations(
    query: str = Query("", max_length=120),
    limit: int = Query(300, ge=1, le=1000),
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    grouped = _group_all_messages(client_id)
    reads = _last_read_map(client_id)
    needle = query.strip().lower()
    conversations: List[Dict[str, Any]] = []

    for (seller, buyer), messages in grouped.items():
        if not messages:
            continue
        searchable = " ".join(
            [seller, buyer] + [str(message.get("text") or "") for message in messages]
        ).lower()
        if needle and needle not in searchable:
            continue

        last = messages[-1]
        last_read = reads.get((seller, buyer), 0)
        unread = sum(
            1
            for message in messages
            if message.get("role") == "user"
            and int(message.get("id") or 0) > last_read
        )
        conversations.append(
            {
                "id": int(last.get("id") or 0),
                "seller": seller,
                "buyer": buyer,
                "role": last.get("role"),
                "text": last.get("text"),
                "message_type": last.get("message_type"),
                "is_bot": bool(last.get("is_bot")),
                "withdrawn": bool(last.get("withdrawn")),
                "occurred_at": last.get("occurred_at"),
                "last_read_message_id": last_read,
                "unread_count": unread,
                "record_scope": "conversation",
            }
        )

    conversations.sort(
        key=lambda item: (str(item.get("occurred_at") or ""), int(item.get("id") or 0)),
        reverse=True,
    )
    return {
        "conversations": conversations[:limit],
        "server_time": core._now(),
        "record_scope": "conversation",
    }


@router.get("/api/bot-web/conversation/messages")
def web_bot_qa_messages(
    seller: str = Query(..., min_length=1, max_length=120),
    buyer: str = Query(..., min_length=1, max_length=120),
    before_id: int = Query(0, ge=0),
    limit: int = Query(100, ge=1, le=300),
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    cp = core._cp
    with cp.db() as conn:
        rows = conn.execute(
            """
            SELECT id,client_id,message_key,seller,buyer,role,text,message_type,
                   is_bot,withdrawn,occurred_at,created_at
            FROM bot_messages
            WHERE client_id=? AND seller=? AND buyer=?
            ORDER BY occurred_at ASC,id ASC
            """,
            (client_id, seller.strip(), buyer.strip()),
        ).fetchall()
    messages = [_public_message(row) for row in rows]

    if before_id > 0:
        cursor = next(
            (message for message in messages if int(message.get("id") or 0) == before_id),
            None,
        )
        if cursor:
            cursor_key = (
                str(cursor.get("occurred_at") or ""),
                int(cursor.get("id") or 0),
            )
            messages = [
                message for message in messages
                if (
                    str(message.get("occurred_at") or ""),
                    int(message.get("id") or 0),
                ) < cursor_key
            ]
        else:
            messages = [
                message for message in messages
                if int(message.get("id") or 0) < before_id
            ]

    has_more = len(messages) > limit
    return {
        "messages": messages[-limit:],
        "has_more": has_more,
        "record_scope": "conversation",
    }


@router.post("/api/bot-web/conversation/read")
def web_mark_bot_qa_read(
    data: ConversationReadInput,
    client: Dict[str, Any] = Depends(core._web_client),
) -> Dict[str, Any]:
    client_id = int(client["id"])
    cp = core._cp
    message_id = int(data.message_id or 0)
    if message_id < 1:
        with cp.db() as conn:
            row = conn.execute(
                """
                SELECT MAX(id) AS last_id
                FROM bot_messages
                WHERE client_id=? AND seller=? AND buyer=?
                """,
                (client_id, data.seller.strip(), data.buyer.strip()),
            ).fetchone()
        message_id = int(row["last_id"] or 0) if row else 0

    with cp.db() as conn:
        conn.execute(
            """
            INSERT INTO bot_conversation_reads(client_id,seller,buyer,last_read_message_id,updated_at)
            VALUES(?,?,?,?,?)
            ON CONFLICT(client_id,seller,buyer) DO UPDATE SET
                last_read_message_id=MAX(last_read_message_id,excluded.last_read_message_id),
                updated_at=excluded.updated_at
            """,
            (client_id, data.seller.strip(), data.buyer.strip(), message_id, core._now()),
        )
    return {"ok": True, "last_read_message_id": message_id, "record_scope": "conversation"}
