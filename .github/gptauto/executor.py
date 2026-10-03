from __future__ import annotations

import argparse
import json
from pathlib import Path

from .model import Gate, GateStatus, State, Task
from .orchestrator import canonicalize_task

_FAILURES = {"failure", "timed_out", "startup_failure"}


def _host_control(task: Task, action: str) -> dict:
    """Compatibility projection of canonical Task State v2 for hosts."""
    authority = canonicalize_task(task)
    terminal = authority["terminal_done"]
    blocked = authority["phase"] == "USER_ACTION_REQUIRED"
    allow_exit = authority["allow_foreground_exit"]
    return {
        "schema": "gptauto.host-control/v2",
        "task_schema": authority["protocol"],
        "task_id": task.task_id,
        "phase": authority["phase"],
        "generation": authority["generation"],
        "repair_owner": authority["repair_owner"],
        "terminal_done": terminal,
        "task_alive": not terminal,
        "allow_foreground_exit": allow_exit,
        "foreground_disposition": "TASK_DONE" if terminal else "USER_ACTION_REQUIRED" if blocked else "CONTINUE_REQUIRED",
        "requires_foreground_poll": not (terminal or blocked),
        "continuation_required": authority["continuation_required"],
        "continuation_key": authority["continuation_key"],
        "blocked_reason": authority.get("blocked_reason", ""),
        "next_host_action": (
            "TASK_CLOSED"
            if terminal
            else "REQUEST_USER_ACTION"
            if blocked
            else "RESUME_CHATGPT_SAME_TASK"
            if authority["continuation_required"]
            else "WAIT_FOR_TASK_STATE_CHANGE"
        ),
    }


def _lease(task: Task, action: str) -> dict:
    meta = task.metadata
    host_control = _host_control(task, action)
    terminal = host_control["terminal_done"]
    blocked = host_control["phase"] == "USER_ACTION_REQUIRED"
    return {
        "active": not terminal,
        "completion_gate": str(meta.get("completion_gate") or ""),
        "release_required": bool(meta.get("release_required")),
        "may_finish_foreground": host_control["allow_foreground_exit"],
        "task_alive": host_control["task_alive"],
        "foreground_completion_status": "terminal" if terminal else "blocked" if blocked else "continue_required",
        "foreground_disposition": host_control["foreground_disposition"],
        "allow_foreground_exit": host_control["allow_foreground_exit"],
        "requires_foreground_poll": host_control["requires_foreground_poll"],
        "continuation_required": host_control["continuation_required"],
        "host_control": host_control,
        "foreground_instruction": (
            "Report task completion from terminal DONE evidence."
            if terminal
            else "Report the explicit user action required, then resume the same GPTAuto task after that action."
            if blocked
            else "The engineering task is still active. Do not finish the foreground task; poll/recover GitHub gates until canonical DONE or explicit USER_ACTION_REQUIRED."
        ),
    }


def _with_lease(task: Task, action: dict) -> dict:
    action_name = str(action.get("action") or "")
    lease = _lease(task, action_name)
    action["completion_lease"] = lease
    current_head = str(task.metadata.get("current_pr_head_sha") or task.metadata.get("head_sha") or "no-head")
    action["dedupe_key"] = f"{task.task_id}:{lease['host_control']['generation']}:{current_head}:{action_name}"
    return action


