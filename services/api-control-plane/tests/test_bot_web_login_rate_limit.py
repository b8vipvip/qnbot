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


def test_login_rate_limit_bounds_forwarded_source_key_length(monkeypatch):
    core._LOGIN_ATTEMPTS.clear()
    monkeypatch.setattr(core.time, "time", lambda: 1_800_000_000.0)
    try:
        core._rate_limit_login(RequestStub("127.0.0.1", "x" * 10_000))
        assert len(next(iter(core._LOGIN_ATTEMPTS))) == core._LOGIN_ATTEMPT_KEY_MAX_LENGTH
    finally:
        core._LOGIN_ATTEMPTS.clear()
