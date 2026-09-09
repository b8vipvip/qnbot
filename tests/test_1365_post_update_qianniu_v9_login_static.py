from pathlib import Path


SOURCE = Path("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")


def _source() -> str:
    return SOURCE.read_text(encoding="utf-8-sig")


def test_auto_update_no_longer_drives_qianniu_login_ui():
    text = _source()
    assert "PrimaryLoginButtonName" not in text
    assert "MaxPostRestartLoginAttempts" not in text
    assert "PostRestartLoginRetryInterval" not in text
    assert "TryClickPrimaryLoginButton" not in text
    assert "TryClickSavedAccountLoginFallback" not in text
    assert "DriveQianniuSavedSessionUi" not in text
    assert "Mouse.Click" not in text
    assert "FlaUI" not in text


def test_auto_update_never_restarts_or_switches_qianniu_credentials():
    text = _source()
    assert "TryRestartQianniuOnceAsync" not in text
    assert "Process.Start" not in text
    assert "CloseMainWindow" not in text
    assert ".Kill()" not in text
    assert ".SetText(" not in text
    assert "Password" not in text
    assert "SelectItem(" not in text
    assert "不自动重启千牛" in text
    assert "保护当前千牛进程与登录态" in text


def test_post_update_waits_for_existing_injection_after_listener_recovery():
    text = _source()
    assert "PostUpdateReconnectWindow = TimeSpan.FromSeconds(120)" in text
    assert 'WaitForInjectionAsync(PostUpdateReconnectWindow, "post-update-preserve-session")' in text
    assert "原千牛注入在保护窗口内重新连接；未重启千牛" in text
