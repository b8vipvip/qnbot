from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
UPDATER = ROOT / "src" / "Bot" / "Update" / "BotAutoUpdater.ps1"


def _source() -> str:
    return UPDATER.read_text(encoding="utf-8-sig")


def test_updater_waits_for_websocket_port_to_be_bindable_before_new_bot_start():
    source = _source()

    assert "function Test-LoopbackPortBindable" in source
    assert "System.Net.Sockets.TcpListener" in source
    assert "ExclusiveAddressUse = $true" in source
    assert "function Wait-BotWebSocketPortRelease" in source
    assert "Stop-BotProcesses $TargetInstallDir" in source
    assert "Get-LoopbackPortOwnerSummary" in source
    assert "Get-NetTCPConnection" in source

    barrier = "Wait-BotWebSocketPortRelease $InstallDir 41010 45"
    start = "$newBot = Start-Process -FilePath $installedExe"
    assert barrier in source
    assert start in source
    assert source.index(barrier) < source.index(start)


def test_port_handoff_failure_is_fail_closed_and_diagnostic():
    source = _source()

    assert "Bot WebSocket port 41010 is still occupied" in source
    assert "Automatic rollback will start" in source
    assert "Bot WebSocket handoff ready" in source
    assert "Bot WebSocket handoff waiting" in source
