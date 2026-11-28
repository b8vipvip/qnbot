from pathlib import Path

import importlib.util
import sqlite3
import threading
from concurrent.futures import ThreadPoolExecutor
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
    assert "_MAX_RECORDS = 10000" in s and "16 * 1024 * 1024" in s
    assert "seen_ids=set()" in s
    assert "记录 ID 不允许重复" in s
    assert "math.isfinite(number)" in s
    assert "list_fields=(\"Entities\",\"Aliases\",\"Conditions\",\"Exclusions\",\"RequiredContext\",\"ProductIds\")" in s
    assert "any(not isinstance(x,str) for x in value)" in s
    assert "Enabled 必须是布尔值" in s
    assert "标题和答案必须是非空文本" in s
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
    assert "IsEnabledForShop(shop)" in s
    assert "KnowledgeCloudSyncService.IsEnabledForShop" not in s
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
    assert "expected_revision:state.knowledgeV2EditRevision" in js
    assert "err.status===409" in js
    assert "await loadKnowledgeV2(false)" in js
    assert 'bot-web-v2.js?v=15' in html


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
        conn.execute(
            """
            CREATE TABLE bot_commands(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_id INTEGER NOT NULL,
                command_type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                status TEXT NOT NULL,
                result_json TEXT NOT NULL,
                error TEXT NOT NULL,
                created_at TEXT NOT NULL
            )
            """
        )
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


@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
@pytest.mark.parametrize(
    "field,value",
    [
        ("Authority", float("nan")),
        ("Authority", float("inf")),
        ("Authority", float("-inf")),
        ("Confidence", float("nan")),
        ("Confidence", float("inf")),
        ("Confidence", float("-inf")),
        ("Authority", 1.01),
        ("Confidence", -0.01),
        ("Authority", 10**400),
        ("Confidence", -(10**400)),
    ],
)
def test_v2_record_numeric_metadata_must_be_finite_and_bounded(tmp_path, field, value):
    from fastapi import HTTPException

    module = _load_v2_cas_module(tmp_path)
    item = _v2_cas_record("answer")
    item[field] = value

    with pytest.raises(HTTPException) as caught:
        module._save(1, [item], "windows", expected_revision=0)

    assert caught.value.status_code == 400
    assert field in caught.value.detail
    assert module._state(1)["revision"] == 0


@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
def test_v2_record_ids_must_be_unique_within_a_cloud_snapshot(tmp_path):
    from fastapi import HTTPException

    module = _load_v2_cas_module(tmp_path)
    first = _v2_cas_record("first answer")
    second = _v2_cas_record("second answer")
    second["Id"] = first["Id"]

    with pytest.raises(HTTPException) as caught:
        module._save(1, [first, second], "windows", expected_revision=0)

    assert caught.value.status_code == 400
    assert "记录 ID 不允许重复" in caught.value.detail
    assert module._state(1)["revision"] == 0


@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
def test_v2_record_title_and_answer_must_be_text(tmp_path):
    from fastapi import HTTPException

    module = _load_v2_cas_module(tmp_path)
    item = _v2_cas_record("answer")
    item["Title"] = {"unexpected": "object"}

    with pytest.raises(HTTPException) as caught:
        module._save(1, [item], "windows", expected_revision=0)

    assert caught.value.status_code == 400
    assert "Title 必须是文本" in caught.value.detail
    assert module._state(1)["revision"] == 0


@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
@pytest.mark.parametrize(
    "field,value",
    [
        ("Entities", {"not": "an array"}),
        ("Aliases", ["valid", 7]),
        ("Enabled", "false"),
        ("CreatedAt", 12345),
    ],
)
def test_v2_record_schema_rejects_malformed_typed_fields(tmp_path, field, value):
    from fastapi import HTTPException

    module = _load_v2_cas_module(tmp_path)
    item = _v2_cas_record("answer")
    item[field] = value

    with pytest.raises(HTTPException) as caught:
        module._save(1, [item], "windows", expected_revision=0)

    assert caught.value.status_code == 400
    assert module._state(1)["revision"] == 0


