
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_visual_understanding_is_shop_scoped_and_uses_only_a_generic_template():
    service = read("src/Bot/ChromeNs/VisualUnderstandingRuleService.cs")
    page = read("src/Bot/Knowledge/KnowledgeV2VisualUnderstandingPage.cs")
    shell = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    targets = read("src/Directory.Build.targets")

    for field in ("ShopKey", "Seller", "RequiredKeywords", "OptionalKeywords",
                  "ExcludedKeywords", "FieldPatterns", "Enabled"):
        assert field in service
    assert "ResolveRuntimeByShopKey(shopKey)" in service
    assert "string.Equals(shop.SellerNick, seller" in service
    template = service.split("private static void ApplyGenericAccountTemplate", 1)[1].split(
        "private static void EnsureSchema", 1
    )[0]
    assert "移动端账号中心（通用模板）" in template
    assert '"{应用名称}"' in template
    assert "酷狗" not in template and "Kugou" not in template
    assert "账号ID" in template and "会员到期" in template
    assert "EnsureDefaultMobileAccountTemplate" in service
    assert "EnsureDefaultKugouPreset" not in service
    assert "rule.Enabled = false;" in template
    assert "_enabled.IsChecked = item != null && item.Enabled;" in page
    assert 'Nav("视觉理解"' in shell
    assert "KnowledgeV2VisualUnderstandingPage.cs" in targets
    assert "新增预设" in page and "保存规则" in page and "删除规则" in page


def test_legacy_visual_preset_is_converted_to_generic_values_without_overwriting_custom_rules():
    service = read("src/Bot/ChromeNs/VisualUnderstandingRuleService.cs")
    migration = service.split("One-time conversion of the old automatically seeded sample", 1)[1].split(
        "if (rules.Any", 1
    )[0]
    assert "ApplyGenericAccountTemplate(legacy)" in migration
    assert "legacy.Enabled = false;" in migration
    assert "UpdatedAtTicks" in migration
    assert "rules.FirstOrDefault" in migration


def test_visual_understanding_requires_all_required_ocr_keywords_and_honors_exclusions():
    service = read("src/Bot/ChromeNs/VisualUnderstandingRuleService.cs")
    match = service.split("public static bool TryMatch", 1)[1].split(
        "private static Dictionary<string, string> Extract", 1
    )[0]
    assert "required.Any" in match
    assert "excluded.Any" in match
    assert ".Where(x => x.Enabled)" in match
    assert "IsApplicationNamePlaceholder(rule.AppName)" in match
    assert "configuredNames.Any" in match
    assert "NormalizeApplicationText(name)" in match
    assert "启用视觉理解规则前必须填写真实应用名称" in service
    assert "Extract(best.FieldPatterns, ocrText)" in match



def test_unconfigured_account_template_cannot_match_or_claim_an_application_identity():
    service = read("src/Bot/ChromeNs/VisualUnderstandingRuleService.cs")
    match = service.split("public static bool TryMatch", 1)[1].split(
        "private static Dictionary<string, string> Extract", 1
    )[0]
    resolver = service.split("public static bool TryResolveConfiguredApplication", 1)[1].split(
        "private static IEnumerable<string> SplitApplicationNames", 1
    )[0]
    assert "IsApplicationNamePlaceholder(rule.AppName)" in match
    assert "configuredNames.Count > 0" in match
    assert "SplitApplicationNames(rule.AppName)" in resolver
    assert 'return name.Contains("{") || name.Contains("}");' in service
    assert 'rule.AppName = "{应用名称}";' in service
    assert "rule.Enabled = false;" in service

