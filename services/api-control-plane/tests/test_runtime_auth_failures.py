from __future__ import annotations

import contextlib
import hashlib
import sqlite3

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from fastapi.testclient import TestClient

import runtime_auth_failures


class ControlPlane:
    def __init__(self, path):
        self.path = str(path)
        self.app = FastAPI()
        with self.db() as conn:
            conn.execute(
                "CREATE TABLE client_tokens (id INTEGER PRIMARY KEY, token_hash TEXT, enabled INTEGER)"
            )

        @self.app.get("/api/runtime/v1/probe")
        def unauthorized_probe():
            return JSONResponse({"detail": "客户端令牌无效"}, status_code=401)

        @self.app.get("/healthz")
        def health():
            return JSONResponse({"status": "ok"})

    @contextlib.contextmanager
    def db(self):
        conn = sqlite3.connect(self.path)
        conn.row_factory = sqlite3.Row
        try:
            yield conn
            conn.commit()
        finally:
            conn.close()

    @staticmethod
    def hash_token(token):
        return hashlib.sha256(token.encode("utf-8")).hexdigest()

    @staticmethod
    def bearer_token(request):
        value = request.headers.get("authorization", "")
        return value.split(" ", 1)[1].strip() if value.lower().startswith("bearer ") else ""

    @staticmethod
    def require_admin(request: Request):
        if request.headers.get("x-test-admin") != "yes":
            raise HTTPException(status_code=401, detail="admin only")
        return "admin"


def test_runtime_401_attribution_preserves_auth_and_hides_credentials(tmp_path):
    cp = ControlPlane(tmp_path / "auth.db")
    enabled_token = "qnb_enabled_example_test_123456789abcdef"
    disabled_token = "qnb_disabled_example_test_123456789abcdef"
    unknown_token = "qnb_unknown_example_test_123456789abcdef"
    with cp.db() as conn:
        conn.execute(
            "INSERT INTO client_tokens(id,token_hash,enabled) VALUES(?,?,1)",
            (1, cp.hash_token(enabled_token)),
        )
        conn.execute(
            "INSERT INTO client_tokens(id,token_hash,enabled) VALUES(?,?,0)",
            (2, cp.hash_token(disabled_token)),
        )
    tracker = runtime_auth_failures.install(cp)
    with TestClient(cp.app) as client:
        assert client.get("/api/admin/runtime-auth-failures").status_code == 401
        assert client.get("/healthz").status_code == 200
        for token in (disabled_token, unknown_token, enabled_token):
            response = client.get(
                "/api/runtime/v1/probe",
                headers={
                    "Authorization": "Bearer " + token,
                    "X-Shop-Key": "shop_a",
                    "User-Agent": "qianniu-test/1.0",
                },
            )
            assert response.status_code == 401
            assert response.json()["detail"] == "客户端令牌无效"
        assert client.get("/api/runtime/v1/probe").status_code == 401
        response = client.get(
            "/api/admin/runtime-auth-failures",
            headers={"x-test-admin": "yes"},
        )
        assert response.status_code == 200
        report = response.json()
        assert report["total"] == 4
        counts = {row["reason"]: row["count"] for row in report["groups"]}
        assert counts == {
            "disabled": 1,
            "unknown": 1,
            "valid_token_other_401": 1,
            "missing": 1,
        }
        assert next(r for r in report["groups"] if r["reason"] == "disabled")["client_id"] == 2
        assert next(r for r in report["groups"] if r["reason"] == "unknown")["client_id"] is None
        assert next(r for r in report["groups"] if r["reason"] == "unknown")["credential_id"].startswith("unknown-")
        raw_report = response.text
        for secret in (enabled_token, disabled_token, unknown_token):
            assert secret not in raw_report
            assert cp.hash_token(secret) not in raw_report
        assert tracker.snapshot()["total"] == 4


def test_group_cap_and_minute_expiry(tmp_path, monkeypatch):
    cp = ControlPlane(tmp_path / "limit.db")
    tracker = runtime_auth_failures.RuntimeAuthFailureTracker(cp, max_groups=2)
    now = [1000.0]
    monkeypatch.setattr(runtime_auth_failures.time, "monotonic", lambda: now[0])
    from starlette.requests import Request

    def request(token):
        headers = [(b"authorization", ("Bearer " + token).encode())]
        return Request({"type": "http", "method": "GET", "path": "/api/runtime/v1/probe",
                        "headers": headers, "query_string": b"", "scheme": "http",
                        "server": ("test", 80)})

    for token in ("bad-a", "bad-b", "bad-c", "bad-d"):
        tracker.record(request(token))
    report = tracker.snapshot()
    assert report["total"] == 4
    assert len(report["groups"]) == 3
    assert any(row["reason"] == "overflow" and row["count"] == 2 for row in report["groups"])
    now[0] += 301
    assert tracker.snapshot()["total"] == 0
