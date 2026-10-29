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