def test_ocr_visual_template_precedes_knowledge_v2_and_ai_receives_extracted_facts_on_miss():
    decision = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")
    match = decision.index("VisualUnderstandingRuleService.TryMatch")
    knowledge = decision.index("KnowledgeEngineV2Service.Resolve")
    assert match < knowledge
    assert "OCR 未命中本店视觉理解预设，转交上游视觉 AI" in decision
    assert "BuildVisualFallback(visualMatch, ocr" in decision
    assert "contextAddon = visualMatch.ContextText" in decision
    assert "KnowledgeV2ContextPromptAddon = contextAddon" in decision
    assert "knowledgeV2ContextAddon" in vision
    assert "requestPrompt = prompt + LocalOcrService.BuildPromptEvidence(localOcr)" in vision


def test_generic_account_template_extracts_unmasked_account_id_nickname_and_membership_expiry():
    service = read("src/Bot/ChromeNs/VisualUnderstandingRuleService.cs")
    template = service.split("private static void ApplyGenericAccountTemplate", 1)[1].split(
        "private static void EnsureSchema", 1
    )[0]
    assert '账号ID|(?:^|[\\r\\n])[^\\r\\n]{0,16}ID\\s*[:：]?\\s*([A-Za-z0-9_-]{5,})' in template
    assert '昵称|(?:账号|帐号)中心\\s*[\\r\\n]+' in template
    assert '会员到期|(?:VIP|会员)' in template
    assert "Redact(" not in service


def test_generic_settings_and_help_do_not_ship_brand_specific_examples():
    service = read("src/Bot/ChromeNs/VisualUnderstandingRuleService.cs")
    help_window = read("src/Bot/Knowledge/KnowledgeCenterHelpWindow.cs")
    reception_page = read("src/Bot/Knowledge/KnowledgeV2ReceptionRulesPage.cs")
    policy_profile = read("src/Bot/Knowledge/KnowledgePolicyProfileUi.cs")
    correction_window = read("src/Bot/AssistWindow/Widget/Robot/ConversationEditWindow.cs")
    reply_management = read("src/Bot/Knowledge/KnowledgeV2BotReplyManagementPage.cs")
    memory_ui = read("src/Bot/Knowledge/KnowledgeMemoryEngineUi.cs")
    operations_ui = read("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    recharge_settings = read("services/api-control-plane/static/manual-recharge.html")
    vision_prompt = read("src/Bot/ChromeNs/VisionRequestService.cs")
    operator_ui = read("src/Bot/Knowledge/KnowledgeV2OperatorUiBridge.cs")
    config = read("src/Bot/ChromeNs/ShopReceptionConfigService.cs")
    default_config = config.split("private static JObject DefaultDocument", 1)[1].split(
        "private static JObject MigrateLegacyV1", 1
    )[0]
    template = service.split("private static void ApplyGenericAccountTemplate", 1)[1].split(
        "private static void EnsureSchema", 1
    )[0]
    assert "酷狗" not in template
    assert "酷狗" not in default_config
    for surface in (help_window, reception_page, policy_profile, correction_window,
                    reply_management, memory_ui, operations_ui, recharge_settings,
                    vision_prompt, operator_ui):
        assert "酷狗" not in surface


def test_new_shop_reception_settings_use_generic_schema_and_path_with_legacy_read_support():
    config = read("src/Bot/ChromeNs/ShopReceptionConfigService.cs")
    default = config.split("private static JObject DefaultDocument", 1)[1].split(
        "private static JObject MigrateLegacyV1", 1
    )[0]
    assert 'qnbot.shop.reception-rules.v3' in config
    assert '"version"] = 3' in default
    assert '"shop-reception-rules.json"' in config
    assert "LegacySchemaV1" in config and "LegacySchemaV2" in config
    assert "LegacyPathForShop" in config


def test_visual_understanding_precedes_knowledge_v2_and_unmatched_images_fall_back_to_ai():
    decision = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    assert decision.index("VisualUnderstandingRuleService.TryMatch") < decision.index(
        "KnowledgeEngineV2Service.Resolve"
    )
    assert "OCR 未命中本店视觉理解预设，转交上游视觉 AI" in decision
    assert "BuildVisualFallback(visualMatch, ocr" in decision
