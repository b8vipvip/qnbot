from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
SERVER=ROOT/"services/api-control-plane/knowledge_v2_sync.py"
HTML=ROOT/"services/api-control-plane/static/bot-web.html"
JS=ROOT/"services/api-control-plane/static/bot-web-v2.js"

def read(p): return p.read_text(encoding="utf-8-sig")

def test_v2_runtime_settings_audit_is_client_isolated_and_bounded():
    s=read(SERVER)
    assert '@router.get("/api/bot-web/knowledge-v2/runtime-settings/audit")' in s
    assert "WHERE client_id=?" in s
    assert "ORDER BY id DESC LIMIT 30" in s
    assert 'command_type IN (\'knowledge_v2_settings_get\',\'knowledge_v2_settings_set\')' in s
    assert 'for key in ("enabled","mode","direct_threshold","min_confidence","shop_key")' in s

def test_mobile_renders_settings_audit_without_command_payload_dump():
    h=read(HTML); s=read(JS)
    assert 'id="knowledgeV2SettingsAudit"' in h
    assert 'id="refreshKnowledgeV2SettingsAuditBtn"' in h
    assert 'src="/static/bot-web-v2.js?v=10"' in h
    assert 'api("/api/bot-web/knowledge-v2/runtime-settings/audit")' in s
    assert "loadKnowledgeV2SettingsAudit(false)" in s
    assert "x.settings||{}" in s
