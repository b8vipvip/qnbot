from __future__ import annotations
import json
from pathlib import Path
from .model import Task, utcnow

REF_KEYS={"branch","head_sha","pr_number","merge_sha","run_id","artifact_name"}
def registry_path(root=".gptauto"): return Path(root)/"tasks"/"index.json"
def register(task:Task, root=".gptauto", **refs):
    path=registry_path(root);path.parent.mkdir(parents=True,exist_ok=True)
    data={"version":2,"tasks":{}}
    if path.exists():
        try:data=json.loads(path.read_text(encoding="utf-8"))
        except Exception:pass
    entry=data.setdefault("tasks",{}).setdefault(task.task_id,{})
    entry.update({"task_id":task.task_id,"repository":task.repository,"goal":task.goal,"state":task.state.value,"created_at":task.created_at,"updated_at":utcnow()})
    entry.update({k:v for k,v in task.metadata.items() if k in REF_KEYS})
    entry.update({k:v for k,v in refs.items() if k in REF_KEYS and v is not None and str(v).strip()})
    data["latest_task_id"]=task.task_id
    path.write_text(json.dumps(data,indent=2,ensure_ascii=False)+"\n",encoding="utf-8")
    return path
def latest(root=".gptauto"):
    path=registry_path(root)
    if not path.exists():return None
    data=json.loads(path.read_text(encoding="utf-8"));tid=data.get("latest_task_id")
    return data.get("tasks",{}).get(tid) if tid else None
def diagnostic_report(root=".gptauto"):
    item=latest(root)
    if not item:return {"found":False,"message":"No registered GPTAuto task found."}
    tid=item["task_id"]
    return {"found":True,"task_id":tid,"repository":item.get("repository"),"goal":item.get("goal"),"state":item.get("state"),"artifact_name":item.get("artifact_name") or "gptauto-"+tid,"references":{k:item.get(k) for k in sorted(REF_KEYS) if item.get(k)},"analysis_prompt":f"@GitHub 检查 {item.get('repository')} 的 GPTAuto 任务 {tid}，定位对应 PR、Actions Run 和 Artifact，读取完整日志并验证 GPTAuto 是否按最终目标持续执行；列出异常、缺失证据和需要修复的地方。"}