def next_action(task: Task) -> dict:
    """Turn observer state into an explicit, machine-consumable executor action."""
    authority = canonicalize_task(task)
    meta = task.metadata
    failed = [step.gate.value for step in task.plan if step.status == GateStatus.FAILED]
    conclusion = str(meta.get("workflow_conclusion") or "").lower()
    event_head = str(meta.get("event_head_sha") or meta.get("head_sha") or "")
    current_head = str(meta.get("current_pr_head_sha") or meta.get("head_sha") or "")
    superseded = bool(event_head and current_head and event_head != current_head)
    time_level = str(meta.get("time_budget_level") or "NORMAL")
    time_action = str(meta.get("time_budget_action") or "continue")
    elapsed = meta.get("chat_session_elapsed_minutes", 0)

    if authority["terminal_done"]:
        return _with_lease(
            task,
            {
                "action": "done",
                "reason": "Definition of Done is satisfied",
                "task_id": task.task_id,
                "completion_receipt": {
                    "completion_gate": str(meta.get("completion_gate") or ""),
                    "release_required": bool(meta.get("release_required")),
                    "pr_number": str(meta.get("pr_number") or ""),
                    "merge_sha": str(meta.get("merge_sha") or ""),
                    "pr_ci_run_id": str(meta.get("pr_ci_run_id") or ""),
                    "main_ci_run_id": str(meta.get("main_ci_run_id") or ""),
                    "release_run_id": str(meta.get("release_run_id") or ""),
                },
            },
        )

    if superseded and conclusion in (_FAILURES | {"cancelled", "action_required"}):
        return _with_lease(
            task,
            {
                "action": "superseded",
                "reason": "Workflow result belongs to an older PR head and must not change the current task",
                "task_id": task.task_id,
                "pr_number": str(meta.get("pr_number") or ""),
                "run_id": str(meta.get("run_id") or ""),
                "event_head_sha": event_head,
                "current_head_sha": current_head,
            },
        )

    # Approval-required workflow runs are user-action gates, not repair evidence.
    if authority["phase"] == "USER_ACTION_REQUIRED" or conclusion == "action_required":
        return _with_lease(
            task,
            {
                "action": "user_action_required",
                "reason": "GitHub workflow requires maintainer approval or another explicit user action",
                "task_id": task.task_id,
                "pr_number": str(meta.get("pr_number") or ""),
                "run_id": str(meta.get("run_id") or meta.get("pr_ci_run_id") or ""),
                "head_sha": current_head,
                "blocked_reason": str(meta.get("blocked_reason") or "workflow_approval_required"),
            },
        )

    if conclusion == "cancelled":
        return _with_lease(
            task,
            {
                "action": "lease_wait",
                "reason": "Current-head workflow was cancelled; await/reconcile replacement evidence",
                "task_id": task.task_id,
                "pr_number": str(meta.get("pr_number") or ""),
                "head_sha": current_head,
            },
        )

    if failed or conclusion in _FAILURES:
        return _with_lease(
            task,
            {
                "action": "repair_request",
                "reason": "GitHub Actions reported a failing gate",
                "task_id": task.task_id,
                "pr_number": str(meta.get("pr_number") or ""),
                "run_id": str(meta.get("run_id") or ""),
                "workflow": str(meta.get("workflow_name") or ""),
                "head_sha": current_head,
                "repair_key": f"{task.task_id}:{current_head or 'no-head'}",
                "failed_gates": failed,
            },
        )

    steps = {step.gate: step for step in task.plan}
    pr_ci = steps.get(Gate.PR_CI)
    merge = steps.get(Gate.MERGE)
    if meta.get("pr_number") and pr_ci and pr_ci.status == GateStatus.PASSED and merge and merge.status != GateStatus.PASSED:
        return _with_lease(
            task,
            {
                "action": "merge",
                "reason": "PR CI passed and merge gate is waiting",
                "task_id": task.task_id,
                "pr_number": str(meta.get("pr_number")),
                "head_sha": current_head,
                "release_required": bool(meta.get("release_required")),
            },
        )

    if task.state == State.VERIFY:
        merge_sha = str(meta.get("merge_sha") or "")
        pr_number = str(meta.get("pr_number") or "")
        if pr_number and merge_sha:
            return _with_lease(
                task,
                {
                    "action": "reconcile_adopt",
                    "reason": "Merged task is unfinished; ensure a Reconciler owns the Completion Lease regardless of who merged the PR",
                    "task_id": task.task_id,
                    "pr_number": pr_number,
                    "merge_sha": merge_sha,
                    "release_required": bool(meta.get("release_required")),
                    "completion_gate": str(meta.get("completion_gate") or ""),
                },
            )
        return _with_lease(
            task,
            {
                "action": "verify",
                "reason": "Waiting for post-merge CI/release evidence",
                "task_id": task.task_id,
                "completion_gate": str(meta.get("completion_gate") or ""),
            },
        )

    if time_action in {"checkpoint", "handoff", "detach"}:
        return _with_lease(
            task,
            {
                "action": "timeout_recovery",
                "reason": f"Task time budget entered {time_level}",
                "task_id": task.task_id,
                "time_budget_level": time_level,
                "elapsed_minutes": elapsed,
                "handoff": time_action in {"handoff", "detach"},
                "detach": time_action == "detach",
            },
        )

    return _with_lease(
        task,
        {
            "action": "lease_wait",
            "reason": "Definition of Done is not satisfied; completion lease remains active",
            "task_id": task.task_id,
            "pr_number": str(meta.get("pr_number") or ""),
            "head_sha": current_head,
        },
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("state")
    args = parser.parse_args()
    task = Task.from_dict(json.loads(Path(args.state).read_text(encoding="utf-8")))
    print(json.dumps(next_action(task), ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
