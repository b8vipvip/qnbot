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
        service.index("internal static List<KnowledgeV2Record> MergeCloudAndLocal"):
        service.index("private static void AddUnique")
    ]
    candidate = service[
        service.index("private static void AddCandidate"):
        service.index("private static void AddUnique")
    ]

    assert "MergeCloudAndLocal(records,latestLocal,preserveLocalOnly:true)" in service
    assert "cloud_sync_conflict_candidate" in candidate
    assert 'candidate.Enabled=false;' in candidate
    assert 'candidate.Status="candidate";' in candidate
    assert "candidate.SourceId=originalId" in candidate
    assert "AddCandidate(merged, mergedIds, local, id)" in merge


def test_v2_cloud_replacement_uses_atomic_repository_compare_and_swap_without_v1_mirror():
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
    assert "KnowledgeEngineV2Service.ReplaceSnapshot(seller,normalized)" in method
    assert "MirrorAllToLegacy" not in method
    assert "BotFeatureStore.SaveKnowledgeBase" not in method

def test_v2_cloud_sync_uses_same_canonical_fingerprint_for_persistence_and_compare():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")

    assert "KnowledgeEngineV2Repository.ComputeCloudSyncFingerprint(records)" in service
    assert "new ShopScopedSettingsStore(shop,Paths)" in service
    assert "store.TryGetString(FingerprintKey,out fingerprint)" in service
    persist = service[
        service.index("private static void PersistState"):
        service.index("internal static List<KnowledgeV2Record> MergeCloudAndLocal")
    ]
    assert "store.MergeValues(new Dictionary<string,string>(StringComparer.Ordinal)" in persist
    assert "{ RevisionKey, revision }" in persist
    assert "{ FingerprintKey, fingerprint }" in persist
    assert "store.SetString(" not in persist
    assert "private static State LoadState(ShopContext shop)" in service
    assert "private static void PersistState(ShopContext shop,State state,int targetRevision,string targetFingerprint)" in service
    assert "JsonConvert.SerializeObject(projection, Formatting.None)" in repository
    assert "sha.ComputeHash(Encoding.UTF8.GetBytes(json))" in repository


