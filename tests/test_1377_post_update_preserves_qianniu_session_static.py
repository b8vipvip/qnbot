from pathlib import Path


SOURCE = Path("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")


def _source() -> str:
    return SOURCE.read_text(encoding="utf-8-sig")


def test_bot_update_preserves_logged_in_qianniu_process():
    text = _source()
    assert "PostUpdateReconnectWindow = TimeSpan.FromSeconds(120)" in text
    assert "更新后仅恢复Bot监听并等待原千牛注入重连，不自动重启千牛" in text
    assert "保持原千牛进程与登录态" in text
    assert "不重启千牛、不操作登录界面" in text
    assert "TryRestartQianniuOnceAsync" not in text
    assert "Process.GetProcessesByName" not in text
    assert "Process.Start" not in text
    assert "Mouse.Click" not in text
    assert "FlaUI" not in text


def test_post_update_repair_authority_is_limited_to_bot_listener_and_waiting():
    text = _source()
    assert "MyWebSocketServer.WSocketSvrInst.Start()" in text
    assert 'WaitForInjectionAsync(PostUpdateGracePeriod, "post-update-grace")' in text
    assert 'WaitForInjectionAsync(PostUpdateReconnectWindow, "post-update-preserve-session")' in text
    assert "RunDegradedRecoveryAsync" in text
