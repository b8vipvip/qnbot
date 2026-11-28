from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "Bot" / "Knowledge" / "RulePolicyImportExportUi.cs"
PROPS = ROOT / "src" / "Bot" / "Directory.Build.props"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_store_rule_center_has_versioned_json_import_export():
    source = read(SOURCE)
    assert '"qnbot.store-rules"' in source
    assert 'Title = "导入店铺规则"' in source
    assert 'Title = "导出店铺规则"' in source
    assert 'StorePromptProfileService.ParseRulesJson' in source
    assert 'StorePromptProfileService.SaveStructured(raw, core, rules)' in source


def test_store_rule_import_backs_up_before_overwrite():
    source = read(SOURCE)
    start = source.index("private static void ImportStoreRules")
    backup = source.index("var backup = BackupStoreRules(window)", start)
    save = source.index("StorePromptProfileService.SaveStructured(raw, core, rules)", backup)
    assert backup < save
    assert "导入前会自动备份当前配置" in source


def test_knowledge_policy_export_contains_complete_config_and_learning_stats():
    source = read(ROOT / "src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    model = read(ROOT / "src/Bot/Knowledge/KnowledgeEngineV2.Models.cs")
    assert "KnowledgeV2PortablePackage" in source
    assert "Records = records" in source
    assert "Settings = KnowledgeEngineV2Service.GetSettingsView(_seller)" in source
    assert "FeedbackEvents = KnowledgeEngineV2FeedbackService.ExportEvents(_seller)" in source
    for field in ("Conditions", "Exclusions", "RequiredContext", "UseCount", "AcceptedCount", "CorrectionCount", "WithdrawCount"):
        assert field in model


def test_knowledge_policy_import_merges_and_restores_learning_stats():
    source = read(ROOT / "src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    feedback = read(ROOT / "src/Bot/Knowledge/KnowledgeEngineV2FeedbackService.cs")
    assert "KnowledgeV2CloudSyncService.MergeCloudAndLocal" in source
    assert "CreateAutomaticBackup(before)" in source
    assert "KnowledgeEngineV2FeedbackService.MergeBackupEvents" in source
    assert "ids.Add(x.Id)" in feedback
    assert "state.Db.SaveRecordsInTransaction(additions)" in feedback


def test_import_validates_schema_and_version_and_reports_counts():
    source = read(SOURCE)
    v2 = read(ROOT / "src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    assert "ValidateSchema(root, StoreSchema)" in source
    assert "不支持的导入文件版本" in source
    assert "version.Type != JTokenType.Integer" in v2
    assert "ParseValidatedPackage" in v2
    assert "!ids.Add(((string)id).Trim())" in v2
    assert "replacement.Count" in v2 and "restoredFeedback" in v2


def test_ui_extension_is_compiled_for_bot_and_wpf_temp_projects():
    props = read(PROPS)
    assert "..\\Directory.Build.props" in props
    assert "Knowledge\\RulePolicyImportExportUi.cs" in props
    assert not (ROOT / "src" / "Bot" / "Directory.Build.targets").exists()
    source = read(SOURCE)
    assert "RulePolicyImportExportBootstrap" in source
    assert "EventManager.RegisterClassHandler" in source