@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
@pytest.mark.parametrize(
    "field,value",
    [
        ("direct_threshold", float("nan")),
        ("direct_threshold", float("inf")),
        ("direct_threshold", float("-inf")),
        ("min_confidence", float("nan")),
        ("min_confidence", float("inf")),
        ("min_confidence", float("-inf")),
    ],
)
def test_v2_runtime_settings_reject_non_finite_thresholds_without_queueing(tmp_path, field, value):
    from fastapi import HTTPException

    module = _load_v2_cas_module(tmp_path)
    values = {
        "enabled": True,
        "mode": "production",
        "direct_threshold": 0.82,
        "min_confidence": 0.72,
    }
    values[field] = value
    payload = module.KnowledgeV2RuntimeSettingsInput(**values)

    with pytest.raises(HTTPException) as caught:
        module.web_v2_runtime_settings_save(payload, client={"id": 1})

    assert caught.value.status_code == 400
    assert "有限数值" in caught.value.detail
    with module._cp.db() as conn:
        queued = conn.execute("SELECT COUNT(*) FROM bot_commands").fetchone()[0]
    assert queued == 0


@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
def test_two_windows_syncing_from_the_same_revision_cannot_overwrite_the_winner(tmp_path):
    module = _load_v2_cas_module(tmp_path)
    barrier = threading.Barrier(2)

    def push(answer):
        barrier.wait(timeout=5)
        return module._save(
            1,
            [_v2_cas_record(answer)],
            "windows",
            expected_revision=0,
        )

    with ThreadPoolExecutor(max_workers=2) as executor:
        futures = [
            executor.submit(push, "device A answer"),
            executor.submit(push, "device B answer"),
        ]
        results = [future.result(timeout=10) for future in futures]

    state = module._state(1)
    assert state["revision"] == 1
    assert state["records"][0]["Answer"] in ("device A answer", "device B answer")
    # Both clients must receive the single committed winner, never a stale local write.
    assert all(result["revision"] == 1 for result in results)
    assert all(result["records"][0]["Answer"] == state["records"][0]["Answer"] for result in results)


@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
def test_runtime_sync_rejects_physical_deletion_and_accepts_same_id_tombstones(tmp_path):
    module = _load_v2_cas_module(tmp_path)
    first = _v2_cas_record("first answer")
    second = _v2_cas_record("second answer")
    second["Id"] = "b"
    initial = [first, second]
    seeded = module._save(1, initial, "windows", expected_revision=0)
    assert seeded["revision"] == 1

    # A damaged/partially restored local database must not erase a previously
    # synchronized cloud record by sending a shorter full-corpus snapshot.
    rejected = module.runtime_sync(
        module.KnowledgeV2SyncInput(revision=1, records=[first]),
        types.SimpleNamespace(),
        x_shop_key="qn_0123456789ab",
    )
    assert rejected["revision"] == 1
    assert rejected["records"] == initial
    assert rejected["sync_warning"]["code"] == "missing_tombstones"
    assert rejected["sync_warning"]["missing_count"] == 1
    assert module._state(1)["records"] == initial

    # The supported delete path keeps the original ID and writes a disabled
    # tombstone, so the server can distinguish intentional deletion from data loss.
    tombstone = dict(second)
    tombstone["Enabled"] = False
    tombstone["Status"] = "deleted"
    accepted = module.runtime_sync(
        module.KnowledgeV2SyncInput(revision=1, records=[first, tombstone]),
        types.SimpleNamespace(),
        x_shop_key="qn_0123456789ab",
    )
    assert accepted["revision"] == 2
    assert accepted["records"][1]["Id"] == "b"
    assert accepted["records"][1]["Enabled"] is False
    assert accepted["records"][1]["Status"] == "deleted"
    assert module._state(1)["revision"] == 2

    # A stale restored snapshot that still contains the same ID as an enabled
    # record must not resurrect a cloud tombstone at the same revision.
    stale_resurrection = module.runtime_sync(
        module.KnowledgeV2SyncInput(revision=2, records=initial),
        types.SimpleNamespace(),
        x_shop_key="qn_0123456789ab",
    )
    assert stale_resurrection["revision"] == 2
    assert stale_resurrection["sync_warning"]["code"] == "cloud_delete_wins"
    assert stale_resurrection["sync_warning"]["conflict_count"] == 1
    assert module._state(1)["records"][1]["Status"] == "deleted"
    assert module._state(1)["records"][1]["Enabled"] is False


