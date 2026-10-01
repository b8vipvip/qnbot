from dataclasses import dataclass
from .engine import gate
from .github import GitHubClient
from .model import CriterionStatus,Gate,GateStatus,State,Task

@dataclass(frozen=True)
class Decision:
    action:str
    reason:str

def _step(task:Task, gate_name:Gate):
    return next((s for s in task.plan if s.gate==gate_name),None)

def canonical_phase(task:Task)->str:
    """Protocol v2 single lifecycle authority derived only from durable task evidence.

    ChatGPT executions are disposable workers. The durable GPTAuto task, not a
    foreground turn or guard, owns lifecycle state and terminality.
    """
    failed=next((s for s in task.plan if s.required and s.status==GateStatus.FAILED),None)
    conclusion=str(task.metadata.get("workflow_conclusion") or "").lower()
    event_head=str(task.metadata.get("event_head_sha") or "")
    current_head=str(task.metadata.get("current_pr_head_sha") or task.metadata.get("head_sha") or "")
    current_failure=conclusion in {"failure","timed_out","action_required","startup_failure"} and not (event_head and current_head and event_head!=current_head)
    if failed or current_failure:
        return "REPAIR_REQUIRED"
    merge=_step(task,Gate.MERGE)
    main_ci=_step(task,Gate.MAIN_CI)
    release=_step(task,Gate.RELEASE)
    if merge and merge.required and merge.status not in {GateStatus.PASSED,GateStatus.SKIPPED}:
        pr_ci=_step(task,Gate.PR_CI)
        if pr_ci and pr_ci.status in {GateStatus.WAITING,GateStatus.PENDING}:
            return "PR_CI"
        return "MERGE"
    if main_ci and main_ci.required and main_ci.status not in {GateStatus.PASSED,GateStatus.SKIPPED}:
        return "POST_MERGE_CI"
    if bool(task.metadata.get("release_required")):
        if not release or release.status not in {GateStatus.PASSED,GateStatus.SKIPPED}:
            return "RELEASE_VERIFY" if task.metadata.get("release_run_id") else "RELEASE"
    if task.required_gates_satisfied() and task.criteria_satisfied():
        return "DONE"
    if task.state==State.BLOCKED:
        return "USER_ACTION_REQUIRED"
    return "RUNNING" if task.state!=State.VERIFY else "WAITING_GITHUB"

def canonicalize_task(task:Task)->dict:
    """Project evidence into the one canonical task/host lifecycle state."""
    phase=canonical_phase(task)
    previous=str(task.metadata.get("phase") or "")
    if phase=="DONE":
        task.state=State.DONE
    elif phase=="REPAIR_REQUIRED":
        task.state=State.EXECUTE
    elif phase in {"POST_MERGE_CI","RELEASE","RELEASE_VERIFY","WAITING_GITHUB"}:
        task.state=State.VERIFY
    elif phase=="USER_ACTION_REQUIRED":
        task.state=State.BLOCKED
    elif task.state==State.DONE:
        # Observer/Reconcile may propose DONE, but only this authority may retain it.
        task.state=State.VERIFY
    generation=int(task.metadata.get("generation") or 0)
    if phase=="REPAIR_REQUIRED" and previous!="REPAIR_REQUIRED":
        generation+=1
    task.metadata["phase"]=phase
    task.metadata["generation"]=generation
    # One repair generation has exactly one owner. The three repair tiers are
    # sequential stages inside this pipeline, never competing repair authorities.
    task.metadata["repair_owner"]="repair_pipeline" if phase=="REPAIR_REQUIRED" else ""
    terminal=phase=="DONE"
    task.metadata["terminal_done"]=terminal
    task.metadata["allow_foreground_exit"]=terminal
    task.metadata["completion_lease"]="DONE" if terminal else "ACTIVE"
    task.metadata["protocol"]="gptauto.task-state/v2"
    task.metadata["continuation_required"]=phase=="REPAIR_REQUIRED"
    current_head=str(task.metadata.get("current_pr_head_sha") or task.metadata.get("head_sha") or "")
    task.metadata["continuation_key"]=f"{task.task_id}:{generation}:{current_head or 'no-head'}"
    if previous!=phase:
        task.record(f"canonical lifecycle transition {previous or '-'} -> {phase}",kind="lifecycle",evidence=f"generation={generation}")
    return {
        "phase":phase,
        "generation":generation,
        "repair_owner":str(task.metadata.get("repair_owner") or ""),
        "terminal_done":terminal,
        "allow_foreground_exit":terminal,
        "completion_lease":"DONE" if terminal else "ACTIVE",
        "protocol":"gptauto.task-state/v2",
        "continuation_required":phase=="REPAIR_REQUIRED",
        "continuation_key":str(task.metadata.get("continuation_key") or ""),
    }

class Orchestrator:
    def __init__(self,client:GitHubClient):self.github=client
    def decide_gate(self,task,step):
        m=task.metadata;g=step.gate
        if g==Gate.PR:return Decision("passed",f"PR #{m['pr_number']} recorded") if m.get("pr_number") else Decision("wait","PR not recorded")
        if g==Gate.PR_CI:
            branch=m.get("work_branch")
            if not branch:return Decision("blocked","work_branch is required")
            r=self.github.latest_run(branch=branch,event="pull_request",workflow=m.get("ci_workflow"),head_sha=m.get("work_head_sha"));c=self.github.classify_run(r)
            return Decision("wait" if c in {"waiting","missing"} else c,f"PR CI run {r.run_id} is {c}")
        if g==Gate.MERGE:
            n=m.get("pr_number")
            if not n:return Decision("blocked","pr_number is required")
            return Decision("passed",f"PR #{n} merged") if self.github.pull(int(n)).get("merged") else Decision("wait",f"PR #{n} not merged")
        if g==Gate.MAIN_CI:
            r=self.github.latest_run(branch=m.get("default_branch","main"),event="push",workflow=m.get("ci_workflow"),head_sha=m.get("merge_sha"));c=self.github.classify_run(r)
            return Decision("wait" if c in {"waiting","missing"} else c,f"main CI run {r.run_id} is {c}")
        if g==Gate.RELEASE:
            tag=m.get("release_tag")
            if not tag:return Decision("blocked","release_tag is required")
            rel=self.github.release_by_tag(tag);return Decision("passed",f"release {tag} exists") if rel and not rel.get("draft") else Decision("wait",f"release {tag} missing")
        if g==Gate.RUNTIME_VERIFY:
            v=m.get("verification",{})
            if v.get("passed") is True:return Decision("passed",v.get("reason","final verification passed"))
            if v.get("passed") is False:return Decision("failed",v.get("reason","final verification failed"))
            return Decision("wait","final verification evidence not recorded")
        return Decision("worker",f"{g.value} requires host/reasoning action")
    def reconcile_once(self,task):
        canonicalize_task(task)
        if task.state!=State.EXECUTE:return Decision("wait",f"{task.state.value} is not an execution state")
        step=next((x for x in task.plan if x.required and x.status not in {GateStatus.PASSED,GateStatus.SKIPPED}),None)
        if not step:return Decision("ready","all required gates satisfied")
        d=self.decide_gate(task,step)
        if d.action=="passed":gate(task,step.gate,GateStatus.PASSED,d.reason)
        elif d.action=="failed":gate(task,step.gate,GateStatus.FAILED,d.reason)
        elif d.action=="blocked":task.state=State.BLOCKED;task.record(d.reason)
        elif d.action=="wait":step.status=GateStatus.WAITING
        canonicalize_task(task)
        return d
