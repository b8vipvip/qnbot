from __future__ import annotations

from types import SimpleNamespace

import runtime_routing_guard as guard


class FakeControlPlane:
    def __init__(self):
        self.logged = []

    @staticmethod
    def messages_have_image(messages):
        return False

    @staticmethod
    def list_providers(include_secret=False, enabled_only=False):
        return [
            {
                "id": 1,
                "name": "provider-a",
                "api_key": "test",
                "main_text_model": "main-model",
                "backup_text_models": ["backup-model"],
            }
        ]

    @staticmethod
    def model_candidates(provider, requested_model, vision):
        return ["main-model", "backup-model"]

    @staticmethod
    def protocol_candidates(provider, model, vision):
        return ["chat", "responses", "legacy"]

    def log_request(self, client_name, kind, requested_model, attempt):
        self.logged.append((client_name, kind, requested_model, attempt["model"], attempt["protocol"]))


class TwoProviderControlPlane(FakeControlPlane):
    @staticmethod
    def list_providers(include_secret=False, enabled_only=False):
        return [
            {"id": 1, "name": "relay-a", "api_key": "a"},
            {"id": 2, "name": "relay-b", "api_key": "b"},
        ]

    @staticmethod
    def model_candidates(provider, requested_model, vision):
        suffix = "a" if provider["id"] == 1 else "b"
        return [f"main-{suffix}", f"backup-{suffix}"]


def test_route_order_gives_backup_model_a_chance_before_secondary_protocol():
    cp = FakeControlPlane()
    routes = guard._build_routes(cp, "text-default", [{"role": "user", "content": "hi"}])
    assert [(model, protocol) for _, model, protocol in routes[:4]] == [
        ("main-model", "chat"),
        ("backup-model", "chat"),
        ("main-model", "responses"),
        ("backup-model", "responses"),
    ]


def test_route_order_interleaves_relays_before_secondary_protocols():
    cp = TwoProviderControlPlane()
    routes = guard._build_routes(cp, "text-default", [{"role": "user", "content": "hi"}])
    assert [(provider["name"], model, protocol) for provider, model, protocol in routes[:6]] == [
        ("relay-a", "main-a", "chat"),
        ("relay-b", "main-b", "chat"),
        ("relay-a", "backup-a", "chat"),
        ("relay-b", "backup-b", "chat"),
        ("relay-a", "main-a", "responses"),
        ("relay-b", "main-b", "responses"),
    ]


def test_dispatch_rotates_to_backup_model_with_small_per_attempt_timeout(monkeypatch):
    cp = FakeControlPlane()
    calls = []

    def fake_call(control_plane, provider, model, protocol, messages, max_tokens, temperature, timeout):
        calls.append((model, protocol, timeout))
        if model == "backup-model" and protocol == "chat":
            return {
                "provider_id": 1,
                "provider_name": "provider-a",
                "model": model,
                "protocol": protocol,
                "url": "https://example.invalid/v1/chat/completions",
                "latency_ms": 10,
                "success": True,
                "answer": "ok",
            }
        return {
            "provider_id": 1,
            "provider_name": "provider-a",
            "model": model,
            "protocol": protocol,
            "url": "https://example.invalid/v1/chat/completions",
            "latency_ms": 10,
            "success": False,
            "error": "timeout",
        }

    monkeypatch.setattr(guard, "fast_upstream_call", fake_call)
    result = guard.dispatch_chat(
        cp,
        "client",
        "text-default",
        [{"role": "user", "content": "hi"}],
        128,
        0.1,
        120,
    )

    assert result["success"] is True
    assert result["routing_profile"] == "realtime"
    assert calls[:2][0][:2] == ("main-model", "chat")
    assert calls[:2][1][:2] == ("backup-model", "chat")
    assert all(timeout <= guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS for _, _, timeout in calls)
    assert len(calls) == 2


def test_dispatch_protocol_scope_never_calls_responses_or_legacy(monkeypatch):
    cp = FakeControlPlane()
    calls = []

    def fake_call(control_plane, provider, model, protocol, messages, max_tokens, temperature, timeout):
        calls.append((model, protocol, timeout))
        return {
            "provider_id": provider["id"],
            "provider_name": provider["name"],
            "model": model,
            "protocol": protocol,
            "url": "https://example.invalid/v1/chat/completions",
            "latency_ms": 10,
            "success": False,
            "error": "timeout",
        }

    monkeypatch.setattr(guard, "fast_upstream_call", fake_call)
    result = guard.dispatch_chat(
        cp,
        "client",
        "text-default",
        [{"role": "user", "content": "hi"}],
        128,
        0.1,
        120,
        allowed_protocols={"chat"},
    )

    assert result["success"] is False
    assert [(model, protocol) for model, protocol, _ in calls] == [
        ("main-model", "chat"),
        ("backup-model", "chat"),
    ]


