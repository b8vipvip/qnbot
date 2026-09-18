import argparse,json,uuid
from .audit import write_audit
from .engine import begin_verify,criterion,finish,gate,is_complete,plan_ready,start
from .model import Criterion,CriterionStatus,Gate,GateStatus,Task
from .planner import GoalPlanner

def persist(t,path,log_root):
    t.save(path);return write_audit(t,log_root)
def main():
    p=argparse.ArgumentParser(prog="gptauto");p.add_argument("--log-root",default=".gptauto/logs");s=p.add_subparsers(dest="command",required=True)
    i=s.add_parser("init");i.add_argument("--goal",required=True);i.add_argument("--repo",required=True);i.add_argument("--done",action="append",default=[]);i.add_argument("--gate",action="append",default=[]);i.add_argument("--out",default=".gptauto/task.json")
    st=s.add_parser("status");st.add_argument("task")
    gp=s.add_parser("gate");gp.add_argument("task");gp.add_argument("name");gp.add_argument("status",choices=[x.value for x in GateStatus]);gp.add_argument("--evidence",default="")
    vf=s.add_parser("verify");vf.add_argument("task");vf.add_argument("index",type=int);vf.add_argument("status",choices=[x.value for x in CriterionStatus]);vf.add_argument("--evidence",default="")
    fn=s.add_parser("finish");fn.add_argument("task")
    lg=s.add_parser("log");lg.add_argument("task")
    a=p.parse_args()
    if a.command=="init":
        tid="GA-"+uuid.uuid4().hex[:12];t=Task(tid,a.goal,a.repo,[Criterion(x) for x in a.done]);t.record("goal contract created",kind="goal");start(t)
        gates=[Gate(x) for x in a.gate] if a.gate else None;GoalPlanner().apply(t,gates,a.done or None);plan_ready(t);paths=persist(t,a.out,a.log_root);print(json.dumps(paths,ensure_ascii=False));return 0
    t=Task.load(a.task)
    if a.command=="status":
        print(json.dumps({"task_id":t.task_id,"state":t.state.value,"complete":is_complete(t),"repairs":t.repair_attempts,"plan":[{"gate":x.gate.value,"status":x.status.value,"required":x.required,"evidence":x.evidence} for x in t.plan],"definition_of_done":[{"text":x.text,"status":x.status.value,"evidence":x.evidence} for x in t.definition_of_done]},ensure_ascii=False));return 0
    if a.command=="log":print(json.dumps(write_audit(t,a.log_root),ensure_ascii=False));return 0
    if a.command=="gate":gate(t,Gate(a.name),GateStatus(a.status),a.evidence)
    elif a.command=="verify":
        if t.state.value=="EXECUTE":begin_verify(t)
        criterion(t,a.index,CriterionStatus(a.status),a.evidence)
    elif a.command=="finish":finish(t)
    paths=persist(t,a.task,a.log_root);print(json.dumps({"state":t.state.value,"logs":paths},ensure_ascii=False));return 0
if __name__=="__main__":raise SystemExit(main())
