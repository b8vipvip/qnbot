from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
SERVER=ROOT/"services/api-control-plane/knowledge_v2_sync.py"
WINDOWS=ROOT/"src/Bot/ChromeNs/BotWebConsoleSyncService.cs"
HTML=ROOT/"services/api-control-plane/static/bot-web.html"
JS=ROOT/"services/api-control-plane/static/bot-web-v2.js"

def read(p): return p.read_text(encoding="utf-8-sig")

def test_server_queues_client_isolated_native_v2_smart_import():
    s=read(SERVER)
    assert '@router.post("/api/bot-web/knowledge-v2/smart-import")' in s
    assert '@router.get("/api/bot-web/knowledge-v2/smart-import/{command_id}")' in s
    assert '"knowledge_v2_smart_import"' in s
    assert 'client_id=int(client["id"])' in s
    assert "len(text)>20000" in s
    assert "max(15,min(180" in s
    assert "WHERE id=? AND client_id=? AND command_type=\'knowledge_v2_smart_import\'" in s

def test_windows_executes_existing_native_smart_import_service_in_shop_scope():
    s=read(WINDOWS)
    assert 'string.Equals(type, "knowledge_v2_smart_import"' in s
    assert "ExecuteKnowledgeV2SmartImportAsync" in s
    assert "state.Shop.DisplayName" in s
    assert "new Bot.Knowledge.ClipboardKnowledgeData { Text = text }" in s
    assert "new Bot.Knowledge.KnowledgeV2SmartImportService()" in s
    assert "service.ImportAsync(" in s
    assert '["duplicate_skipped"] = imported.DuplicateSkipped' in s

def test_mobile_submits_confirms_and_polls_v2_smart_import():
    h=read(HTML); s=read(JS)
    assert 'id="knowledgeV2SmartImportText"' in h
    assert 'id="knowledgeV2SmartImportBtn"' in h
    assert 'src="/static/bot-web-v2.js?v=9"' in h
    assert 'api("/api/bot-web/knowledge-v2/smart-import"' in s
    assert "/api/bot-web/knowledge-v2/smart-import/${id}" in s
    assert "confirm(" in s
    assert "AI 正在整理原生 V2 结构" in s
    assert "await loadKnowledgeV2(false)" in s
