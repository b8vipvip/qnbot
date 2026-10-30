"""Fail-closed, Git-SHA-bound smart update plan for GPTAuto consumers.

This module only classifies changes. It never dispatches workflows, modifies Git,
copies files into containers, or deploys to production.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
from dataclasses import asdict, dataclass
from pathlib import Path

SCHEMA = "gptauto.smart-update/v1"
SHA_RE = re.compile(r"^[0-9a-f]{40}$")
CONSUMERS = {"chat2api", "qnbot", "mv3c"}

@dataclass(frozen=True)
class UpdatePlan:
    schema: str
    repository: str
    from_sha: str
    target_sha: str
    mode: str
    required_checks: tuple[str, ...]
    changed_files: tuple[str, ...]
    reasons: tuple[str, ...]
    manual_deploy_only: bool = True
    full_build_required: bool = False

    def to_dict(self) -> dict:
        result = asdict(self)
        result["required_checks"] = list(self.required_checks)
        result["changed_files"] = list(self.changed_files)
        result["reasons"] = list(self.reasons)
        return result


def _valid_path(name: str) -> bool:
    return bool(name and not name.startswith(("/", "./")) and
                "\\" not in name and "\x00" not in name and
                all(part not in {"", ".", ".."} for part in name.split("/")))


def _metadata_only(name: str) -> bool:
    return (name.endswith((".md", ".rst")) or
            name.startswith(("docs/", "tests/", ".github/gptauto/", ".github/actions/")) or
            name in {"AGENTS.md", ".gitignore"})


def _hot_file(repo: str, path: str, status: str) -> bool:
    if status != "M":
        return False
    if repo == "chat2api":
        if not path.startswith("app/") or path in {
            "app/runtime_contract.py", "app/server_update_patch.py",
            "app/entry.py", "app/config.py",
        }:
            return False
        return path.endswith((".py", ".js", ".css", ".html"))
    if repo == "qnbot":
        prefix = "services/api-control-plane/"
        if not path.startswith(prefix) or path in {
            prefix + "bootstrap.py", prefix + "version_update_admin.py",
            prefix + "app.py",
        }:
            return False
        part = path.removeprefix(prefix)
        return (("/" not in part and part.endswith(".py")) or
                (part.startswith("static/") and
                 part.endswith((".js", ".css", ".html"))))
    # mv3c requires a verified official release image for now. Plain source
    # overlays must not impersonate its immutable Release image proof.
    return False


def plan_update(repo: str, from_sha: str, target_sha: str,
                changes: list[tuple[str, str]]) -> UpdatePlan:
    repo = repo.rsplit("/", 1)[-1]
    if repo not in CONSUMERS:
        raise ValueError("Unsupported consumer repository")
    if not SHA_RE.fullmatch(from_sha) or not SHA_RE.fullmatch(target_sha):
        raise ValueError("An exact 40-character Git commit SHA is required")
    if len(changes) > 500:
        raise ValueError("Excessive change count; manual review required")
    seen = set()
    paths = []
    risky = []
    client = []
    meta = []
    hot = []
    for status, path in changes:
        if not _valid_path(path) or status not in {"A", "M", "D", "T"}:
            risky.append(path)
            continue
        if path in seen:
            raise ValueError("Duplicate changed path")
        seen.add(path)
        paths.append(path)
        if _metadata_only(path):
            meta.append(path)
        elif repo == "qnbot" and path.startswith(("src/", "tools/qn_discovery_lab/")):
            client.append(path)
        elif _hot_file(repo, path, status):
            hot.append(path)
        else:
            risky.append(path)

    paths.sort()
    if from_sha == target_sha:
        if changes:
            raise ValueError("Non-empty change set for identical SHAs")
        mode, checks, reasons = "no-op", (), ("Target SHA already deployed",)
    elif not paths:
        raise ValueError("No changed files between distinct commits")
    elif risky:
        mode = "verified-image"
        checks = {
            "chat2api": ("CI", "Production image smoke"),
            "qnbot": ("API control plane CI",),
            "mv3c": ("mv3c validation", "mv3c source Docker build"),
        }[repo]
        if client:
            checks = checks + ("Windows x64 release build",)
        reasons = ("Runtime/packaging/unknown changes require an image-level proof",)
    elif client and (hot or meta):
        mode = "verified-image"
        checks = ("API control plane CI", "Windows x64 release build")
        reasons = ("Mixed server and Windows client changes require both gates",)
    elif client:
        mode = "client-build"
        checks = ("Windows x64 release build",)
        reasons = ("Compiled Windows client cannot be deployed by copying source files",)
    elif hot:
        mode = "runtime-hotfix"
        checks = {
            "chat2api": ("CI",),
            "qnbot": ("API control plane CI",),
        }[repo]
        reasons = ("Only allowlisted runtime files changed; no manifest or Dockerfile changed",)
    else:
        mode = "checks-only"
        checks = {
            "chat2api": ("CI",),
            "qnbot": ("API control plane CI",),
            "mv3c": ("mv3c validation",),
        }[repo]
        reasons = ("Metadata or test changes do not alter production runtime",)

    return UpdatePlan(
        schema=SCHEMA, repository=repo,
        from_sha=from_sha, target_sha=target_sha, mode=mode,
        required_checks=checks, changed_files=tuple(paths),
        reasons=reasons, full_build_required=(mode == "verified-image"),
    )


def _run(repo_path: Path, *args: str) -> str:
    return subprocess.check_output(
        ["git", "-C", str(repo_path), *args],
        text=True, stderr=subprocess.PIPE, timeout=25)


def plan_for_git(repo_path: Path, repository: str,
                 from_sha: str, target_sha: str) -> UpdatePlan:
    if not SHA_RE.fullmatch(from_sha) or not SHA_RE.fullmatch(target_sha):
        raise ValueError("Invalid commit SHA")
    if _run(repo_path, "rev-parse", "--verify", "HEAD").strip() not in {
            from_sha, target_sha}:
        # This is a read-only classification utility; it does not checkout.
        raise ValueError("Working tree is not at one of the declared revisions")
    if subprocess.run(["git", "-C", str(repo_path), "merge-base",
                       "--is-ancestor", from_sha, target_sha],
                      timeout=25, check=False).returncode != 0:
        raise ValueError("Target is not a fast-forward descendant")
    output = _run(repo_path, "diff", "--name-status", "--no-renames",
                  from_sha, target_sha)
    changes = []
    for line in output.splitlines():
        if "\t" not in line:
            raise ValueError("Unparseable Git diff record")
        status, path = line.split("\t", 1)
        changes.append((status, path))
    return plan_update(repository, from_sha, target_sha, changes)


def main() -> int:
    parser = argparse.ArgumentParser(description="GPTAuto read-only smart update classifier")
    parser.add_argument("--repo", required=True, choices=sorted(CONSUMERS))
    parser.add_argument("--worktree", required=True, type=Path)
    parser.add_argument("--from-sha", required=True)
    parser.add_argument("--target-sha", required=True)
    args = parser.parse_args()
    try:
        plan = plan_for_git(args.worktree, args.repo, args.from_sha, args.target_sha)
        print(json.dumps(plan.to_dict(), ensure_ascii=False, sort_keys=True))
    except (ValueError, subprocess.SubprocessError, OSError) as exc:
        print(json.dumps({"schema": SCHEMA, "error": str(exc),
                          "mode": "blocked"}, ensure_ascii=False))
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
