from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_vless_credentials_are_console_managed_and_encrypted():
    module = read("services/api-control-plane/github_vless_proxy.py")
    assert "version_update_proxy_settings" in module
    assert "_cp.encrypt_secret(vless_url)" in module
    assert "_cp.decrypt_secret" in module
    assert '@router.get("/api/admin/version-update/proxy")' in module
    assert '@router.put("/api/admin/version-update/proxy")' in module
    assert '@router.post("/api/admin/version-update/proxy/test")' in module
    assert 'if not value.lower().startswith("vless://")' in module
    assert '"security": security' in module
    assert 'security in {"tls", "reality"}' in module
    assert '"public_key": public_key' in module
    assert 'transport_type in {"ws", "websocket"}' in module
    assert 'transport_type == "grpc"' in module
    assert 'transport_type in {"httpupgrade", "http-upgrade"}' in module


def test_proxy_is_process_local_and_never_changes_host_network():
    module = read("services/api-control-plane/github_vless_proxy.py")
    progress = read("services/api-control-plane/bot_update_progress.py")
    assert 'LISTEN_HOST = "127.0.0.1"' in module
    assert 'LOCAL_PROXY = f"socks5h://{LISTEN_HOST}:{LISTEN_PORT}"' in module
    assert '"tun": False' in module
    assert '"system_proxy": False' in module
    assert '"server_route_changed": False' in module
    assert "set_github_proxy(LOCAL_PROXY)" in module
    assert 'proxies={"http": LOCAL_PROXY, "https": LOCAL_PROXY}' in module
    assert "os.environ[\"HTTP_PROXY\"]" not in module
    assert "os.environ[\"HTTPS_PROXY\"]" not in module
    assert "iptables" not in module.lower()
    assert "def github_proxy()" in progress
    assert "def set_github_proxy(" in progress
    assert "def proxy_revision()" in progress
    assert 'return {"proxies": {"http": proxy, "https": proxy}}' in progress
    assert "GitHub 下载代理配置已变更，正在从断点切换网络" in progress


def test_saved_proxy_is_applied_before_github_prefetch_and_stopped_on_shutdown():
    bootstrap = read("services/api-control-plane/bootstrap.py")
    init_proxy = bootstrap.index("github_vless_proxy.init_github_vless_proxy()")
    init_cache = bootstrap.index("bot_update_cache.init_bot_update_cache()")
    init_prefetch = bootstrap.index("bot_update_prefetch.init_bot_update_prefetch()")
    assert init_proxy < init_cache < init_prefetch
    assert "github_vless_proxy.install(control_plane)" in bootstrap
    assert "github_vless_proxy.stop_github_vless_proxy()" in bootstrap


def test_sing_box_binary_is_pinned_and_reused_from_verified_local_image():
    dockerfile = read("services/api-control-plane/Dockerfile")
    compose = read("services/api-control-plane/docker-compose.bt.yml")
    updater = read("scripts/update-api-control-plane.sh")
    assert "ghcr.io/sagernet/sing-box:v1.13.19@sha256:78ca1bc9f3b563b0944fd6b1126522ee631e7b44d540b91fc435ba7062ef08b2" in dockerfile
    assert "FROM ${SING_BOX_SOURCE_IMAGE} AS sing-box-provider" in dockerfile
    assert "COPY --from=sing-box-provider /usr/local/bin/sing-box" in dockerfile
    assert "sing-box version 1.13.19" in dockerfile
    assert "CONTROL_PLANE_SING_BOX_SOURCE_IMAGE" in compose
    assert "prepare_sing_box_source" in updater
    assert 'docker exec "$CONTAINER_NAME" /usr/local/bin/sing-box version' in updater
    assert 'docker image tag "$candidate" "$cache_tag"' in updater
    assert "docker compose -f docker-compose.bt.yml build --pull" not in updater
    assert "https://github.com/SagerNet/sing-box/releases/download/" not in dockerfile


def test_version_console_exposes_vless_controls_without_echoing_saved_secret():
    html = read("services/api-control-plane/static/index.html")
    js = read("services/api-control-plane/static/version-update.js")
    assert 'id="githubProxyVlessUrl" type="password"' in html
    assert 'id="githubProxyEnabled"' in html
    assert "保存并应用" in html
    assert "测试节点" in html
    assert "清除节点" in html
    assert '/static/version-update.js?v=4' in html
    assert 'api("/api/admin/version-update/proxy"' in js
    assert 'api("/api/admin/version-update/proxy/test"' in js
    assert "vless_url:value" in js
    assert "localStorage" not in js
    assert "无需修改服务器" in js


def test_version_status_uses_runtime_proxy_not_import_time_environment_snapshot():
    admin = read("services/api-control-plane/version_update_admin.py")
    progress = read("services/api-control-plane/bot_update_progress.py")
    assert "bot_update_progress.github_proxy()" in admin
    assert "bot_update_progress.GITHUB_PROXY" not in admin
    assert '_DEFAULT_GITHUB_PROXY = os.getenv("BOT_UPDATE_GITHUB_PROXY", "").strip()' in progress
    assert "_RUNTIME_GITHUB_PROXY" in progress
