from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WATCHDOG = ROOT / "src" / "Bot" / "Update" / "BotUpdateProcessWatchdog.Fast.cs"


def source() -> str:
    return WATCHDOG.read_text(encoding="utf-8-sig")


def test_watchdog_uses_install_scoped_runtime_directory_and_lock():
    text = source()
    assert 'Path.Combine(runtimeRoot, "watchdogs", installKey)' in text
    assert '"watchdog-owner.lock"' in text
    assert 'Acquire-WatchdogOwnership' in text
    assert '[System.IO.FileShare]::None' in text
    assert 'watchdog ownership acquisition timed out' in text


def test_startup_reaps_only_qnbot_watchdogs_for_same_install():
    text = source()
    assert 'CleanupStaleWatchdogs(exe, installKey, currentProcess.Id)' in text
    assert 'ManagementObjectSearcher' in text
    assert 'bot-process-watchdog.ps1' in text
    assert 'commandLine.IndexOf(\n                                    exePath,' in text
    assert 'commandLine.IndexOf(\n                                    installKey,' in text
    assert 'stale.Kill()' in text
    assert 'BotAutoUpdater.ps1' not in text.split('private static int CleanupStaleWatchdogs', 1)[1].split('private static string BuildInstallKey', 1)[0]


def test_install_key_is_stable_and_path_derived():
    text = source()
    assert 'Path.GetFullPath(exePath ?? string.Empty)' in text
    assert 'SHA256.Create()' in text
    assert 'builder.Append(hash[i].ToString("x2"))' in text
    assert ' -InstallKey ' in text
    assert ' -WatchdogLockPath ' in text


def test_watchdog_releases_exclusive_owner_on_exit():
    text = source()
    assert 'finally {' in text
    assert '$watchdogLock.Dispose()' in text
    assert 'duplicate watcher will not stay resident' in text


def test_existing_update_handoff_recovery_semantics_remain_present():
    text = source()
    assert "auto-update expected exit pid=" in text
    assert "$softDeadline = (Get-Date).AddSeconds(90)" in text
    assert "$hardDeadline = (Get-Date).AddMinutes(5)" in text
    assert "auto-update timeout recovery" in text
    assert "restart suppressed: reached 5 unexpected exits in 10 minutes" in text
