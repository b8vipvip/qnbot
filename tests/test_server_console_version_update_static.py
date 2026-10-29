from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVICE = ROOT / "services" / "api-control-plane"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def test_console_has_version_update_navigation_and_realtime_page():
    html = read(SERVICE / "static" / "index.html")
    js = read(SERVICE / "static" / "version-update.js")
    assert 'data-page="version-update"' in html
    assert 'id="page-version-update"' in html
    assert "同步 GitHub 状态" in html
    assert "/api/admin/version-update/status" in js
    assert "/api/admin/version-update/sync" in js
    assert "setInterval" in js and "1000" in js


def test_server_update_is_admin_triggered_through_host_agent():
    api = read(SERVICE / "version_update_admin.py")
    agent = read(ROOT / "scripts" / "api-control-plane-update-agent.sh")
    installer = read(ROOT / "scripts" / "install-api-control-plane-update-agent.sh")
    assert '/api/admin/version-update/server/start' in api
    assert '"web-console-runtime-reverify"' in api
    assert 'else "web-console"' in api
    assert "update-api-control-plane.sh" in agent
    assert "构建新服务镜像" in agent
    assert "本机健康检查" in agent
    assert "更新完成" in agent
    assert "systemctl enable --now" in installer
    assert "ExecStart=/bin/bash" in installer


def test_client_release_download_reports_bytes_and_pushes_only_after_verify():
    api = read(SERVICE / "version_update_admin.py")
    progress = read(SERVICE / "bot_update_progress.py")
    push = read(SERVICE / "bot_update_push.py")
    js = read(SERVICE / "static" / "version-update.js")
    assert '/api/admin/version-update/client/start' in api
    assert "start_cached_package" in api
    assert "downloaded_bytes" in progress
    assert "total_bytes" in progress
    assert "progress_percent" in progress
    assert "SHA-256" in progress
    assert "_mirror_ready(metadata)" in push
    assert "get_push_status" in push
    assert "active_streams" in push
    assert "客户端安装包不会直连 GitHub" in js


def test_runtime_image_contains_version_update_modules():
    dockerfile = read(SERVICE / "Dockerfile")
    bootstrap = read(SERVICE / "bootstrap.py")
    assert "bot_update_progress.py" in dockerfile
    assert "version_update_admin.py" in dockerfile
    assert "bot_update_progress.install()" in bootstrap
    assert "version_update_admin.install(control_plane)" in bootstrap
