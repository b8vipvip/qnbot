from __future__ import annotations

import re
from datetime import datetime, timezone
from typing import Any, Dict, Iterable, List, Optional, Tuple


_AI_MARKER = re.compile(r"\s*\[AI\]\s*$", re.IGNORECASE)
_BOT_ANSWER_TYPES = {"bot_answer", "ai_answer", "bot_reply"}
_DUPLICATE_WINDOW_SECONDS = 120
_QUESTION_LOOKBACK_SECONDS = 300
_QUESTION_BURST_GAP_SECONDS = 45


def _parse_time(value: Any) -> Optional[datetime]:
    text = str(value or "").strip()
    if not text:
        return None
    try:
        parsed = datetime.fromisoformat(text.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.astimezone(timezone.utc)
    except Exception:
        return None


def _seconds_between(left: Dict[str, Any], right: Dict[str, Any]) -> Optional[float]:
    left_at = _parse_time(left.get("occurred_at"))
    right_at = _parse_time(right.get("occurred_at"))
    if left_at is None or right_at is None:
        return None
    return abs((right_at - left_at).total_seconds())


def _normalize_text(value: Any, strip_ai_marker: bool = False) -> str:
    text = str(value or "").replace("\x00", "").strip()
    if strip_ai_marker:
        text = _AI_MARKER.sub("", text).strip()
    return re.sub(r"\s+", " ", text)


def _is_bot_answer(row: Dict[str, Any]) -> bool:
    if str(row.get("role") or "").lower() != "assistant":
        return False
    message_type = str(row.get("message_type") or "").lower()
    return message_type in _BOT_ANSWER_TYPES or bool(
        _AI_MARKER.search(str(row.get("text") or ""))
    )


def _deduplicate_consecutive(rows: Iterable[Dict[str, Any]]) -> List[Dict[str, Any]]:
    result: List[Dict[str, Any]] = []
    for original in rows:
        row = dict(original)
        role = str(row.get("role") or "").lower()
        normalized = _normalize_text(
            row.get("text"),
            strip_ai_marker=_is_bot_answer(row),
        )
        if not normalized:
            continue
        row["_normalized_text"] = normalized

        if result:
            previous = result[-1]
            same_role = str(previous.get("role") or "").lower() == role
            same_text = str(previous.get("_normalized_text") or "") == normalized
            delta = _seconds_between(previous, row)
            if same_role and same_text and (
                delta is None or delta <= _DUPLICATE_WINDOW_SECONDS
            ):
                continue
        result.append(row)
    return result


def _question_tail(
    pending_users: List[Dict[str, Any]],
    answer: Dict[str, Any],
) -> List[Dict[str, Any]]:
    if not pending_users:
        return []
    answer_at = _parse_time(answer.get("occurred_at"))
    eligible: List[Dict[str, Any]] = []
    for item in pending_users:
        item_at = _parse_time(item.get("occurred_at"))
        if answer_at is None or item_at is None:
            eligible.append(item)
            continue
        delta = (answer_at - item_at).total_seconds()
        if 0 <= delta <= _QUESTION_LOOKBACK_SECONDS:
            eligible.append(item)
    if not eligible:
        return []

    tail = [eligible[-1]]
    for item in reversed(eligible[:-1]):
        gap = _seconds_between(item, tail[0])
        if gap is not None and gap > _QUESTION_BURST_GAP_SECONDS:
            break
        tail.insert(0, item)
    return tail


def _public_message(row: Dict[str, Any], message_type: str) -> Dict[str, Any]:
    item = {
        key: value
        for key, value in row.items()
        if not str(key).startswith("_")
    }
    item["message_type"] = message_type
    if message_type == "bot_answer":
        item["text"] = _normalize_text(item.get("text"), strip_ai_marker=True)
    else:
        item["text"] = str(item.get("text") or "").strip()
    return item


def project_conversation_message(row: Any) -> Dict[str, Any]:
    """Normalize one desktop-mirrored message for the mobile inbox."""
    item = dict(row)
    text = str(item.get("text") or "")
    role = str(item.get("role") or "").lower()
    message_type = str(item.get("message_type") or "").lower()
    withdrawn = bool(item.get("withdrawn"))
    is_bot = bool(item.get("is_bot")) or message_type in _BOT_ANSWER_TYPES

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



def build_bot_qa_messages(
    rows: Iterable[Dict[str, Any]],
) -> List[Dict[str, Any]]:
    ordered = sorted(
        (dict(row) for row in rows),
        key=lambda item: (
            int(item.get("id") or 0),
            str(item.get("occurred_at") or ""),
        ),
    )
    deduped = _deduplicate_consecutive(ordered)
    pending_users: List[Dict[str, Any]] = []
    output: List[Dict[str, Any]] = []
    recent_pairs: List[Tuple[str, Optional[datetime]]] = []

    for row in deduped:
        role = str(row.get("role") or "").lower()
        if role == "user":
            pending_users.append(row)
            if len(pending_users) > 40:
                pending_users = pending_users[-40:]
            continue

        if not _is_bot_answer(row):
            continue

        questions = _question_tail(pending_users, row)
        pending_users = []
        if not questions:
            continue

        question_text = "\n".join(
            str(item.get("_normalized_text") or "")
            for item in questions
        )
        answer_text = str(row.get("_normalized_text") or "")
        pair_key = question_text + "\n=>\n" + answer_text
        answer_at = _parse_time(row.get("occurred_at"))

        duplicate_pair = False
        for previous_key, previous_at in reversed(recent_pairs[-10:]):
            if previous_key != pair_key:
                continue
            if answer_at is None or previous_at is None:
                duplicate_pair = True
                break
            if abs((answer_at - previous_at).total_seconds()) <= _DUPLICATE_WINDOW_SECONDS:
                duplicate_pair = True
                break
        if duplicate_pair:
            continue

        for question in questions:
            output.append(_public_message(question, "bot_question"))
        output.append(_public_message(row, "bot_answer"))
        recent_pairs.append((pair_key, answer_at))

    return output
