from __future__ import annotations
import argparse,hashlib,json
from pathlib import Path
from .audit import write_audit
from .model import Criterion,CriterionStatus,Event,Gate,GateStatus,GateStep,State,Task,utcnow

def observed_task_id(repo,pr_number="",head_sha=""):
    anchor=("pr:"+str(pr_number)) if str(pr_number).strip() else ("sha:"+str(head_sha))
    return "GA-"+hashlib.sha256((repo+":"+anchor).encode()).hexdigest()[:12]

def capture(repo,event,default_branch="",branch="",head_sha="",pr_number="",pr_title="",pr_state="",pr_merged="false",merge_sha="",run_id="",actor="",log_root=".gptauto/logs"):
    tid=observed_task_id(repo,pr_number,head_sha)
    goal=pr_title.strip() or ("Observe repository change "+head_sha[:12])
    merged=str(pr_merged).lower()=="true"
    plan=[GateStep(Gate.INSPECT,status=GateStatus.PASSED,evidence=f"GitHub {event} event"),GateStep(Gate.COMMIT,status=GateStatus.PASSED,evidence=head_sha)]
    if pr_number:
        plan.append(GateStep(Gate.PR,status=GateStatus.PASSED,evidence=f"PR #{pr_number}"))
        plan.append(GateStep(Gate.MERGE,status=GateStatus.PASSED if merged else GateStatus.WAITING,evidence=merge_sha if merged else "PR not merged at observation time"))
    dod=[Criterion("Repository change is traceable from GitHub evidence",CriterionStatus.PASSED,f"event={event}; head={head_sha}; pr={pr_number or '-'}")]
    state=State.DONE if (not pr_number or merged) else State.EXECUTE
    t=Task(tid,goal,repo,dod,state=state,plan=plan,metadata={"provenance":"repository_observer","task_type":"observed","branch":branch,"head_sha":head_sha,"pr_number":pr_number,"merge_sha":merge_sha,"run_id":run_id,"artifact_name":"gptauto-"+tid,"actor":actor,"default_branch":default_branch})
    t.history=[Event(utcnow(),state.value,"repository event captured",kind="observation",evidence=json.dumps({"event":event,"actor":actor,"pr_state":pr_state},ensure_ascii=False))]
    paths=write_audit(t,log_root)
    return {"task_id":tid,"state":state.value,"provenance":"repository_observer","artifact_name":"gptauto-"+tid,"paths":paths}

def main():
    p=argparse.ArgumentParser();s=p.add_subparsers(dest="cmd",required=True);c=s.add_parser("capture")
    for name in ["event","repo","default-branch","branch","head-sha","pr-number","pr-title","pr-state","pr-merged","merge-sha","run-id","actor"]:c.add_argument("--"+name,default="")
    a=p.parse_args();print(json.dumps(capture(a.repo,a.event,a.default_branch,a.branch,a.head_sha,a.pr_number,a.pr_title,a.pr_state,a.pr_merged,a.merge_sha,a.run_id,a.actor),ensure_ascii=False))
if __name__=="__main__":main()
