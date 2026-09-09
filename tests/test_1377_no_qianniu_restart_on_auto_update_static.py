from pathlib import Path


def test_auto_update_connection_recovery_never_restarts_qianniu():
    text = Path("src/Bot/Update/BotUpdateStartupConnection.Fast.cs").read_text(encoding="utf-8-sig")
    forbidden = (
        "TryRestartQianniuOnceAsync",
        "CaptureWorkbenchExecutablePath",
        "TryCloseWorkbenchWindows",
        "KillRemainingWorkbenchProcesses",
        "Process.Start",
        "CloseMainWindow",
        ".Kill()",
        "Mouse.Click",
        "UIA3Automation",
    )
    for token in forbidden:
        assert token not in text
