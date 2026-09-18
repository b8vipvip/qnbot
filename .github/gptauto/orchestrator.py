from dataclasses import dataclass
from .engine import gate
from .github import GitHubClient
from .model import Gate,GateStatus,State,Task
@dataclass(frozen=True)
class Decision:action:str;reason:str
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
        if task.state!=State.EXECUTE:return Decision("wait",f"{task.state.value} is not an execution state")
        step=next((x for x in task.plan if x.required and x.status not in {GateStatus.PASSED,GateStatus.SKIPPED}),None)
        if not step:return Decision("ready","all required gates satisfied")
        d=self.decide_gate(task,step)
        if d.action=="passed":gate(task,step.gate,GateStatus.PASSED,d.reason)
        elif d.action=="failed":gate(task,step.gate,GateStatus.FAILED,d.reason)
        elif d.action=="blocked":task.state=State.BLOCKED;task.record(d.reason)
        elif d.action=="wait":step.status=GateStatus.WAITING
        return d