def test_windows_cloud_sync_recovers_missing_tombstone_snapshot_from_cloud():
    service = read(WINDOWS)
    sync = service[
        service.index("private static async Task SyncOnce"):
        service.index("private static State LoadState")
    ]
    recovery = sync[
        sync.index("if(missingTombstones && cloudRevision == state.Revision)"):
        sync.index("if(cloudRevision<state.Revision)")
    ]
    assert 'root["sync_warning"] as JObject' in sync
    assert '["code"], "missing_tombstones"' in sync
    assert '["code"], "cloud_delete_wins"' in sync
    assert "MergeCloudAndLocal(records, latestLocal, preserveLocalOnly: true, baseline: state.RecordBaseline)" in recovery
    assert "Backup(shop, latestLocal, cloudRevision)" in recovery
    assert recovery.index("Backup(shop, latestLocal, cloudRevision)") < recovery.index("TryReplaceAllIfUnchanged")
    assert "local_changed_during_tombstone_recovery" in recovery
    assert "PersistState(shop, state, cloudRevision, cloudFingerprint, records)" in recovery

@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
def test_feedback_two_devices_retry_and_paginate_without_duplicate_counts(tmp_path):
    from fastapi import HTTPException
    import uuid
    m=_load_v2_cas_module(tmp_path)
    with m._cp.db() as conn:
        conn.execute("CREATE TABLE bot_client_shop_binding(client_id INTEGER PRIMARY KEY,shop_key TEXT)")
        conn.execute("INSERT INTO bot_client_shop_binding VALUES(1,'shop-a')")
    def event(kind="sent"):
        return {"Id":uuid.uuid4().hex,"KnowledgeId":"a","EventType":kind,"UtcTicks":m._feedback_now_ticks()}
    a,b=event(),event("accepted")
    first=m.runtime_feedback_sync(m.KnowledgeV2FeedbackInput(events=[a]),None,"shop-a")
    second=m.runtime_feedback_sync(m.KnowledgeV2FeedbackInput(events=[a,b]),None,"shop-a")
    assert len(second["events"])==2
    assert m.web_feedback({"id":1})["items"]["a"]["sent"]==1
    assert m.web_feedback({"id":1})["items"]["a"]["accepted"]==1
    latest=m.runtime_feedback_sync(m.KnowledgeV2FeedbackInput(epoch=first["epoch"],cursor=first["cursor"]),None,"shop-a")
    assert latest["events"]==[b]
    assert m.runtime_feedback_sync(m.KnowledgeV2FeedbackInput(epoch=latest["epoch"],cursor=latest["cursor"]),None,"shop-a")["events"]==[]
    with pytest.raises(HTTPException) as wrong:
        m.runtime_feedback_sync(m.KnowledgeV2FeedbackInput(events=[a]),None,"shop-b")
    assert wrong.value.status_code==403
    with pytest.raises(HTTPException) as clash:
        m.runtime_feedback_sync(m.KnowledgeV2FeedbackInput(events=[dict(a,EventType="correction")]),None,"shop-a")
    assert clash.value.status_code==409
    more=[event() for _ in range(200)]
    m.runtime_feedback_sync(m.KnowledgeV2FeedbackInput(events=more),None,"shop-a")
    page=m.runtime_feedback_sync(m.KnowledgeV2FeedbackInput(),None,"shop-a")
    assert len(page["events"])==200 and page["has_more"]
    tail=m.runtime_feedback_sync(m.KnowledgeV2FeedbackInput(epoch=page["epoch"],cursor=page["cursor"]),None,"shop-a")
    assert len(tail["events"])==2 and not tail["has_more"]
    assert m.web_feedback({"id":1})["items"]["a"]["sent"]==201

