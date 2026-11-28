from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_v2_record_operations_pin_shop_and_bulk_delete_commits_only_after_all_cas_checks():
    page = read("src/Bot/Knowledge/KnowledgeCenterV2RecordsPage.cs")
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    assert "ShopContextLocator.ResolveRuntimeByShopKey(_shopKey)" in page
    assert "using (ShopSettingsScope.Enter(RequireShop())) return action();" in page
    assert "InShop(() => KnowledgeEngineV2Repository.LoadAll(_seller))" in page
    assert "InShop(() => KnowledgeEngineV2Repository.TrySaveEditorRecord" in page
    assert "InShop(() => KnowledgeEngineV2Repository.TryDeleteEditorRecords" in page
    assert "SelectionMode = DataGridSelectionMode.Extended" in page
    method = repo.split("internal static bool TryDeleteEditorRecords(", 1)[1].split("public static bool Delete(", 1)[0]
    assert method.index("lock (state.Sync)") < method.index("EditorRecordFingerprint(record), pair.Value") < method.index("state.Db.SaveRecordsInTransaction(writes)")
    assert 'record.Status = "deleted"' in method
    assert "PreviousJson = before" in method
    assert method.index("state.Db.SaveRecordsInTransaction(writes)") < method.index("ApplyRecordDelete")

def test_candidate_approval_changes_authority_and_rejects_stale_or_deleted_records():
    code = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    method = code.split("internal static bool TryPromoteCandidate(", 1)[1]
    assert "LoadCommittedRecord(seller, id)" in method
    assert 'string.Equals(record.Status, "candidate", StringComparison.OrdinalIgnoreCase)' in method
    assert "EditorRecordFingerprint(record), expectedFingerprint" in method
    assert 'record.SourceType = "manual_confirmed"' in method
    assert "record.Enabled = true" in method
    assert "TrySaveEditorRecord(seller, record, expectedFingerprint)" in method
    assert "KnowledgeEngineV2Repository.Save(" not in method

def test_v2_records_show_native_reliability_statistics():
    page = read("src/Bot/Knowledge/KnowledgeCenterV2RecordsPage.cs")
    for name in ("UseCount", "AcceptedCount", "CorrectionCount", "WithdrawCount"):
        assert '"' + name + '"' in page
