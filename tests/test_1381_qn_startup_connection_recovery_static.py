from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_recovery_requires_authoritative_cdp_not_any_standby_websocket():
    recovery = read("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")
    diagnostics = read("src/Bot/ChromeNs/MyWebSocketServer.cs")

    assert "AuthoritativeCdpSessionCount <= 0" in recovery
    assert "snapshot.CdpReady" in recovery
    assert "snapshot.InjectionConnected" in recovery
    assert "HealthySnapshotMaxAge" in recovery
    assert "public bool CdpReady" in diagnostics
    assert "public bool InjectionConnected" in diagnostics


def test_degraded_recovery_runs_safe_deferred_migration_after_qianniu_exits():
    recovery = read("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")
    startup = read("src/Bot/StartUp/BootStrap.cs")
    qn = read("src/Bot/Common/QNInject.cs")

    assert "TryRepairDeferredResourcesAsync" in recovery
    assert "CheckAndRepairLanguageSafely" in recovery
    assert "QNInject.TryRepairWhenQianniuClosedAsync" in recovery
    assert "IsQianniuRunning()" in recovery
    assert "IsQianniuResourceProcessRunning" in startup
    assert "TryRepairWhenQianniuClosedAsync" in qn


def test_dead_bot_listener_reference_is_recreated_and_stale_cdp_sessions_are_released():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")

    assert "if (_webSocketServer != null && IsLoopbackListenerActive())" in server
    assert "ReleaseStaleSessions(" in server
    assert "BotConnectionDiagnostics.RecordAuthoritativeCdpSessionCount(0)" in server