@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
def test_web_delete_preserves_tombstone_and_rejects_resurrection(tmp_path):
    from fastapi import HTTPException
    module=_load_v2_cas_module(tmp_path)
    record=_v2_cas_record("answer")
    module._save(1,[record],"windows",expected_revision=0)
    result=module.web_put(module.KnowledgeV2WebInput(records=[],expected_revision=1),client={"id":1})
    assert result["records"][0]["Id"]==record["Id"]
    assert result["records"][0]["Status"]=="deleted" and result["records"][0]["Enabled"] is False
    with pytest.raises(HTTPException) as exc:
        module.web_put(module.KnowledgeV2WebInput(records=[record],expected_revision=2),client={"id":1})
    assert exc.value.status_code==409
    js=read(ROOT/"services/api-control-plane/static/bot-web-v2.js")
    assert "knowledgeV2EditRevision!==state.knowledgeV2Revision" in js
    assert "expected_revision:state.knowledgeV2EditRevision" in js
    assert "/api/bot-web/knowledge-v2/feedback" in js

@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
def test_v2_web_packages_and_backups_are_cas_guarded_and_do_not_restore_deleted_ids(tmp_path):
    from fastapi import HTTPException
    m=_load_v2_cas_module(tmp_path)
    original=_v2_cas_record("first")
    m._save(1,[original],"windows",expected_revision=0)
    pkg=m.web_v2_export({"id":1})
    assert pkg["SchemaVersion"]==2 and pkg["Records"]==[original]
    conflicting=dict(original,Answer="changed")
    payload=m.KnowledgeV2PackageInput(expected_revision=1,mode="merge",package=dict(pkg,Records=[conflicting]))
    merged=m.web_v2_import(payload,{"id":1})
    assert len(merged["records"])==2
    assert any(x["Status"]=="candidate" and not x["Enabled"] for x in merged["records"])
    with pytest.raises(HTTPException) as stale:
        m.web_v2_import(payload,{"id":1})
    assert stale.value.status_code==409
    m.web_put(m.KnowledgeV2WebInput(records=[],expected_revision=2),{"id":1})
    backups=m.web_v2_backups(client={"id":1})["backups"]
    prior=next(x for x in backups if x["revision"]==1)
    restored=m.web_v2_restore(prior["id"],m.KnowledgeV2RestoreInput(expected_revision=3,restore_confirmed=True),{"id":1})
    assert all(x["Status"]=="deleted" for x in restored["records"])

def test_web_v2_editor_and_packages_do_not_call_legacy_knowledge_api():
    js=read(ROOT/"services/api-control-plane/static/bot-web-v2.js")
    html=read(ROOT/"services/api-control-plane/static/bot-web.html")
    assert 'openKnowledgeV2Dialog(null,p.question,p.answer)' in js
    assert 'api("/api/bot-web/knowledge-v2/export")' in js
    assert 'api("/api/bot-web/knowledge-v2/import"' in js
    assert '/api/bot-web/knowledge/' not in js
    assert '/api/bot-web/knowledge?' not in js
    assert 'id="knowledgeDialog"' not in html
    render=js.split("function renderKnowledgeV2(",1)[1].split("function openKnowledgeV2Dialog",1)[0]
    assert 'esc(quality)' in render

