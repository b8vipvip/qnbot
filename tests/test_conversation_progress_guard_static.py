import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def policy() -> dict:
    return json.loads(read("src/Bot/default-business-policy.json"))


def test_import_export_bootstrap_is_explicitly_initialized():
    app = read("src/Bot/App.xaml.cs")
    assert "RulePolicyImportExportUi.InitializeForApp();" in app
    assert "KnowledgePolicyProfileUi.Initialize();" not in app
    source = read("src/Bot/Knowledge/RulePolicyImportExportUi.cs")
    assert "typeof(StorePromptProfileWindow)" in source


def test_progress_guard_tracks_state_without_persisting_sensitive_values():
    source = read("src/Bot/ChromeNs/ConversationProgressGuardService.cs")
    config = policy()
    assert "HasOrderEvidence" in source
    assert "DeviceAccountConfirmed" in source
    assert "HasPhoneNumber" in source
    assert "HasVerificationCode" in source
    assert 'CurrentInputKind = "phone_number"' in source
    assert 'CurrentInputKind = "verification_code"' in source
    assert 'AddFact(state, progress.HasPhoneNumber, T("facts.phone"))' in source
    assert 'AddFact(state, progress.HasVerificationCode, T("facts.code"))' in source
    assert config["facts"] == {}
    assert config["prompts"] == {}


def test_router_filters_regressive_knowledge_and_forces_contextual_flow():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    guard = "ConversationProgressGuardService.RequiresContextualHandling(commerceAuthority.ConversationState)"
    assert guard in runtime
    assert runtime.index(guard) < runtime.index("if (!ReplyModeService.IsLocalFirst")
    assert "禁止固定知识直答" in runtime
    assert "decision.CanDirectReply = false" in runtime
    assert "ConversationProgressGuardService.RequiresContextualHandling(authority.ConversationState)" in pipeline


def test_validator_uses_shop_configured_messages_for_repeated_or_regressive_requests():
    validator = read("src/Bot/ChromeNs/PreSendAnswerValidator.cs")
    guard = read("src/Bot/ChromeNs/ConversationProgressGuardService.cs")
    config = policy()
    assert "ConversationProgressGuardService.AddValidationIssues" in validator
    assert 'T("validationIssues.repeatScreenshot")' in guard
    assert 'T("validationIssues.repeatOrder")' in guard
    assert 'T("validationIssues.flowRegression")' in guard
    assert config["validationIssues"] == {}


def test_general_inquiry_does_not_force_a_business_specific_evidence_request_by_default():
    guard = read("src/Bot/ChromeNs/ConversationProgressGuardService.cs")
    config = policy()
    assert "!p.HasOrderEvidence" in guard
    assert "!p.ExplicitCompatibilityQuestion" in guard
    assert "!p.AsksScreenshotTarget" in guard
    assert "!p.AsksPurchaseTarget" in guard
    assert config["prompts"] == {}
    assert config["stages"]["preSaleGeneral"]["stage"] == "一般咨询"


def test_progress_guard_is_in_shared_build_targets():
    targets = read("src/Directory.Build.targets")
    assert "ConversationProgressGuardService.cs" in targets
