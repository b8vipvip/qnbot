from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_v2_cloud_replay_backs_up_before_any_shop_scoped_replace():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    apply = service[
        service.index("if(!string.Equals(mergedFingerprint,latestFingerprint"):
        service.index("PersistState(shop,state,cloudRevision,cloudFingerprint);", service.index("if(!string.Equals(mergedFingerprint,latestFingerprint"))
    ]

    assert "Backup(shop,latestLocal,cloudRevision)" in apply
    assert "TryReplaceAllIfUnchanged" in apply
    assert apply.index("Backup(shop,latestLocal,cloudRevision)") < apply.index("TryReplaceAllIfUnchanged")
    assert "return;" in apply


def test_v2_cloud_conflicts_preserve_local_facts_as_disabled_review_candidates():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    merge = service[
        service.index("private static List<KnowledgeV2Record> MergeCloudAndLocal"):
        service.index("private static void AddUnique")
    ]
    candidate = service[
        service.index("private static void AddCandidate"):
        service.index("private static void AddUnique")
    ]

    assert "MergeCloudAndLocal(records,latestLocal,preserveLocalOnly:!hasBaseline)" in service
    assert "cloud_sync_conflict_candidate" in candidate
    assert 'candidate.Enabled=false;' in candidate
    assert 'candidate.Status="candidate";' in candidate
    assert "candidate.SourceId=originalId" in candidate
    assert "AddCandidate(merged,mergedIds,local,id)" in merge


def test_v2_cloud_replacement_uses_atomic_repository_compare_and_swap():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    method = repository[
        repository.index("internal static bool TryReplaceAllIfUnchanged"):
        repository.index("public static string GetDatabasePath")
    ]

    assert "lock(state.Sync)" in method
    assert "ComputeCloudSyncFingerprint(current)" in method
    assert "if(!string.Equals(currentFingerprint,expectedFingerprint??string.Empty" in method
    assert method.index("if(!string.Equals(currentFingerprint") < method.index("state.Db.ReplaceRecordsInTransaction")
    assert "state.Db.ReplaceRecordsInTransaction" in method
    assert "MirrorAllToLegacy(shop,normalized)" in method
    assert "KnowledgeEngineV2Service.ReplaceSnapshot(seller,normalized)" in method


def test_v2_cloud_sync_uses_same_canonical_fingerprint_for_persistence_and_compare():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")

    assert "KnowledgeEngineV2Repository.ComputeCloudSyncFingerprint(records)" in service
    assert "new ShopScopedSettingsStore(shop,Paths)" in service
    assert "store.TryGetString(FingerprintKey,out fingerprint)" in service
    persist = service[
        service.index("private static void PersistState"):
        service.index("private static List<KnowledgeV2Record> MergeCloudAndLocal")
    ]
    assert "store.MergeValues(new Dictionary<string,string>(StringComparer.Ordinal)" in persist
    assert "{ RevisionKey, revision }" in persist
    assert "{ FingerprintKey, fingerprint }" in persist
    assert "store.SetString(" not in persist
    assert "private static State LoadState(ShopContext shop)" in service
    assert "private static void PersistState(ShopContext shop,State state,int targetRevision,string targetFingerprint)" in service
    assert "KnowledgeAiService.ContentHash(JsonConvert.SerializeObject(projection),string.Empty)" in repository


def test_v2_cloud_sync_checkpoint_advances_only_after_persisted_values_are_verified():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    persist = service[
        service.index("private static void PersistState"):
        service.index("private static List<KnowledgeV2Record> MergeCloudAndLocal")
    ]
    sync = service[
        service.index("private static async Task SyncOnce"):
        service.index("private static State LoadState")
    ]

    # A failed disk write/read-back must not advance the in-memory revision/hash.
    verify = persist.index("if(!store.TryGetString(RevisionKey")
    revision_advance = persist.index("state.Revision=Math.Max(0,targetRevision);")
    fingerprint_advance = persist.index("state.Fingerprint=targetFingerprint??string.Empty;")
    assert verify < revision_advance < fingerprint_advance
    assert "store.MergeValues(new Dictionary<string,string>(StringComparer.Ordinal)" in persist
    assert "{ RevisionKey, revision }" in persist
    assert "{ FingerprintKey, fingerprint }" in persist
    assert "store.SetString(" not in persist
    assert 'PersistState(shop,state,cloudRevision,cloudFingerprint);' in sync
    assert 'PersistState(shop,state,cloudRevision,localFingerprint);' in sync
    assert "state.Revision=cloudRevision;" not in sync
    assert "state.Fingerprint=Fingerprint(records);" not in sync

def test_v2_cloud_sync_publishes_privacy_safe_revision_and_fingerprint_diagnostics():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    snapshot = service[
        service.index("private static void WriteDiagnosticsSnapshot"):
        service.index("private static string Fingerprint(")
    ]

    assert 'knowledge-v2-sync-snapshot.json' in snapshot
    assert '["schemaVersion"]=1' in snapshot
    assert '["processStartTimeUtcTicks"]=processStartTicks' in snapshot
    assert '["shopRef"]=state.ShopRef' in snapshot
    assert '["cloudRevision"]=state.Revision' in snapshot
    assert '["localFingerprintPrefix"]' in snapshot
    assert '["cloudFingerprintPrefix"]' in snapshot
    assert '["converged"]=state.LastConverged' in snapshot
    assert '["lastSuccessUtc"]' in snapshot
    assert '["lastStatus"]' in snapshot
    assert "File.Replace(temp,path,null)" in snapshot
    assert '"records"' not in snapshot
    assert '"ShopKey"' not in snapshot
    assert '"seller"' not in snapshot
    assert '"answer"' not in snapshot


def test_v2_cloud_sync_in_memory_checkpoint_and_diagnostic_snapshot_share_one_lock():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    persist = service[
        service.index("private static void PersistState"):
        service.index("private static string FingerprintPrefix")
    ]

    assert "lock(state.SyncRoot)" in persist
    assert "state.Revision=Math.Max(0,targetRevision);" in persist
    assert "state.Fingerprint=targetFingerprint??string.Empty;" in persist

def test_v2_cloud_replacement_clears_rows_and_writes_corpus_metadata_in_one_sqlite_transaction():
    helper = read("src/BotLib/Db/Sqlite/SQLiteHelper.cs")
    replace = helper[
        helper.index("public void ReplaceRecordsInTransaction"):
        helper.index("public int DeleteInTransaction")
    ]
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    method = repository[
        repository.index("internal static bool TryReplaceAllIfUnchanged"):
        repository.index("public static string GetDatabasePath")
    ]

    assert "conn.RunInTransaction" in replace
    assert "conn.DeleteAll(table)" in replace
    assert "conn.InsertOrReplace(record)" in replace
    assert "state.Db.ReplaceRecordsInTransaction(" in method
    assert "replacementRows.Add(new KnowledgeV2MetaRow" in method
    assert "state.Db.ClearTable(" not in method
    assert "state.Db.SaveRecordsInTransaction(" not in method
