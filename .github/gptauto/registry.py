from __future__ import annotations
import json
from pathlib import Path
from .model import Task, utcnow

def registry_path(root=".gptauto"): return Path(root)/"tasks"/"index.json"
def register(task:Task, root=".gptauto", **refs):
    path=registry_path(root);path.parent.mkdir(parents=True,exist_ok=True)
    data={"version":1,"tasks":{}}
    if path.exists():
        try:data=json.loads(path.read_text(encoding="utf-8"))
        except Exception:pass
    entry=data.setdefault("tasks",{}).setdefault(task.task_id,{})
    entry.update({"task_id":task.task_id,"repository":task.repository,"goal":task.goal,"state":task.state.value,"created_at":task.created_at,"updated_at":utcnow()})
    entry.update({k:v for k,v in task.metadata.items() if k in {"branch","head_sha","pr_number","merge_sha","run_id","artifact_name"}})
    entry.update({k:v for k,v in refs.items() if v is not None and str(v).strip()})
    data["latest_task_id"]=task.task_id
    path.write_text(json.dumps(data,indent=2,ensure_ascii=False)+"\n",encoding="utf-8")
    return path
def latest(root=".gptauto"):
    path=registry_path(root)
    if not path.exists():return None
    data=json.loads(path.read_text(encoding="utf-8"));tid=data.get("latest_task_id")
    return data.get("tasks",{}).get(tid) if tid else None
