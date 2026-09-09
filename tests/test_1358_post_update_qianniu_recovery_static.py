from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
BOOTSTRAP = ROOT / "src" / "Bot" / "StartUp" / "BootStrap.cs"
RECOVERY = ROOT / "src" / "Bot" / "Update" / "BotUpdateStartupConnection.Fast.cs"
HEALTH = ROOT / "src" / "Bot" / "Update" / "UpdateStartupHealthService.cs"


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_language_startup_gate_uses_qninject_as_single_version_authority():
    source = _read(BOOTSTRAP)
    assert "QNInject.IsInjected(resourceDirectory.FullName)" in source
    assert "20260714-zh-cn-v9" not in source
    assert "20260713-hans-all-pages-v3" not in source
    assert "ZipContainsMarker" not in source
    assert "语言：简体中文 ✓" in source


def test_post_update_capability_is_version_bound_and_health_acknowledged():
    health = _read(HEALTH)
    assert "IsPostUpdateLaunchAuthorized" in health
    assert "IsPostUpdateStartupReady" in health
    assert "QIANNIU_BOT_UPDATE_EXPECTED_VERSION" in health
    assert "string.Equals(expectedVersion, currentVersion" in health

    file_move = health.index("File.Move(temporary, path);")
    ready = health.index("Interlocked.Exchange(ref _postUpdateStartupReady, 1);")
    assert file_move < ready


def test_normal_and_post_update_startup_are_non_destructive_to_qianniu():
    source = _read(RECOVERY)
    assert "var postUpdateLaunchAuthorized = UpdateStartupHealthService.IsPostUpdateLaunchAuthorized();" in source
    assert "UpdateStartupHealthService.IsPostUpdateStartupReady()" in source
    assert "TryRestartQianniuOnceAsync" not in source
    assert "Process.Start" not in source
    assert "Process.GetProcessesByName" not in source
    assert "FlaUI" not in source
    assert "Mouse.Click" not in source
    assert "不自动重启千牛" in source
    assert "不操作登录界面" in source


def test_post_update_preserves_session_and_has_bounded_reconnect_window():
    source = _read(RECOVERY)
    assert "PostUpdateGracePeriod = TimeSpan.FromSeconds(25)" in source
    assert "PostUpdateReconnectWindow = TimeSpan.FromSeconds(120)" in source
    assert 'WaitForInjectionAsync(PostUpdateReconnectWindow, "post-update-preserve-session")' in source
    assert "为保护已登录千牛会话，不执行千牛重启、不操作登录界面" in source
    assert "保持原千牛进程与登录态" in source


def test_recovery_repairs_only_bot_listener_then_degrades_without_qianniu_restart():
    source = _read(RECOVERY)
    assert "MyWebSocketServer.WSocketSvrInst.Start()" in source
    assert "RunDegradedRecoveryAsync" in source
    degraded = source[source.index("private static async Task RunDegradedRecoveryAsync()") :]
    assert "不会自动重启或操作登录界面" in degraded
    assert "Process." not in degraded
    assert "Mouse.Click" not in degraded
