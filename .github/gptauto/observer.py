from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path

from .audit import write_audit
from .model import Criterion, CriterionStatus, Event, Gate, GateStatus, GateStep, State, Task, utcnow

_VERSION_RE = re.compile(r"(?<![\w.])v?\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?(?![\w.])", re.I)
_RELEASE_RE = re.compile(r"\b(?:release|publish|published|shipping)\b|正式版|发布|发版", re.I)
_RELEASE_WORKFLOW_RE = re.compile(r"(?:^|[\s_-])(?:release|publish)(?:$|[\s_-])", re.I)
_NON_RELEASE_PREFIX_RE = re.compile(r"^\s*(?:chore|docs|test|ci|build|deps|refactor)(?:\([^)]*\))?\s*:", re.I)
_FAILURES = {"failure", "timed_out", "action_required", "startup_failure"}


def release_expected(*texts):
    values = [str(value or "") for value in texts]
    primary = values[0] if values else ""
    if _RELEASE_RE.search(primary):
        return True
    if _NON_RELEASE_PREFIX_RE.search(primary):
        return False
    if _VERSION_RE.search(primary):
        return True
    secondary = " ".join(values[1:])
    return bool(_RELEASE_RE.search(secondary))


def observed_task_id(repo, pr_number="", head_sha="", merge_sha=""):
    if str(pr_number).strip():
        anchor = "pr:" + str(pr_number).strip()
    elif str(merge_sha).strip():
        anchor = "merge:" + str(merge_sha).strip()
    else:
        anchor = "sha:" + str(head_sha).strip()
    return "GA-" + hashlib.sha256((repo + ":" + anchor).encode()).hexdigest()[:12]


def _passed(value):
    return str(value or "").lower() == "success"


def _release_workflow(name):
    return bool(_RELEASE_WORKFLOW_RE.search(str(name or "")))


