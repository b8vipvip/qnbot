from dataclasses import dataclass
from .model import CriterionStatus, Gate, GateStatus, State, Task
class ProtocolError(RuntimeError):pass
@dataclass(frozen=True)
class Signal: name:str; reason:str=""
def start(task):
    if task.state!=State.GOAL:raise ProtocolError("task is not at GOAL")
    task.state=State.PLAN;task.record("goal accepted; planning required");return task
def plan_ready(task):
    if task.state!=State.PLAN or not task.plan:raise ProtocolError("a non-empty dynamic plan is required")
    task.state=State.EXECUTE;task.record("dynamic plan accepted");return task
def gate(task,name,status,evidence=""):
    if task.state!=State.EXECUTE:raise ProtocolError("gates can only be updated while EXECUTE")
    step=next((x for x in task.plan if x.gate==name),None)
    if not step:raise ProtocolError(f"gate {name.value} is not part of this task")
    step.status=status;step.evidence=evidence;task.record(f"{name.value}={status.value}: {evidence}".rstrip(),kind="gate",gate=name.value,status=status.value,evidence=evidence)
    if status==GateStatus.FAILED:
        task.repair_attempts+=1
        if task.repair_attempts>task.max_repair_attempts:task.state=State.BLOCKED;task.record("repair budget exhausted",kind="blocked")
    return task
def begin_verify(task):
    if task.state!=State.EXECUTE:raise ProtocolError("task is not executing")
    if not task.required_gates_satisfied():raise ProtocolError("required gates are not satisfied")
    task.state=State.VERIFY;task.record("required dynamic gates satisfied; verifying Definition of Done");return task
def criterion(task,index,status,evidence=""):
    if task.state!=State.VERIFY:raise ProtocolError("criteria can only be verified in VERIFY")
    if status==CriterionStatus.PASSED and not evidence.strip():raise ProtocolError("passed criterion requires evidence")
    c=task.definition_of_done[index];c.status=status;c.evidence=evidence;task.record(f"DoD[{index}]={status.value}: {evidence}".rstrip(),kind="criterion",status=status.value,evidence=evidence);return task
def finish(task):
    if task.state!=State.VERIFY:raise ProtocolError("task is not verifying")
    if not task.criteria_satisfied():raise ProtocolError("all Definition of Done criteria need passed evidence")
    task.state=State.DONE;task.record("Definition of Done satisfied",kind="done");return task
def block(task,reason):task.state=State.BLOCKED;task.record(reason,kind="blocked");return task
def is_complete(task):return task.state==State.DONE and task.required_gates_satisfied() and task.criteria_satisfied()
