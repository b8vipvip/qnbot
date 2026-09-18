import sys,tempfile,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT))
from gptauto.audit import write_audit
from gptauto.engine import begin_verify,criterion,finish,gate,is_complete,plan_ready,start
from gptauto.model import CriterionStatus,Gate,GateStatus,Task
from gptauto.planner import GoalPlanner
class GPTAutoV031Tests(unittest.TestCase):
    def make(self,goal):
        t=Task("t",goal,"b8vipvip/qnbot",[]);start(t);GoalPlanner().apply(t);plan_ready(t);return t
    def test_merge_does_not_force_release(self):
        t=self.make("完成修改并合并到 master");gs=[x.gate for x in t.plan];self.assertIn(Gate.MERGE,gs);self.assertNotIn(Gate.RELEASE,gs)
    def test_ci_goal_does_not_force_release(self):
        t=self.make("修复 Actions 直到 CI 全绿");self.assertIn(Gate.PR_CI,[x.gate for x in t.plan]);self.assertNotIn(Gate.RELEASE,[x.gate for x in t.plan])
    def test_done_requires_dod_evidence(self):
        t=self.make("修改文档")
        for s in t.plan:gate(t,s.gate,GateStatus.PASSED,"ok")
        begin_verify(t);self.assertFalse(is_complete(t))
        for i in range(len(t.definition_of_done)):criterion(t,i,CriterionStatus.PASSED,"verified")
        finish(t);self.assertTrue(is_complete(t))
    def test_audit_bundle_is_generated(self):
        t=self.task("修改 README 文档") if hasattr(self,"task") else self.make("修改文档")
        with tempfile.TemporaryDirectory() as d:
            paths=write_audit(t,d)
            self.assertTrue(Path(paths["task_log"]).exists())
            self.assertTrue(Path(paths["state"]).exists())
            self.assertTrue(Path(paths["events"]).exists())
            self.assertTrue(Path(paths["summary"]).exists())
if __name__=="__main__":unittest.main()
