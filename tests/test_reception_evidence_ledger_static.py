"""Regression coverage for the generic, non-authoritative reception evidence ledger."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
def source(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_event_ledger_is_shop_scoped_atomic_and_deduplicated():
    code=source("src/Bot/ChromeNs/ReceptionEvidenceLedger.cs")
    assert "ResolveRuntimeBySellerNick(seller)" in code
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
    instruction=source("src/Bot/ChromeNs/KugouReceptionPolicy.cs")
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


def test_audit_read_failure_is_not_misreported_as_an_empty_evidence_ledger():
    code = source("src/Bot/ChromeNs/ReceptionEvidenceLedger.cs")
    audit = code[code.index("internal static string GetAuditJson"):]

    assert "if (!File.Exists(file)) return \"[]\";" in audit
    assert "throw new InvalidDataException" in audit
    assert "JsonConvert.DeserializeObject<List<EvidenceRow>>(raw)" in audit
    assert "records.Any(x => x == null" in audit
    assert "Distinct(StringComparer.Ordinal).Count() != records.Count" in audit
    assert audit.count('return "[]";') == 1
    assert "catch (Exception ex)" in audit
    assert 'throw new InvalidDataException("接待证据账本JSON损坏，原文件已保留。", ex);' in audit
    assert 'Log.ErrorWithMaxCount("读取接待证据审计失败' not in audit


def test_reception_audit_ui_surfaces_ledger_errors_instead_of_showing_no_evidence():
    page = source("src/Bot/Knowledge/KnowledgeV2ReceptionAuditPage.cs")
    refresh = page[page.index("public void RefreshView"):page.index("private void ExportAudit")]

    assert "if (t.IsFaulted)" in refresh
    assert '_status.Text = "读取失败";' in refresh
    assert "_output.Text = t.Exception.GetBaseException().Message;" in refresh
