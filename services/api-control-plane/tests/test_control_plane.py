import importlib
import os
import sys
from pathlib import Path

import pytest


@pytest.fixture()
def control_plane(tmp_path, monkeypatch):
    monkeypatch.setenv("DATABASE_PATH", str(tmp_path / "test.db"))
    monkeypatch.setenv("DATA_DIR", str(tmp_path))
    monkeypatch.setenv("APP_SECRET", "test-secret")
    monkeypatch.setenv("API_KEY_ENCRYPTION_KEY", "iV7XoPa3z4i44n5gP5gsLt5mFMQbdbGICxVMJ4K7VQk=")
    monkeypatch.setenv("COOKIE_SECURE", "false")
    monkeypatch.setenv("DISABLE_SCHEDULER", "true")
    service_dir = Path(__file__).resolve().parents[1]
    sys.path.insert(0, str(service_dir))
    sys.modules.pop("app", None)
    module = importlib.import_module("app")
    module.init_db()
    yield module
    sys.modules.pop("app", None)
    if str(service_dir) in sys.path:
        sys.path.remove(str(service_dir))


def test_latest_model_ranking(control_plane):
    items = [
        {"model": "gpt-5.4"},
        {"model": "gpt-5.5"},
        {"model": "gpt-5.6-luna"},
        {"model": "gpt-5.6-sol"},
    ]
    assert control_plane.sort_model_results_newest(items)[0]["model"] == "gpt-5.6-sol"


def test_protocol_fallback_uses_discovered_methods_first(control_plane):
    provider = {
        "protocol_order": ["chat", "responses", "legacy"],
        "model_capabilities": {
            "gpt-5.5": {
                "text_protocols": ["responses", "chat"],
                "vision_protocols": ["responses"],
            }
        },
    }
    assert control_plane.protocol_candidates(provider, "gpt-5.5", False)[:2] == ["responses", "chat"]
    assert control_plane.protocol_candidates(provider, "gpt-5.5", True) == ["responses", "chat"]


def test_chat_responses_multimodal_conversion(control_plane):
    messages = [
        {
            "role": "user",
            "content": [
                {"type": "text", "text": "识别图片"},
                {"type": "image_url", "image_url": {"url": "data:image/png;base64,AAAA"}},
            ],
        }
    ]
    responses_input = control_plane.convert_chat_to_responses_input(messages)
    assert responses_input[0]["content"][0]["type"] == "input_text"
    assert responses_input[0]["content"][1]["type"] == "input_image"
    round_trip = control_plane.convert_responses_input_to_chat(responses_input)
    assert round_trip[0]["content"][1]["type"] == "image_url"


def test_deep_test_promotes_latest_available_and_builds_backups(control_plane):
    now = control_plane.iso_now()
    with control_plane.db() as conn:
        cursor = conn.execute(
            """
            INSERT INTO providers(
                name,base_url,api_key_cipher,enabled,priority,main_text_model,
                backup_text_models_json,main_vision_model,backup_vision_models_json,
                protocol_order_json,model_capabilities_json,auto_test_enabled,
                auto_test_interval_hours,auto_test_options_json,created_at,updated_at
            ) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            """,
            (
                "测试站",
                "https://example.com/v1",
                control_plane.encrypt_secret("test-key"),
                1,
                1,
                "gpt-5.5",
                "[]",
                "gpt-5.5",
                "[]",
                '["chat"]',
                "{}",
                1,
                12,
                "{}",
                now,
                now,
            ),
        )
        provider_id = cursor.lastrowid
    provider = control_plane.get_provider(provider_id)
    result = {
        "finished_at": now,
        "model_results": [
            {
                "model": "gpt-5.5",
                "text_available": False,
                "vision_available": False,
                "successful_text_methods": [],
                "successful_vision_methods": [],
            },
            {
                "model": "gpt-5.6-luna",
                "text_available": True,
                "vision_available": False,
                "successful_text_methods": [{"kind": "responses", "elapsed": 1.2}],
                "successful_vision_methods": [],
            },
            {
                "model": "gpt-5.6-sol",
                "text_available": True,
                "vision_available": True,
                "successful_text_methods": [{"kind": "responses", "elapsed": 1.0}, {"kind": "chat", "elapsed": 1.1}],
                "successful_vision_methods": [{"kind": "responses", "elapsed": 1.5}],
            },
        ],
    }
    applied = control_plane.apply_deep_test_result(
        provider_id,
        provider,
        result,
        {**control_plane.default_deep_test_options(), "require_vision_for_full": False},
    )
    assert applied["main_text_model"] == "gpt-5.6-sol"
    assert applied["backup_text_models"] == ["gpt-5.6-luna"]
    assert applied["main_vision_model"] == "gpt-5.6-sol"
    assert applied["protocol_order"][:2] == ["responses", "chat"]


def test_api_roots_prefer_v1_and_keep_plain_fallback(control_plane):
    assert control_plane.get_api_roots("https://example.com/v1") == [
        "https://example.com/v1",
        "https://example.com",
    ]
    assert control_plane.get_api_roots("https://example.com") == [
        "https://example.com/v1",
        "https://example.com",
    ]


