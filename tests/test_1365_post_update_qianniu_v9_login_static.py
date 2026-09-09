from pathlib import Path


SOURCE = Path("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")


def _source() -> str:
    return SOURCE.read_text(encoding="utf-8")


def test_v9_login_is_exact_and_never_targets_account_management_actions():
    text = _source()
    assert 'private const string PrimaryLoginButtonName = "登录";' in text
    assert 'string.Equals(SafeName(element), PrimaryLoginButtonName, StringComparison.Ordinal)' in text
    assert 'LoginButtonNames = { "登录", "立即登录", "登录千牛", "进入千牛" }' not in text
    assert 'PrimaryLoginButtonName = "单账号登录"' not in text
    assert 'PrimaryLoginButtonName = "添加账号"' not in text


def test_v9_login_retries_are_bounded_and_wait_for_health_evidence():
    text = _source()
    assert "MaxPostRestartLoginAttempts = 8" in text
    assert "PostRestartLoginRetryInterval = TimeSpan.FromSeconds(6)" in text
    assert "var loginAttempts = 0;" in text
    assert "var lastLoginAttemptAt = DateTime.MinValue;" in text
    assert "loginAttempts < MaxPostRestartLoginAttempts" in text
    assert "now - lastLoginAttemptAt >= PostRestartLoginRetryInterval" in text
    assert "snapshot.WebSocketSessionCount > 0" in text
    assert text.index("snapshot.WebSocketSessionCount > 0", text.index("WaitForInjectionAndRestoreUiAsync")) < text.index(
        "DriveQianniuSavedSessionUi(", text.index("WaitForInjectionAndRestoreUiAsync")
    )


def test_v9_login_focuses_window_and_uses_element_bounds_not_fixed_coordinates():
    text = _source()
    assert "window.Focus();" in text
    assert "SafeBoundingRectangle(element)" in text
    assert "rect.Left + rect.Width / 2" in text
    assert "rect.Top + rect.Height / 2" in text
    assert "focused-bounded-coordinate" in text
    assert "Mouse.Click(new System.Drawing.Point(390" not in text
    assert "Mouse.Click(new System.Drawing.Point(534" not in text


def test_post_update_login_does_not_read_write_or_switch_credentials():
    text = _source()
    login_helper = text[text.index("private static bool TryClickPrimaryLoginButton"):text.index("private static System.Drawing.Rectangle SafeBoundingRectangle")]
    assert ".SetText(" not in login_helper
    assert ".Enter(" not in login_helper
    assert "Password" not in login_helper
    assert "AsComboBox(" not in login_helper
    assert "SelectItem(" not in login_helper
    assert "Keyboard.Type" not in login_helper
    assert "Process.Start" not in login_helper


def test_single_restart_authority_is_preserved():
    text = _source()
    assert text.count("TryRestartQianniuOnceAsync") == 2
    assert "不会重复重启" in text
    assert "RunPostUpdateRecoveryAsync" in text
