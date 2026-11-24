"""Regression coverage for the generic, non-authoritative reception evidence ledger."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
def source(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_event_ledger_is_shop_scoped_atomic_and_deduplicated():
    code=source("src/Bot/ChromeNs/ReceptionEvidenceLedger.cs")
    assert "ShopReceptionPolicy.ResolveShopForSeller(seller)" in code
    assert "ShopSettingsScope.Current" not in code
    assert 'Path.Combine(parent, "reception-evidence-ledger.json")' in code
    assert "CrossProcessAtomicStateFile.Acquire" in code
    assert "WriteAllTextAtomic" in code
    assert "records.Any(x => x != null && x.Id == id)" in code
    assert "KeepDays = 7" in code
    assert "MaxRows = 500" in code
    assert "return;" in code

def test_evidence_has_no_raw_account_or_photo_data_and_no_fulfillment_authority():
    code=source("src/Bot/ChromeNs/ReceptionEvidenceLedger.cs")
    assert "Digest(seller + " in code
    assert "EvidenceDigest = sourceHash" in code
    assert "SourceMessageKey = sourceKey" in code
    assert "ProductKey = Digest(productId ?? " in code
    assert "SkuKey = Digest(sku ?? " in code
    assert "authority" in code.lower()
    assert "SendTextWithRetryAsync(" not in code
    assert "Charge(" not in code
    assert "Refund(" not in code
    assert "raw OCR" in code

def test_order_delivery_and_vision_analysis_are_written_only_as_observations():
    instruction=source("src/Bot/ChromeNs/ShopReceptionPolicy.cs")
    vision=source("src/Bot/ChromeNs/VisionRequestService.cs")
    assert "ReceptionEvidenceLedger.RecordDeliveredEvidenceRequest(seller, buyer, text)" in instruction
    assert "ReceptionEvidenceLedger.RecordVisionObservation(" in vision
    assert "result.VisualSummary, result.LocalOcrConfidence, false" in vision
    assert "vision_observation_only" in source("src/Bot/ChromeNs/ReceptionEvidenceLedger.cs")

def test_followup_uses_original_image_identity_without_raw_chat_storage():
    ledger=source("src/Bot/ChromeNs/ReceptionEvidenceLedger.cs")
    followup=source("src/Bot/ChromeNs/VisionFollowUpContextPipeline.cs")
    assert "RecordImageFollowUp(" in ledger
    assert '"image_followup", "linked_to_prior_image"' in ledger
    assert "ReceptionEvidenceLedger.RecordImageFollowUp(" in followup
    assert "recent.Item.MessageKey, question" in followup



def test_evidence_audit_read_failure_is_not_misreported_as_an_empty_ledger():
    code = source("src/Bot/ChromeNs/ReceptionEvidenceLedger.cs")
    audit = code[
        code.index("internal static string GetAuditJson"):
        code.index("\n    }\n}")
    ]
    page = source("src/Bot/Knowledge/KnowledgeV2ReceptionAuditPage.cs")
    refresh = page[
        page.index("public void RefreshView()"):
        page.index("private void ExportAudit()")
    ]

    assert "ReadAllTextShared(file, 2, 30, out error)" in audit
    assert "throw new IOException(" in audit
    assert "return string.IsNullOrWhiteSpace(raw) ? \"[]\" : raw;" in audit
    assert "catch (Exception ex)" not in audit
    assert "if (t.IsFaulted)" in refresh
    assert '_status.Text = "读取失败";' in refresh



def test_evidence_ledger_requires_stable_seller_identity_and_nonempty_shopkey():
    code = source("src/Bot/ChromeNs/ReceptionEvidenceLedger.cs")
    path = code[
        code.index("private static string PathForSeller"):
        code.index("/// <summary>", code.index("private static string PathForSeller"))
    ]

    assert "ShopReceptionPolicy.ResolveShopForSeller(seller)" in path
    assert "string.IsNullOrWhiteSpace(shop.ShopKey)" in path
    assert "!shop.HasStableSellerId" in path
    assert "拒绝证据写入" in path



def test_evidence_ledger_never_persists_non_finite_confidence():
    code = source("src/Bot/ChromeNs/ReceptionEvidenceLedger.cs")
    normalizer = code[
        code.index("private static double NormalizeConfidence"):
        code.index("private static string PathForSeller")
    ]
    append = code[
        code.index("private static void Append"):
        code.index("internal static void RecordDeliveredEvidenceRequest")
    ]

    assert "double.IsNaN(confidence)" in normalizer
    assert "double.IsInfinity(confidence)" in normalizer
    assert "Confidence = NormalizeConfidence(confidence)" in append


def test_evidence_dedup_identity_includes_state_transition_fields():
    code = source("src/Bot/ChromeNs/ReceptionEvidenceLedger.cs")
    append = code[
        code.index("private static void Append"):
        code.index("internal static void RecordDeliveredEvidenceRequest")
    ]

    # The same image/message may first be analyzed and later withdrawn. Those are
    # separate evidence events even when the source message and visual digest match.
    assert '"|" + (observation ?? "")' in append
    assert '"|" + (stage ?? "")' in append
    assert '"|" + (authority ?? "")' in append
    assert 'withdrawn ? "withdrawn_image_seen" : "image_analyzed"' in code
    assert "records.Any(x => x != null && x.Id == id)" in append
