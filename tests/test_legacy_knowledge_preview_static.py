from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_legacy_preview_and_v1_runtime_ui_are_retired():
    shell = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    window = read("src/Bot/Knowledge/KnowledgeCenterWindow.cs")
    settings = read("src/Bot/Options/CtlRobotOptions.xaml.cs")

    assert not (ROOT / "src/Bot/Knowledge/LegacyKnowledgePreviewWindow.cs").exists()
    assert "LegacyKnowledgePreviewWindow" not in shell
    assert "旧版知识库预览" not in shell
    assert "KnowledgeCenterV2UiBridge.Initialize();" in window
    assert "KnowledgeCenterV2 知识库" in window
    assert "旧知识库页面不会作为兜底" in shell
    assert "已阻止回退到旧知识库" in shell
    assert "KnowledgeCenterWindow.MyShow(this)" in settings
    assert "private UIElement BuildKnowledgeTab()" in settings


def test_build_compiles_only_the_v2_history_importer():
    props = read("src/Bot/Directory.Build.props")
    root_targets = read("src/Directory.Build.targets")

    assert "KnowledgeV2HistoryImportService.cs" in props
    assert "LegacyKnowledgePreviewWindow.cs" not in props
    assert "KnowledgeV2LegacyDeltaImportService.cs" not in props
    assert "KnowledgeContextualReplyService.cs" not in root_targets
    assert not (ROOT / "src/Bot/Knowledge/KnowledgeV2LegacyDeltaImportService.cs").exists()
    assert not (ROOT / "src/Bot/ChromeNs/KnowledgeContextualReplyService.cs").exists()


def test_history_import_writes_only_to_the_shop_scoped_v2_repository():
    importer = read("src/Bot/Knowledge/KnowledgeV2HistoryImportService.cs")
    ai = read("src/Bot/Knowledge/KnowledgeAiService.cs")

    assert "ShopContextLocator.ResolveRuntimeByShopKey(shopKey)" in importer
    assert "shop == null || !shop.HasStableSellerId" in importer
    assert "using (ShopSettingsScope.Enter(shop))" in importer
    assert "KnowledgeEngineV2Repository.LoadAll(seller)" in importer
    assert "KnowledgeEngineV2Repository.Save(seller, record)" in importer
    assert "SourceType = \"chat_history_import\"" in importer
    assert "BotFeatureStore.GetKnowledgeBase" not in ai
    assert "BotFeatureStore.SaveKnowledgeBase" not in ai


def test_help_and_docs_describe_evidence_backed_auto_publish_not_manual_approval():
    help = read("src/Bot/Knowledge/KnowledgeCenterHelpWindow.cs")
    docs = read("docs/KNOWLEDGE_CENTER_V2_USER_HELP.md")

    for source in (help, docs):
        assert "旧版知识库预览" not in source
        assert "必须人工批准" not in source
        assert "真人" in source
        assert "Knowledge V2" in source
