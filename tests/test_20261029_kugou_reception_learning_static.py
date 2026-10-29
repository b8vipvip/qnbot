"""Workflow learning must be review-only; generated policy must never auto-publish."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LEARNING = "src/Bot/ChromeNs/ConversationSessionLearningService.cs"
POLICY = "src/Bot/ChromeNs/KugouReceptionPolicy.cs"

def text(p):
    return (ROOT / p).read_text(encoding="utf-8-sig")

def test_five_minute_learning_keeps_original_timing_and_review_only_flows():
    source = text(LEARNING)
    assert "TimeSpan.FromMinutes(InactivityMinutes)" in source
    assert '["workflow_proposals"]' in source
    assert '"workflow_proposals"' in source or r'\\"workflow_proposals\\"' in source
    assert "SerializeSuggestionsWithWorkflowReview(" in source
    assert "RequiresWorkflowReview(suggestion)" in source
    assert "未写入生产知识库" in source
    assert '["kind"] = "reception_workflow_proposal"' in source
    assert '["action"] = "manual_review"' in source
    assert '["applied"] = false' in source
    assert "ReviewedKnowledgeLearningService.ApplyReviewedKnowledge(" in source
    assert source.index("RequiresWorkflowReview(suggestion)") < source.index(
        "ReviewedKnowledgeLearningService.ApplyReviewedKnowledge("
    )

def test_workflow_candidates_require_evidence_and_are_never_sent_as_answers():
    source = text(LEARNING)
    assert "var allowed = new HashSet<string>" in source
    assert '"WAIT", "ASK_EVIDENCE", "REPLY", "SEND_VERIFIED_GUIDE", "HANDOFF"' in source
    assert "evidence.Length == 0 || requiredEvidence.Length == 0" in source
    assert '["apply_message"] = "流程候选仅供审核：未修改生产流程与知识库"' in source
    assert "proposedFlows.Take(4)" in source

def test_merchant_scoped_state_is_short_lived_and_privacy_minimized():
    policy = text(POLICY)
    assert "GetRecentStage(string seller, string buyer)" in policy
    assert "BuyerHash = HashBuyer(seller, buyer)" in policy
    assert "AtUtc > DateTime.UtcNow.AddHours(-2)" in policy
    assert '"awaiting_binding_proof"' in policy
    assert '"awaiting_order_review"' in policy
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in policy
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
