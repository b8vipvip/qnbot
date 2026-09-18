from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
SERVER=ROOT/"services/api-control-plane/knowledge_v2_sync.py"
BOOT=ROOT/"services/api-control-plane/bootstrap.py"
DOCKER=ROOT/"services/api-control-plane/Dockerfile"
WINDOWS=ROOT/"src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs"
PROPS=ROOT/"src/Bot/Directory.Build.props"

def read(p): return p.read_text(encoding="utf-8-sig")

def test_server_exposes_client_isolated_v2_bridge():
    s=read(SERVER)
    assert "CREATE TABLE IF NOT EXISTS bot_knowledge_v2_state" in s
    assert "client_id INTEGER PRIMARY KEY" in s
    assert '@router.get("/api/bot-web/knowledge-v2")' in s
    assert '@router.put("/api/bot-web/knowledge-v2")' in s
    assert '@router.post("/api/runtime/v1/bot-web/knowledge-v2-sync")' in s
    assert "Depends(bot_web_console._web_client)" in s
    assert "bot_web_console._runtime_client(request)" in s
    assert "_MAX_RECORDS = 5000" in s and "4 * 1024 * 1024" in s
    for secret in ("api_key","authorization","cookie","password"):
        assert ('"'+secret+'"') not in s.lower()

def test_first_runtime_report_seeds_state_and_stale_runtime_cannot_overwrite_web():
    s=read(SERVER)
    assert 'current["revision"] == 0 and payload.records is not None' in s
    assert 'current["revision"] > payload.revision' in s
    assert 'return _save(client_id,payload.records,"windows")' in s
    assert 'return _save(int(client["id"]), payload.records, "web")' in s

def test_windows_bridge_reuses_shop_repository_and_existing_cloud_credentials():
    s=read(WINDOWS)
    assert "ShopControlPlaneConnectionStore" in s
    assert "KnowledgeCloudSyncService.IsEnabledForShop" in s
    assert '"/api/runtime/v1/bot-web/knowledge-v2-sync"' in s
    assert 'request.Headers.TryAddWithoutValidation("X-Shop-Key",shop.ShopKey)' in s
    assert "KnowledgeEngineV2Repository.LoadAll" in s
    assert "KnowledgeEngineV2Repository.ReplaceAll" in s
    assert "ApiKey" not in s and "Cookie" not in s and "Password" not in s
    assert "KnowledgeV2CloudSyncService.cs" in read(PROPS)

def test_control_plane_packages_and_initializes_bridge():
    assert "import knowledge_v2_sync" in read(BOOT)
    assert "knowledge_v2_sync.install(control_plane, bot_web_console)" in read(BOOT)
    assert "knowledge_v2_sync.init_db()" in read(BOOT)
    assert "knowledge_v2_sync.py" in read(DOCKER)
