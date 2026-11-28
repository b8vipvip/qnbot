from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_policy_sidecar_has_modes_conditions_and_reliability_evidence():
    assert not (ROOT / "src/Bot/ChromeNs/KnowledgePolicyProfileService.cs").exists()
    model = read("src/Bot/Knowledge/KnowledgeEngineV2.Models.cs")
    semantics = read("src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs")
    feedback = read("src/Bot/Knowledge/KnowledgeEngineV2FeedbackService.cs")
    for field in ("Conditions", "Exclusions", "RequiredContext", "ProductIds", "CorrectionCount", "WithdrawCount"):
        assert field in model
        assert field in semantics or field in feedback
    assert "GetQualityAdjustment" in feedback
    assert "IsApplicable" in semantics


def test_router_applies_policy_before_direct_answer_and_records_route_selection():
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    assert "IsEligibleProductionMatch" in public
    assert "IsApplicable(" in index
    assert "ShopReceptionPolicy.IsQuarantined" in index
    assert "KnowledgeEngineV2FeedbackService.RecordDirectSend" in runtime
    assert "BuildContextPromptAddon" in runtime
    assert "IsSelectedKnowledgeCurrent" in runtime


def test_reviewed_human_corrections_feed_reliability_profile():
    code = read("src/Bot/ChromeNs/ReviewedKnowledgeLearningService.cs")
    repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    feedback = read("src/Bot/Knowledge/KnowledgeEngineV2FeedbackService.cs")
    assert "TrySaveReviewedDirectAnswer" in code
    assert "CorrectionCount++" in repo
    assert "PreviousJson" in repo
    assert '"manual_reply:"' in feedback
    assert "GetQualityAdjustment" in feedback


def test_knowledge_policy_editor_is_available_from_knowledge_manager():
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2RecordsPage.cs")
    props = read("src/Bot/Directory.Build.props")
    shell = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    for label in ("适用条件", "排除条件", "必要上下文", "绑定商品ID", "可信度", "权威度"):
        assert label in ui
    assert "TrySaveEditorRecord" in ui
    assert 'Nav("知识"' in shell
    assert "KnowledgeCenterV2RecordsPage.cs" in props

