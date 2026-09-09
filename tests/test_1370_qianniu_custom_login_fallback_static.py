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


def test_fallback_never_selects_or_mutates_credentials():
    helper = HELPER.read_text(encoding="utf-8-sig")
    assert "单账号登录" in helper
    assert "添加账号" in helper
    assert ".SetText(" not in helper
    assert ".Enter(" not in helper
    assert "Password" not in helper
    assert "Select(" not in helper
    assert "Process.Start" not in helper
