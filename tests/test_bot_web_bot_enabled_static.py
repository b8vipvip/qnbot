from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVER = ROOT / "services" / "api-control-plane" / "bot_web_bot_enabled.py"
BOOTSTRAP = ROOT / "services" / "api-control-plane" / "bootstrap.py"
DOCKERFILE = ROOT / "services" / "api-control-plane" / "Dockerfile"
PAGE = ROOT / "services" / "api-control-plane" / "static" / "bot-web.html"
SCRIPT = ROOT / "services" / "api-control-plane" / "static" / "bot-web-bot-enabled.js"
WINDOWS = ROOT / "src" / "Bot" / "ChromeNs" / "BotWebBotEnabledSyncService.cs"
PROPS = ROOT / "src" / "Bot" / "Directory.Build.props"
WORKFLOW = ROOT / ".github" / "workflows" / "api-control-plane-ci.yml"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_web_page_exposes_independent_bot_master_switch():
    page = read(PAGE)
    script = read(SCRIPT)
    assert 'id="botEnabled"' in page
    assert 'id="botEnabledHint"' in page
    assert "启用 Bot" in page
    assert "关闭后 Bot 不再参与消息处理" in page
    assert 'src="/static/bot-web-bot-enabled.js?v=2"' in page
    assert 'script.src = "/static/bot-web-auto-reply-rules.js?v=2"' in script
    assert 'api("/api/bot-web/bot-enabled")' in script
    assert 'method: "PUT"' in script
    assert 'enabled: $("botEnabled").checked' in script
    assert "Bot 总开关" in script
    assert "Windows 当前实际状态" in script


def test_web_polling_starts_with_app_and_stops_on_logout_or_expired_session():
    script = read(SCRIPT)
    assert "const baseShowLogin = showLogin" in script
    assert "const baseShowApp = showApp" in script
    assert "stopBotTimer();" in script
    assert "startBotTimer();" in script
    assert "clearInterval(botTimer)" in script
    assert "botState = null" in script


def test_server_uses_dedicated_per_client_state_without_json_sync_races():
    source = read(SERVER)
    assert "CREATE TABLE IF NOT EXISTS bot_client_bot_enabled" in source
    assert "client_id INTEGER PRIMARY KEY" in source
    assert "desired_enabled INTEGER" in source
    assert "current_enabled INTEGER" in source
    assert '@router.get("/api/bot-web/bot-enabled")' in source
    assert '@router.put("/api/bot-web/bot-enabled")' in source
    assert '@router.post("/api/runtime/v1/bot-web/bot-enabled-sync")' in source
    assert "Depends(bot_web_console._web_client)" in source
    assert "Depends(bot_web_console._runtime_client)" in source
    assert 'request.headers.get("x-shop-key")' in source
    assert "ON CONFLICT(client_id) DO UPDATE SET" in source
    assert "desired_enabled=COALESCE(" in source
    assert "excluded.current_enabled" in source
    assert "last_seen_at=excluded.last_seen_at" in source
    assert "current_settings_json" not in source
    assert "desired_settings_json" not in source


def test_server_binds_each_client_token_to_one_shop_key():
    source = read(SERVER)
    assert 'SELECT shop_key FROM bot_client_bot_enabled WHERE client_id=?' in source
    assert "bound_shop_key and bound_shop_key != shop_key" in source
    assert "该客户端令牌已绑定其他 ShopKey" in source
    assert "status_code=409" in source


def test_windows_sync_applies_web_value_in_shop_scope_and_reports_current_value():
    source = read(WINDOWS)
    props = read(PROPS)
    assert "BotWebBotEnabledSyncService.InitializeForApp" in source
    assert "ShopSettingsScope.Enter(shop)" in source
    assert "ShopControlPlaneConnectionStore" in source
    assert '"/api/runtime/v1/bot-web/bot-enabled-sync"' in source
    assert 'request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey)' in source
    assert '["current_enabled"] = currentEnabled' in source
    assert 'root.Value<bool?>("desired_enabled")' in source
    assert "Params.Robot.CanUseRobot = desired.Value" in source
    assert "ControlPlaneClientToken" not in source
    assert "token=" not in source.lower()
    assert "ChromeNs\\BotWebBotEnabledSyncService.cs" in props


def test_server_bootstrap_container_and_ci_package_new_bridge():
    bootstrap = read(BOOTSTRAP)
    dockerfile = read(DOCKERFILE)
    workflow = read(WORKFLOW)
    assert "import bot_web_bot_enabled" in bootstrap
    assert "bot_web_bot_enabled.install(control_plane)" in bootstrap
    assert "bot_web_bot_enabled.init_db()" in bootstrap
    assert "bot_web_bot_enabled.py" in dockerfile
    assert "python -m py_compile app.py bootstrap.py" in workflow
    assert "bot_web_bot_enabled.py" in workflow
    assert "node --check static/bot-web-bot-enabled.js" in workflow
    assert "services/api-control-plane/bot_web_bot_enabled.py" in workflow
