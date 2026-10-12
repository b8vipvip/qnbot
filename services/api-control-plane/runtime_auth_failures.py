"""Bounded, admin-only runtime 401 attribution, without retaining credentials."""
from __future__ import annotations

import hashlib
import hmac
import re
import secrets
import threading
import time
from datetime import datetime, timezone
from typing import Any, Dict, List, Tuple

from fastapi import Depends, Request


class RuntimeAuthFailureTracker:
    """Five rolling minute buckets; data is process-local and never persisted."""

    def __init__(self, control_plane: Any, max_groups: int = 256):
        self._cp = control_plane
        self._secret = secrets.token_bytes(32)
        self._lock = threading.Lock()
        self._groups: Dict[Tuple[str, int, str, str, str, str, str], Dict[str, Any]] = {}
        self._max_groups = max_groups

    def _anonymous_id(self, value: str) -> str:
        return hmac.new(self._secret, value.encode("utf-8"), hashlib.sha256).hexdigest()[:12]

    def _source(self, request: Request) -> Tuple[str, int, str]:
        token = self._cp.bearer_token(request)
        if not token:
            return "missing", 0, "none"
        digest = self._cp.hash_token(token)
        with self._cp.db() as conn:
            row = conn.execute(
                "SELECT id,enabled FROM client_tokens WHERE token_hash=?", (digest,)
            ).fetchone()
        if row is None:
            return "unknown", 0, "unknown-" + self._anonymous_id(token)
        client_id = int(row["id"])
        if not bool(row["enabled"]):
            return "disabled", client_id, "client-" + str(client_id)
        # A valid client credential means the downstream 401 is not a token lookup failure.
        return "valid_token_other_401", client_id, "client-" + str(client_id)

    @staticmethod
    def _header(value: str, maximum: int) -> str:
        # Header labels are untrusted; never echo control characters or unbounded values.
        value = (value or "").strip()
        if not value or len(value) > maximum:
            return ""
        return value if re.fullmatch(r"[\w.:/ +@-]+", value) else ""

    def _prune(self, current_minute: int) -> None:
        earliest = current_minute - 4
        for key, entry in list(self._groups.items()):
            entry["buckets"] = {
                minute: count
                for minute, count in entry["buckets"].items()
                if minute >= earliest
            }
            if not entry["buckets"]:
                del self._groups[key]

    def record(self, request: Request) -> None:
        reason, client_id, credential_id = self._source(request)
        shop_key = self._header(request.headers.get("x-shop-key", ""), 80)
        agent = self._header(request.headers.get("user-agent", ""), 96) or "other"
        key = (
            reason, client_id, credential_id, shop_key, agent,
            request.method, request.url.path[:120],
        )
        minute = int(time.monotonic() // 60)
        with self._lock:
            self._prune(minute)
            if key not in self._groups and len(self._groups) >= self._max_groups:
                key = ("overflow", 0, "overflow", "", "other", "*", "*")
            entry = self._groups.setdefault(key, {"buckets": {}, "last_seen_utc": ""})
            entry["buckets"][minute] = entry["buckets"].get(minute, 0) + 1
            entry["last_seen_utc"] = datetime.now(timezone.utc).isoformat(timespec="seconds")

    def snapshot(self) -> Dict[str, Any]:
        minute = int(time.monotonic() // 60)
        with self._lock:
            self._prune(minute)
            groups: List[Dict[str, Any]] = []
            for key, entry in self._groups.items():
                reason, client_id, credential_id, shop_key, user_agent, method, path = key
                groups.append({
                    "reason": reason,
                    "client_id": client_id or None,
                    "credential_id": credential_id,
                    "claimed_shop_key": shop_key,
                    "user_agent": user_agent,
                    "method": method,
                    "path": path,
                    "count": sum(entry["buckets"].values()),
                    "last_seen_utc": entry["last_seen_utc"],
                })
        groups.sort(key=lambda item: item["count"], reverse=True)
        return {
            "window_seconds": 300,
            "total": sum(item["count"] for item in groups),
            "groups": groups,
            "notes": (
                "Data resets on process restart. claimed_shop_key and user_agent are "
                "untrusted request headers; credential_id is a process-local keyed alias, "
                "not a token or token hash."
            ),
        }


def install(control_plane: Any) -> RuntimeAuthFailureTracker:
    tracker = RuntimeAuthFailureTracker(control_plane)

    @control_plane.app.get("/api/admin/runtime-auth-failures")
    def admin_runtime_auth_failures(
        _: str = Depends(control_plane.require_admin),
    ) -> Dict[str, Any]:
        return tracker.snapshot()

    @control_plane.app.middleware("http")
    async def record_runtime_401(request: Request, call_next):
        response = await call_next(request)
        path = request.url.path
        if response.status_code == 401 and (
            path.startswith("/api/runtime/v1/") or path.startswith("/v1/")
        ):
            try:
                tracker.record(request)
            except Exception:
                # Diagnostics must never affect availability or authentication semantics.
                pass
        return response

    return tracker
