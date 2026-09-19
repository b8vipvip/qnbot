from __future__ import annotations

import argparse
import json
from pathlib import Path

from .model import Gate, GateStatus, State, Task

_FAILURES = {"failure", "timed_out", "action_required", "startup_failure", "cancelled"}


def next_action(task: Task) -> dict:
    """Turn observer state into an explicit, machine-consumable executor action."""
    meta = task.metadata
    failed = [step.gate.value for step in task.plan if step.status == GateStatus.FAILED]
    conclusion = str(meta.get("workflow_conclusion") or "").lower()

    if task.state == State.DONE:
        return {"action": "done", "reason": "Definition of Done is satisfied", "task_id": task.task_id}

    if failed or conclusion in _FAILURES:
        return {
            "action": "repair_request",
            "reason": "GitHub Actions reported a failing gate",
            "task_id": task.task_id,
            "pr_number": str(meta.get("pr_number") or ""),
            "run_id": str(meta.get("run_id") or ""),
            "workflow": str(meta.get("workflow_name") or ""),
            "failed_gates": failed,
        }

    steps = {step.gate: step for step in task.plan}
    pr_ci = steps.get(Gate.PR_CI)
    merge = steps.get(Gate.MERGE)
    if (
        meta.get("pr_number")
        and pr_ci
        and pr_ci.status == GateStatus.PASSED
        and merge
        and merge.status != GateStatus.PASSED
    ):
        return {
            "action": "merge",
            "reason": "PR CI passed and merge gate is waiting",
            "task_id": task.task_id,
            "pr_number": str(meta.get("pr_number")),
            "head_sha": str(meta.get("head_sha") or ""),
            "release_required": bool(meta.get("release_required")),
        }

    if task.state == State.VERIFY:
        return {
            "action": "verify",
            "reason": "Waiting for post-merge CI/release evidence",
            "task_id": task.task_id,
            "completion_gate": str(meta.get("completion_gate") or ""),
        }

    return {"action": "wait", "reason": "No safe executable transition is ready", "task_id": task.task_id}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("state")
    args = parser.parse_args()
    task = Task.from_dict(json.loads(Path(args.state).read_text(encoding="utf-8")))
    print(json.dumps(next_action(task), ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
