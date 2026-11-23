"""Workflow learning may auto-publish only after exact-shop evidence and action validation."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LEARNING = "src/Bot/ChromeNs/ConversationSessionLearningService.cs"
POLICY = "src/Bot/ChromeNs/KugouReceptionPolicy.cs"

def text(p):
    return (ROOT / p).read_text(encoding="utf-8-sig")

def test_five_minute_learning_keeps_original_timing_and_auto_publishes_verified_flows():
    source = text(LEARNING)
    finalize = source[
        source.index("private static async Task FinalizeAsync"):
        source.index("private static async Task<JObject> AnalyzeAsync")
    ]
    assert "TimeSpan.FromMinutes(InactivityMinutes)" in source
    assert '["workflow_proposals"]' in source
    assert '"workflow_proposals"' in source or r'\\"workflow_proposals\\"' in source
    assert "SerializeSuggestionsWithWorkflowReview(" in source
    assert "RequiresWorkflowReview(suggestion)" in source
    assert "未写入生产知识库" in source
    assert '"auto_published"' in source
    assert "ReceptionWorkflowAgentService.PublishProposals(" in finalize
    assert "receptionWorkflowPublished" in finalize
    assert "ReviewedKnowledgeLearningService.ApplyReviewedKnowledge(" in source
    assert source.index("RequiresWorkflowReview(suggestion)") < source.index(
        "ReviewedKnowledgeLearningService.ApplyReviewedKnowledge("
    )


def test_workflow_candidates_require_real_evidence_and_are_not_general_answers():
    agent = text("src/Bot/ChromeNs/ReceptionWorkflowAgentService.cs")
    assert 'confidence < 0.90' in agent
    assert '"buyer_message"' in agent
    assert '"manual_correction"' in agent
    assert '"order_snapshot"' in agent
    assert 'replyEvidence.Kind != "manual_correction"' in agent
    assert '"WAIT", "ASK_EVIDENCE", "HANDOFF", "REPLY"' in agent
    assert 'UnsafeReplyText.IsMatch(replyTemplate)' in agent
    assert "proposals.OfType<JObject>().Take(4)" in agent
def test_merchant_scoped_state_is_short_lived_and_privacy_minimized():
    policy = text(POLICY)
    assert "GetRecentStage(string seller, string buyer)" in policy
    assert "BuyerHash = HashBuyer(seller, buyer)" in policy
    assert "AtUtc > DateTime.UtcNow.AddHours(-2)" in policy
    assert '"awaiting_binding_proof"' in policy
    assert '"awaiting_order_review"' in policy
    assert "ResolveShopForSeller(seller)" in policy
    assert 'Path.Combine(directory, "kugou-reception-decisions.json")' in policy

def test_order_photo_wait_only_advances_after_confirmed_delivery():
    policy = text(POLICY)
    order = text("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "ObserveDeliveredOrderInstruction(string seller, string buyer, string text)" in policy
    assert "verified_order_photo_instruction_delivered" in policy
    assert "GetRecentStage(seller, buyer)" in policy
    assert order.count("KugouReceptionPolicy.ObserveDeliveredOrderInstruction(plan.Seller, plan.Buyer, text);") == 2
    assert order.index("if (sent)") < order.index(
        "KugouReceptionPolicy.ObserveDeliveredOrderInstruction(plan.Seller, plan.Buyer, text);"
    )
    assert "RemoteSellerEchoVerification.Delivered" in order

def test_ai_and_observed_human_learning_cannot_publish_store_policy():
    learn = text("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    policy = text(POLICY)
    assert "RequiresManualKnowledgeReview(string seller, string question, string answer)" in policy
    assert learn.count("KugouReceptionPolicy.RequiresManualKnowledgeReview(") == 2
    assert "店铺业务知识自动学习改为会话复盘待审核" in learn
    assert 'string.Equals(sourceType, "人工修改", StringComparison.Ordinal)' in learn
    assert "等待人工审核，未自动写入" in learn

def test_knowledge_center_exposes_scoped_read_only_audit():
    ui = text("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    audit = text("src/Bot/Knowledge/KnowledgeV2ReceptionAuditPage.cs")
    build = text("src/Directory.Build.targets")
    assert 'Nav("接待决策", () => new KnowledgeV2ReceptionAuditPage(_seller))' in ui
    assert "KugouReceptionPolicy.GetAuditJson(_seller)" in audit
    assert "IsReadOnly = true" in audit
    assert "导出脱敏JSON" in audit
    assert "KnowledgeV2ReceptionAuditPage.cs" in build

def test_shop_rule_editor_is_validated_versioned_and_reversible():
    rules = text("src/Bot/ChromeNs/KugouReceptionConfigService.cs")
    ui = text("src/Bot/Knowledge/KnowledgeV2ReceptionRulesPage.cs")
    nav = text("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    target = text("src/Directory.Build.targets")
    assert "qnbot.kugou.reception-rules.v2" in rules
    assert 'obj.Value<bool?>("manual_review_required") != true' in rules
    assert 'obj.Value<bool?>("automatic_recharge_enabled") != false' in rules
    assert "IsHttpsUrl" in rules
    assert "guide_url" in rules
    assert "product_ids" in rules
    assert "quarantine_knowledge_ids" in rules
    assert "visual_rules" in rules
    assert "CrossProcessAtomicStateFile.WriteAllTextAtomic(" in rules
    assert ".backup." in rules
    assert "RestoreLatestBackup(" in rules
    assert "KugouReceptionPolicy.ResolveShopForSeller(seller)" in rules
    assert 'Nav("接待规则", () => new KnowledgeV2ReceptionRulesPage(_seller))' in nav
    assert "KugouReceptionConfigService.SaveJson(_seller, _editor.Text)" in ui
    assert "KugouReceptionConfigService.RestoreLatestBackup(_seller)" in ui
    assert "KugouReceptionConfigService.cs" in target
    assert "KnowledgeV2ReceptionRulesPage.cs" in target

def test_optional_guidance_flags_never_disable_critical_safety():
    policy = text(POLICY)
    chat = text("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    assert "EnablePhoneWait" in policy
    assert "EnableAutoGuidance" in policy
    assert "KugouReceptionConfigService.GetConfig(seller).EnablePhoneWait" in chat
    assert "KugouReceptionPolicy.BlockOutgoing(" in chat
    assert "KugouReceptionPolicy.IsQuarantined" in text(
        "src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs"
    )



def test_private_profile_migration_is_explicit_and_never_auto_enables_old_store_policy():
    rules = text("src/Bot/ChromeNs/KugouReceptionConfigService.cs")
    ui = text("src/Bot/Knowledge/KnowledgeV2ReceptionRulesPage.cs")
    assert "MigrateLegacyV1" in rules
    assert 'migrated["notes"]' in rules
    assert '["enabled"] = false' in rules
    assert "专属接待保持关闭" in ui
    assert "私有商品ID" in ui
    assert "ShopKey" in ui
