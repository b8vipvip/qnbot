from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PREVIEW = (ROOT / "src/Bot/Knowledge/LegacyKnowledgePreviewWindow.cs").read_text(encoding="utf-8")
SETTINGS = (ROOT / "src/Bot/Options/FeatureSettingsOptionsControl.cs").read_text(encoding="utf-8")
SHELL = (ROOT / "src/Bot/Knowledge/KnowledgeCenterV2Ui.cs").read_text(encoding="utf-8")
PROPS = (ROOT / "src/Bot/Directory.Build.props").read_text(encoding="utf-8")
HELP = (ROOT / "src/Bot/Knowledge/KnowledgeCenterHelpWindow.cs").read_text(encoding="utf-8")
DOC = (ROOT / "docs/KNOWLEDGE_CENTER_V2_USER_HELP.md").read_text(encoding="utf-8")


def test_preview_is_reachable_inside_v2_without_settings_intermediate_page():
    assert 'Button("旧版知识库预览", 136)' in SHELL
    assert 'ToolTip = "只读查看当前店铺的旧版知识库，不启用旧版功能"' in SHELL
    assert "LegacyKnowledgePreviewWindow.MyShow(_owner, _seller);" in SHELL
    assert "RemoveLegacyKnowledgeGuideTab();" in SETTINGS
    assert "ReplaceKnowledgePageWithEmbeddedLauncher" not in SETTINGS


def test_preview_resolves_the_current_shop_and_uses_the_real_pre_v2_v1_shell():
    assert "ShopScopedUiBridge.Get(owner)" in PREVIEW
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(effectiveSeller)" in PREVIEW
    assert "using (ShopSettingsScope.Enter(shop))" in PREVIEW
    assert "ShopScopedUiBridge.Attach(window, shop);" in PREVIEW
    assert "86e0138b2f2e4583530aaf0264b6215a8443f35e" in PREVIEW
    assert "new KnowledgeManagerControl" in PREVIEW
    assert "new KnowledgeImportControl" in PREVIEW
    assert "new AiOptimizationHistoryControl" in PREVIEW
    for label in ["智能导入", "问答管理", "AI优化记录", "导入知识库完整包", "导出知识库完整包"]:
        assert label in PREVIEW
    assert "LegacyKnowledgeKey" not in PREVIEW
    assert "PreviewRow" not in PREVIEW


def test_preview_keeps_old_ui_visible_but_mutation_controls_are_read_only():
    for guard in ["button.IsEnabled = false", "text.IsReadOnly = true", "password.IsEnabled = false", "check.IsEnabled = false", "radio.IsEnabled = false", "grid.IsReadOnly = true", "grid.CanUserAddRows = false", "grid.CanUserDeleteRows = false"]:
        assert guard in PREVIEW
    assert "SaveKnowledgeBase(" not in PREVIEW
    assert "ImportKnowledgePackage(" not in PREVIEW
    assert "ExportKnowledgePackage(" not in PREVIEW
    assert "KnowledgeEngineV2Repository.Save(" not in PREVIEW


def test_preview_and_operator_sources_ship_in_normal_and_wpf_temporary_builds():
    assert "Knowledge\\LegacyKnowledgePreviewWindow.cs" in PROPS
    assert not (ROOT / "src/Bot/Directory.Build.targets").exists()
    for name in ["KnowledgeV2NaturalLanguageService.cs", "KnowledgeV2OperatorUiBridge.cs", "KnowledgeV2LegacyDeltaImportService.cs"]:
        assert name in PROPS


def test_help_documents_still_state_preview_is_non_activating():
    for source in (HELP, DOC):
        assert "旧版知识库预览" in source
        assert "不会启用旧版检索、匹配或自动回复" in source
