from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_visual_understanding_rules_are_shop_scoped_and_have_a_kugou_account_preset():
    service = read("src/Bot/ChromeNs/VisualUnderstandingRuleService.cs")
    page = read("src/Bot/Knowledge/KnowledgeV2VisualUnderstandingPage.cs")
    shell = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    targets = read("src/Directory.Build.targets")

    for field in ("ShopKey", "Seller", "RequiredKeywords", "OptionalKeywords",
                  "ExcludedKeywords", "FieldPatterns", "Enabled"):
        assert field in service
    assert "ResolveRuntimeByShopKey(shopKey)" in service
    assert "string.Equals(shop.SellerNick, seller" in service
    assert "手机端酷狗账号中心" in service
    assert "酷狗ID" in service
    assert "会员到期" in service
    assert "Guid.NewGuid().ToString" in service
    assert 'Nav("视觉理解"' in shell
    assert "KnowledgeV2VisualUnderstandingPage.cs" in targets
    assert "新增预设" in page and "保存规则" in page and "删除规则" in page


def test_visual_understanding_requires_all_required_ocr_keywords_and_honors_exclusions():
    service = read("src/Bot/ChromeNs/VisualUnderstandingRuleService.cs")
    match = service.split("public static bool TryMatch", 1)[1].split(
        "private static Dictionary<string, string> Extract", 1
    )[0]
    assert "required.Any" in match
    assert "excluded.Any" in match
    assert ".Where(x => x.Enabled)" in match
    assert "Extract(best.FieldPatterns, ocrText)" in match


def test_ocr_visual_preset_precedes_knowledge_v2_and_ai_receives_extracted_facts_on_miss():
    decision = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")
    match = decision.index("VisualUnderstandingRuleService.TryMatch")
    knowledge = decision.index("KnowledgeEngineV2Service.Resolve")
    assert match < knowledge
    assert "OCR 未命中本店视觉理解预设，转交上游视觉 AI" in decision
    assert "BuildVisualFallback(visualMatch, ocr" in decision
    assert "KnowledgeV2ContextPromptAddon = visualMatch.ContextText" in decision
    assert "knowledgeV2ContextAddon" in vision
    assert "requestPrompt = prompt + LocalOcrService.BuildPromptEvidence(localOcr)" in vision


def test_kugou_account_preset_extracts_unmasked_id_nickname_and_vip_expiry():
    service = read("src/Bot/ChromeNs/VisualUnderstandingRuleService.cs")
    assert '酷狗ID|酷狗ID\\s*[:：]?\\s*([A-Za-z0-9_-]{5,})' in service
    assert '昵称|帐号中心\\s*[\\r\\n]+' in service
    assert '会员到期|超级VIP\\s*(\\d{4}-\\d{2}-\\d{2}到期)' in service
    assert "不做通用长数字脱敏" in service
    assert "Redact(" not in service