def capture(
    repo,
    event,
    default_branch="",
    branch="",
    head_sha="",
    pr_number="",
    pr_title="",
    pr_state="",
    pr_merged="false",
    merge_sha="",
    run_id="",
    actor="",
    log_root=".gptauto/logs",
    pr_body="",
    workflow_name="",
    workflow_conclusion="",
    release_tag="",
    release_state="",
    pr_ci_conclusion="",
    pr_ci_run_id="",
    main_ci_conclusion="",
    main_ci_run_id="",
    release_conclusion="",
    release_run_id="",
    current_pr_head_sha="",
):
    tid = observed_task_id(repo, pr_number, head_sha, merge_sha)
    goal = pr_title.strip() or ("Observe repository change " + (merge_sha or head_sha)[:12])
    merged = str(pr_merged).lower() == "true"
    release_required = release_expected(pr_title, pr_body, release_tag)
    workflow_success = _passed(workflow_conclusion)
    release_done = _passed(release_conclusion) or (
        event == "release" and str(release_state or "").lower() in {"published", "released"}
    ) or (
        event == "workflow_run"
        and workflow_success
        and _release_workflow(workflow_name)
    )
    main_ci_done = _passed(main_ci_conclusion) or (
        event == "workflow_run"
        and workflow_success
        and not _release_workflow(workflow_name)
        and bool(default_branch)
        and branch == default_branch
    )
    pr_ci_done = _passed(pr_ci_conclusion) or (
        event == "workflow_run"
        and workflow_success
        and bool(pr_number)
        and not merged
        and branch != default_branch
        and not _release_workflow(workflow_name)
    )

    # Normalize the evidence run IDs when completion is learned directly from the
    # current workflow_run event. This keeps the terminal receipt self-contained
    # even when the caller did not separately resolve the same historical run.
    effective_pr_ci_run_id = str(pr_ci_run_id or "")
    effective_main_ci_run_id = str(main_ci_run_id or "")
    effective_release_run_id = str(release_run_id or "")
    if event == "workflow_run" and workflow_success:
        if _release_workflow(workflow_name):
            effective_release_run_id = effective_release_run_id or str(run_id or "")
        elif merged and branch == default_branch:
            effective_main_ci_run_id = effective_main_ci_run_id or str(run_id or "")
        elif pr_number and not merged and branch != default_branch:
            effective_pr_ci_run_id = effective_pr_ci_run_id or str(run_id or "")

    plan = [
        GateStep(Gate.INSPECT, status=GateStatus.PASSED, evidence=f"GitHub {event} event"),
        GateStep(Gate.COMMIT, status=GateStatus.PASSED, evidence=head_sha or merge_sha),
    ]
    if pr_number:
        plan.extend(
            [
                GateStep(Gate.PR, status=GateStatus.PASSED, evidence=f"PR #{pr_number}"),
                GateStep(
                    Gate.PR_CI,
                    required=False,
                    status=GateStatus.PASSED if pr_ci_done else GateStatus.WAITING,
                    evidence=(
                        f"CI run {effective_pr_ci_run_id or run_id}"
                        if pr_ci_done
                        else "No successful PR CI evidence observed yet"
                    ),
                ),
                GateStep(
                    Gate.MERGE,
                    status=GateStatus.PASSED if merged else GateStatus.WAITING,
                    evidence=merge_sha if merged else "PR not merged at observation time",
                ),
            ]
        )
    if merged:
        plan.append(
            GateStep(
                Gate.MAIN_CI,
                status=GateStatus.PASSED if main_ci_done else GateStatus.WAITING,
                evidence=(
                    f"main CI run {effective_main_ci_run_id or run_id}"
                    if main_ci_done
                    else "Post-merge CI not yet successful"
                ),
            )
        )
        if release_required:
            release_status = GateStatus.PASSED if release_done else GateStatus.WAITING
            if (
                event == "workflow_run"
                and _release_workflow(workflow_name)
                and str(workflow_conclusion or "").lower() in _FAILURES
            ):
                release_status = GateStatus.FAILED
            plan.append(
                GateStep(
                    Gate.RELEASE,
                    status=release_status,
                    evidence=(
                        release_tag or f"Release run {effective_release_run_id or run_id}"
                        if release_done
                        else "Release completion not yet observed"
                    ),
                )
            )

    dod = [
        Criterion(
            "Repository change is traceable from GitHub evidence",
            CriterionStatus.PASSED,
            f"event={event}; head={head_sha}; pr={pr_number or '-'}",
        )
    ]
    if pr_number:
        dod.append(
            Criterion(
                "Pull request is merged",
                CriterionStatus.PASSED if merged else CriterionStatus.PENDING,
                merge_sha if merged else "",
            )
        )
    if merged:
        dod.append(
            Criterion(
                "Post-merge CI completed successfully",
                CriterionStatus.PASSED if main_ci_done else CriterionStatus.PENDING,
                f"run={effective_main_ci_run_id or run_id}" if main_ci_done else "",
            )
        )
        if release_required:
            dod.append(
                Criterion(
                    "Requested release completed successfully",
                    CriterionStatus.PASSED if release_done else CriterionStatus.PENDING,
                    (
                        release_tag or f"release_run={effective_release_run_id or run_id}"
                        if release_done
                        else ""
                    ),
                )
            )

    if pr_number and not merged:
        state = State.EXECUTE
    elif merged:
        if release_required:
            state = State.DONE if (main_ci_done and release_done) else State.VERIFY
        else:
            state = State.DONE if main_ci_done else State.VERIFY
    elif event == "workflow_run" and workflow_success and branch == default_branch:
        state = State.DONE
    elif event in {"push", "workflow_run", "release"}:
        state = State.VERIFY
    else:
        state = State.EXECUTE

    if (
        event == "workflow_run"
        and str(workflow_conclusion or "").lower() in _FAILURES
        and (merged or bool(pr_number))
    ):
        state = State.EXECUTE

    completion_gate = "release" if release_required else ("main_ci" if merged else "merge")
    t = Task(
        tid,
        goal,
        repo,
        dod,
        state=state,
        plan=plan,
        metadata={
            "provenance": "repository_observer",
            "task_type": "observed",
            "branch": branch,
            "head_sha": current_pr_head_sha or head_sha,
            "event_head_sha": head_sha,
            "current_pr_head_sha": current_pr_head_sha or head_sha,
            "pr_number": pr_number,
            "merge_sha": merge_sha,
            "run_id": run_id,
            "artifact_name": "gptauto-" + tid,
            "actor": actor,
            "default_branch": default_branch,
            "event": event,
            "workflow_name": workflow_name,
            "workflow_conclusion": workflow_conclusion,
            "release_tag": release_tag,
            "release_required": release_required,
            "completion_gate": completion_gate,
            "pr_ci_run_id": effective_pr_ci_run_id,
            "main_ci_run_id": effective_main_ci_run_id,
            "release_run_id": effective_release_run_id,
        },
    )
    t.history = [
        Event(
            utcnow(),
            state.value,
            "repository event captured",
            kind="observation",
            evidence=json.dumps(
                {
                    "event": event,
                    "actor": actor,
                    "pr_state": pr_state,
                    "workflow": workflow_name,
                    "conclusion": workflow_conclusion,
                    "release_tag": release_tag,
                    "completion_gate": completion_gate,
                },
                ensure_ascii=False,
            ),
        )
    ]
    # Hydrate the previous observer artifact into one cumulative task ledger.
    # The consumer workflow restores the latest artifact into log_root before capture().
    previous_state = Path(log_root) / tid / "state.json"
    if previous_state.exists():
        try:
            previous = Task.from_dict(json.loads(previous_state.read_text(encoding="utf-8")))
        except (OSError, ValueError, KeyError, TypeError):
            previous = None
        if previous and previous.task_id == tid:
            t.created_at = previous.created_at
            # Preserve durable metadata that a later GitHub event may not carry.
            merged_metadata = dict(previous.metadata)
            merged_metadata.update({k: v for k, v in t.metadata.items() if v not in ("", None)})
            t.metadata = merged_metadata
            seen = {(e.at, e.kind, e.reason, e.gate, e.status, e.evidence) for e in previous.history}
            current = [e for e in t.history if (e.at, e.kind, e.reason, e.gate, e.status, e.evidence) not in seen]
            t.history = list(previous.history) + current

    # Persist a session time-budget checkpoint with every observation. This makes
    # ChatGPT/UI lifetime a recoverable concern instead of a task lifetime.
    from .time_budget import apply as apply_time_budget
    time_budget = apply_time_budget(t)

    paths = write_audit(t, log_root)
    return {
        "task_id": tid,
        "state": state.value,
        "provenance": "repository_observer",
        "artifact_name": "gptauto-" + tid,
        "completion_gate": completion_gate,
        "release_required": release_required,
        "paths": paths,
    }


