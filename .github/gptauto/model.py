from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from enum import Enum
import json
from pathlib import Path
from typing import Any

class State(str, Enum):
    GOAL="GOAL"; PLAN="PLAN"; EXECUTE="EXECUTE"; VERIFY="VERIFY"; DONE="DONE"; BLOCKED="BLOCKED"
class Gate(str, Enum):
    INSPECT="INSPECT"; IMPLEMENT="IMPLEMENT"; COMMIT="COMMIT"; PR="PR"; PR_CI="PR_CI"; MERGE="MERGE"; MAIN_CI="MAIN_CI"; RELEASE="RELEASE"; DEPLOY="DEPLOY"; RUNTIME_VERIFY="RUNTIME_VERIFY"
class GateStatus(str, Enum):
    PENDING="pending"; WAITING="waiting"; PASSED="passed"; FAILED="failed"; SKIPPED="skipped"
class CriterionStatus(str, Enum):
    PENDING="pending"; PASSED="passed"; FAILED="failed"
@dataclass
class Event: at:str; state:str; reason:str
@dataclass
class Criterion:
    text:str; status:CriterionStatus=CriterionStatus.PENDING; evidence:str=""
@dataclass
class GateStep:
    gate:Gate; required:bool=True; status:GateStatus=GateStatus.PENDING; evidence:str=""
@dataclass
class Task:
    task_id:str; goal:str; repository:str; definition_of_done:list[Criterion]
    state:State=State.GOAL; plan:list[GateStep]=field(default_factory=list)
    repair_attempts:int=0; max_repair_attempts:int=3
    metadata:dict[str,Any]=field(default_factory=dict); history:list[Event]=field(default_factory=list)
    def record(self,reason): self.history.append(Event(datetime.now(timezone.utc).isoformat(),self.state.value,reason))
    def required_gates_satisfied(self): return all((not s.required) or s.status in {GateStatus.PASSED,GateStatus.SKIPPED} for s in self.plan)
    def criteria_satisfied(self): return bool(self.definition_of_done) and all(c.status==CriterionStatus.PASSED for c in self.definition_of_done)
    def to_dict(self):
        d=asdict(self);d["state"]=self.state.value
        for c in d["definition_of_done"]:c["status"]=c["status"].value if hasattr(c["status"],"value") else c["status"]
        for s in d["plan"]:
            s["gate"]=s["gate"].value if hasattr(s["gate"],"value") else s["gate"];s["status"]=s["status"].value if hasattr(s["status"],"value") else s["status"]
        return d
    @classmethod
    def from_dict(cls,data):
        p=dict(data);p["state"]=State(p.get("state","GOAL"))
        p["definition_of_done"]=[Criterion(x) if isinstance(x,str) else Criterion(x["text"],CriterionStatus(x.get("status","pending")),x.get("evidence","")) for x in p.get("definition_of_done",[])]
        p["plan"]=[GateStep(Gate(x["gate"]),x.get("required",True),GateStatus(x.get("status","pending")),x.get("evidence","")) for x in p.get("plan",[])]
        p["history"]=[Event(**e) for e in p.get("history",[])];p.pop("release_required",None);return cls(**p)
    def save(self,path): Path(path).write_text(json.dumps(self.to_dict(),indent=2,ensure_ascii=False)+"\n",encoding="utf-8")
    @classmethod
    def load(cls,path): return cls.from_dict(json.loads(Path(path).read_text(encoding="utf-8")))