@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
def test_remote_v2_operations_are_shop_bound_confirmed_and_exclusive(tmp_path):
    from fastapi import HTTPException
    m=_load_v2_cas_module(tmp_path)
    with m._cp.db() as conn:
        conn.execute("CREATE TABLE bot_client_shop_binding(client_id INTEGER PRIMARY KEY,shop_key TEXT)")
        conn.execute("INSERT INTO bot_client_shop_binding VALUES(1,'shop-a')")
        conn.execute("ALTER TABLE bot_commands ADD COLUMN completed_at TEXT")
        conn.execute("INSERT INTO bot_knowledge_v2_devices VALUES(1,'shop-a','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','2026-11-28T08:00:00+00:00')")
    with pytest.raises(HTTPException):
        m.web_v2_operation(m.KnowledgeV2OperationInput(action="history_scan"),{"id":1})
    with pytest.raises(HTTPException):
        m.web_v2_operation(m.KnowledgeV2OperationInput(action="rollback",confirmed=True),{"id":1})
    task=m.web_v2_operation(m.KnowledgeV2OperationInput(action="history_scan",confirmed=True,device_id="a"*32),{"id":1})
    with pytest.raises(HTTPException) as busy:
        m.web_v2_operation(m.KnowledgeV2OperationInput(action="revision_list",device_id="a"*32),{"id":1})
    assert busy.value.status_code==409
    assert m.web_v2_operation_status(task["command_id"],{"id":1})["status"]=="pending"
    assert m.web_v2_operation_cancel(task["command_id"],{"id":1})["status"]=="cancelled"
    assert m.web_v2_operation(m.KnowledgeV2OperationInput(action="history_status",device_id="a"*32),{"id":1})["command_id"]!=task["command_id"]

def test_remote_v2_operations_claim_before_dispatch_and_guard_results():
    s=read(ROOT/"services/api-control-plane/bot_web_console.py")
    claim=s.split("def _command_rows(",1)[1].split("def _decode_message_attachment",1)[0]
    assert 'conn.execute("BEGIN IMMEDIATE")' in claim
    assert "payload[\"claim\"]=secrets.token_hex(24)" in claim
    assert "payload.get(\"shop_key\")!=shop_key" in claim
    assert "status='running'" in claim and "status='pending'" in claim
    assert "secrets.compare_digest(expected,supplied)" in s
    assert "knowledge_v2_operation_cancel" in claim

