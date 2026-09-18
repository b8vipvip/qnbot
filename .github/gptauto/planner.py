from __future__ import annotations
from dataclasses import dataclass
from .model import Criterion, Gate, GateStep, Task
@dataclass(frozen=True)
class PlanSpec: gates:list[Gate]; criteria:list[str]
class GoalPlanner:
    def infer(self,goal:str)->PlanSpec:
        g=goal.lower();gates=[Gate.INSPECT];criteria=[]
        change=any(k in g for k in ["fix","修复","改","implement","开发","嵌入","代码","readme","文档","doc"])
        if change:gates += [Gate.IMPLEMENT,Gate.COMMIT];criteria.append("请求的变更已经实现并形成可验证的提交")
        wants_ci=any(k in g for k in ["ci","actions","test","测试","编译","build","全绿"])
        wants_merge=any(k in g for k in ["merge","合并","main","master"])
        wants_release=any(k in g for k in ["release","发布","正式版","tag","版本"])
        wants_deploy=any(k in g for k in ["deploy","部署","上线"])
        if wants_ci:gates.append(Gate.PR_CI);criteria.append("用户要求的 CI/Actions 结果已经达到目标")
        if wants_merge:
            if Gate.COMMIT not in gates:gates.append(Gate.COMMIT)
            gates += [Gate.PR,Gate.MERGE];criteria.append("目标变更已经合并到目标分支")
        if wants_release:
            if Gate.PR not in gates:gates += [Gate.PR,Gate.PR_CI,Gate.MERGE,Gate.MAIN_CI]
            gates.append(Gate.RELEASE);criteria.append("请求的正式版本已经发布并可验证")
        if wants_deploy:gates.append(Gate.DEPLOY);criteria.append("请求的部署已经完成")
        if not criteria:criteria=["用户请求的最终结果已经有明确证据证明完成"]
        gates.append(Gate.RUNTIME_VERIFY)
        return PlanSpec(self._dedupe(gates),criteria)
    def apply(self,task:Task,gates:list[Gate]|None=None,criteria:list[str]|None=None):
        spec=self.infer(task.goal) if gates is None else PlanSpec(gates,criteria or [c.text for c in task.definition_of_done])
        task.plan=[GateStep(x) for x in self._dedupe(spec.gates)]
        if criteria is not None or not task.definition_of_done:task.definition_of_done=[Criterion(x) for x in (criteria or spec.criteria)]
        task.record("dynamic goal plan created");return task
    @staticmethod
    def _dedupe(items):
        out=[]
        for x in items:
            if x not in out:out.append(x)
        return out
