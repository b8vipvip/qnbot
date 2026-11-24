from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_visual_understanding_rules_are_shop_scoped_and_cloud_syncable_but_not_faq_records():
    service = read("src/Bot/ChromeNs/VisualUnderstandingRuleService.cs")
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    cloud = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")

    assert 'RuleSourceType = "visual_understanding_rule"' in service
    assert "ResolveRuntimeByShopKey(shopKey)" in service
    assert "ShopSettingsScope.Enter(shop, false)" in service
    assert "KnowledgeEngineV2Repository.LoadAll(shop.SellerNick)" in service
    assert "KnowledgeEngineV2Repository.Save(shop.SellerNick, record)" in service
    assert 'record.Status = "disabled"' in service
    assert "record.Enabled = false" in service
    assert "JArray.FromObject(local)" in cloud
    assert "ComputeCloudSyncFingerprint" in cloud
    assert "IsVisualUnderstandingRule(record)" in repository
    assert "record.SourceType, \"visual_understanding_rule\"" in repository
    assert '.Where(x => x != null && x.Enabled' in semantics


def test_kugou_account_center_preset_extracts_unredacted_account_and_membership_fields():
    service = read("src/Bot/ChromeNs/VisualUnderstandingRuleService.cs")
    assert 'Name = "手机端酷狗账号中心"' in service
    assert 'RequiredKeywords = "酷狗ID,我的会员"' in service
    assert 'MatchAnyKeywords = "帐号中心,账号中心"' in service
    for key in ['"kugou_id"', '"nickname"', '"phone"', '"membership_status"', '"membership_expiry"']:
        assert key in service
    assert r'(1[3-9]\d{9})' in service
    assert "CleanFieldValue" in service
    assert "Redact(" not in service
    assert "BuildContextText" in service
    assert "当前买家和当前会话" in service


def test_visual_rule_match_precedes_knowledge_retrieval_and_miss_routes_upstream():
    ocr = read("src/Bot/ChromeNs/OcrFirstKnowledgeDecisionService.cs")
    vision = read("src/Bot/ChromeNs/VisionRequestService.cs")
    match_at = ocr.index("VisualUnderstandingRuleService.TryMatch")
    resolve_at = ocr.index("KnowledgeEngineV2Service.Resolve")
    assert match_at < resolve_at
    miss = ocr[match_at:resolve_at]
    assert "交给上游AI" in miss
    assert "return null;" in miss
    assert "BuildKnowledgeQuestion(task.CombinedQuestion, timeline, ocr.Text, visualContext)" in ocr
    assert "BuildVisualFallback(task, ocr, visualMatch, visualContext" in ocr
    assert "KnowledgeV2ContextPromptAddon = addon" in ocr
    assert "当前图片结构化字段" in vision
    assert "不得在本轮上下文中自行脱敏或改写" in vision


def test_visual_preset_direct_answers_keep_existing_pre_send_trust_and_shop_isolation():
    pipeline = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")
    assert 'result.MatchedVisualKnowledgeId,\n                "ocr-direct-knowledge-v2"' in pipeline
    assert '.StartsWith("visual-rule:", StringComparison.Ordinal)' in pipeline
    assert "ParallelReplyRelevanceGate.ShouldSend(" in pipeline
    assert "lifecycleLease.CancellationToken" in pipeline


def test_visual_understanding_page_is_registered_and_hidden_from_normal_faq_list():
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    page = read("src/Bot/Knowledge/VisualUnderstandingRulesPage.cs")
    records = read("src/Bot/Knowledge/KnowledgeCenterV2RecordsPage.cs")
    props = read("src/Bot/Directory.Build.props")
    assert 'Nav("视觉理解", () => new VisualUnderstandingRulesPage(_seller, _shopKey))' in ui
    assert "载入酷狗账号中心预设" in page
    assert "字段提取规则 JSON" in page
    assert "测试匹配" in page
    assert "visual_understanding_rule" in records
    assert "VisualUnderstandingRulesPage.cs" in props
    assert "VisualUnderstandingRuleService.cs" in props


def test_ocr_admin_ledger_redaction_does_not_redact_runtime_response_payload():
    runtime = read("services/api-control-plane/runtime_ocr.py")
    local = read("src/Bot/ChromeNs/LocalOcrService.cs")
    assert "_sanitize_ocr_text(result_text or \"\")" in runtime
    assert '"result_text_redacted"' in runtime
    assert '"text": text' in runtime
    assert '"**result"' in runtime or "**result," in runtime
    assert "Text = Limit(remote.text)" in local
