from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
UPDATER = ROOT / "src" / "Bot" / "Update" / "BotAutoUpdater.ps1"
WATCHDOG = ROOT / "src" / "Bot" / "Update" / "BotUpdateProcessWatchdog.Fast.cs"
HEALTH = ROOT / "src" / "Bot" / "Update" / "UpdateStartupHealthService.cs"


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_updater_stops_target_watchdog_before_port_handoff_and_mutation():
    text = _read(UPDATER)
    assert "function Stop-BotWatchdogs" in text
    assert "bot-process-watchdog.ps1" in text
    assert "Stop-BotWatchdogs $InstallDir" in text
    preflight = text.index("Confirming Bot WebSocket port handoff before any install mutation")
    backup = text.index("Preparing bounded rollback backup")
    replace = text.index("Replacing program files")
    assert preflight < backup < replace
    assert "Wait-BotWebSocketPortRelease $InstallDir 41010 60" in text
    assert "Reconfirming Bot WebSocket port handoff before target start" in text


def test_startup_health_is_bound_to_exact_target_version():
    updater = _read(UPDATER)
    health = _read(HEALTH)
    assert "QIANNIU_BOT_UPDATE_EXPECTED_VERSION" in updater
    assert "QIANNIU_BOT_UPDATE_EXPECTED_VERSION" in health
    assert "release_version" in health
    assert "expected_version" in health
    assert "Test-BotHealthy $installedExe $newBot.Id $healthFile $ExpectedVersion" in updater
    assert "[string]$health.release_version -eq $ExpectedReleaseVersion" in updater
    assert "更新启动健康检查拒绝旧版本进程冒充目标版本" in health


def test_watchdog_cannot_inherit_one_shot_update_health_capability():
    text = _read(WATCHDOG)
    assert "watcherStartInfo.EnvironmentVariables.Remove(UpdateHealthFileEnvironmentVariable)" in text
    assert "watcherStartInfo.EnvironmentVariables.Remove(UpdateExpectedVersionEnvironmentVariable)" in text


def test_updater_persists_a_diagnostic_result_for_next_runtime_log():
    updater = _read(UPDATER)
    health = _read(HEALTH)
    assert "last-update-result.json" in updater
    assert "Write-UpdateResult $resultPath 'success'" in updater
    assert "Write-UpdateResult $resultPath 'failed'" in updater
    assert "last-update-result.json" in health
    assert "上次Bot更新器结果" in health