def test_heavy_structured_request_uses_long_background_attempt_timeout(monkeypatch):
    cp = FakeControlPlane()
    calls = []

    def fake_call(control_plane, provider, model, protocol, messages, max_tokens, temperature, timeout):
        calls.append((model, protocol, timeout))
        return {
            "provider_id": 1,
            "provider_name": "provider-a",
            "model": model,
            "protocol": protocol,
            "url": "https://example.invalid/v1/chat/completions",
            "latency_ms": 25000,
            "success": True,
            "answer": "ok",
        }

    monkeypatch.setattr(guard, "fast_upstream_call", fake_call)
    result = guard.dispatch_chat(
        cp,
        "client",
        "text-default",
        [{"role": "user", "content": "optimize a large knowledge batch"}],
        5000,
        0.05,
        300,
    )

    assert result["success"] is True
    assert result["routing_profile"] == "background"
    assert calls[0][2] == guard.BACKGROUND_ATTEMPT_TIMEOUT_SECONDS
    assert calls[0][2] > guard.RUNTIME_ATTEMPT_TIMEOUT_SECONDS
    assert cp.logged[0][1] == "text-background"


def test_dispatch_rotates_to_second_relay_before_secondary_protocol(monkeypatch):
    cp = TwoProviderControlPlane()
    calls = []

    def fake_call(control_plane, provider, model, protocol, messages, max_tokens, temperature, timeout):
        calls.append((provider["name"], model, protocol, timeout))
        success = provider["name"] == "relay-b" and model == "main-b" and protocol == "chat"
        return {
            "provider_id": provider["id"],
            "provider_name": provider["name"],
            "model": model,
            "protocol": protocol,
            "url": "https://example.invalid/v1/chat/completions",
            "latency_ms": 10,
            "success": success,
            "answer": "ok" if success else "",
            "error": "timeout" if not success else "",
        }

    monkeypatch.setattr(guard, "fast_upstream_call", fake_call)
    result = guard.dispatch_chat(
        cp,
        "client",
        "text-default",
        [{"role": "user", "content": "hi"}],
        128,
        0.1,
        120,
    )

    assert result["success"] is True
    assert [(provider, model, protocol) for provider, model, protocol, _ in calls] == [
        ("relay-a", "main-a", "chat"),
        ("relay-b", "main-b", "chat"),
    ]


def test_install_replaces_app_dispatcher():
    cp = SimpleNamespace(dispatch_chat=lambda *args, **kwargs: {"old": True})
    guard.install(cp)
    assert callable(cp.dispatch_chat)
    assert cp.dispatch_chat.__name__ == "guarded_dispatch_chat"


def test_terminal_provider_failure_skips_secondary_protocol_for_same_provider(monkeypatch):
    cp = FakeControlPlane()
    calls = []

    def fake_call(control_plane, provider, model, protocol, messages, max_tokens, temperature, timeout):
        calls.append((provider["id"], model, protocol))
        return {
            "provider_id": provider["id"],
            "provider_name": provider["name"],
            "model": model,
            "protocol": protocol,
            "url": "https://example.invalid",
            "latency_ms": 10,
            "success": False,
            "error": "timeout",
            "terminal_provider_failure": True,
        }

    monkeypatch.setattr(guard, "fast_upstream_call", fake_call)
    result = guard.dispatch_chat(
        cp,
        "client",
        "text-default",
        [{"role": "user", "content": "hi"}],
        128,
        0.1,
        120,
    )

    assert result["success"] is False
    assert len(calls) == 1
    assert calls[0] == (1, "main-model", "chat")


def test_realtime_budget_resolver_is_route_scoped(monkeypatch):
    cp = FakeControlPlane()
    original_resolver = getattr(guard, "_realtime_total_budget_resolver", None)
    seen = []

    def resolver(routes, default_budget):
        seen.append((len(routes), default_budget))
        return 90

    def fake_call(control_plane, provider, model, protocol, messages, max_tokens, temperature, timeout):
        return {
            "provider_id": provider["id"],
            "provider_name": provider["name"],
            "model": model,
            "protocol": protocol,
            "url": "https://example.invalid",
            "latency_ms": 10,
            "success": True,
            "answer": "ok",
        }

    try:
        guard._realtime_total_budget_resolver = resolver
        monkeypatch.setattr(guard, "fast_upstream_call", fake_call)
        result = guard.dispatch_chat(
            cp,
            "client",
            "text-default",
            [{"role": "user", "content": "hi"}],
            128,
            0.1,
            90,
        )
        assert result["success"] is True
        assert seen and seen[0][1] == guard.RUNTIME_TOTAL_BUDGET_SECONDS
    finally:
        if original_resolver is None:
            try:
                delattr(guard, "_realtime_total_budget_resolver")
            except AttributeError:
                pass
        else:
            guard._realtime_total_budget_resolver = original_resolver
