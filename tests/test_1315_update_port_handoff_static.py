from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
UPDATER = ROOT / "src" / "Bot" / "Update" / "BotAutoUpdater.ps1"


def _source() -> str:
    return UPDATER.read_text(encoding="utf-8-sig")


def test_updater_waits_for_real_websocket_listener_to_release_before_new_bot_start():
    source = _source()

    assert "function Test-LoopbackPortBindable" in source
    assert "System.Net.Sockets.TcpListener" in source
    assert "ExclusiveAddressUse = $true" in source
    assert "function Get-LoopbackPortListenerState" in source
    assert "Get-NetTCPConnection -LocalPort $Port -State Listen" in source
    assert "Known = $true; Listeners = @($listeners)" in source
    assert "Known = $false; Listeners = @()" in source
    assert "function Wait-BotWebSocketPortRelease" in source
    assert "Stop-BotProcesses $TargetInstallDir" in source
    assert "Get-LoopbackPortOwnerSummary" in source

    barrier = "Wait-BotWebSocketPortRelease $InstallDir 41010 45"
    start = "$newBot = Start-Process -FilePath $installedExe"
    assert barrier in source
    assert start in source
    assert source.index(barrier) < source.index(start)


def test_known_no_listener_does_not_false_rollback_on_strict_bind_probe():
    source = _source()

    state = "$listenerState = Get-LoopbackPortListenerState $Port"
    no_listener = "if (@($listenerState.Listeners).Count -eq 0)"
    transient = "strict exclusive bind probe is still blocked by transient TCP state"
    final_health = "startup health will perform the final bind validation"

    assert state in source
    assert "if ([bool]$listenerState.Known)" in source
    assert no_listener in source
    assert transient in source
    assert final_health in source
    assert source.index(state) < source.index(no_listener) < source.index(transient)

    # When listener enumeration is unavailable, keep the old conservative fallback.
    assert "elseif (Test-LoopbackPortBindable $Port)" in source
    assert "listener state unavailable" in source


def test_real_port_listener_failure_is_fail_closed_and_diagnostic():
    source = _source()

    assert "still has a real LISTEN owner after old Bot shutdown" in source
    assert "Automatic rollback will start" in source
    assert "Bot WebSocket handoff ready" in source
    assert "Bot WebSocket handoff waiting" in source
    assert "owner=none" in source
