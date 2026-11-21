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
    assert 'return _save(client_id,payload.records,"windows",expected_revision=0)' in s
    assert 'return _save(client_id,payload.records,"windows",expected_revision=payload.revision)' in s
    assert 'return _save(int(client["id"]), payload.records, "web")' in s

def test_windows_bridge_reuses_shop_repository_and_existing_cloud_credentials():
    s=read(WINDOWS)
    assert "ShopControlPlaneConnectionStore" in s
    assert "KnowledgeCloudSyncService.IsEnabledForShop" in s
    assert '"/api/runtime/v1/bot-web/knowledge-v2-sync"' in s
    assert 'request.Headers.TryAddWithoutValidation("X-Shop-Key",shop.ShopKey)' in s
    assert "KnowledgeEngineV2Repository.LoadAll" in s
    assert "KnowledgeEngineV2Repository.TryReplaceAllIfUnchanged" in s
    assert "ApiKey" not in s and "Cookie" not in s and "Password" not in s
    assert "KnowledgeV2CloudSyncService.cs" in read(PROPS)

def test_control_plane_packages_and_initializes_bridge():
    assert "import knowledge_v2_sync" in read(BOOT)
    assert "knowledge_v2_sync.install(control_plane, bot_web_console)" in read(BOOT)
    assert "knowledge_v2_sync.init_db()" in read(BOOT)
    assert "knowledge_v2_sync.py" in read(DOCKER)


def test_cloud_replay_preserves_record_updated_at_for_idempotent_poll():
    s=read(ROOT/"src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    part=s[s.index("private static KnowledgeV2Record CloneAndNormalize"):s.index("private static void NormalizeForSave")]
    assert "var updatedAt = clone.UpdatedAt;" in part
    assert "clone.UpdatedAt = updatedAt;" in part
    assert "clone.LastVerifiedAt = verifiedAt;" in part


def test_windows_v2_cloud_revision_and_fingerprint_survive_process_restart():
    s=read(WINDOWS)
    assert 'private const string RevisionKey = "KnowledgeV2CloudRevision"' in s
    assert 'private const string FingerprintKey = "KnowledgeV2CloudFingerprint"' in s
    assert "States.GetOrAdd(shop.ShopKey,_=>LoadState(shop))" in s
    assert "new ShopScopedSettingsStore(shop,Paths)" in s
    assert "store.TryGetString(RevisionKey,out revisionText)" in s
    assert "store.MergeValues(new Dictionary<string,string>(StringComparer.Ordinal)" in s
    assert "{ RevisionKey, revision }" in s
    assert "{ FingerprintKey, fingerprint }" in s

def test_runtime_sync_save_uses_atomic_revision_compare_and_swap():
    s=read(SERVER)
    save=s[s.index("def _save("):s.index("@router.get(\"/api/bot-web/knowledge-v2\")")]
    runtime=s[s.index("def runtime_sync("):s.index("def web_smart_import(")]
    assert 'conn.execute("BEGIN IMMEDIATE")' in save
    assert 'expected_revision is not None and current["revision"] != expected_revision' in save
    assert 'expected_revision=0' in runtime
    assert 'expected_revision=payload.revision' in runtime
    assert runtime.index('current["revision"] > payload.revision') < runtime.index('expected_revision=payload.revision')
    assert 'return current' in save
