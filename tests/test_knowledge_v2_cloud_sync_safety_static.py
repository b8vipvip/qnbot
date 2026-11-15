from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_v2_cloud_replay_backs_up_before_any_shop_scoped_replace():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    apply = service[
        service.index("if(!string.Equals(mergedFingerprint,latestFingerprint"):
        service.index("state.Revision=cloudRevision", service.index("if(!string.Equals(mergedFingerprint,latestFingerprint"))
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
    assert method.index("if(!string.Equals(currentFingerprint") < method.index("state.Db.ClearTable")
    assert "MirrorAllToLegacy(shop,normalized)" in method
    assert "KnowledgeEngineV2Service.ReplaceSnapshot(seller,normalized)" in method


def test_v2_cloud_sync_uses_same_canonical_fingerprint_for_persistence_and_compare():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")

    assert "KnowledgeEngineV2Repository.ComputeCloudSyncFingerprint(records)" in service
    assert "PersistentParams.GetParam2Key(FingerprintKey,SettingsScope" in service
    assert "PersistentParams.TrySaveParam2Key(FingerprintKey,SettingsScope" in service
    assert "private static State LoadState(ShopContext shop)" in service
    assert "private static void PersistState(ShopContext shop,State state)" in service
    assert "KnowledgeAiService.ContentHash(JsonConvert.SerializeObject(projection))" in repository
