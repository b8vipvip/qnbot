"""Prevent merchant business strings and live internet hostnames in public docs."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
FILES = [ROOT / "README.md", ROOT / "AGENTS.md"]
FILES.extend((ROOT / "docs").rglob("*.md"))
FILES.extend((ROOT / "docs").rglob("*.txt"))
FILES.append(ROOT / "services/api-control-plane/README.md")
FILES.extend((ROOT / "ci-last-build").rglob("*.txt"))
# Keep literal merchant names out of the test file as well.
PRIVATE_TOKENS = [
    "\u9177\u72d7", "\u96f7\u9e1f", "\u6d77\u4fe1",
    "\u805a\u597d\u770b", "\u5f53\u8d1d", "\u9177\u55b5", "\u534e\u6570",
    "howhub", "aboter.mv3", "1057505313937", "1066362196568"
]
LIVE_URL = re.compile(r"https?://(?!<|127\.0\.0\.1\b|localhost\b)[a-z0-9-]+(?:\.[a-z0-9-]+)+", re.I)
GIT_URL = re.compile(r"\bgit@(?:[a-z0-9-]+\.)+[a-z]{2,}:", re.I)

def test_public_docs_do_not_contain_merchant_literals_or_real_hosts():
    violations = []
    for path in FILES:
        if not path.is_file():
            continue
        data = path.read_text(encoding="utf-8-sig", errors="replace")
        low = data.lower()
        for token in PRIVATE_TOKENS:
            if token.lower() in low:
                violations.append(f"{path.relative_to(ROOT)} contains a merchant identifier")
        if LIVE_URL.search(data) or GIT_URL.search(data):
            violations.append(f"{path.relative_to(ROOT)} contains a live hostname")
    assert not violations, "\n".join(violations)
