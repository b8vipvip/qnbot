from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Callable, Iterable


REPORT_SCHEMA = "gptauto.repair-report/v1"
DEFAULT_RULES = ".gptauto/repair-rules.json"


@dataclass
class FixerResult:
    name: str
    status: str
    detail: str = ""


def _write_report(path: Path, *, changed_files: list[str], results: list[FixerResult], errors: list[str]) -> None:
    payload = {
        "schema": REPORT_SCHEMA,
        "deterministic_changed": bool(changed_files),
        "changed_files": sorted(set(changed_files)),
        "fixers": [asdict(item) for item in results],
        "errors": errors,
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def _changed_files(repo: Path) -> list[str]:
    proc = subprocess.run(
        ["git", "status", "--porcelain"],
        cwd=repo,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if proc.returncode != 0:
        return []
    files: list[str] = []
    for line in proc.stdout.splitlines():
        if len(line) < 4:
            continue
        path = line[3:].strip()
        if " -> " in path:
            path = path.split(" -> ", 1)[1]
        files.append(path)
    return files


def _single_group(pattern: str, text: str, *, label: str) -> tuple[re.Match[str], str]:
    regex = re.compile(pattern, re.MULTILINE)
    matches = list(regex.finditer(text))
    if len(matches) != 1:
        raise ValueError(f"{label} must match exactly once; matched {len(matches)} times")
    match = matches[0]
    if match.lastindex != 1:
        raise ValueError(f"{label} regex must contain exactly one capture group")
    return match, match.group(1)


def _replace_group(text: str, pattern: str, value: str, *, label: str) -> tuple[str, bool]:
    match, current = _single_group(pattern, text, label=label)
    if current == value:
        return text, False
    start, end = match.span(1)
    return text[:start] + value + text[end:], True


def _apply_version_sync(repo: Path, config: dict, log_text: str) -> tuple[list[FixerResult], list[str]]:
    results: list[FixerResult] = []
    errors: list[str] = []
    groups = config.get("version_sync", [])
    if not isinstance(groups, list):
        return results, ["version_sync must be a list"]

    for index, group in enumerate(groups):
        name = str(group.get("name") or f"version-sync-{index + 1}")
        try:
            patterns = group.get("when_log_matches") or []
            if patterns and not any(re.search(str(pattern), log_text, re.I | re.M) for pattern in patterns):
                results.append(FixerResult(name, "skipped", "failure log did not match rule trigger"))
                continue

            source = group["source"]
            source_path = repo / str(source["file"])
            source_text = source_path.read_text(encoding="utf-8")
            _, version = _single_group(str(source["regex"]), source_text, label=f"{name}.source")

            changed = 0
            targets = group.get("targets") or []
            if not targets:
                raise ValueError(f"{name} has no targets")
            for target in targets:
                target_path = repo / str(target["file"])
                target_text = target_path.read_text(encoding="utf-8")
                updated, did_change = _replace_group(
                    target_text,
                    str(target["regex"]),
                    version,
                    label=f"{name}:{target['file']}",
                )
                if did_change:
                    target_path.write_text(updated, encoding="utf-8")
                    changed += 1
            results.append(FixerResult(name, "changed" if changed else "clean", f"canonical version {version}; updated {changed} target(s)"))
        except (KeyError, OSError, ValueError, re.error) as exc:
            message = f"{name}: {exc}"
            errors.append(message)
            results.append(FixerResult(name, "error", str(exc)))
    return results, errors


def _default_runner(command: list[str], repo: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        command,
        cwd=repo,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        timeout=180,
        check=False,
    )


def _tool_available(command: str) -> bool:
    return shutil.which(command) is not None


def _formatter_candidates(repo: Path, log_text: str) -> Iterable[tuple[str, list[str]]]:
    lowered = log_text.lower()
    if (repo / "Cargo.toml").exists() and ("cargo fmt" in lowered or "rustfmt" in lowered):
        yield "rustfmt", ["cargo", "fmt", "--all"]
    if any((repo / marker).exists() for marker in ("pyproject.toml", "ruff.toml", ".ruff.toml")) and "ruff format" in lowered:
        yield "ruff-format", ["ruff", "format", "."]
    if (repo / "pyproject.toml").exists() and "black" in lowered and ("would reformat" in lowered or "--check" in lowered):
        yield "black", ["black", "."]
    if (repo / "package.json").exists() and "prettier" in lowered and ("--check" in lowered or "code style issues" in lowered):
        yield "prettier", ["npx", "--no-install", "prettier", "--write", "."]


def _apply_formatters(
    repo: Path,
    log_text: str,
    *,
    runner: Callable[[list[str], Path], subprocess.CompletedProcess[str]] = _default_runner,
    tool_available: Callable[[str], bool] = _tool_available,
) -> tuple[list[FixerResult], list[str]]:
    results: list[FixerResult] = []
    errors: list[str] = []
    for name, command in _formatter_candidates(repo, log_text):
        if not tool_available(command[0]):
            results.append(FixerResult(name, "unavailable", f"{command[0]} is not installed"))
            continue
        try:
            proc = runner(command, repo)
        except (OSError, subprocess.SubprocessError) as exc:
            errors.append(f"{name}: {exc}")
            results.append(FixerResult(name, "error", str(exc)))
            continue
        tail = (proc.stdout or "")[-2000:]
        if proc.returncode == 0:
            results.append(FixerResult(name, "ran", tail))
        else:
            message = f"{name} exited {proc.returncode}"
            errors.append(message)
            results.append(FixerResult(name, "failed", tail or message))
    return results, errors


def apply_deterministic_repairs(
    repo: Path,
    log_text: str,
    *,
    rules_path: Path | None = None,
    runner: Callable[[list[str], Path], subprocess.CompletedProcess[str]] = _default_runner,
    tool_available: Callable[[str], bool] = _tool_available,
) -> dict:
    repo = repo.resolve()
    results: list[FixerResult] = []
    errors: list[str] = []

    rule_file = rules_path if rules_path is not None else repo / DEFAULT_RULES
    if rule_file.exists():
        try:
            config = json.loads(rule_file.read_text(encoding="utf-8"))
            version_results, version_errors = _apply_version_sync(repo, config, log_text)
            results.extend(version_results)
            errors.extend(version_errors)
        except (OSError, ValueError, TypeError) as exc:
            errors.append(f"repair rules: {exc}")
            results.append(FixerResult("repair-rules", "error", str(exc)))
    else:
        results.append(FixerResult("repair-rules", "absent", f"optional {DEFAULT_RULES} not present"))

    formatter_results, formatter_errors = _apply_formatters(
        repo,
        log_text,
        runner=runner,
        tool_available=tool_available,
    )
    results.extend(formatter_results)
    errors.extend(formatter_errors)

    return {
        "schema": REPORT_SCHEMA,
        "deterministic_changed": bool(_changed_files(repo)),
        "changed_files": _changed_files(repo),
        "fixers": [asdict(item) for item in results],
        "errors": errors,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Apply conservative deterministic GPTAuto CI repairs")
    parser.add_argument("--repo", default=".")
    parser.add_argument("--log", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument("--rules", default="")
    args = parser.parse_args()

    repo = Path(args.repo)
    log_text = Path(args.log).read_text(encoding="utf-8", errors="replace")
    rules = Path(args.rules) if args.rules else None
    payload = apply_deterministic_repairs(repo, log_text, rules_path=rules)
    report = Path(args.report)
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(payload, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
