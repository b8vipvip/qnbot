from __future__ import annotations

from types import SimpleNamespace

import pytest
from fastapi import HTTPException

import bot_web_console as core


class RequestStub:
    def __init__(self, host: str, forwarded: str = ""):
        self.headers = {"x-forwarded-for": forwarded} if forwarded else {}
        self.client = SimpleNamespace(host=host)


def test_login_rate_limit_prunes_expired_sources_and_caps_cardinality(monkeypatch):
    core._LOGIN_ATTEMPTS.clear()
    now = 1_800_000_000.0
    monkeypatch.setattr(core.time, "time", lambda: now)
    try:
        for index in range(core._LOGIN_ATTEMPT_MAX_KEYS):
            core._LOGIN_ATTEMPTS[f"source-{index}"] = [now - 1]
        core._LOGIN_ATTEMPTS["expired-source"] = [
            now - core._LOGIN_ATTEMPT_WINDOW_SECONDS - 1
        ]

        core._rate_limit_login(RequestStub("198.51.100.1"))

        assert "expired-source" not in core._LOGIN_ATTEMPTS
        assert len(core._LOGIN_ATTEMPTS) <= core._LOGIN_ATTEMPT_MAX_KEYS
        assert "198.51.100.1" in core._LOGIN_ATTEMPTS
    finally:
        core._LOGIN_ATTEMPTS.clear()


def test_login_rate_limit_preserves_twelve_attempt_window_and_success_reset(monkeypatch):
    core._LOGIN_ATTEMPTS.clear()
    now = 1_800_000_000.0
    monkeypatch.setattr(core.time, "time", lambda: now)
    request = RequestStub("198.51.100.2")
    try:
        for _ in range(core._LOGIN_ATTEMPT_LIMIT):
            core._rate_limit_login(request)

        with pytest.raises(HTTPException) as caught:
            core._rate_limit_login(request)
        assert caught.value.status_code == 429

        core._clear_login_attempts(request)
        core._rate_limit_login(request)
        assert len(core._LOGIN_ATTEMPTS["198.51.100.2"]) == 1
    finally:
        core._LOGIN_ATTEMPTS.clear()


def test_login_rate_limit_bounds_untrusted_peer_key_length(monkeypatch):
    core._LOGIN_ATTEMPTS.clear()
    monkeypatch.setattr(core.time, "time", lambda: 1_800_000_000.0)
    try:
        core._rate_limit_login(RequestStub("x" * 10_000, "203.0.113.8"))
        assert len(next(iter(core._LOGIN_ATTEMPTS))) == core._LOGIN_ATTEMPT_KEY_MAX_LENGTH
    finally:
        core._LOGIN_ATTEMPTS.clear()


def test_login_rate_limit_ignores_forwarded_header_from_untrusted_peer(monkeypatch):
    monkeypatch.setattr(core, "_TRUSTED_PROXY_NETWORKS", (core.ipaddress.ip_network("127.0.0.0/8"),))
    request = RequestStub("198.51.100.10", "203.0.113.99")
    assert core._login_attempt_key(request) == "198.51.100.10"


def test_login_rate_limit_uses_rightmost_untrusted_address_from_trusted_proxy(monkeypatch):
    monkeypatch.setattr(core, "_TRUSTED_PROXY_NETWORKS", (
        core.ipaddress.ip_network("127.0.0.0/8"),
        core.ipaddress.ip_network("172.16.0.0/12"),
    ))
    # The leftmost entry is attacker-supplied; Caddy appends the real peer at right.
    request = RequestStub("172.20.0.5", "203.0.113.99, 198.51.100.22")
    assert core._login_attempt_key(request) == "198.51.100.22"


def test_login_rate_limit_walks_multiple_trusted_proxies_from_right_to_left(monkeypatch):
    monkeypatch.setattr(core, "_TRUSTED_PROXY_NETWORKS", (
        core.ipaddress.ip_network("127.0.0.0/8"),
        core.ipaddress.ip_network("172.16.0.0/12"),
    ))
    request = RequestStub("172.20.0.5", "198.51.100.22, 172.20.0.4")
    assert core._login_attempt_key(request) == "198.51.100.22"
