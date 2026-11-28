import importlib.util
import json
from pathlib import Path
import zipfile

import pytest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts/verify_knowledge_v2_history_evidence.py"
spec = importlib.util.spec_from_file_location("verify_history_evidence", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def package(tmp_path, committed=150, observed=160, wrong_contact=False):
    messages = [{"contact": f"{1 + i % 2:016x}", "event_hash": f"{i:024x}", "text": "fixture"}
                for i in range(observed)]
    events = []
    for i in (1, 2):
        contact = f"{i:016x}"
        hashes = [row["event_hash"] for row in messages[:committed] if row["contact"] == contact]
        if wrong_contact and i == 1:
            hashes[0] = messages[1]["event_hash"]
        events.extend([
            {"type": "learning_commit", "contact": contact, "message_hashes": hashes,
             "observed_turns": len(hashes)+10, "analyzed_pairs": 3,
             "model_referenced": 1, "supplemented_pairs": 1, "non_reusable_pairs": 1},
            {"type": "contact", "contact": contact, "status": "completed"},
        ])
    manifest = {"Status": "completed", "FailedContacts": 0,
                "DistinctBuyerEventHashes": observed, "CommittedLearningContacts": 2,
                "ObservedTurns": committed+20, "AnalyzedHumanPairs": 6,
                "ModelReferencedPairs": 2, "SupplementedPairs": 2, "NonReusablePairs": 2}
    path = tmp_path / "sample.zip"
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("manifest.json", json.dumps(manifest))
        z.writestr("messages-redacted.jsonl", "\n".join(map(json.dumps, messages)))
        z.writestr("events.jsonl", "\n".join(map(json.dumps, events)))
    return path


def test_counts_only_successfully_committed_message_evidence(tmp_path):
    result = module.verify(package(tmp_path))
    assert result["sample_accounting_pass"]
    assert result["committed_buyer_messages"] == 150
    assert result["unique_observed_buyer_messages"] == 160
    assert not result["whole_shop_coverage_verified"]
    assert not result["upstream_authenticity_verified"]


def test_uncommitted_reads_cannot_satisfy_threshold(tmp_path):
    assert not module.verify(package(tmp_path, committed=149))["sample_accounting_pass"]


def test_cross_contact_hash_cannot_satisfy_commit(tmp_path):
    with pytest.raises(ValueError, match="different-contact"):
        module.verify(package(tmp_path, wrong_contact=True))
