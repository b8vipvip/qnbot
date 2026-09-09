from pathlib import Path


def test_post_update_recovery_policy_documents_non_destructive_contract():
    text = Path("docs/POST_UPDATE_QIANNIU_SESSION_RECOVERY.md").read_text(encoding="utf-8-sig")
    assert "自动更新只替换并重启 Bot" in text
    assert "不应关闭、结束、重新启动或操作已经登录的千牛进程" in text
    assert "127.0.0.1:41010" in text
    assert "等待原千牛 WebView" in text
    assert "进入低频非破坏性恢复" in text
