from pathlib import Path

HELPER = Path("src/Bot/Update/BotUpdateSavedAccountLoginFallback.Fast.cs")
SOURCE = Path("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")


def test_custom_rendered_login_helper_is_not_wired_into_auto_update_recovery():
    helper = HELPER.read_text(encoding="utf-8-sig")
    source = SOURCE.read_text(encoding="utf-8-sig")
    # Keep the old helper available for a future explicitly initiated/manual recovery path, but the
    # automatic updater must never use it because field Windows Server rejects cross-process input.
    assert "TryClickSavedAccountLoginFallback" in helper
    assert "QnSavedAccountLoginFallback.TryClickSavedAccountLoginFallback" not in source
    assert "Mouse.Click" not in source
    assert "FlaUI" not in source
    assert "不操作登录界面" in source


def test_legacy_helper_remains_credential_safe_while_unreferenced():
    helper = HELPER.read_text(encoding="utf-8-sig")
    assert "单账号登录" in helper
    assert "添加账号" in helper
    assert ".SetText(" not in helper
    assert ".Enter(" not in helper
    assert "Password" not in helper
    assert "Select(" not in helper
    assert "Process.Start" not in helper
