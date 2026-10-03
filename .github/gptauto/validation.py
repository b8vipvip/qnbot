from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import time
import tomllib
from dataclasses import asdict, dataclass
from pathlib import Path


REPORT_SCHEMA = "gptauto.targeted-validation/v1"


@dataclass
class ValidationResult:
    name: str
    status: str
    detail: str = ""
    seconds: float = 0.0


def changed_files(repo: Path) -> list[str]:
    proc = subprocess.run(
        ["git", "diff", "--name-only", "--diff-filter=ACMRTUXB"],
        cwd=repo,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if proc.returncode != 0:
        return []
    return sorted({line.strip() for line in proc.stdout.splitlines() if line.strip()})


def _cargo_root(repo: Path, rel_path: str) -> Path | None:
    path = (repo / rel_path).resolve()
    current = path.parent if path.suffix else path
    repo = repo.resolve()
    while current == repo or repo in current.parents:
        if (current / "Cargo.toml").is_file():
            return current
        if current == repo:
            break
        current = current.parent
    return None


def validation_plan(repo: Path, files: list[str]) -> dict:
    json_files = [p for p in files if p.lower().endswith(".json")]
    toml_files = [p for p in files if p.lower().endswith(".toml")]
    python_files = [p for p in files if p.lower().endswith(".py")]
    shell_files = [p for p in files if p.lower().endswith(".sh")]
    powershell_files = [p for p in files if p.lower().endswith(".ps1")]
    node_files = [p for p in files if p.lower().endswith((".js", ".mjs", ".cjs"))]
    cargo_roots = sorted(
        {
            str(root.relative_to(repo.resolve())).replace("\\", "/") or "."
            for p in files
            if p.lower().endswith((".rs", ".toml", ".lock"))
            for root in [_cargo_root(repo, p)]
            if root is not None
        }
    )
    return {
        "files": files,
        "json": json_files,
        "toml": toml_files,
        "python": python_files,
        "shell": shell_files,
        "powershell": powershell_files,
        "node": node_files,
        "cargo_roots": cargo_roots,
    }


def _run(name: str, command: list[str], repo: Path, *, timeout: int = 240) -> ValidationResult:
    started = time.monotonic()
    if shutil.which(command[0]) is None:
        return ValidationResult(name, "skipped", f"{command[0]} is unavailable", 0.0)
    try:
        proc = subprocess.run(
            command,
            cwd=repo,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            timeout=timeout,
            check=False,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        return ValidationResult(name, "failed", str(exc), round(time.monotonic() - started, 3))
    output = (proc.stdout or "")[-4000:]
    return ValidationResult(
        name,
        "passed" if proc.returncode == 0 else "failed",
        output,
        round(time.monotonic() - started, 3),
    )


def run_targeted_validation(repo: Path) -> dict:
    repo = repo.resolve()
    files = changed_files(repo)
    plan = validation_plan(repo, files)
    results: list[ValidationResult] = []

    for rel in plan["json"]:
        started = time.monotonic()
        try:
            json.loads((repo / rel).read_text(encoding="utf-8"))
            status, detail = "passed", "valid JSON"
        except (OSError, ValueError) as exc:
            status, detail = "failed", str(exc)
        results.append(ValidationResult(f"json:{rel}", status, detail, round(time.monotonic() - started, 3)))

    for rel in plan["toml"]:
        started = time.monotonic()
        try:
            with (repo / rel).open("rb") as handle:
                tomllib.load(handle)
            status, detail = "passed", "valid TOML"
        except (OSError, ValueError, tomllib.TOMLDecodeError) as exc:
            status, detail = "failed", str(exc)
        results.append(ValidationResult(f"toml:{rel}", status, detail, round(time.monotonic() - started, 3)))

    for rel in plan["python"]:
        started = time.monotonic()
        try:
            source = (repo / rel).read_text(encoding="utf-8")
            compile(source, rel, "exec")
            status, detail = "passed", "compiled in-memory"
        except (OSError, SyntaxError, UnicodeError) as exc:
            status, detail = "failed", str(exc)
        results.append(ValidationResult(f"python:{rel}", status, detail, round(time.monotonic() - started, 3)))

    for rel in plan["shell"]:
        results.append(_run(f"bash:{rel}", ["bash", "-n", rel], repo))

    ps_script = (
        "$tokens=$null; $errors=$null; "
        "[System.Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$tokens,[ref]$errors) | Out-Null; "
        "if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_ }; exit 1 }"
    )
    for rel in plan["powershell"]:
        results.append(_run(f"pwsh:{rel}", ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", ps_script, rel], repo))

    for rel in plan["node"]:
        results.append(_run(f"node:{rel}", ["node", "--check", rel], repo))

    for root in plan["cargo_roots"]:
        manifest = "Cargo.toml" if root == "." else f"{root}/Cargo.toml"
        root_path = repo if root == "." else repo / root
        locked = (root_path / "Cargo.lock").is_file()
        fmt = ["cargo", "fmt", "--manifest-path", manifest, "--all", "--", "--check"]
        results.append(_run(f"cargo-fmt:{root}", fmt, repo))
        test = ["cargo", "test"]
        if locked:
            test.append("--locked")
        test += ["--manifest-path", manifest, "--all-targets", "--no-fail-fast"]
        results.append(_run(f"cargo-test:{root}", test, repo, timeout=360))

    failed = [item for item in results if item.status == "failed"]
    return {
        "schema": REPORT_SCHEMA,
        "changed_files": files,
        "plan": plan,
        "checks": [asdict(item) for item in results],
        "passed": not failed,
        "failed_checks": [item.name for item in failed],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Run fast validators only for files changed by a GPTAuto repair patch")
    parser.add_argument("--repo", default=".")
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    payload = run_targeted_validation(Path(args.repo))
    report = Path(args.report)
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(payload, ensure_ascii=False))
    return 0 if payload["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
