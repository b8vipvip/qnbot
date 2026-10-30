"""Read-only proof of successful GitHub Actions validation at an exact SHA."""
from __future__ import annotations
import argparse
import json
import re
import sys

try:
    from .runtime_hotfix import github_ci_succeeded
except ImportError:
    from runtime_hotfix import github_ci_succeeded

SHA = re.compile(r"^[0-9a-f]{40}$")
REQUIRED = {
    "chat2api": {"CI", "Production image smoke"},
    "qnbot": {"API control plane CI", "Windows x64 release build"},
}


def main() -> int:
    p = argparse.ArgumentParser(description="Verify successful CI for one exact SHA")
    p.add_argument("--repo", required=True, choices=sorted(REQUIRED))
    p.add_argument("--sha", required=True)
    p.add_argument("--workflow", required=True)
    a = p.parse_args()
    if not SHA.fullmatch(a.sha) or a.workflow not in REQUIRED[a.repo]:
        print(json.dumps({"verified": False, "reason": "Invalid SHA/workflow"}))
        return 2
    try:
        passed = github_ci_succeeded(a.repo, a.sha, (a.workflow,))
    except Exception as exc:
        print(json.dumps({"verified": False, "reason": "GitHub evidence unavailable",
                          "error_type": type(exc).__name__}))
        return 2
    print(json.dumps({"verified": passed, "sha": a.sha,
                      "workflow": a.workflow}))
    return 0 if passed else 2


if __name__ == "__main__":
    sys.exit(main())
