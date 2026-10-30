from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_v2_navigation_exposes_smart_import_history_and_ai_history():
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    assert 'Nav("智能导入", () => new KnowledgeV2SmartImportPage' in ui
    assert 'Nav("历史聊天整理", () => new KnowledgeV2ChatHistoryPage' in ui
    assert 'Nav("优化记录", () => new KnowledgeV2AiOptimizationHistoryPage' in ui


def test_v2_smart_import_writes_native_v2_repository_not_legacy_faq_conversion():
    service = read("src/Bot/Knowledge/KnowledgeV2SmartImportService.cs")
    assert "KnowledgeAiService.SplitTextBatches" in service
    assert "KnowledgeAiService.ContentHash" in service
    assert "KnowledgeEngineV2Repository.Save(seller, record)" in service
    assert 'record.SourceType = "ai_smart_import"' in service
    assert "KnowledgeEngineV2Semantics.NormalizeType" in service
    assert "KnowledgeEngineV2Semantics.NormalizeIntent" in service
    assert "KnowledgeEngineV2Semantics.NormalizePredicate" in service
    assert "KnowledgeAiService.ParseAiKnowledgeResult" not in service
    assert "KnowledgeEngineV2Semantics.FromLegacy" not in service
    assert "BotFeatureStore.SaveKnowledgeBase" not in service
    assert ".ImportAsync(_data" not in service
    assert "禁止输出旧版 faqs/category/question/keywords" in service


def test_v2_smart_import_preserves_timeout_retry_vision_fallback_and_partial_progress():
    service = read("src/Bot/Knowledge/KnowledgeV2SmartImportService.cs")
    for term in [
        "CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))",
        "正在自动重试一次",
        "IsVisionUnsupported",
        "UnsupportedImageSkipped",
        "已写入 Knowledge V2",
        "DuplicateSkipped",
    ]:
        assert term in service


def test_v2_smart_import_creates_shop_scoped_ai_audit_record():
    service = read("src/Bot/Knowledge/KnowledgeV2SmartImportService.cs")
    assert "KnowledgeEngineV2GovernanceAuditService.TryAppendAction" in service
    assert '"ai_smart_import"' in service
    assert '"knowledge_import"' in service
    assert '"schema=knowledge_v2' in service
    assert 'AppendAudit(seller, result, "success"' in service
    assert 'AppendAudit(seller, result, "failed"' in service


def test_ai_optimization_history_is_v2_audit_backed_and_includes_revision_workflow():
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2AiToolsUi.cs")
    assert "KnowledgeEngineV2GovernanceAuditService.GetEntries(_seller, 800)" in ui
    for action in [
        "ai_smart_import",
        "generate_revision_candidates",
        "apply_revision",
        "reject_revision",
        "rollback_revision",
    ]:
        assert action in ui
    assert "BotFeatureStore.GetKnowledgeBase" not in ui


def test_v2_ai_sources_are_in_wpf_compile_list():
    props = read("src/Bot/Directory.Build.props")
    assert "KnowledgeV2SmartImportService.cs" in props
    assert "KnowledgeCenterV2AiToolsUi.cs" in props


def test_legacy_smart_import_remains_unchanged_for_preview_reference():
    legacy = read("src/Bot/Knowledge/KnowledgeAiService.cs")
    preview = read("src/Bot/Knowledge/LegacyKnowledgePreviewWindow.cs")
    assert "SaveDeduped(parsed.Items)" in legacy
    assert "BotFeatureStore.SaveKnowledgeBase(existing)" in legacy
    assert "KnowledgeImportControl" in preview
    assert "AiOptimizationHistoryControl" in preview
