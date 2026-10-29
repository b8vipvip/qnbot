"""Server updater observability and administrator-only runtime log regression."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SERVICE = ROOT / "services" / "api-control-plane"

def read(path):
    return path.read_text(encoding="utf-8-sig")

def test_runtime_log_is_packaged_and_bootstrap_installed():
    docker = read(SERVICE / "Dockerfile")
    bootstrap = read(SERVICE / "bootstrap.py")
    assert "server_runtime_logs.py" in docker
    assert "import server_runtime_logs" in bootstrap
    assert "server_runtime_logs.install(control_plane)" in bootstrap

def test_log_api_requires_admin_and_has_bounded_sources():
    backend = read(SERVICE / "server_runtime_logs.py")
    assert 'Depends(_admin)' in backend
    assert 'return _cp.require_admin(request)' in backend
    assert '"/api/admin/server-runtime/logs"' in backend
    assert '"/api/admin/server-runtime/logs/download"' in backend
    assert 'source in {"update", "build"}' in backend
    assert 'if source == "runtime"' in backend
    assert 'MAX_READ_BYTES = 512 * 1024' in backend
    assert 'maxBytes=3 * 1024 * 1024, backupCount=5' in backend
    assert "os.chmod(RUNTIME_FILE, 0o600)" in backend
    assert 'request.scope.get("route")' in backend
    assert 'getattr(endpoint, "path", None) or "/"' in backend
    assert "redact(" in backend

def test_update_is_observable_without_faking_percentage():
    agent = read(ROOT / "scripts" / "api-control-plane-update-agent.sh")
    backend = read(SERVICE / "version_update_admin.py")
    assert 'BUILD_LAST_REPORT_AT' in agent
    assert 'BUILD_START_AT' in agent
    assert 'if (( now - BUILD_LAST_REPORT_AT >= 10 ))' in agent
    assert '"构建新服务镜像" 35' in agent
    assert 'status["log_age_seconds"]' in backend
    assert 'status["build_suspected_stalled"]' in backend
    assert 'status["build_phase_not_percentage"]' in backend
    assert 'status["diagnostic"]' in backend

def test_log_page_and_build_diagnostics_are_linked():
    index = read(SERVICE / "static" / "index.html")
    js = read(SERVICE / "static" / "server-runtime-logs.js")
    update = read(SERVICE / "static" / "version-update.js")
    nav = read(SERVICE / "static" / "console-sections.js")
    assert 'data-page="server-runtime"' in index
    assert 'id="page-server-runtime"' in index
    assert 'server-runtime-logs.js' in index
    assert '"server-runtime"' in nav
    assert 'loadServerRuntimeLogs()' in js
    assert "downloadServerRuntimeLogs()" in js
    assert 'serverRuntimeOutput' in js
    assert 'build_suspected_stalled' in update
    assert "查看构建日志" in update
