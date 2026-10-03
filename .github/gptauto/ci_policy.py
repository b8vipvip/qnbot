from __future__ import annotations

import argparse
import re
from pathlib import Path


_TRANSIENT_PATTERNS = [
    r"\bECONNRESET\b",
    r"\bETIMEDOUT\b",
    r"connection reset by peer",
    r"TLS handshake timeout",
    r"temporary failure in name resolution",
    r"service unavailable",
    r"bad gateway",
    r"gateway timeout",
    r"HTTP[^\n]*(?:502|503|504)",
    r"failed to download action",
    r"unable to resolve action",
    r"hosted runner[^\n]*(?:lost|disconnected|unavailable)",
    r"runner[^\n]*(?:lost communication|was terminated)",
]


def is_transient_failure(log_text: str, *, conclusion: str = "failure", run_attempt: int = 1) -> bool:
    if run_attempt >= 2:
        return False
    conclusion = (conclusion or "").strip().lower()
    if conclusion == "startup_failure":
        return True
    if conclusion not in {"failure", "timed_out"}:
        return False
    return any(re.search(pattern, log_text, re.I | re.M) for pattern in _TRANSIENT_PATTERNS)


def main() -> int:
    parser = argparse.ArgumentParser(description="Classify whether one failed Actions run deserves one failed-jobs-only retry")
    parser.add_argument("--log", required=True)
    parser.add_argument("--conclusion", default="failure")
    parser.add_argument("--attempt", type=int, default=1)
    args = parser.parse_args()
    text = Path(args.log).read_text(encoding="utf-8", errors="replace")
    transient = is_transient_failure(text, conclusion=args.conclusion, run_attempt=args.attempt)
    print("true" if transient else "false")
    return 0 if transient else 1


if __name__ == "__main__":
    raise SystemExit(main())
