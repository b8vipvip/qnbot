from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
UPDATER = ROOT / "src" / "Bot" / "Update" / "BotAutoUpdater.ps1"


def _source() -> str:
    return UPDATER.read_text(encoding="utf-8-sig")


def test_updater_has_one_canonical_websocket_handoff_authority():
    source = _source()
    authority = "function Get-BotWebSocketHandoffState"
    waiter = "function Wait-BotWebSocketPortRelease"
    assert authority in source
    assert waiter in source
    assert "There is exactly one yes/no authority" in source
    assert "$handoff = Get-BotWebSocketHandoffState $TargetInstallDir $Port" in source
    assert "if ([bool]$handoff.Ready)" in source

    # Both mutation barriers consume the same authority through the same waiter.
    pre = "Wait-BotWebSocketPortRelease $InstallDir 41010 60"
    post = "Wait-BotWebSocketPortRelease $InstallDir 41010 30"
    start = "$newBot = Start-Process -FilePath $installedExe"
    assert pre in source and post in source and start in source
    assert source.index(pre) < source.index(post) < source.index(start)


def test_stale_listener_pid_is_diagnostic_not_a_second_veto():
    source = _source()
    authority_start = source.index("function Get-BotWebSocketHandoffState")
    waiter_start = source.index("function Wait-BotWebSocketPortRelease")
    authority = source[authority_start:waiter_start]

    assert "Get-LiveProcessStateById $ownerPid" in authority
    assert "if (-not [bool]$ownerState.Exists)" in authority
    assert "$staleOwnerPids += $ownerPid" in authority
    assert "$liveOwners += $ownerState.Process" in authority
    assert "$ready = $sensorKnown -and $liveInstallProcesses.Count -eq 0 -and $liveOwners.Count -eq 0" in authority
    assert "stale-listener-pids=" in authority
    assert "StrictBindableDiagnostic" in authority


def test_unknown_sensor_or_live_owner_fails_closed_but_bind_probe_is_diagnostic_only():
    source = _source()
    authority_start = source.index("function Get-BotWebSocketHandoffState")
    waiter_start = source.index("function Wait-BotWebSocketPortRelease")
    authority = source[authority_start:waiter_start]

    assert "$sensorKnown = [bool]$installState.Known -and [bool]$listenerState.Known -and $ownerResolutionKnown" in authority
    assert "$ready = $sensorKnown -and" in authority
    assert "$strictBindable = Test-LoopbackPortBindable $Port" in authority
    assert "Bindability is recorded for diagnosis only. It never participates in $ready." in authority
    ready_line = next(line for line in authority.splitlines() if line.strip().startswith("$ready ="))
    assert "strictBindable" not in ready_line


def test_current_pid_is_never_blindly_killed_after_pid_reuse():
    source = _source()
    process_start = source.index("function Get-InstallProcessIds")
    process_end = source.index("function Stop-BotProcesses")
    process_logic = source[process_start:process_end]

    assert "$ids += [int]$CurrentPid" not in process_logic
    assert "Test-PathUnderInstallRoot" in source
    assert "Test-InstallProcessIdAlive $InstallDir $CurrentPid" in source
    assert "Stop-Process -Id $CurrentPid" in source
    guard = "if (Test-InstallProcessIdAlive $InstallDir $CurrentPid)"
    assert source.index(guard) < source.index("Stop-Process -Id $CurrentPid")


def test_live_foreign_listener_still_blocks_and_startup_health_remains_version_bound():
    source = _source()
    assert "$liveOwners += $ownerState.Process" in source
    assert "$liveOwners.Count -eq 0" in source
    assert "QIANNIU_BOT_UPDATE_EXPECTED_VERSION" in source
    assert "release_version -eq $ExpectedReleaseVersion" in source
    assert "Target version passed version-bound startup health." in source
