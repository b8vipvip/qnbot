"""Kugou merchant reception: non-destructive policy and send authority guards."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def src(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

POLICY = "src/Bot/ChromeNs/KugouReceptionPolicy.cs"
TEXT = "src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs"
VISION = "src/Bot/ChromeNs/VisionRequestService.cs"
SENDER = "src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs"

def test_scoped_identity_prevents_cross_shop_knowledge_leak():
    p = src(POLICY)
    assert "KnowledgeEngineV2Repository.LoadAll(seller)" in p
    assert 'x.ProductIds.Any(id => id == FirstProduct)' in p
    assert "return matched;" in p
    assert 'internal const string SecondProduct = "1066362196568"' in p

def test_quarantine_is_non_destructive_and_applies_to_both_knowledge_paths():
    p = src(POLICY)
    assert "QuarantineIds" in p
    assert "25ef03a5b5d6412dbb3c9c0e9d8dd69e" in p
    assert "3df9874e49d24a16b0c01685558e7c15" in p
    assert "07ed86dfbdb149d7a2565288c582c25a" in p
    v2 = src("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    smart = src("src/Bot/ChromeNs/SmartReplyRouterService.cs")
    assert "KugouReceptionPolicy.IsQuarantined(seller, record.Id, record.Title, record.Answer)" in v2
    assert "KugouReceptionPolicy.IsQuarantined(seller, x.Id, x.Title, x.Answer)" in smart
    assert "KnowledgeEngineV2Repository.ReplaceAll" not in p
    assert "Delete(" not in p

def test_phone_only_wait_blocks_before_other_auto_reply_lanes():
    p = src(POLICY)
    premerge = src("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "PhoneOnly.IsMatch(input)" in p
    assert "PhotoInstruction.IsMatch(instruction)" in p
    assert 'instruction.IndexOf("手机号", StringComparison.Ordinal) >= 0' in p
    assert premerge.index("KugouReceptionPolicy.TryWaitForEvidence(") < premerge.index(
        "ManualRechargeAssistantService.TryHandleAsync("
    )
    assert 'return CanonicalPreMergeOutcome.Consumed;' in premerge

def test_all_reply_lanes_share_reception_send_gate():
    text = src(TEXT)
    premerge = src("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    vision = src(SENDER)
    assert 'lease.MarkCompleted("reception_business_guard_suppressed");' in text
    assert "KugouReceptionPolicy.BlockOutgoing(" in text
    assert "KugouReceptionPolicy.BlockOutgoing(" in premerge
    assert "KugouReceptionPolicy.BlockOutgoing(" in vision
    assert 'lifecycleLease.MarkCompleted("vision_business_guard_suppressed");' in vision
    assert "ParallelReplyRelevanceGate.ShouldSend(" in vision

def test_visual_branch_uses_evidence_not_product_title_to_authorize():
    policy = src(POLICY)
    vision = src(VISION)
    assert "KugouReceptionPolicy.TryResolveVisual(" in vision
    assert "result.VisualSummary, result.LocalOcrText" in vision
    assert "hisense_builtin_decline" in policy
    assert "tcl_qq_reject" in policy
    assert "tcl_need_verified_binding" in policy
    assert "xiaomi_builtin_karaoke" in policy
    assert "https://howhub.k2n.cn/guide/tcl-ln" in policy
    assert "if (answer.Length == 0) return false;" in policy

def test_explicit_photo_referential_followups_last_90_seconds():
    followup = src("src/Bot/ChromeNs/VisionFollowUpContextPipeline.cs")
    assert "ExplicitReferentialWindowSeconds = 90" in followup
    assert 'compact == "是这个不"' in followup
    assert r'@"^\d{5,20}$"' in followup
    assert "最近图片未与后续文字合并" in followup

def test_decision_audit_is_bounded_and_anonymizes_buyer():
    p = src(POLICY)
    assert "CrossProcessAtomicStateFile.Acquire(" in p
    assert "WriteAllTextAtomic(" in p
    assert "HashBuyer(seller, buyer)" in p
    assert "SHA256.Create()" in p
    assert ".Take(300)" in p
    assert "AddDays(-7)" in p