def main():
    p = argparse.ArgumentParser()
    s = p.add_subparsers(dest="cmd", required=True)
    c = s.add_parser("capture")
    for name in [
        "event",
        "repo",
        "default-branch",
        "branch",
        "head-sha",
        "pr-number",
        "pr-title",
        "pr-body",
        "pr-state",
        "pr-merged",
        "merge-sha",
        "run-id",
        "actor",
        "workflow-name",
        "workflow-conclusion",
        "release-tag",
        "release-state",
        "pr-ci-conclusion",
        "pr-ci-run-id",
        "main-ci-conclusion",
        "main-ci-run-id",
        "release-conclusion",
        "release-run-id",
        "current-pr-head-sha",
    ]:
        c.add_argument("--" + name, default="")
    a = p.parse_args()
    print(
        json.dumps(
            capture(
                a.repo,
                a.event,
                a.default_branch,
                a.branch,
                a.head_sha,
                a.pr_number,
                a.pr_title,
                a.pr_state,
                a.pr_merged,
                a.merge_sha,
                a.run_id,
                a.actor,
                pr_body=a.pr_body,
                workflow_name=a.workflow_name,
                workflow_conclusion=a.workflow_conclusion,
                release_tag=a.release_tag,
                release_state=a.release_state,
                pr_ci_conclusion=a.pr_ci_conclusion,
                pr_ci_run_id=a.pr_ci_run_id,
                main_ci_conclusion=a.main_ci_conclusion,
                main_ci_run_id=a.main_ci_run_id,
                release_conclusion=a.release_conclusion,
                release_run_id=a.release_run_id,
                current_pr_head_sha=a.current_pr_head_sha,
            ),
            ensure_ascii=False,
        )
    )


if __name__ == "__main__":
    main()
