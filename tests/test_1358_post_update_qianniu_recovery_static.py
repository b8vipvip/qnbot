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


def test_post_update_restart_capability_is_version_bound_and_health_acknowledged():
    health = _read(HEALTH)
    assert "IsPostUpdateLaunchAuthorized" in health
    assert "IsPostUpdateStartupReady" in health
    assert "QIANNIU_BOT_UPDATE_EXPECTED_VERSION" in health
    assert "string.Equals(expectedVersion, currentVersion" in health

    file_move = health.index("File.Move(temporary, path);")
    ready = health.index("Interlocked.Exchange(ref _postUpdateStartupReady, 1);")
    assert file_move < ready


def test_normal_startup_never_enters_destructive_qianniu_recovery():
    source = _read(RECOVERY)
    assert "var postUpdateLaunchAuthorized = UpdateStartupHealthService.IsPostUpdateLaunchAuthorized();" in source
    assert "if (postUpdateLaunchAuthorized)" in source
    assert "UpdateStartupHealthService.IsPostUpdateStartupReady()" in source

    post_update_call = source.index("RunPostUpdateRecoveryAsync()")
    authorization_guard = source.rfind("if (postUpdateLaunchAuthorized)", 0, post_update_call)
    assert authorization_guard >= 0

    degraded = source[source.index("private static async Task RunDegradedRecoveryAsync()") :]
    assert "TryRestartQianniuOnceAsync" not in degraded
    assert "不会继续/重复重启千牛" in degraded


def test_post_update_qianniu_restart_is_single_shot_and_has_bounded_grace():
    source = _read(RECOVERY)
    assert "PostUpdateGracePeriod = TimeSpan.FromSeconds(25)" in source
    assert "PostRestartRecoveryTimeout = TimeSpan.FromSeconds(120)" in source
    assert source.count("await TryRestartQianniuOnceAsync()") == 1
    assert "宽限期内注入已自行恢复，已取消千牛重启" in source
    assert "已执行唯一一次千牛重启" in source


def test_saved_login_and_history_dialog_automation_is_narrowly_scoped():
    source = _read(RECOVERY)
    assert '"登录", "立即登录", "登录千牛", "进入千牛"' in source
    assert "保留千牛自己的账号、密码和默认账号选择" in source
    assert "未读取、填写或修改账号密码" in source
    assert 'WindowContainsText(windowName, descendants, "是否需要打开之前的消息")' in source
    assert 'new[] { "确认" }' in source
    assert "只点击了明确的“确认”" in source
    assert "SetText(" not in source


def test_recovery_restores_reception_entry_before_giving_up_without_repeat_restart():
    source = _read(RECOVERY)
    assert '"接待台", "千牛接待台", "接待中心", "客服接待", "消息接待"' in source
    assert "TryClickExactNamedElement(descendants, ReceptionEntryNames" in source
    assert "等待千牛恢复接待聊天WebView与注入连接" in source
    assert "转入低频等待" in source
