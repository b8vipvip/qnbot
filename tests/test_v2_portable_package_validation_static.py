from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_v2_package_rejects_invalid_shape_and_duplicate_ids_before_replacement():
    source = read("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    page = source.split("internal sealed class KnowledgeV2ImportExportPage", 1)[1].split("internal sealed class KnowledgeV2SettingsPage", 1)[0]
    assert page.index("var package = ParseValidatedPackage(") < page.index("var backup = CreateAutomaticBackup(before);") < page.index("KnowledgeEngineV2Repository.TryReplaceAllIfUnchanged")
    assert "version.Type != JTokenType.Integer" in page
    assert "version.Value<long>() != KnowledgeEngineV2Constants.SchemaVersion || rows == null" in page
    assert "!ids.Add(((string)id).Trim())" in page
    assert "id.Type != JTokenType.String" in page
    assert "answer.Type != JTokenType.String" in page
    assert "package.Records.Count == 0" in page
    assert "Guid.NewGuid().ToString" in page
    assert "ShopContextLocator.ResolveRuntimeByShopKey(_shopKey)" in page
    assert "using (ShopSettingsScope.Enter(RequireShop())) action();" in page
    for operation in ("ExportPackage", "ImportPackage", "ExportRecords", "RebuildIndex"):
        assert "InShop(" + operation + ")" in page

def test_invalid_threshold_is_rejected_before_settings_write():
    source = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    method = source.split("public static void SetSettings(", 1)[1].split("public static KnowledgeV2Decision Resolve(", 1)[0]
    assert method.index("double.IsNaN(threshold)") < method.index("store.MergeValues")
    assert "double.IsInfinity(minConfidence)" in method

def test_restored_non_finite_thresholds_and_reviewed_write_races_are_guarded():
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    assert "if (double.IsNaN(threshold) || double.IsInfinity(threshold))" in index
    assert "if (double.IsNaN(confidence) || double.IsInfinity(confidence))" in index
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    method = repo.split("public static bool TrySaveReviewedDirectAnswer(string seller,", 2)[2].split("public static bool TryApplyCommerceScenarioScope", 1)[0]
    assert "var expectedFingerprint = exact.Count == 1" in method
    assert "EditorRecordFingerprint(currentRecord), expectedFingerprint" in method
    assert "matchingQuestion(x) && !string.Equals(x.Id, record.Id" in method
    review = read("src/Bot/ChromeNs/ReviewedKnowledgeLearningService.cs")
    assert "TrySaveReviewedDirectAnswer(" in review
    assert "KnowledgeEngineV2Repository.Save(" not in review

def test_portable_import_preserves_tombstones_and_uses_exact_backup_snapshot_for_cas():
    source = read("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    assert "ComputeCloudSyncFingerprint(before.Records)" in source
    assert "BuildImportReplacement(before.Records, package.Records)" in source
    assert "CreateAutomaticBackup(before)" in source
    assert "TryReplaceAllIfUnchanged(_seller, expectedFingerprint, replacement, recovery)" in source
    assert "snapshot ?? BuildPackage()" in source
    assert 'tombstone.Status = "deleted"' in source
    assert "rows[old.Id] = old;" in source
    assert "deletedEvidence.Contains((row.SourceId ?? string.Empty).Trim())" in source

def test_complete_backup_carries_same_shop_feedback_without_replacing_existing_events():
    page = read("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    assert "SourceShopKey = _shopKey" in page
    assert "KnowledgeEngineV2FeedbackService.ExportEvents(_seller)" in page
    assert "KnowledgeEngineV2FeedbackService.ValidateBackupEvents(package.FeedbackEvents, package.Records)" in page
    assert "package.SourceShopKey, _shopKey" in page
    assert "KnowledgeEngineV2Repository.ResumePortableImport" in page

def test_additive_import_and_records_only_export_use_validated_v2_packages():
    source = read("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    assert 'ImportPackage("merge")' in source
    assert 'ImportPackage("append")' in source
    assert "KnowledgeV2CloudSyncService.MergeCloudAndLocal(before.Records" in source
    assert "append ? package.Records.Where(x => !before.Records.Any(old => old.Id == x.Id))" in source
    assert "TargetSettings = package.Settings" in source
    assert "var recovery = replace ?" in source
    export = source.split("private void ExportRecords()", 1)[1].split("private void ImportPackage()", 1)[0]
    assert "SchemaVersion = KnowledgeEngineV2Constants.SchemaVersion" in export
    assert "FeedbackEvents = null" in export and "Settings = null" in export

def test_portable_import_journal_commits_with_knowledge_and_replays_before_sync():
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    page = read("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    cloud = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    assert "Key = PendingPortableImportKey" in repo
    assert "Value = JsonConvert.SerializeObject(recovery, Formatting.None)" in repo
    assert repo.index("Value = JsonConvert.SerializeObject(recovery, Formatting.None)") < repo.index("state.Db.ReplaceRecordsInTransaction(", repo.index("Value = JsonConvert.SerializeObject(recovery, Formatting.None)"))
    restore = repo.split("internal static int ResumePortableImport",1)[1].split("public static string GetDatabasePath",1)[0]
    assert restore.index("MergeBackupEvents") < restore.index("TryRestorePortableSettings") < restore.index("Value = string.Empty")
    assert "知识已提交，反馈/设置恢复尚未完成" in page
    assert "KnowledgeEngineV2Repository.ResumePortableImport(shop.DisplayName)" in cloud

def test_portable_import_intent_is_written_in_cas_method_not_unconditional_replace():
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    plain = repo.split("public static void ReplaceAll(",1)[1].split("internal static string ComputeCloudSyncFingerprint",1)[0]
    cas = repo.split("internal static bool TryReplaceAllIfUnchanged(",1)[1].split("internal static int ResumePortableImport",1)[0]
    assert "if (recovery != null)" not in plain
    assert cas.index("currentFingerprint,expectedFingerprint") < cas.index("JsonConvert.SerializeObject(recovery") < cas.index("state.Db.ReplaceRecordsInTransaction")
    assert "PendingPortableImportKey" in cas
