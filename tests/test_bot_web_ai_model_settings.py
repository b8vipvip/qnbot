from __future__ import annotations

import importlib.util
import sqlite3
import sys
from contextlib import contextmanager
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "services" / "api-control-plane" / "bot_web_ai_model_settings.py"
WINDOWS_SYNC_PATH = ROOT / "src" / "Bot" / "ChromeNs" / "BotWebAiModelSettingsSyncService.cs"
WINDOWS_PROPS_PATH = ROOT / "src" / "Bot" / "Directory.Build.props"
BOOTSTRAP_PATH = ROOT / "services" / "api-control-plane" / "bootstrap.py"
DOCKERFILE_PATH = ROOT / "services" / "api-control-plane" / "Dockerfile"
PAGE_PATH = ROOT / "services" / "api-control-plane" / "static" / "bot-web.html"
WEB_JS_PATH = ROOT / "services" / "api-control-plane" / "static" / "bot-web-ai-model-settings.js"
LOADER_JS_PATH = ROOT / "services" / "api-control-plane" / "static" / "bot-web-bot-enabled.js"
HAS_FASTAPI = importlib.util.find_spec("fastapi") is not None
needs_server_deps = pytest.mark.skipif(not HAS_FASTAPI, reason="server dependencies are not installed in Windows static CI")


def load_module():
    name = "bot_web_ai_model_settings_under_test"
    spec = importlib.util.spec_from_file_location(name, MODULE_PATH)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


class FakeControlPlane:
    def __init__(self, path: Path):
        self.path = path

    @contextmanager
    def db(self):
        conn = sqlite3.connect(str(self.path))
        conn.row_factory = sqlite3.Row
        try:
            yield conn
            conn.commit()
        except Exception:
            conn.rollback()
            raise
        finally:
            conn.close()

    @staticmethod
    def iso_now() -> str:
        return "2026-09-18T04:00:00+00:00"


class FakeConsole:
    @staticmethod
    def _is_online(value):
        return bool(value)


