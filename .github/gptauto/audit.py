from __future__ import annotations
from datetime import datetime
import json
from pathlib import Path
from .engine import is_complete
from .model import Task

def _safe_id(task_id:str)->str:return "".join(c if c.isalnum() or c in "-_." else "_" for c in task_id)
def task_dir(root:str|Path,task:Task)->Path:return Path(root)/_safe_id(task.task_id)
def write_audit(task:Task,root:str|Path=".gptauto/logs")->dict[str,str]:
    out=task_dir(root,task);out.mkdir(parents=True,exist_ok=True)
    state=out/"state.json";events=out/"events.jsonl";human=out/"task.log";summary=out/"summary.md"
    state.write_text(json.dumps(task.to_dict(),ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    events.write_text("".join(json.dumps(e.__dict__,ensure_ascii=False)+"\n" for e in task.history),encoding="utf-8")
    lines=[f"[GPTAuto] Task: {task.task_id}",f"[Goal] {task.goal}",f"[Repository] {task.repository}","",f"[State] {task.state.value}","[Dynamic Gates]"]
    for s in task.plan:lines.append(f" - {s.gate.value}: {s.status.value} required={str(s.required).lower()} evidence={s.evidence or '-'}")
    lines+=["","[Definition of Done]"]
    for i,c in enumerate(task.definition_of_done):lines.append(f" - [{i}] {c.status.value}: {c.text} evidence={c.evidence or '-'}")
    lines+=["","[Events]"]
    for e in task.history:lines.append(f"[{e.at}] {e.kind.upper()} {e.reason}")
    lines+=["",f"[GPTAuto] STATE = {task.state.value}",f"Complete: {'YES' if is_complete(task) else 'NO'}",f"Repairs: {task.repair_attempts}"]
    human.write_text("\n".join(lines)+"\n",encoding="utf-8")
    passed=sum(1 for x in task.plan if x.status.value in {"passed","skipped"} and x.required);required=sum(1 for x in task.plan if x.required)
    dod=sum(1 for x in task.definition_of_done if x.status.value=="passed")
    md=f"""# GPTAuto Task Summary

- Task: `{task.task_id}`
- Repository: `{task.repository}`
- State: **{task.state.value}**
- Complete: **{'YES' if is_complete(task) else 'NO'}**
- Required gates: **{passed}/{required}**
- DoD: **{dod}/{len(task.definition_of_done)}**
- Repairs: **{task.repair_attempts}**
- Created: {task.created_at}
- Updated: {task.updated_at}

## Goal

{task.goal}

## How to analyze this task

Give ChatGPT the repository name plus task ID `{task.task_id}`. If repository access is available, ChatGPT can inspect `.gptauto/logs/{_safe_id(task.task_id)}/`. Otherwise attach `task.log`, `summary.md`, or `state.json`.
"""
    summary.write_text(md,encoding="utf-8")
    return {"directory":str(out),"task_log":str(human),"state":str(state),"events":str(events),"summary":str(summary)}
