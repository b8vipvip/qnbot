from pathlib import Path

import importlib.util
import sqlite3
import sys
import types
from contextlib import contextmanager

import pytest

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
    assert 'expected_revision=payload.expected_revision' in s
    assert 'raise_on_conflict=True' in s

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


def test_web_v2_edits_use_compare_and_swap_to_prevent_stale_device_overwrites():
    server = read(SERVER)
    js = read(ROOT / "services/api-control-plane/static/bot-web-v2.js")
    html = read(ROOT / "services/api-control-plane/static/bot-web.html")
    model = server[
        server.index("class KnowledgeV2WebInput"):
        server.index("class KnowledgeV2SmartImportInput")
    ]
    save = server[
        server.index("def _save("):
        server.index('@router.get("/api/bot-web/knowledge-v2")')
    ]
    web_put = server[
        server.index('@router.put("/api/bot-web/knowledge-v2")'):
        server.index('@router.post("/api/runtime/v1/bot-web/knowledge-v2-sync")')
    ]

    assert "expected_revision: int" in model
    assert "raise_on_conflict=False" in save
    assert "raise_on_conflict" in save
    assert "status_code=409" in save
    assert '"code": "revision_conflict"' in save
    assert "expected_revision=payload.expected_revision" in web_put
    assert "raise_on_conflict=True" in web_put
    assert "knowledgeV2Revision:0" in js
    assert "expected_revision:state.knowledgeV2Revision" in js
    assert "err.status===409" in js
    assert "await loadKnowledgeV2(false)" in js
    assert 'bot-web-v2.js?v=11' in html


class _V2CasFakeControlPlane:
    def __init__(self, path):
        self.path = path

    @contextmanager
    def db(self):
        conn = sqlite3.connect(str(self.path))
        conn.row_factory = sqlite3.Row
        try:
            yield conn
            conn.commit()
        except Exception:
            conn.rollback()
            raise
        finally:
            conn.close()

    @staticmethod
    def iso_now():
        return "2026-11-22T08:00:00+00:00"


def _load_v2_cas_module(tmp_path):
    core = types.ModuleType("bot_web_console")
    core._web_client = lambda request=None: {"id": 1}
    core._runtime_client = lambda request=None: {"id": 1}
    sys.modules["bot_web_console"] = core

    name = "knowledge_v2_sync_revision_cas_under_test"
    spec = importlib.util.spec_from_file_location(name, ROOT / "services/api-control-plane/knowledge_v2_sync.py")
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    sys.modules[name] = module
    spec.loader.exec_module(module)

    module._cp = _V2CasFakeControlPlane(tmp_path / "knowledge-v2.db")
    module.bot_web_console = core
    with module._cp.db() as conn:
        conn.execute("CREATE TABLE client_tokens (id INTEGER PRIMARY KEY)")
        conn.execute("INSERT INTO client_tokens(id) VALUES(1)")
    module.init_db()
    return module


def _v2_cas_record(answer):
    return {
        "Id": "a",
        "Type": "general",
        "Title": "same question",
        "Intent": "general",
        "Subject": "device",
        "Predicate": "compatibility",
        "Entities": [],
        "Aliases": [],
        "Answer": answer,
        "ShortAnswer": "",
        "Conditions": [],
        "Exclusions": [],
        "RequiredContext": [],
        "ProductIds": [],
        "RiskLevel": "normal",
        "SourceType": "web_manual",
        "SourceId": "",
        "Authority": 0.92,
        "Confidence": 0.92,
        "Enabled": True,
        "Status": "active",
    }


@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
def test_stale_web_v2_editor_cannot_overwrite_newer_cloud_revision(tmp_path):
    from fastapi import HTTPException

    module = _load_v2_cas_module(tmp_path)
    first = module._save(1, [_v2_cas_record("windows answer")], "windows", expected_revision=0)
    assert first["revision"] == 1

    with pytest.raises(HTTPException) as conflict:
        module.web_put(
            module.KnowledgeV2WebInput(
                records=[_v2_cas_record("stale web answer")],
                expected_revision=0,
            ),
            client={"id": 1},
        )
    assert conflict.value.status_code == 409
    assert conflict.value.detail["code"] == "revision_conflict"
    assert conflict.value.detail["current_revision"] == 1
    assert module._state(1)["records"][0]["Answer"] == "windows answer"

    updated = module.web_put(
        module.KnowledgeV2WebInput(
            records=[_v2_cas_record("fresh web answer")],
            expected_revision=1,
        ),
        client={"id": 1},
    )
    assert updated["revision"] == 2
    assert updated["records"][0]["Answer"] == "fresh web answer"


@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
def test_stale_windows_v2_sync_returns_newer_web_revision_without_overwrite(tmp_path):
    module = _load_v2_cas_module(tmp_path)
    module._save(1, [_v2_cas_record("windows answer")], "windows", expected_revision=0)
    module._save(
        1,
        [_v2_cas_record("web answer")],
        "web",
        expected_revision=1,
        raise_on_conflict=True,
    )

    stale = module._save(1, [_v2_cas_record("stale windows answer")], "windows", expected_revision=1)
    assert stale["revision"] == 2
    assert stale["records"][0]["Answer"] == "web answer"
    assert module._state(1)["records"][0]["Answer"] == "web answer"
