"""The host update status alone must not claim an unstamped container is current."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SERVICE = ROOT / "services" / "api-control-plane"

def read(path):
    return path.read_text(encoding="utf-8-sig")

def test_runtime_image_without_build_stamp_is_unverified_not_latest():
    api = read(SERVICE / "version_update_admin.py")
    assert "needs_runtime_verification = not bool(running_image_commit)" in api
    assert 'or not current or current != latest' in api
    assert '"needs_runtime_verification": needs_runtime_verification' in api
    assert '"version_source": "running_image" if running_image_commit else "updater_status"' in api

def test_same_commit_rebuild_is_allowed_only_for_unverified_image():
    api = read(SERVICE / "version_update_admin.py")
    assert "needs_runtime_verification = not bool(_runtime_build_commit())" in api
    assert "if current and current == target and not needs_runtime_verification:" in api
    assert '"source": "web-console-runtime-reverify" if needs_runtime_verification and current == target else "web-console"' in api
    assert 'if str(current_status.get("state") or "") in {"queued", "running"}:' in api
    assert 'Depends(_admin)' in api
    assert "SERVER_UPDATE_REQUEST, payload" in api

def test_browser_labels_unverifiable_image_and_requires_confirmation():
    js=read(SERVICE / "static" / "version-update.js")
    assert "server.needs_runtime_verification" in js
    assert 'badge("运行镜像版本未验证","warn")' in js
    assert "重新构建并验证当前服务端版本" in js
    assert "上方 100% 仅表示上一次更新任务完成" in js
    assert "versionUpdateState.last&&versionUpdateState.last.server" in js
    assert "if(!confirm(text))return;" in js

def test_gateway_html_is_not_dumped_into_console_error_area():
    js=read(SERVICE / "static" / "version-update.js")
    assert "服务端暂时无法连接" in js
    assert "页面会自动重试" in js
    assert "esc(message)" in js
