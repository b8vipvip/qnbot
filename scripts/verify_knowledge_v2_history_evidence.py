"""Read-only verification of one QnBot history evidence package.

Validates internal consistency, not the authenticity of the upstream Qianniu
history API. No customer text or identifiers are printed.
"""
import argparse
import json
import re
import sys
import zipfile

_HASH = re.compile(r"[0-9A-Fa-f]{24}")
_CONTACT = re.compile(r"[0-9A-Fa-f]{16}")
_FIELDS = {
    "observed_turns": "ObservedTurns",
    "analyzed_pairs": "AnalyzedHumanPairs",
    "model_referenced": "ModelReferencedPairs",
    "supplemented_pairs": "SupplementedPairs",
    "non_reusable_pairs": "NonReusablePairs",
}

def verify(path, minimum=150):
    if minimum < 1:
        raise ValueError("minimum must be positive")
    with zipfile.ZipFile(path) as archive:
        required = {"manifest.json", "messages-redacted.jsonl", "events.jsonl"}
        names = archive.namelist()
        if any(names.count(name) != 1 for name in required):
            raise ValueError("missing or duplicate evidence entry")
        if any(archive.getinfo(name).file_size > 128 * 1024 * 1024 for name in required):
            raise ValueError("evidence entry too large")
        manifest = json.loads(archive.read("manifest.json").decode("utf-8-sig"))
        def rows(name):
            with archive.open(name) as stream:
                for raw in stream:
                    if raw.strip():
                        yield json.loads(raw.decode("utf-8-sig"))
        messages = {}
        for row in rows("messages-redacted.jsonl"):
            key = row.get("event_hash", "")
            contact = row.get("contact", "")
            if not _HASH.fullmatch(key) or not _CONTACT.fullmatch(contact) or key in messages:
                raise ValueError("invalid or duplicate message evidence")
            messages[key] = contact
        commits = {}
        completed_contacts = set()
        committed_messages = set()
        totals = dict.fromkeys(_FIELDS, 0)
        for event in rows("events.jsonl"):
            if event.get("type") == "contact" and event.get("status") == "completed":
                completed_contacts.add(event.get("contact"))
            if event.get("type") != "learning_commit":
                continue
            contact = event.get("contact", "")
            hashes = event.get("message_hashes")
            if contact in commits or not _CONTACT.fullmatch(contact):
                raise ValueError("duplicate or invalid committed contact")
            if not isinstance(hashes, list) or len(set(hashes)) != len(hashes):
                raise ValueError("missing or duplicate committed message hashes")
            if any(messages.get(key) != contact for key in hashes):
                raise ValueError("learning commit references absent or different-contact message")
            for field in _FIELDS:
                value = event.get(field)
                if type(value) is not int or value < 0:
                    raise ValueError("invalid learning count")
                totals[field] += value
            if event["analyzed_pairs"] != sum(event[k] for k in
                    ("model_referenced", "supplemented_pairs", "non_reusable_pairs")):
                raise ValueError("human-pair accounting mismatch")
            if event["observed_turns"] < len(hashes):
                raise ValueError("committed messages exceed observed turns")
            commits[contact] = True
            committed_messages.update(hashes)
    if len(messages) != manifest.get("DistinctBuyerEventHashes"):
        raise ValueError("manifest unique-message count mismatch")
    if len(commits) != manifest.get("CommittedLearningContacts"):
        raise ValueError("manifest committed-contact count mismatch")
    if any(manifest.get(dest) != totals[field] for field, dest in _FIELDS.items()):
        raise ValueError("manifest learning totals mismatch")
    if not set(commits).issubset(completed_contacts):
        raise ValueError("learning commit lacks completed-contact event")
    complete = manifest.get("Status") == "completed" and manifest.get("FailedContacts") == 0
    sample_pass = (complete and len(committed_messages) >= minimum
                   and len(commits) >= 2 and totals["analyzed_pairs"] > 0)
    return {
        "run_status": manifest.get("Status"),
        "unique_observed_buyer_messages": len(messages),
        "committed_buyer_messages": len(committed_messages),
        "committed_learning_contacts": len(commits),
        **totals,
        "minimum_buyer_messages": minimum,
        "sample_accounting_pass": sample_pass,
        "whole_shop_coverage_verified": False,
        "upstream_authenticity_verified": False,
    }

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package")
    parser.add_argument("--minimum", type=int, default=150)
    args = parser.parse_args()
    try:
        result = verify(args.package, args.minimum)
    except (ValueError, TypeError, KeyError, OSError, zipfile.BadZipFile):
        print(json.dumps({"sample_accounting_pass": False, "error": "invalid or incomplete evidence package"}))
        return 2
    print(json.dumps(result, indent=2))
    return 0 if result["sample_accounting_pass"] else 1

if __name__ == "__main__":
    sys.exit(main())