def test_v2_cloud_sync_checkpoint_advances_only_after_persisted_values_are_verified():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    persist = service[
        service.index("private static void PersistState"):
        service.index("internal static List<KnowledgeV2Record> MergeCloudAndLocal")
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

def test_v2_full_package_import_uses_the_same_atomic_replace_primitive():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    replace_all = repository[
        repository.index("public static void ReplaceAll"):
        repository.index("internal static string ComputeCloudSyncFingerprint")
    ]

    assert "state.Db.ReplaceRecordsInTransaction(" in replace_all
    assert "replacementRows.Add(new KnowledgeV2MetaRow" in replace_all
    assert "state.Db.ClearTable(" not in replace_all
    assert "state.Db.SaveRecordsInTransaction(" not in replace_all

def test_v2_repository_rejects_seller_mismatch_inside_an_explicit_shop_scope():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    resolve = repository[
        repository.index("private static ShopContext ResolveShopRequired"):
        repository.index("private static double Clamp")
    ]

    assert "var current = ShopSettingsScope.Current;" in resolve
    assert "DirectOrderIdentityResolver.IdentityEquals(current.SellerNick, seller)" in resolve
    assert "DirectOrderIdentityResolver.IdentityEquals(current.DisplayName, seller)" in resolve
    assert "已拒绝跨店访问" in resolve
    assert "ShopContextLocator.ResolveBySellerNick(seller)" in resolve

def test_delete_tombstones_remain_in_cloud_snapshots_and_are_accepted_by_runtime_sync():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    delete = repository.split("public static bool Delete(", 1)[1].split(
        "public static void ReplaceAll(", 1
    )[0]
    sync = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    api = read("services/api-control-plane/knowledge_v2_sync.py")

    assert "tombstone.Enabled = false;" in delete
    assert 'tombstone.Status = "deleted";' in delete
    assert "tombstone.Answer =" not in delete
    assert "SaveRecordsInTransaction(new List<object> { ToRow(tombstone), revision });" in delete
    assert "var local=KnowledgeEngineV2Repository.LoadAll(shop.DisplayName);" in sync
    assert 'payload["records"]=JArray.FromObject(local);' in sync
    assert '"Enabled","Status"' in api
    assert "not isinstance(answer,str)" in api
    assert "not answer.strip()" in api


def test_v2_cloud_merge_compares_every_semantic_field_and_preserves_monotonic_metadata():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    merge = service[
        service.index("internal static List<KnowledgeV2Record> MergeCloudAndLocal"):
        service.index("private static void AddUnique")
    ]
    compare = service[
        service.index("private static bool SameList"):
        service.index("private static KnowledgeV2Record CloneRecord")
    ]

    assert "MergeRecordMetadata(cloud, local)" in merge
    for field in (
        "ShortAnswer", "Subject", "Predicate", "Entities", "Aliases",
        "Conditions", "Exclusions", "RequiredContext", "ProductIds",
        "RiskLevel", "SourceType", "SourceId", "Authority", "Confidence",
    ):
        assert f"left.{field}" in compare and f"right.{field}" in compare
    assert "left.Enabled==right.Enabled" in compare
    assert "left.Status" in compare
    assert "left.Type" in compare
    assert "left.Intent" in compare
    assert "SequenceEqual" in compare
    metadata = compare[compare.index("private static void MergeRecordMetadata"):]
    for field in ("UseCount", "AcceptedCount", "CorrectionCount", "WithdrawCount"):
        assert f"target.{field}=Math.Max" in metadata
    assert "target.LastVerifiedAt=source.LastVerifiedAt" in metadata


def test_cloud_revision_reset_backs_up_local_corpus_and_restarts_safe_reconciliation():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    sync = service[
        service.index("private static async Task SyncOnce"):
        service.index("private static State LoadState")
    ]
    rollback = sync[
        sync.index("if(cloudRevision<state.Revision)"):
        sync.index("if(cloudRevision>state.Revision && !snapshotAcknowledged)")
    ]

    assert "Backup(shop,local,cloudRevision)" in rollback
    assert "PersistState(shop,state,0,string.Empty)" in rollback
    assert 'state.LastStatus="cloud_revision_reset_reconcile"' in rollback
    assert "return;" in rollback
    assert "re-negotiate" in rollback or "重新协商" in rollback



def test_verified_human_evidence_is_deduplicated_across_different_device_record_ids():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    merge = service[
        service.index("internal static List<KnowledgeV2Record> MergeCloudAndLocal"):
        service.index("private static void AddUnique")
    ]
    assert "recordsByHumanEvidence" in merge
    assert "AutoPublishedEvidenceKey(record)" in merge
    assert "AutoPublishedEvidenceKey(local)" in merge
    assert "KnowledgeV2AuthorityPolicy.IsAutoPublishedHumanEvidenceSource(record.SourceType)" in merge
    assert "SameRecordContent(existingEvidence, record)" in merge
    assert "SameRecordContent(evidenceRecord, local)" in merge
    assert "AddUnique(merged, mergedIds, duplicate)" in merge
    assert "cloudById[duplicate.Id] = duplicate" in merge
    assert "AddCandidate(merged, mergedIds, local, local.Id)" in merge
    assert "if (preserveLocalOnly || IsDeleteTombstone(local))" in merge
    # The deterministic human-evidence SourceId, not a random record ID, is the cross-device dedupe key.
    assert "return (record.SourceId ?? string.Empty).Trim();" in merge


def test_two_device_knowledge_v2_comparator_requires_fresh_converged_fingerprints_and_no_pii():
    script = read("scripts/Compare-QnBotKnowledgeV2Devices.ps1")
    assert "KnowledgeV2SyncSnapshotStatus -eq 'fresh'" in script
    assert "KnowledgeV2ShopStates" in script
    assert "DeviceAShopRef" in script
    assert "DeviceBShopRef" in script
    assert "CloudRevision" in script
    assert "LocalFingerprintPrefix" in script
    assert "CloudFingerprintPrefix" in script
    assert "Converged" in script
    assert "Syncing" in script
    assert "Device A shop is not syncing" in script
    assert "Device B shop is not syncing" in script
    assert "MaxSampleSkewMinutes" in script
    assert "MaxSampleAgeMinutes" in script
    assert "Device A sample is recent" in script
    assert "Device B sample is recent" in script
    assert "Each local fingerprint matches its cloud fingerprint" in script
    assert "RawShopKeysIncluded = $false" in script
    assert "BuyerMessagesIncluded = $false" in script
    assert "KnowledgeContentIncluded = $false" in script
    assert "ShopRef aliases are not compared across devices" in script

def test_acceptance_uses_same_process_local_hmac_shop_alias_and_current_sample():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    learning = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    script = read("scripts/Compare-QnBotKnowledgeV2Devices.ps1")
    assert 'ShopRef=MyWebSocketServer.RuntimeAcceptanceDiagnosticRef("shop",shop.ShopKey)' in service
    assert '["shopRef"]=MyWebSocketServer.RuntimeAcceptanceDiagnosticRef("shop",report.ShopKey)' in learning
    assert "internal static string RuntimeAcceptanceDiagnosticRef" in server
    assert "state.LastConverged=false;" in service
    assert "$latest = $systemRows |" in script
    assert "latest system sample has no fresh" in script
    assert "CSV" in script and "Get-FileHash" in script
    assert "Device A last sync succeeded" in script


def test_own_upload_ack_preserves_edits_and_deletes_made_during_http_request():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    sync = service[service.index("private static async Task SyncOnce"):service.index("private static State LoadState")]
    assert "snapshotAcknowledged=string.Equals(cloudFingerprint,localFingerprint,StringComparison.Ordinal)" in sync
    assert "if(cloudRevision>state.Revision && !snapshotAcknowledged)" in sync
    assert sync.index("if(cloudRevision>state.Revision && !snapshotAcknowledged)") < sync.index("MergeCloudAndLocal(records,latestLocal")
    ack = sync[sync.index("// Same-revision responses"):]
    assert "if(!snapshotAcknowledged)" in ack
    assert ack.index("throw new InvalidOperationException") < ack.index("PersistState(shop,state,cloudRevision,localFingerprint)")
    assert "KnowledgeEngineV2Repository.LoadAll(shop.DisplayName)" in ack
    assert "afterRequestFingerprint,localFingerprint" in ack
    assert "TryReplaceAllIfUnchanged" not in ack
    assert "revisionToken.Type!=JTokenType.Integer" in sync
    assert 'root["records"] as JArray' in sync
    assert "records.Any(x=>x==null)" in sync


def test_cloud_fingerprint_covers_all_persisted_record_fields():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    fingerprint = repository.split("internal static string ComputeCloudSyncFingerprint", 1)[1].split("internal static bool TryReplaceAllIfUnchanged", 1)[0]
    for field in ("Id", "Type", "Title", "Intent", "Subject", "Predicate", "Entities", "Aliases", "Answer",
                  "ShortAnswer", "Conditions", "Exclusions", "RequiredContext", "ProductIds", "RiskLevel",
                  "SourceType", "SourceId", "Authority", "Confidence", "UseCount", "AcceptedCount",
                  "CorrectionCount", "WithdrawCount", "Enabled", "Status", "CreatedAt", "UpdatedAt", "LastVerifiedAt"):
        assert f"x.{field}" in fingerprint
    assert ".OrderBy(x=>x.Id,StringComparer.Ordinal)" in fingerprint


def test_concurrent_explicit_delete_wins_without_resurrecting_cloud_or_local_answer():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    merge = service.split("internal static List<KnowledgeV2Record> MergeCloudAndLocal", 1)[1].split(
        "private static bool IsDeleteTombstone", 1
    )[0]
    assert "IsDeleteTombstone(local)" in merge
    assert "IsDeleteTombstone(cloud)" in merge
    assert "var tombstone = CloneRecord(local);" in merge
    assert "var index = merged.IndexOf(cloud);" in merge
    assert "merged[index] = tombstone;" in merge
    assert "AddCandidate(merged, mergedIds, cloud, id);" in merge
    assert "AddCandidate(merged, mergedIds, local, id);" in merge


def test_concurrent_delete_wins_across_different_ids_for_same_verified_human_evidence():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    merge = service[
        service.index("internal static List<KnowledgeV2Record> MergeCloudAndLocal"):
        service.index("private static bool IsDeleteTombstone")
    ]
    evidence = merge.split(
        "if (evidenceId.Length > 0 && recordsByHumanEvidence.TryGetValue(evidenceId, out evidenceRecord))",
        1
    )[1].split("if (preserveLocalOnly || IsDeleteTombstone(local))", 1)[0]
    assert "IsDeleteTombstone(local) && !IsDeleteTombstone(evidenceRecord)" in evidence
    assert "AddCandidate(merged, mergedIds, evidenceRecord, evidenceRecord.Id);" in evidence
    assert "var tombstone = CloneRecord(local);" in evidence
    assert "mergedIds.Remove((evidenceRecord.Id ?? string.Empty).Trim());" in evidence
    assert "merged[evidenceIndex] = tombstone;" in evidence
    assert "cloudById.Remove((evidenceRecord.Id ?? string.Empty).Trim());" in evidence
    assert "recordsByHumanEvidence[evidenceId] = tombstone;" in evidence
    assert "IsDeleteTombstone(evidenceRecord) && !IsDeleteTombstone(local)" in evidence
    assert "AddCandidate(merged, mergedIds, local, local.Id);" in evidence


def test_knowledge_v2_sync_isolates_profile_failures_per_shop():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    queue_all = service.split("private static void QueueAll()", 1)[1].split(
        "private static void Queue(ShopContext shop)", 1
    )[0]
    assert "foreach (var profile in profiles ?? new List<ShopProfile>())" in queue_all
    assert "try" in queue_all
    assert "Queue(shop);" in queue_all
    assert "catch (Exception ex)" in queue_all
    assert "跳过异常店铺并继续处理其他店铺" in queue_all
    assert "Profiles.GetAll()" in queue_all


def test_cloud_cas_fingerprint_does_not_use_lossy_faq_normalization():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    fingerprint = repository.split("internal static string ComputeCloudSyncFingerprint", 1)[1].split("internal static bool TryReplaceAllIfUnchanged", 1)[0]
    assert "SHA256.Create()" in fingerprint
    assert "Encoding.UTF8.GetBytes(json)" in fingerprint
    assert "KnowledgeAiService.ContentHash" not in fingerprint
    assert "NormalizeQuestion" not in fingerprint
    assert "JsonConvert.SerializeObject(projection, Formatting.None)" in fingerprint


def test_delete_evidence_normalization_keeps_all_device_ids_and_handles_clean_cloud_pull():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    merge = service.split("internal static List<KnowledgeV2Record> MergeCloudAndLocal", 1)[1].split("private static HashSet<string> DeletedEvidenceKeys", 1)[0]
    assert "DeletedEvidenceKeys(cloudInput.Concat(localInput))" in merge
    assert "cloudRecords = NormalizeEvidenceDeletes(cloudInput, deletedEvidence)" in merge
    assert "localRecords = NormalizeEvidenceDeletes(localInput, deletedEvidence)" in merge
    assert "IsDeleteTombstone(record) ? string.Empty : AutoPublishedEvidenceKey(record)" in merge
    assert "IsDeleteTombstone(local) ? string.Empty : AutoPublishedEvidenceKey(local)" in merge
    assert "if (preserveLocalOnly || IsDeleteTombstone(local))" in merge
    normalize = service.split("private static List<KnowledgeV2Record> NormalizeEvidenceDeletes", 1)[1].split("private static bool IsDeleteTombstone", 1)[0]
    assert "CloneRecord(record)" in normalize
    assert "tombstone.Enabled = false" in normalize
    assert 'tombstone.Status = "deleted"' in normalize
    assert "tombstone.Id =" not in normalize
    assert ": MergeCloudAndLocal(records, Enumerable.Empty<KnowledgeV2Record>(), preserveLocalOnly:true)" in service
    assert "NormalizeEvidenceDeletes(local, DeletedEvidenceKeys(local))" in service

def test_cloud_duplicate_evidence_keeps_existing_ids_without_tombstoning_canonical_record():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    merge = service.split("internal static List<KnowledgeV2Record> MergeCloudAndLocal", 1)[1]
    duplicate = merge.split("var duplicate = CloneRecord(record);", 1)[1].split("continue;", 1)[0]
    assert 'duplicate.Status = "candidate"' in duplicate
    assert "duplicate.Enabled = false" in duplicate
    assert "duplicate.Id =" not in duplicate
    assert "cloudById[duplicate.Id] = duplicate" in duplicate
    assert "preserveLocalOnly:true" in service

def test_conflict_candidate_identity_is_stable_across_usage_metadata_changes():
    source = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    candidate = source.split("private static void AddCandidate",1)[1].split("private static void AddUnique",1)[0]
    assert "identity.UseCount = identity.AcceptedCount = identity.CorrectionCount = identity.WithdrawCount = 0" in candidate
    assert "identity.CreatedAt = identity.UpdatedAt = identity.LastVerifiedAt = DateTime.MinValue" in candidate
    assert "Fingerprint(new[]{identity})" in candidate
    assert "!IsDeleteTombstone(existing)" in candidate
