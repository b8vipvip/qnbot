"""Exact SHA-bound consumer PR merge gate for GitHub Actions.

Fail closed. A PR must have successfully completed all product-required
pull_request workflow validations before the sync workflow may merge it.
"""
from __future__ import annotations

import argparse
import json
import re
import sys

REQUIRED = {
    "chat2api": ("CI",),
    "qnbot": ("CI",),
    "mv3c": ("CI", "mv3c validation", "mv3c source Docker build"),
    "GPTWork": ("CI", "Private Core Boundary", "Actions Policy Check"),
}
SHA = re.compile(r"^[a-f0-9]{40}$")
FAILED = {"failure", "cancelled", "timed_out", "action_required",
          "startup_failure", "stale"}


def verdict(repo: str, sha: str, workflow_runs: list[dict]) -> dict:
    if repo not in REQUIRED or not SHA.fullmatch(sha):
        return {"status": "blocked", "reason": "unsupported consumer or invalid SHA"}
    missing = []
    failed = []
    for required in REQUIRED[repo]:
        matching = [
            r for r in workflow_runs
            if r.get("name") == required and r.get("head_sha") == sha
            and r.get("event") == "pull_request"
        ]
        if any(r.get("status") == "completed" and
               r.get("conclusion") == "success" for r in matching):
            continue
        if any(r.get("status") == "completed" and
               r.get("conclusion") in FAILED for r in matching):
            failed.append(required)
        else:
            missing.append(required)
    if failed:
        return {"status": "failed", "failed": failed, "missing": missing}
    if missing:
        return {"status": "pending", "missing": missing}
    return {"status": "passed", "verified_sha": sha,
            "workflows": list(REQUIRED[repo])}


def main() -> int:
    p = argparse.ArgumentParser(description="Fail-closed GPTAuto Consumer Sync PR CI gate")
    p.add_argument("--repository", required=True)
    p.add_argument("--sha", required=True)
    args = p.parse_args()
    try:
        payload = json.load(sys.stdin)
        result = verdict(args.repository.rsplit("/", 1)[-1], args.sha,
                         payload["workflow_runs"])
    except (ValueError, TypeError, KeyError, AttributeError):
        result = {"status": "blocked", "reason": "invalid GitHub workflow run data"}
    print(json.dumps(result, ensure_ascii=False))
    return 0 if result["status"] == "passed" else (1 if result["status"] == "pending" else 2)


if __name__ == "__main__":
    sys.exit(main())
