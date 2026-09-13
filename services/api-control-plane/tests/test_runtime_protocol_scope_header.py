from types import SimpleNamespace

from fastapi import FastAPI
from fastapi.testclient import TestClient
from starlette.concurrency import run_in_threadpool

import runtime_routing_guard


def _make_probe_app(monkeypatch):
    observed = []

    def fake_dispatch(
        control_plane,
        client_name,
        requested_model,
        messages,
        max_tokens,
        temperature,
        timeout,
        *,
        allowed_protocols=None,
    ):
        observed.append(None if allowed_protocols is None else sorted(allowed_protocols))
        return {"success": True}

    monkeypatch.setattr(runtime_routing_guard, "dispatch_chat", fake_dispatch)
    control_plane = SimpleNamespace(app=FastAPI())
    runtime_routing_guard.install(control_plane)

    @control_plane.app.post("/probe")
    async def probe():
        result = await run_in_threadpool(
            control_plane.dispatch_chat,
            "test-client",
            "text-default",
            [{"role": "user", "content": "hello"}],
            220,
            0.15,
            15,
        )
        return result

    return control_plane, observed


def test_internal_header_scopes_threadpool_dispatch_and_resets(monkeypatch):
    control_plane, observed = _make_probe_app(monkeypatch)

    with TestClient(control_plane.app) as client:
        scoped = client.post(
            "/probe",
            headers={runtime_routing_guard.REQUEST_ALLOWED_PROTOCOLS_HEADER: "chat"},
        )
        default = client.post("/probe")

    assert scoped.status_code == 200
    assert default.status_code == 200
    assert observed == [["chat"], None]


def test_internal_header_normalizes_known_protocols_and_invalid_scope_fails_closed(monkeypatch):
    control_plane, observed = _make_probe_app(monkeypatch)

    with TestClient(control_plane.app) as client:
        normalized = client.post(
            "/probe",
            headers={runtime_routing_guard.REQUEST_ALLOWED_PROTOCOLS_HEADER: " CHAT, responses,unknown "},
        )
        invalid = client.post(
            "/probe",
            headers={runtime_routing_guard.REQUEST_ALLOWED_PROTOCOLS_HEADER: "unknown-only"},
        )

    assert normalized.status_code == 200
    assert invalid.status_code == 200
    assert observed == [["chat", "responses"], []]


def test_install_adds_protocol_scope_middleware_only_once(monkeypatch):
    control_plane, _ = _make_probe_app(monkeypatch)
    middleware_count = len(control_plane.app.user_middleware)

    runtime_routing_guard.install(control_plane)

    assert len(control_plane.app.user_middleware) == middleware_count
