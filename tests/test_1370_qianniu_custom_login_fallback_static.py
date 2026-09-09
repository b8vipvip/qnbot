from pathlib import Path

HELPER = Path("src/Bot/Update/BotUpdateSavedAccountLoginFallback.Fast.cs")
SOURCE = Path("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")


def test_custom_rendered_login_fallback_is_wired_into_bounded_recovery():
    helper = HELPER.read_text(encoding="utf-8-sig")
    source = SOURCE.read_text(encoding="utf-8-sig")
    assert "TryClickSavedAccountLoginFallback" in helper
    assert "saved-account-relative" in helper
    assert "compactLoginGeometry" in helper
    assert "QnSavedAccountLoginFallback.TryClickSavedAccountLoginFallback(window, descendants)" in source
    assert "MaxPostRestartLoginAttempts = 8" in source
    assert "PostRestartLoginRetryInterval = TimeSpan.FromSeconds(6)" in source


def test_focus_access_denied_does_not_suppress_coordinate_click():
    helper = HELPER.read_text(encoding="utf-8-sig")
    focus = helper.index("window.Focus()")
    focus_error = helper.index("激活千牛v9自绘登录窗口失败，继续尝试屏幕坐标点击")
    coordinate_click = helper.index("Mouse.Click(point)")
    assert focus < focus_error < coordinate_click
    assert "屏幕坐标点击失败" in helper
    assert "ex.GetType().Name" in helper


def test_fallback_never_selects_or_mutates_credentials():
    helper = HELPER.read_text(encoding="utf-8-sig")
    assert "单账号登录" in helper
    assert "添加账号" in helper
    assert ".SetText(" not in helper
    assert ".Enter(" not in helper
    assert "Password" not in helper
    assert "Select(" not in helper
    assert "Process.Start" not in helper
