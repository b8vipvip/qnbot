from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone

from .model import Task


@dataclass(frozen=True)
class TimeBudget:
    soft_warning_minutes: int = 30
    handoff_ready_minutes: int = 40
    checkpoint_minutes: int = 45
    timeout_guard_minutes: int = 48


def _parse(value: str) -> datetime:
    dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
    return dt if dt.tzinfo else dt.replace(tzinfo=timezone.utc)


def elapsed_minutes(task: Task, now: datetime | None = None) -> float:
    now = now or datetime.now(timezone.utc)
    return max(0.0, (now - _parse(task.created_at)).total_seconds() / 60.0)


def classify(task: Task, now: datetime | None = None, budget: TimeBudget | None = None) -> dict:
    budget = budget or TimeBudget()
    elapsed = elapsed_minutes(task, now)
    if elapsed >= budget.timeout_guard_minutes:
        level, action = "UI_TIMEOUT_GUARD", "detach"
    elif elapsed >= budget.checkpoint_minutes:
        level, action = "CHECKPOINT", "checkpoint"
    elif elapsed >= budget.handoff_ready_minutes:
        level, action = "HANDOFF_READY", "handoff"
    elif elapsed >= budget.soft_warning_minutes:
        level, action = "SOFT_WARNING", "checkpoint"
    else:
        level, action = "NORMAL", "continue"
    return {
        "level": level,
        "action": action,
        "elapsed_minutes": round(elapsed, 2),
        "soft_warning_minutes": budget.soft_warning_minutes,
        "handoff_ready_minutes": budget.handoff_ready_minutes,
        "checkpoint_minutes": budget.checkpoint_minutes,
        "timeout_guard_minutes": budget.timeout_guard_minutes,
    }


def apply(task: Task, now: datetime | None = None, budget: TimeBudget | None = None) -> dict:
    status = classify(task, now, budget)
    previous = str(task.metadata.get("time_budget_level") or "")
    task.metadata["time_budget_level"] = status["level"]
    task.metadata["chat_session_elapsed_minutes"] = status["elapsed_minutes"]
    task.metadata["time_budget_action"] = status["action"]
    if status["level"] != previous:
        task.record(
            f"time budget entered {status['level']}",
            kind="time_budget",
            evidence=(
                f"elapsed={status['elapsed_minutes']}m; action={status['action']}; "
                f"guard={status['timeout_guard_minutes']}m"
            ),
        )
    return status
