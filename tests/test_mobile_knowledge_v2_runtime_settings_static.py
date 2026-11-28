from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
SERVER=ROOT/"services/api-control-plane/knowledge_v2_sync.py"
WINDOWS=ROOT/"src/Bot/ChromeNs/BotWebConsoleSyncService.cs"
HTML=ROOT/"services/api-control-plane/static/bot-web.html"
JS=ROOT/"services/api-control-plane/static/bot-web-v2.js"

def read(p): return p.read_text(encoding="utf-8-sig")

def test_runtime_settings_api_is_client_isolated_and_validated():
    s=read(SERVER)
    assert '@router.post("/api/bot-web/knowledge-v2/runtime-settings/read")' in s
    assert '@router.post("/api/bot-web/knowledge-v2/runtime-settings")' in s
    assert '@router.get("/api/bot-web/knowledge-v2/runtime-settings/{command_id}")' in s
    assert '"knowledge_v2_settings_get"' in s
    assert '"knowledge_v2_settings_set"' in s
    assert "threshold<0.70 or threshold>0.96" in s
    assert "confidence<0.50 or confidence>0.95" in s
    assert "math.isfinite(threshold)" in s
    assert "math.isfinite(confidence)" in s
    assert "WHERE id=? AND client_id=?" in s

def test_windows_reads_and_applies_existing_shop_scoped_v2_settings():
    s=read(WINDOWS)
    assert 'string.Equals(type, "knowledge_v2_settings_get"' in s
    assert 'string.Equals(type, "knowledge_v2_settings_set"' in s
    assert "KnowledgeEngineV2Service.GetSettingsView(seller)" in s
    assert "KnowledgeEngineV2Service.SetSettings(seller, enabled, mode, threshold, confidence)" in s
    assert "state.Shop.DisplayName" in s
    assert '["shop_key"] = state.Shop.ShopKey' in s

def test_mobile_runtime_settings_require_confirmation_and_show_safety_semantics():
    h=read(HTML); s=read(JS)
    assert 'id="knowledgeV2RuntimeEnabled"' in h
    assert 'id="knowledgeV2RuntimeMode"' in h
    assert 'id="knowledgeV2DirectThreshold"' in h
    assert 'id="knowledgeV2MinConfidence"' in h
    assert 'src="/static/bot-web-v2.js?v=15"' in h
    assert 'api("/api/bot-web/knowledge-v2/runtime-settings/read"' in s
    assert 'api("/api/bot-web/knowledge-v2/runtime-settings"' in s
    assert "Knowledge V2 是唯一检索引擎，始终启用" in s
    assert 'id="knowledgeV2RuntimeEnabled" type="checkbox" checked disabled' in h
    assert 'id="knowledgeV2RuntimeMode" disabled' in h
    assert '<option value="shadow">' not in h
    assert 'mode="production",enabled=true' in s
    assert "Number.isFinite(threshold)" in s
    assert "Shadow 只计算 V2 结果" not in s
    assert "confirm(" in s

def test_web_and_windows_reject_unsupported_legacy_settings_instead_of_acknowledging_them():
    server=read(SERVER)
    windows=read(WINDOWS)
    assert 'if mode != "production" or not payload.enabled:' in server
    assert 'payload.Value<bool?>("enabled") != true' in windows
    assert 'payload.Value<string>("mode"), "production"' in windows
    assert 'double.IsNaN(threshold)' in windows
