from __future__ import annotations
from dataclasses import dataclass
import json, os, subprocess
from urllib.parse import quote

class GitHubError(RuntimeError): pass

@dataclass
class RunSummary:
    status: str
    conclusion: str | None
    run_id: int | None = None

class GitHubClient:
    """Thin gh-based adapter. Authentication comes from GH_TOKEN/GITHUB_TOKEN or gh auth."""
    def __init__(self, repository: str):
        self.repository=repository

    def _api(self, path: str, method: str="GET"):
        cmd=["gh","api"]
        if method!="GET": cmd += ["--method",method]
        cmd.append(path)
        env=dict(os.environ)
        if "GH_TOKEN" not in env and env.get("GITHUB_TOKEN"): env["GH_TOKEN"]=env["GITHUB_TOKEN"]
        try:
            out=subprocess.run(cmd,check=True,capture_output=True,text=True,env=env).stdout
        except (OSError, subprocess.CalledProcessError) as e:
            raise GitHubError(str(e)) from e
        return json.loads(out) if out.strip() else {}

    def pull(self, number: int):
        return self._api(f"repos/{self.repository}/pulls/{number}")

    def merge(self, number: int, method: str="squash"):
        return self._api(f"repos/{self.repository}/pulls/{number}/merge?merge_method={quote(method)}", "PUT")

    def release_by_tag(self, tag: str):
        try: return self._api(f"repos/{self.repository}/releases/tags/{quote(tag,safe='')}")
        except GitHubError: return None

    def latest_run(self, *, branch: str, event: str | None=None):
        suffix=f"?branch={quote(branch)}&per_page=50"
        data=self._api(f"repos/{self.repository}/actions/runs{suffix}")
        runs=data.get("workflow_runs",[])
        if event: runs=[r for r in runs if r.get("event")==event]
        if not runs: return RunSummary("missing",None,None)
        r=runs[0]
        return RunSummary(r.get("status","unknown"),r.get("conclusion"),r.get("id"))

    @staticmethod
    def classify_run(run: RunSummary) -> str:
        if run.status in {"queued","in_progress","waiting","requested","pending"}: return "waiting"
        if run.status=="missing": return "missing"
        if run.status=="completed" and run.conclusion=="success": return "passed"
        if run.status=="completed": return "failed"
        return "waiting"
