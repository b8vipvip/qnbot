from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_mobile_diagnostics_are_client_scoped_and_redacted():
    runtime = (ROOT / "src/Bot/ChromeNs/BotWebConsoleSyncService.cs").read_text(encoding="utf-8-sig")
    server = (ROOT / "services/api-control-plane/bot_web_console.py").read_text(encoding="utf-8")
    html = (ROOT / "services/api-control-plane/static/bot-web.html").read_text(encoding="utf-8")
    js = (ROOT / "services/api-control-plane/static/bot-web-v2.js").read_text(encoding="utf-8")
    assert '["diagnostics"] = new JObject' in runtime
    assert '["sensitive_data_exposed"] = false' in runtime
    assert '"diagnostics_snapshot"' in runtime and '"diagnostics_snapshot"' in server
    assert 'Depends(_web_client)' in server
    assert 'id="diagnosticsInfo"' in html
    assert "不上传原始日志" in html
    assert "runSafeDiagnostics" in js


def test_remote_update_reuses_existing_verified_updater_and_requires_confirmation():
    runtime = (ROOT / "src/Bot/ChromeNs/BotWebConsoleSyncService.cs").read_text(encoding="utf-8-sig")
    remote = (ROOT / "src/Bot/Update/BotUpdateService.Remote.cs").read_text(encoding="utf-8-sig")
    server = (ROOT / "services/api-control-plane/bot_web_console.py").read_text(encoding="utf-8")
    html = (ROOT / "services/api-control-plane/static/bot-web.html").read_text(encoding="utf-8")
    assert 'payload.Value<bool?>("confirmed")' in runtime
    assert "RemoteInstallLatestAsync" in remote
    assert "CheckNowAsync(false)" in remote
    assert "DownloadPackageAsync" in remote
    assert "IsPackageReady" in remote
    assert "LaunchInstaller" in remote
    assert "data.confirmed" in server
    assert '"update_install"' in server
    assert "SHA-256" in html and "watchdog" in html


def test_mobile_status_does_not_expose_raw_logs_or_credentials():
    runtime = (ROOT / "src/Bot/ChromeNs/BotWebConsoleSyncService.cs").read_text(encoding="utf-8-sig")
    start = runtime.index('["diagnostics"] = new JObject')
    end = runtime.index('["update"] = new JObject', start)
    block = runtime[start:end].lower()
    for forbidden in ["api_key", "authorization", "cookie", "password", "验证码", "log_path", "log_text"]:
        assert forbidden not in block