def test_web_operations_ui_uses_selected_device_and_atomic_bulk_revision():
    web=read(ROOT/"services/api-control-plane/static/bot-web-v2-operations.js")
    html=read(ROOT/"services/api-control-plane/static/bot-web.html")
    windows=read(ROOT/"src/Bot/ChromeNs/BotWebConsoleSyncService.cs")
    repo=read(ROOT/"src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    for action in ("history_status","history_scan","history_resume","revision_generate","revision_apply","revision_reject","conflict_list","conflict_resolve","rollback_list","rollback"):
        assert action in web and action in windows
    assert "device_id:selected" in web
    assert "v2Ops.checkpointDevice===$(" in web
    assert 'expected_revision:expected' in web and "v2BulkIds.clear()" in web
    assert 'Status:"deleted"' in web
    assert "knowledgeV2BulkDelete" in html
    assert "knowledge_v2_operation_cancel" in windows
    assert 'EditorRecordFingerprint(before) != EditorRecordFingerprint(applied)' in repo
    assert "expectedRevisionId" in repo

@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
def test_remote_operation_claims_once_across_two_devices_and_rejects_unclaimed_results(tmp_path):
    m=_load_v2_cas_module(tmp_path)
    spec=importlib.util.spec_from_file_location("bot_web_console_claim_test",ROOT/"services/api-control-plane/bot_web_console.py")
    console=importlib.util.module_from_spec(spec)
    spec.loader.exec_module(console)
    console._cp=m._cp
    with m._cp.db() as conn:
        conn.execute("CREATE TABLE bot_client_shop_binding(client_id INTEGER PRIMARY KEY,shop_key TEXT)")
        conn.execute("INSERT INTO bot_client_shop_binding VALUES(1,'shop-a')")
        conn.execute("ALTER TABLE bot_commands ADD COLUMN completed_at TEXT")
        for device in ("a"*32,"b"*32):
            conn.execute("INSERT INTO bot_knowledge_v2_devices VALUES(1,'shop-a',?,?)",(device,m._cp.iso_now()))
    task=m.web_v2_operation(m.KnowledgeV2OperationInput(action="history_scan",confirmed=True,device_id="a"*32),{"id":1})
    assert console._command_rows(1,"shop-a","b"*32)==[]
    barrier=threading.Barrier(2)
    def claim():
        barrier.wait(timeout=5)
        return console._command_rows(1,"shop-a","a"*32)
    with ThreadPoolExecutor(max_workers=2) as pool:
        futures=[pool.submit(claim),pool.submit(claim)]
        rows=[row for future in futures for row in future.result(timeout=10)]
    assert len(rows)==1
    command=rows[0];cid=command["id"];claim_token=command["payload"]["claim"]
    console._apply_command_results(1,[{"id":cid,"success":True,"result":{"forged":True}}])
    assert m.web_v2_operation_status(cid,{"id":1})["status"]=="running"
    console._apply_command_results(1,[{"id":cid,"claim":claim_token,"phase":"running","success":True,"result":{"messages":150}}])
    assert m.web_v2_operation_status(cid,{"id":1})["result"]["messages"]==150
    assert m.web_v2_operation_cancel(cid,{"id":1})["status"]=="cancel_requested"
    assert console._command_rows(1,"shop-a","b"*32)==[]
    assert console._command_rows(1,"shop-a","a"*32)[0]["type"]=="knowledge_v2_operation_cancel"
    console._apply_command_results(1,[{"id":cid,"claim":claim_token,"phase":"cancelled","success":False,"result":{}}])
    assert m.web_v2_operation_status(cid,{"id":1})["status"]=="cancelled"
    console._apply_command_results(1,[{"id":cid,"claim":claim_token,"success":True,"result":{"duplicate":True}}])
    assert m.web_v2_operation_status(cid,{"id":1})["status"]=="cancelled"

@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
def test_feedback_convergence_requires_two_registered_persisted_device_receipts(tmp_path):
    import hashlib,uuid,base64
    from datetime import datetime,timezone
    m=_load_v2_cas_module(tmp_path)
    m._cp.iso_now=lambda:datetime.now(timezone.utc).isoformat()
    with m._cp.db() as conn:
        conn.execute("CREATE TABLE bot_client_shop_binding(client_id INTEGER PRIMARY KEY,shop_key TEXT)")
        conn.execute("INSERT INTO bot_client_shop_binding VALUES(1,'shop-a')")
        for device in ("a"*32,"b"*32):
            conn.execute("INSERT INTO bot_knowledge_v2_devices VALUES(1,'shop-a',?,?)",(device,m._cp.iso_now()))
    event={"Id":uuid.uuid4().hex,"KnowledgeId":"a","EventType":"sent","UtcTicks":m._feedback_now_ticks()}
    canonical="\n".join((event["Id"],base64.b64encode(event["KnowledgeId"].encode()).decode(),event["EventType"],str(event["UtcTicks"])))
    digest=hashlib.sha256(canonical.encode()).hexdigest()
    initial=m.runtime_feedback_sync(m.KnowledgeV2FeedbackInput(events=[event],device_id="a"*32,local_event_count=1,local_event_digest=digest),None,"shop-a")
    assert not m.web_v2_devices({"id":1})["two_device_nonempty_convergence"]
    def prove(device,digest):
        return m.runtime_feedback_sync(m.KnowledgeV2FeedbackInput(epoch=initial["epoch"],cursor=initial["cursor"],device_id=device,
            local_event_count=1,local_event_digest=digest),None,"shop-a")
    prove("a"*32,digest);prove("b"*32,"0"*64)
    assert m.web_v2_devices({"id":1})["verified_devices"]==1
    prove("b"*32,digest)
    proof=m.web_v2_devices({"id":1})
    assert proof["verified_devices"]==2 and proof["two_device_nonempty_convergence"]
    m.runtime_feedback_sync(m.KnowledgeV2FeedbackInput(events=[dict(event,Id=uuid.uuid4().hex)]),None,"shop-a")
    assert not m.web_v2_devices({"id":1})["two_device_nonempty_convergence"]

def test_remote_knowledge_job_receipts_survive_restart_without_replaying_mutations():
    source=read(ROOT/"src/Bot/ChromeNs/BotWebConsoleSyncService.cs")
    assert "ProtectedData.Protect(plain" in source
    assert "ProtectedData.Unprotect(encrypted" in source
    assert "CrossProcessAtomicStateFile.WriteAllTextAtomic(path" in source
    start=source.split("private static void StartKnowledgeOperation(",1)[1].split("private static async Task<JObject> ExecuteKnowledgeOperationAsync",1)[0]
    assert start.index("WriteKnowledgeJournal(path, journalScope, receipt)") < start.index("Task.Run(async")
    assert "WriteKnowledgeJournal(job.JournalPath, job.JournalScope, result)" in start
    assert 'result["phase"] = completed ? "completed" : "interrupted"' in source
    assert "RecoverInterruptedKnowledgeOperation(state, id, result, scope, claim)" in source
    assert "EnsureKnowledgeResultDurable(x.Value, journalScope)" in source
    assert "ReferenceEquals(current, pair.Value)" in source
    assert "ThenBy(x => x.Key).Take(100)" in source
    assert "File.Delete(path)" in source

def test_remote_result_acknowledgement_is_claim_specific_and_idempotent():
    console=read(ROOT/"services/api-control-plane/bot_web_console.py")
    client=read(ROOT/"src/Bot/ChromeNs/BotWebConsoleSyncService.cs")
    assert '"knowledge_result_receipts": knowledge_receipts' in console
    assert "secrets.compare_digest(expected,supplied)" in console
    assert 'knowledge_receipts.append({"id":command_id,"claim":supplied})' in console
    assert 'elif phase=="interrupted":' in console
    assert 'knowledgeReceipts = root["knowledge_result_receipts"] as JArray' in client
    assert 'x.Value<long?>("id") == pair.Key && (string)x["claim"] == claim' in client


@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
def test_revision_history_two_devices_isolated_idempotent_and_never_replays_corpus(tmp_path):
    from fastapi import HTTPException
    import json
    module = _load_v2_cas_module(tmp_path)
    with module._cp.db() as conn:
        conn.execute("CREATE TABLE bot_client_shop_binding (client_id INTEGER PRIMARY KEY, shop_key TEXT)")
        conn.execute("INSERT INTO bot_client_shop_binding VALUES (1,'shop-a')")
    row={"Id":"a"*32,"KnowledgeId":"a","MessageRecordId":"revision-candidate:example",
         "Action":"apply","PreviousJson":json.dumps(_v2_cas_record("before")),
         "CurrentJson":json.dumps(_v2_cas_record("after")),"CreatedAtTicks":638000000000000000}
    send=module.runtime_revision_sync
    model=module.KnowledgeV2RevisionHistoryInput
    first=send(model(rows=[row]),None,"shop-a")
    assert first["ack_ids"]==[row["Id"]]
    other=send(model(),None,"shop-a")
    assert other["rows"]==[row]
    assert send(model(epoch=other["epoch"],cursor=other["cursor"],rows=[row]),None,"shop-a")["rows"]==[]
    with pytest.raises(HTTPException) as conflict:
        send(model(rows=[dict(row,Action="rollback")]),None,"shop-a")
    assert conflict.value.status_code==409
    with pytest.raises(HTTPException) as isolated:
        send(model(),None,"shop-b")
    assert isolated.value.status_code==403
    assert module._state(1)["records"]==[]


@pytest.mark.skipif(importlib.util.find_spec("fastapi") is None, reason="server dependencies are not installed")
def test_sqlite_snapshot_sync_accepts_5001_records_and_preserves_cas(tmp_path):
    module=_load_v2_cas_module(tmp_path)
    records=[dict(_v2_cas_record("answer"),Id=f"r-{i}") for i in range(5001)]
    saved=module._save(1,records,"windows",expected_revision=0)
    assert saved["revision"]==1 and len(saved["records"])==5001
    assert len(module._state(1)["records"])==5001
    stale=module._save(1,[records[0]],"windows",expected_revision=0)
    assert stale["revision"]==1 and len(stale["records"])==5001
