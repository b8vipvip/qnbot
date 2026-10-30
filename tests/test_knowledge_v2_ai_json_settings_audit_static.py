from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_one_line_knowledge_ai_repairs_malformed_structured_response_once():
    source = read("src/Bot/Knowledge/KnowledgeV2NaturalLanguageService.cs")

    assert "BuildRepairMessages" in source
    assert "RepairTimeoutSeconds = 60" in source
    assert source.count("MyOpenAI.CallStructuredChat(") == 2
    assert "首次结构化解析失败，准备一次受控修复" in source
    assert "自动 JSON 修复请求也失败" in source
    assert "首次返回不是有效 JSON，自动修复后仍无法解析" in source
    assert "TryExtractBalancedObject" in source
    assert 'strategy = "markdown-fence"' in source
    assert 'new[] { "knowledge", "record", "data", "result" }' in source


def test_one_line_knowledge_parser_rejects_arbitrary_json_without_knowledge_fields():
    source = read("src/Bot/Knowledge/KnowledgeV2NaturalLanguageService.cs")
    unwrap_start = source.index("private static JObject UnwrapToken")
    unwrap_end = source.index("private static bool LooksLikeKnowledgeObject", unwrap_start)
    unwrap = source[unwrap_start:unwrap_end]

    assert "if (LooksLikeKnowledgeObject(obj)) return obj;" in unwrap
    assert "nestedObject != null && LooksLikeKnowledgeObject(nestedObject)" in unwrap
    assert "singleNested != null && LooksLikeKnowledgeObject(singleNested)" in unwrap
    assert "single != null && LooksLikeKnowledgeObject(single)" in unwrap
    assert "return null;" in unwrap
    assert "UnwrapToken(extracted) ?? extracted" not in source
    assert "JSON 对象未包含知识字段" in source


def test_one_line_knowledge_ai_logs_transport_parse_and_write_boundaries_without_raw_answer_dump():
    service = read("src/Bot/Knowledge/KnowledgeV2NaturalLanguageService.cs")
    ui = read("src/Bot/Knowledge/KnowledgeV2OperatorUiBridge.cs")

    assert "answerChars=" in service
    assert "shape=" in service
    assert "结构化解析成功" in service
    assert "字段标准化完成" in service
    assert "KnowledgeV2 AI一句话新增开始生成" in ui
    assert "KnowledgeV2 AI一句话新增生成成功，准备写库" in ui
    assert "KnowledgeV2 AI一句话新增写库成功" in ui
    assert "KnowledgeV2 AI一句话新增失败且未写库" in ui
    assert "KnowledgeEngineV2Repository.Save(seller, record);" in ui
    assert "Log.Info(raw.Answer" not in service
    assert "Log.Info(repaired.Answer" not in service


def test_knowledge_v2_settings_audits_every_visible_operator_control_and_result():
    page = read("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    audit = read("src/Bot/Knowledge/KnowledgeV2OperatorUiBridge.cs")

    assert 'Content = "启用 Knowledge Engine V2"' in page
    assert 'Content = "保存设置"' in page
    assert 'Content = "立即预热索引"' in page
    assert '_mode = new ComboBox { Visibility = Visibility.Collapsed }' in page
    assert "本地直答匹配阈值" in page
    assert "最低知识可信度" in page
    assert "_mode" in page
    assert "_threshold" in page
    assert "_confidence" in page

    assert "KnowledgeV2SettingsOperationAudit.Initialize();" in audit
    assert "FrameworkElement.LoadedEvent" in audit
    assert "ButtonBase.ClickEvent" in audit
    assert "ToggleButton.CheckedEvent" in audit
    assert "ToggleButton.UncheckedEvent" in audit
    assert "Selector.SelectionChangedEvent" in audit
    assert "TextBoxBase.TextChangedEvent" in audit
    assert "action=button_click" in audit
    assert "action=toggle" in audit
    assert "action=selection_changed" in audit
    assert "action=text_changed" in audit
    assert "KnowledgeV2 设置状态" in audit
    assert "result=" in audit
    assert "enabled=" in audit
    assert "threshold=" in audit
    assert "minConfidence=" in audit
    assert "本地直答匹配阈值" in audit
    assert "最低知识可信度" in audit


def test_settings_audit_does_not_log_secrets_or_complete_knowledge_payloads():
    audit = read("src/Bot/Knowledge/KnowledgeV2OperatorUiBridge.cs")
    audit_start = audit.index("internal static class KnowledgeV2SettingsOperationAudit")
    section = audit[audit_start:]

    for forbidden in ("ApiKey", "api_key", "Authorization", "password", "密码", "PromptBody"):
        assert forbidden not in section
    assert "_seller" in section
    assert "_enabled" in section
    assert "_mode" in section
    assert "_threshold" in section
    assert "_confidence" in section
