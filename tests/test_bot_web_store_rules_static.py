from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PAGE = ROOT / "services" / "api-control-plane" / "static" / "bot-web.html"
LOADER = ROOT / "services" / "api-control-plane" / "static" / "bot-web-bot-enabled.js"
SCRIPT = ROOT / "services" / "api-control-plane" / "static" / "bot-web-store-rules.js"
SERVER = ROOT / "services" / "api-control-plane" / "store_rule_sync.py"

def read(path):
    return path.read_text(encoding="utf-8-sig")

def test_mobile_store_rule_center_is_loaded_from_settings_bundle():
    page, loader = read(PAGE), read(LOADER)
    assert 'bot-web-bot-enabled.js?v=5' in page
    assert 'script.src = "/static/bot-web-store-rules.js?v=1"' in loader
    assert "data-bot-web-store-rules" in loader

def test_store_rule_ui_reuses_cloud_profile_without_sensitive_fields():
    script = read(SCRIPT)
    assert 'api("/api/bot-web/store-rules")' in script
    assert 'api("/api/bot-web/store-rules",{method:"PUT"' in script
    for field in ("rawInput", "corePrompt", "rules", "Title", "Category", "Scope", "Priority", "Enabled", "Triggers", "Content"):
        assert field in script
    assert "API Key、Token" in script
    assert "api_key" not in script.lower()
    assert "bearer" not in script.lower()
    assert "password" not in script.lower()

def test_store_rule_ui_keeps_runtime_limits_visible_before_submit():
    script, server = read(SCRIPT), read(SERVER)
    assert 'maxlength="50000"' in script
    assert 'maxlength="2500"' in script
    assert 'maxlength="2200"' in script
    assert "rules.length>80" in script
    assert "slice(0,20)" in script
    assert "_MAX_PROFILE_BYTES = 512 * 1024" in server
    assert "len(rules) > 80" in server