def prepare(module, tmp_path: Path):
    cp = FakeControlPlane(tmp_path / "ai-settings.db")
    with cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE client_tokens (id INTEGER PRIMARY KEY);
            CREATE TABLE bot_client_state (client_id INTEGER PRIMARY KEY, last_seen_at TEXT);
            INSERT INTO client_tokens(id) VALUES(1),(2);
            INSERT INTO bot_client_state(client_id,last_seen_at)
            VALUES(1,'2026-09-18T04:00:00+00:00'),(2,'2026-09-18T04:00:00+00:00');
            """
        )
    module._cp = cp
    module._console = FakeConsole()
    module.init_db()
    return cp


def endpoint(endpoint_id="ep-1", **changes):
    value = {
        "id": endpoint_id,
        "name": "主接口",
        "enabled": True,
        "text_model": "gpt-text",
        "vision_model": "gpt-vision",
        "supports_vision": True,
        "max_image_size_mb": 5,
        "vision_timeout_seconds": 45,
        "system_prompt": "你是店铺客服。",
        "priority": 1,
        "weight": 1,
        "timeout_seconds": 35,
        "retry_count": 1,
        "last_status": "可用：收发验证通过",
        "last_latency_ms": 321,
        "last_test_time": "2026-09-18T11:59:00+08:00",
    }
    value.update(changes)
    return value


@needs_server_deps
def test_first_runtime_sync_adopts_windows_safe_fields_and_strips_secrets(tmp_path):
    module = load_module()
    prepare(module, tmp_path)
    raw = endpoint()
    raw.update(
        {
            "base_url": "https://secret-endpoint.example/v1",
            "api_key": "sk-never-store-me",
            "authorization": "Bearer never-store-me",
            "cookie": "sid=secret",
            "password": "secret",
        }
    )
    result = module.runtime_sync_ai_model_settings(
        module.RuntimeAiModelSettingsSyncInput(current_settings={"endpoints": [raw]}),
        client={"id": 1},
    )

    assert result["revision"] == 1
    assert result["applied_revision"] == 1
    snapshot = module._snapshot(1)
    assert snapshot["desired"]["endpoints"][0]["text_model"] == "gpt-text"
    assert snapshot["current"]["endpoints"][0]["last_latency_ms"] == 321

    with module._cp.db() as conn:
        row = conn.execute(
            "SELECT desired_settings_json,current_settings_json FROM bot_web_ai_model_settings WHERE client_id=1"
        ).fetchone()
    stored = (row["desired_settings_json"] + row["current_settings_json"]).lower()
    for forbidden in ("secret-endpoint", "sk-never-store-me", "bearer never-store-me", "sid=secret", '"password"'):
        assert forbidden not in stored


@needs_server_deps
def test_web_change_waits_for_windows_actual_confirmation(tmp_path):
    module = load_module()
    prepare(module, tmp_path)
    original = {"endpoints": [endpoint()]}
    module.runtime_sync_ai_model_settings(
        module.RuntimeAiModelSettingsSyncInput(current_settings=original),
        client={"id": 1},
    )

    changed = module.AiModelSettingsInput(
        endpoints=[
            module.AiEndpointInput(
                id="ep-1",
                name="主接口",
                enabled=True,
                text_model="gpt-text-2",
                vision_model="gpt-vision-2",
                supports_vision=True,
                max_image_size_mb=8,
                vision_timeout_seconds=60,
                system_prompt="新的系统提示词",
                priority=2,
                weight=3,
                timeout_seconds=50,
                retry_count=2,
            )
        ]
    )
    waiting = module.put_ai_model_settings(changed, client={"id": 1})
    assert waiting["revision"] == 2
    assert waiting["applied_revision"] == 1

    response = module.runtime_sync_ai_model_settings(
        module.RuntimeAiModelSettingsSyncInput(current_settings=original),
        client={"id": 1},
    )
    desired = response["desired_settings"]["endpoints"][0]
    assert desired["text_model"] == "gpt-text-2"
    assert desired["vision_model"] == "gpt-vision-2"
    assert desired["system_prompt"] == "新的系统提示词"
    assert response["applied_revision"] == 1

    applied_current = dict(desired)
    applied_current.update(
        {
            "last_status": "可用",
            "last_latency_ms": 88,
            "last_test_time": "2026-09-18T12:00:00+08:00",
        }
    )
    applied = module.runtime_sync_ai_model_settings(
        module.RuntimeAiModelSettingsSyncInput(current_settings={"endpoints": [applied_current]}),
        client={"id": 1},
    )
    assert applied["applied_revision"] == 2
    assert module._snapshot(1)["current"]["endpoints"][0]["last_latency_ms"] == 88


@needs_server_deps
def test_windows_local_endpoint_topology_is_authoritative(tmp_path):
    module = load_module()
    prepare(module, tmp_path)
    module.runtime_sync_ai_model_settings(
        module.RuntimeAiModelSettingsSyncInput(current_settings={"endpoints": [endpoint("ep-1")]}),
        client={"id": 1},
    )
    changed = module.AiModelSettingsInput(
        endpoints=[
            module.AiEndpointInput(
                id="ep-1",
                name="Web改名",
                text_model="web-model",
                vision_model="",
                supports_vision=False,
            )
        ]
    )
    module.put_ai_model_settings(changed, client={"id": 1})

    local = {
        "endpoints": [
            endpoint("ep-1", name="Windows旧名", text_model="old-model"),
            endpoint("ep-2", name="Windows新增", text_model="local-new-model", priority=2),
        ]
    }
    result = module.runtime_sync_ai_model_settings(
        module.RuntimeAiModelSettingsSyncInput(current_settings=local),
        client={"id": 1},
    )
    desired = {item["id"]: item for item in result["desired_settings"]["endpoints"]}
    assert desired["ep-1"]["name"] == "Web改名"
    assert desired["ep-1"]["text_model"] == "web-model"
    assert desired["ep-2"]["name"] == "Windows新增"
    assert desired["ep-2"]["text_model"] == "local-new-model"


@needs_server_deps
def test_web_cannot_add_remove_endpoints_or_create_invalid_active_route(tmp_path):
    from fastapi import HTTPException

    module = load_module()
    prepare(module, tmp_path)
    module.runtime_sync_ai_model_settings(
        module.RuntimeAiModelSettingsSyncInput(current_settings={"endpoints": [endpoint()]}),
        client={"id": 1},
    )

    with pytest.raises(HTTPException) as topology:
        module.put_ai_model_settings(module.AiModelSettingsInput(endpoints=[]), client={"id": 1})
    assert topology.value.status_code == 409
    assert "不允许新增或删除接口" in topology.value.detail

    invalid = module.AiModelSettingsInput(
        endpoints=[
            module.AiEndpointInput(
                id="ep-1",
                name="主接口",
                enabled=True,
                text_model="",
                supports_vision=False,
            )
        ]
    )
    with pytest.raises(HTTPException) as invalid_route:
        module.put_ai_model_settings(invalid, client={"id": 1})
    assert invalid_route.value.status_code == 422
    assert "文本模型" in invalid_route.value.detail


@needs_server_deps
def test_state_is_isolated_by_client_id(tmp_path):
    module = load_module()
    prepare(module, tmp_path)
    module.runtime_sync_ai_model_settings(
        module.RuntimeAiModelSettingsSyncInput(current_settings={"endpoints": [endpoint("one", text_model="model-one")]}),
        client={"id": 1},
    )
    module.runtime_sync_ai_model_settings(
        module.RuntimeAiModelSettingsSyncInput(current_settings={"endpoints": [endpoint("two", text_model="model-two")]}),
        client={"id": 2},
    )
    assert module._snapshot(1)["desired"]["endpoints"][0]["text_model"] == "model-one"
    assert module._snapshot(2)["desired"]["endpoints"][0]["text_model"] == "model-two"


def test_windows_sync_whitelists_fields_and_never_reads_or_assigns_endpoint_secrets():
    text = WINDOWS_SYNC_PATH.read_text(encoding="utf-8-sig")
    props = WINDOWS_PROPS_PATH.read_text(encoding="utf-8-sig")
    assert "/api/runtime/v1/bot-web/ai-model-settings/sync" in text
    assert "AiEndpointStore.GetEndpoints()" in text
    assert "AiEndpointStore.SaveEndpoints(endpoints)" in text
    assert '["text_model"]' in text
    assert '["vision_model"]' in text
    assert '["supports_vision"]' in text
    assert '["system_prompt"]' in text
    assert '["timeout_seconds"]' in text
    assert '["retry_count"]' in text
    assert '["last_status"]' in text
    assert '["last_latency_ms"]' in text
    assert "ChromeNs\\BotWebAiModelSettingsSyncService.cs" in props

    build_current = text.split("private static JObject BuildCurrentSettings()", 1)[1].split("private static void ApplyDesiredSettings", 1)[0]
    apply_desired = text.split("private static void ApplyDesiredSettings", 1)[1]
    assert "BaseUrl" not in build_current
    assert "ApiKey" not in build_current
    assert "endpoint.BaseUrl =" not in apply_desired
    assert "endpoint.ApiKey =" not in apply_desired


def test_mobile_ui_exposes_safe_fields_and_no_secret_inputs():
    page = PAGE_PATH.read_text(encoding="utf-8")
    script = WEB_JS_PATH.read_text(encoding="utf-8")
    loader = LOADER_JS_PATH.read_text(encoding="utf-8")
    assert "AI 模型设置" in page
    assert "BaseUrl 与 API Key 始终只保存在 Windows 本机" in page
    assert "aiEndpointList" in page
    assert "saveAiModelSettingsBtn" in page
    assert "/api/bot-web/ai-model-settings" in script
    assert "text_model" in script
    assert "vision_model" in script
    assert "supports_vision" in script
    assert "system_prompt" in script
    assert "timeout_seconds" in script
    assert "retry_count" in script
    assert "Windows 连接测试" in script
    lowered = script.lower()
    for forbidden_field in (
        'data-ai-field="base_url"',
        'data-ai-field="api_key"',
        'data-ai-field="authorization"',
        'data-ai-field="cookie"',
        'data-ai-field="password"',
    ):
        assert forbidden_field not in lowered
    for forbidden_payload_key in ('base_url:', 'api_key:', 'authorization:', 'cookie:', 'password:'):
        assert forbidden_payload_key not in lowered
    assert "/static/bot-web-ai-model-settings.js?v=1" in loader
    assert 'src="/static/bot-web-bot-enabled.js?v=3"' in page


def test_bootstrap_container_and_windows_build_package_ai_model_sync():
    bootstrap = BOOTSTRAP_PATH.read_text(encoding="utf-8-sig")
    dockerfile = DOCKERFILE_PATH.read_text(encoding="utf-8")
    assert "import bot_web_ai_model_settings" in bootstrap
    assert "bot_web_ai_model_settings.install(control_plane, bot_web_console)" in bootstrap
    assert "bot_web_ai_model_settings.init_db()" in bootstrap
    assert "bot_web_ai_model_settings.py" in dockerfile
