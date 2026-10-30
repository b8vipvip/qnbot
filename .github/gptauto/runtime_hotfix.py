"""Host-only transactional Docker runtime overlay for an exact verified Git SHA.

Not a workflow scheduler. Invoked exclusively by the already-locked product host
updater after Git fetch, plan classification and explicit administrator action.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
import time
import urllib.request
from pathlib import Path

try:
    from .smart_update import plan_for_git, _metadata_only
except ImportError:
    from smart_update import plan_for_git, _metadata_only

SHA = re.compile(r"^[0-9a-f]{40}$")
MAP = {
    "chat2api": ("app/", "/app/app/", "CHAT2API_BUILD_COMMIT"),
    "qnbot": ("services/api-control-plane/", "/app/", "CONTROL_PLANE_BUILD_COMMIT"),
}
PROVIDER = {"chat2api": "b8vipvip/chat2api", "qnbot": "b8vipvip/qnbot"}


class HotfixError(RuntimeError):
    pass


def run(*args: str, timeout: int = 30) -> str:
    p = subprocess.run(args, check=True, capture_output=True,
                       text=True, timeout=timeout)
    return p.stdout.strip()


def github_ci_succeeded(repo: str, sha: str, checks: tuple[str, ...]) -> bool:
    """Exact SHA and successful enabled workflow only; no CI evidence = no overlay."""
    url = ("https://api.github.com/repos/" + PROVIDER[repo] +
           "/actions/runs?head_sha=" + sha + "&per_page=100")
    token = os.getenv("GPTAUTO_GITHUB_TOKEN", "").strip()
    headers = {"Accept": "application/vnd.github+json",
               "User-Agent": "GPTAuto-Smart-Update-Host/1"}
    if token:
        headers["Authorization"] = "Bearer " + token
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers),
                                timeout=18) as reply:
        payload = json.load(reply)
    runs = payload.get("workflow_runs", [])
    for check in checks:
        if not any(item.get("name") == check and item.get("head_sha") == sha
                   and item.get("status") == "completed"
                   and item.get("conclusion") == "success"
                   and item.get("event") in {"push", "workflow_dispatch"}
                   for item in runs):
            return False
    return True


def file_sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def image_runtime_sha(container: dict, key: str) -> str:
    env = container.get("Config", {}).get("Env") or []
    for entry in env:
        if entry.startswith(key + "="):
            value = entry[len(key) + 1:]
            if SHA.fullmatch(value):
                return value
    return ""


def verify_current_container(container_id: str, from_sha: str,
                             state_path: Path, image: str, env_key: str) -> None:
    if state_path.is_file():
        state = json.loads(state_path.read_text(encoding="utf-8"))
        if (state.get("deployed_sha") == from_sha and
            state.get("container_id") == container_id and
            state.get("image_id") == image):
            try:
                stamp = run("docker", "exec", container_id,
                            "cat", "/app/.gptauto-runtime-sha")
            except subprocess.CalledProcessError:
                stamp = ""
            if stamp == from_sha:
                return
    info = json.loads(run("docker", "inspect", container_id))[0]
    if image_runtime_sha(info, env_key) != from_sha:
        raise HotfixError(
            "Running image is not proven to match the previous deployed SHA; "
            "a full verified image deployment is required")


def wait_health(url: str, tries: int = 45) -> bool:
    for _ in range(tries):
        try:
            with urllib.request.urlopen(url, timeout=4) as resp:
                if 200 <= resp.status < 300:
                    return True
        except Exception:
            pass
        time.sleep(2)
    return False


def apply_hotfix(repo: str, checkout: Path, from_sha: str, target_sha: str,
                 container_name: str, state_file: Path, health_url: str) -> dict:
    if os.geteuid() != 0:
        raise HotfixError("Hotfix requires the existing host updater's root privileges")
    if repo not in MAP or not health_url.startswith("http://127.0.0.1:"):
        raise HotfixError("Unsupported container or non-loopback health target")
    if not SHA.fullmatch(from_sha) or not SHA.fullmatch(target_sha):
        raise HotfixError("Untrusted Git SHA")

    plan = plan_for_git(checkout, repo, from_sha, target_sha)
    if plan.mode != "runtime-hotfix":
        raise HotfixError("Change requires a different deployment mode: " + plan.mode)
    if not github_ci_succeeded(repo, target_sha, plan.required_checks):
        raise HotfixError("Required GitHub CI is not successful for exact target SHA")

    info = json.loads(run("docker", "inspect", container_name))[0]
    cid = info["Id"]
    if not info.get("State", {}).get("Running", False):
        raise HotfixError("Target container is not running")
    image = info["Image"]
    source_prefix, destination_prefix, build_key = MAP[repo]
    verify_current_container(cid, from_sha, state_file, image, build_key)
    if not wait_health(health_url, tries=1):
        raise HotfixError("Previous version was unhealthy before update")

    paths = []
    for file in plan.changed_files:
        if _metadata_only(file):
            continue
        if not file.startswith(source_prefix):
            raise HotfixError("Source outside permitted runtime prefix")
        rel = file.removeprefix(source_prefix)
        if not rel or "/" in rel.strip("/") and repo == "qnbot" and not rel.startswith("static/"):
            raise HotfixError("Unexpected nested runtime file")
        host = checkout / file
        destination = destination_prefix + rel
        if not host.is_file() or host.is_symlink():
            raise HotfixError("Non-regular changed file: " + file)
        # No new files, symlinks or missing old files in this initial overlay
        # implementation; the classifier must add image mode for those.
        old_blob = subprocess.check_output(
            ["git", "-C", str(checkout), "show", from_sha + ":" + file],
            timeout=25)
        expected = file_sha(host)
        current = run("docker", "exec", cid, "sha256sum", destination).split()[0]
        if current != hashlib.sha256(old_blob).hexdigest():
            raise HotfixError("Live file does not match old Git content: " + file)
        paths.append((file, host, destination, expected))

    state_file.parent.mkdir(parents=True, exist_ok=True)
    backup_dir = Path(tempfile.mkdtemp(
        prefix="gptauto-hotfix-", dir=str(state_file.parent)))
    touched = []
    original_stamp = backup_dir / "original-runtime.sha"
    had_stamp = False
    try:
        try:
            run("docker", "cp", cid + ":/app/.gptauto-runtime-sha",
                str(original_stamp), timeout=20)
            had_stamp = True
        except subprocess.CalledProcessError:
            pass
        for i, (_file, host, destination, digest) in enumerate(paths):
            backup = backup_dir / (str(i) + ".original")
            run("docker", "cp", cid + ":" + destination, str(backup), timeout=35)
            touched.append((backup, destination))
            run("docker", "cp", str(host), cid + ":" + destination, timeout=35)
            actual = run("docker", "exec", cid, "sha256sum", destination).split()[0]
            if actual != digest:
                raise HotfixError("Docker copied file with unexpected SHA256: " + destination)
        code_path = "/app/app" if repo == "chat2api" else "/app"
        run("docker", "exec", cid, "python", "-m", "compileall", "-q",
            code_path, timeout=90)
        # Stamp file lives only inside this container, not in a shared data volume.
        # It disappears if the container is recreated from its previous image.
        stamp = backup_dir / "runtime.sha"
        stamp.write_text(target_sha + "\n", encoding="ascii")
        run("docker", "cp", str(stamp), cid + ":/app/.gptauto-runtime-sha")
        run("docker", "restart", cid, timeout=75)
        if not wait_health(health_url):
            raise HotfixError("New service failed the loopback health check")
        active = json.loads(run("docker", "inspect", cid))[0]
        if not active.get("State", {}).get("Running"):
            raise HotfixError("Container is not running after restart")
        metadata = {
            "schema": "gptauto.runtime-hotfix/v1", "repo": repo,
            "image_id": image, "container_id": cid,
            "previous_sha": from_sha, "deployed_sha": target_sha,
            "source": PROVIDER[repo], "changed_files": list(plan.changed_files),
            "backup": str(backup_dir),
        }
        temp = state_file.with_suffix(".tmp")
        temp.write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        os.replace(temp, state_file)
        return metadata
    except Exception as exc:
        failures = []
        for backup, destination in reversed(touched):
            try:
                run("docker", "cp", str(backup), cid + ":" + destination)
            except Exception as rollback_error:
                failures.append(str(rollback_error))
        try:
            if had_stamp:
                run("docker", "cp", str(original_stamp),
                    cid + ":/app/.gptauto-runtime-sha")
            else:
                run("docker", "exec", cid, "rm", "-f", "/app/.gptauto-runtime-sha")
            run("docker", "restart", cid, timeout=75)
            if not wait_health(health_url):
                failures.append("Health check failed after rollback")
        except Exception as rollback_error:
            failures.append(str(rollback_error))
        message = "Hotfix failed and rollback was attempted: " + str(exc)
        if failures:
            message += "; ROLLBACK UNVERIFIED: " + " | ".join(failures)
        raise HotfixError(message) from exc


def main() -> int:
    p = argparse.ArgumentParser(description="Transactional GPTAuto host-side Docker hotfix")
    p.add_argument("--repo", choices=sorted(MAP), required=True)
    p.add_argument("--worktree", type=Path, required=True)
    p.add_argument("--from-sha", required=True)
    p.add_argument("--target-sha", required=True)
    p.add_argument("--container", required=True)
    p.add_argument("--state-file", type=Path, required=True)
    p.add_argument("--health-url", required=True)
    p.add_argument("--baseline-only", action="store_true", help="Check whether an existing container can be hotpatched; no mutation")
    args = p.parse_args()
    if args.baseline_only:
        try:
            info = json.loads(run("docker", "inspect", args.container))[0]
            if not info.get("State", {}).get("Running"):
                raise HotfixError("Container is not running")
            verify_current_container(info["Id"], args.from_sha,
                                     args.state_file, info["Image"],
                                     MAP[args.repo][2])
            print(json.dumps({"mode": "runtime-hotfix",
                              "reason": "Exact live source baseline verified"}))
        except (OSError, ValueError, HotfixError, subprocess.SubprocessError) as error:
            # A mismatched baseline means the existing verified-image deployer
            # is required. Never attempt a live overlay on an unproven image.
            print(json.dumps({"mode": "verified-image", "reason": str(error)}))
        return 0
    try:
        result = apply_hotfix(args.repo, args.worktree, args.from_sha,
                              args.target_sha, args.container,
                              args.state_file, args.health_url)
        print(json.dumps(result, ensure_ascii=False))
    except (HotfixError, OSError, ValueError, subprocess.SubprocessError) as error:
        print(json.dumps({"error": str(error), "status": "failed"}, ensure_ascii=False))
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