def test_admin_and_runtime_gateway_flow(control_plane, monkeypatch):
    from fastapi.testclient import TestClient

    monkeypatch.setattr(control_plane, "ADMIN_USERNAME", "admin")
    monkeypatch.setattr(control_plane, "ADMIN_PASSWORD", "secret")
    with TestClient(control_plane.app) as client:
        login = client.post("/api/admin/login", json={"username": "admin", "password": "secret"})
        assert login.status_code == 200

        provider = client.post(
            "/api/admin/providers",
            json={
                "name": "测试供应商",
                "base_url": "https://relay.example/v1",
                "api_key": "test-key",
                "main_text_model": "gpt-5.5",
                "main_vision_model": "gpt-5.5",
                "protocol_order": ["responses", "chat"],
            },
        )
        assert provider.status_code == 200

        token_response = client.post("/api/admin/clients", json={"name": "测试Bot"})
        assert token_response.status_code == 200
        token = token_response.json()["token"]

        runtime_config = client.get(
            "/api/runtime/v1/config",
            headers={"Authorization": "Bearer " + token},
        )
        assert runtime_config.status_code == 200
        assert runtime_config.json()["providers"][0]["main_text_model"] == "gpt-5.5"

        monkeypatch.setattr(
            control_plane,
            "dispatch_chat",
            lambda *args, **kwargs: {
                "success": True,
                "attempt": {
                    "answer": "网关正常",
                    "model": "gpt-5.5",
                    "provider_name": "测试供应商",
                    "protocol": "responses",
                    "latency_ms": 123,
                },
                "attempts": [{}],
            },
        )
        chat = client.post(
            "/v1/chat/completions",
            headers={"Authorization": "Bearer " + token},
            json={
                "model": "text-default",
                "messages": [{"role": "user", "content": "测试"}],
            },
        )
        assert chat.status_code == 200
        assert chat.json()["choices"][0]["message"]["content"] == "网关正常"
        assert chat.json()["qianniu_routing"]["protocol"] == "responses"


def test_ordinary_test_never_changes_model_pool(control_plane, monkeypatch):
    now = control_plane.iso_now()
    with control_plane.db() as conn:
        cursor = conn.execute(
            """
            INSERT INTO providers(
                name,base_url,api_key_cipher,enabled,priority,main_text_model,
                backup_text_models_json,main_vision_model,backup_vision_models_json,
                protocol_order_json,model_capabilities_json,auto_test_enabled,
                auto_test_interval_hours,auto_test_options_json,created_at,updated_at
            ) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            """,
            (
                "普通测试站",
                "https://example.com/v1",
                control_plane.encrypt_secret("test-key"),
                1,
                1,
                "gpt-5.5",
                '["gpt-5.4"]',
                "",
                "[]",
                '["chat"]',
                "{}",
                0,
                12,
                "{}",
                now,
                now,
            ),
        )
        provider_id = cursor.lastrowid

    monkeypatch.setattr(
        control_plane,
        "test_model",
        lambda provider, model, options, image: {
            "model": model,
            "text_available": True,
            "vision_available": False,
            "successful_text_methods": [{"kind": "chat", "elapsed": 0.1}],
            "successful_vision_methods": [],
            "all_text_tests": [],
            "all_vision_tests": [],
        },
    )
    control_plane.run_provider_test(
        provider_id,
        "ordinary",
        {"selected_models": ["gpt-5.6-sol"], "auto_apply_results": True},
    )
    provider = control_plane.get_provider(provider_id)
    assert provider["main_text_model"] == "gpt-5.5"
    assert provider["backup_text_models"] == ["gpt-5.4"]


def test_runtime_url_fallback_prefers_deep_test_success(control_plane, monkeypatch):
    provider = {
        "id": 1,
        "name": "测试站",
        "base_url": "https://example.com/v1",
        "api_key": "test-key",
        "model_capabilities": {
            "gpt-5.5": {
                "text_urls": {"responses": ["https://example.com/responses"]}
            }
        },
    }
    calls = []

    class Response:
        status_code = 200
        text = '{"output_text":"OK"}'
        headers = {"content-type": "application/json"}

        def json(self):
            return {"output_text": "OK"}

    def fake_request(method, url, api_key, payload=None, timeout=45):
        calls.append(url)
        return {"network_success": True, "response": Response(), "elapsed": 0.1}

    monkeypatch.setattr(control_plane, "do_request", fake_request)
    result = control_plane.upstream_call(
        provider,
        "gpt-5.5",
        "responses",
        [{"role": "user", "content": "测试"}],
        32,
        0,
        45,
    )
    assert result["success"] is True
    assert calls[0] == "https://example.com/responses"

def test_knowledge_v2_stale_runtime_write_cannot_overwrite_concurrent_web_edit(control_plane):
    service_dir = Path(__file__).resolve().parents[1]
    import importlib
    sys.modules.pop("knowledge_v2_sync", None)
    sync = importlib.import_module("knowledge_v2_sync")
    sync._cp = control_plane
    sync.init_db()

    with control_plane.db() as conn:
        cursor = conn.execute(
            "INSERT INTO client_tokens(name,token_hash,token_prefix,enabled,created_at) VALUES(?,?,?,?,?)",
            ("Knowledge V2 CAS test", "knowledge-v2-cas-test-hash", "cas-test", 1, control_plane.iso_now()),
        )
        client_id = int(cursor.lastrowid)

    initial = [{"Id": "one", "Title": "Initial", "Answer": "initial"}]
    web_edit = [{"Id": "one", "Title": "Edited on web", "Answer": "new web answer"}]
    stale_windows = [{"Id": "one", "Title": "Stale Windows", "Answer": "old local answer"}]

    seeded = sync._save(client_id, initial, "windows", expected_revision=0)
    assert seeded["revision"] == 1

    # A runtime request read revision 1. A web edit commits revision 2 before
    # that runtime request reaches its write phase.
    web_saved = sync._save(client_id, web_edit, "web")
    assert web_saved["revision"] == 2

    stale_result = sync._save(client_id, stale_windows, "windows", expected_revision=1)
    assert stale_result["revision"] == 2
    assert stale_result["records"] == web_edit
    assert sync._state(client_id)["records"] == web_edit
    sys.modules.pop("knowledge_v2_sync", None)
