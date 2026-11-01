from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
HELP = (ROOT / "src/Bot/Knowledge/KnowledgeCenterHelpWindow.cs").read_text(encoding="utf-8")
SHELL = (ROOT / "src/Bot/Knowledge/KnowledgeCenterV2Ui.cs").read_text(encoding="utf-8")
SETTINGS = (ROOT / "src/Bot/Options/FeatureSettingsOptionsControl.cs").read_text(encoding="utf-8")
PROPS = (ROOT / "src/Bot/Directory.Build.props").read_text(encoding="utf-8")
DOC = (ROOT / "docs/KNOWLEDGE_CENTER_V2_USER_HELP.md").read_text(encoding="utf-8")


def test_help_is_reachable_from_settings_help_and_direct_v2_header():
    assert 'string.Equals(page, "知识库", StringComparison.Ordinal)' in SETTINGS
    assert "KnowledgeCenterHelpWindow.MyShow(Window.GetWindow(this), Seller);" in SETTINGS
    assert "RemoveLegacyKnowledgeGuideTab();" in SETTINGS
    assert 'var help = Button("使用帮助", 92);' in SHELL
    assert "KnowledgeCenterHelpWindow.MyShow(_owner, _seller);" in SHELL


def test_help_explains_complete_first_run_configuration_and_modes():
    for text in [
        "设置 → 店铺与连接 → 店铺绑定",
        "设置 → 回复与通知 → 自动回复规则 → 回复模式",
        "AI优先",
        "本地优先",
        "shadow",
        "production",
        "0.82",
        "0.68",
        "立即预热索引",
        "测试台",
    ]:
        assert text in HELP


def test_help_documents_structured_fields_and_direct_reply_gates():
    for text in [
        "Intent",
        "Subject",
        "Predicate",
        "Entities",
        "Aliases",
        "标准答案",
        "适用条件",
        "排除条件",
        "必要上下文",
        "绑定商品ID",
        "可信度 / 权威度",
        "Enabled / Status",
        "Subject + Predicate",
        "learning_candidate",
        "high 风险",
        "候选分差",
    ]:
        assert text in HELP


def test_help_explains_runtime_fallback_feedback_revision_and_governance():
    for text in [
        "最近 45 分钟",
        "Exact、Intent、Predicate、Entity 和中文 2-gram 索引",
        "Smart Reply / AI 兼容链路",
        "SendTextWithRetryAsync",
        "knowledge-feedback-v2.db",
        "knowledge-revision-v2.db",
        "knowledge-governance-v2.db",
        "最近 120 天",
        "默认 180 天",
        "默认 60 天",
        "默认 120 天",
        "负向率至少 25%",
        "SHA-256 指纹",
    ]:
        assert text in HELP


def test_help_is_read_only_and_shop_scoped():
    assert "当前店铺" in HELP
    assert "ShopKey" in HELP
    assert "本帮助只说明和读取当前功能" in HELP
    for mutation in [
        "KnowledgeEngineV2Repository.Save(",
        "KnowledgeEngineV2Service.SetSettings(",
        "KnowledgeEngineV2GovernanceAuditService.SaveSettings(",
        "KnowledgeEngineV2Repository.ReplaceAll(",
    ]:
        assert mutation not in HELP


def test_help_source_and_written_guide_ship_with_the_client_change():
    assert "Knowledge\\KnowledgeCenterHelpWindow.cs" in PROPS
    assert "# Knowledge Center V2 客户端使用帮助" in DOC
    assert "设置 → 知识库 → 使用帮助" in DOC
    assert "帮助窗口不会保存设置、修改知识或触发同步" in DOC
    assert not (ROOT / "src/Bot/Directory.Build.targets").exists()
