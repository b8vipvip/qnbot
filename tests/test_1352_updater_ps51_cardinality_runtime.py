from pathlib import Path
import shutil
import subprocess

import pytest


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "tests" / "windows" / "test-updater-handoff-authority.ps1"
UPDATER = ROOT / "src" / "Bot" / "Update" / "BotAutoUpdater.ps1"


def test_updater_authority_uses_collection_safe_cardinality_contract():
    text = UPDATER.read_text(encoding="utf-8-sig")
    authority = text.split("function Get-BotWebSocketHandoffState", 1)[1].split(
        "function Wait-BotWebSocketPortRelease", 1
    )[0]
    assert "[object[]]$liveInstallProcesses = @()" in authority
    assert "[object[]]$liveWatchdogs = @()" in authority
    assert "[object[]]$liveOwners = @()" in authority
    assert "@($liveInstallProcesses).Count" in authority
    assert "@($liveWatchdogs).Count" in authority
    assert "@($liveOwners).Count" in authority
    assert "$liveInstallProcesses = if" not in authority


def test_updater_authority_runtime_on_windows_powershell_51():
    powershell = shutil.which("powershell.exe") or shutil.which("powershell")
    if not powershell:
        pytest.skip("Windows PowerShell 5.1 is not available on this runner")
    completed = subprocess.run(
        [
            powershell,
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(SCRIPT),
        ],
        cwd=str(ROOT),
        capture_output=True,
        text=True,
        timeout=60,
    )
    assert completed.returncode == 0, (
        "PowerShell 5.1 updater authority regression failed\n"
        + completed.stdout
        + "\n"
        + completed.stderr
    )
