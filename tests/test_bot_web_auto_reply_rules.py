from __future__ import annotations

import importlib.util
import json
import sqlite3
import sys
from contextlib import contextmanager
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "services" / "api-control-plane" / "bot_web_auto_reply_rules.py"
WINDOWS_SYNC_PATH = ROOT / "src" / "Bot" / "ChromeNs" / "BotWebAutoReplyRulesSyncService.cs"
WINDOWS_PROPS_PATH = ROOT / "src" / "Bot" / "Directory.Build.props"
BOOTSTRAP_PATH = ROOT / "services" / "api-control-plane" / "bootstrap.py"
DOCKERFILE_PATH = ROOT / "services" / "api-control-plane" / "Dockerfile"
WEB_JS_PATH = ROOT / "services" / "api-control-plane" / "static" / "bot-web-auto-reply-rules.js"
LOADER_JS_PATH = ROOT / "services" / "api-control-plane" / "static" / "bot-web-bot-enabled.js"
HAS_FASTAPI = importlib.util.find_spec("fastapi") is not None
needs_server_deps = pytest.mark.skipif(not HAS_FASTAPI, reason="server dependencies are not installed in Windows static CI")


def load_module():
    name = "bot_web_auto_reply_rules_under_test"
    spec = importlib.util.spec_from_file_location(name, MODULE_PATH)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    # Match normal Python import semantics. Pydantic resolves postponed annotations
    # through sys.modules while the model classes are being constructed.
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
        return "2026-09-04T10:00:00+00:00"


class FakeConsole:
    @staticmethod
    def _is_online(value):
        return bool(value)


def prepare_base_db(tmp_path: Path):
    cp = FakeControlPlane(tmp_path / "rules.db")
    with cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE client_tokens (id INTEGER PRIMARY KEY);
            CREATE TABLE bot_client_state (client_id INTEGER PRIMARY KEY, last_seen_at TEXT);
            INSERT INTO client_tokens(id) VALUES(1),(2);
            INSERT INTO bot_client_state(client_id,last_seen_at) VALUES(1,'2026-09-04T10:00:00+00:00');
            """
        )
    return cp


def prepare(module, tmp_path: Path):
    cp = prepare_base_db(tmp_path)
    module._cp = cp
    module._console = FakeConsole()
    module.init_db()
    return cp


def local_rules(module, **changes):
    data = dict(module.DEFAULT_RULE_SETTINGS)
    data.update(changes)
    return data


def v1_rules(module, **changes):
    data = {key: module.DEFAULT_RULE_SETTINGS[key] for key in module.BASE_RULE_KEYS}
    data.update(changes)
    return data


@needs_server_deps
def test_first_runtime_sync_adopts_windows_rules_without_overwrite(tmp_path):
    module = load_module()
    prepare(module, tmp_path)
    current = local_rules(
        module,
        manual_handoff_keywords="退款,我要真人",
        work_start_time="10:30",
        work_end_time="23:15",
        off_hours_reply_mode="固定预设答案",
        off_hours_fixed_text="当前已下班，请明天联系。",
        order_placed_reply_enabled=True,
        order_placed_reply_mode="固定预设答案",
        order_placed_reply_text="订单 {订单号} 已收到。",
        order_placed_api_timeout_seconds=19,
        order_placed_reply_delay_seconds=0,
    )

    result = module.runtime_sync_auto_reply_rules(
        module.RuntimeAutoReplyRuleSyncInput(current_settings=current),
        client={"id": 1},
    )

    assert result["revision"] == 1
    assert result["applied_revision"] == 1
    assert result["settings_schema_version"] == 2
    assert result["desired_settings"] == current
    snapshot = module._snapshot(1)
    assert snapshot["desired"] == current
    assert snapshot["current"] == current
    assert snapshot["order_rules_ready"] is True


@needs_server_deps
def test_web_change_waits_for_actual_windows_confirmation(tmp_path):
    module = load_module()
    prepare(module, tmp_path)
    original = local_rules(module)
    module.runtime_sync_auto_reply_rules(
        module.RuntimeAutoReplyRuleSyncInput(current_settings=original),
        client={"id": 1},
    )

    changed_input = module.AutoReplyRuleSettingsInput(
        manual_handoff_keywords="退款,投诉,联系人工",
        work_hours_enabled=True,
        work_start_time="08:30",
        work_end_time="20:45",
        off_hours_reply_mode="固定预设答案",
        off_hours_fixed_text="人工已下班，请在工作时间联系我们。",
        order_placed_reply_enabled=True,
        order_placed_reply_mode="固定预设答案",
        order_placed_reply_text="已收到订单 {订单号}。",
        order_placed_api_timeout_seconds=15,
        order_placed_reply_delay_seconds=0,
    )
    waiting = module.put_auto_reply_rules(changed_input, client={"id": 1})
    assert waiting["revision"] == 2
    assert waiting["applied_revision"] == 1

    response = module.runtime_sync_auto_reply_rules(
        module.RuntimeAutoReplyRuleSyncInput(current_settings=original),
        client={"id": 1},
    )
    assert response["revision"] == 2
    assert response["applied_revision"] == 1
    desired = response["desired_settings"]
    assert desired["manual_handoff_keywords"] == "退款,投诉,联系人工"
    assert desired["work_start_time"] == "08:30"
    assert desired["order_placed_reply_enabled"] is True
    assert desired["order_placed_reply_text"] == "已收到订单 {订单号}。"

    applied = module.runtime_sync_auto_reply_rules(
        module.RuntimeAutoReplyRuleSyncInput(current_settings=desired),
        client={"id": 1},
    )
    assert applied["applied_revision"] == 2
    assert module._snapshot(1)["applied_revision"] == 2


@needs_server_deps
def test_v1_existing_row_adopts_new_order_fields_without_overwriting_pending_v1_edit(tmp_path):
    module = load_module()
    prepare(module, tmp_path)
    old_current = v1_rules(module, manual_handoff_keywords="退款")
    first = module.runtime_sync_auto_reply_rules(
        module.RuntimeAutoReplyRuleSyncInput(current_settings=old_current),
        client={"id": 1},
    )
    assert first["settings_schema_version"] == 1
    assert module._snapshot(1)["order_rules_ready"] is False

    pending = module.put_auto_reply_rules(
        module.AutoReplyRuleSettingsInput(manual_handoff_keywords="退款,投诉"),
        client={"id": 1},
    )
    assert pending["revision"] == 2
    assert pending["applied_revision"] == 1

    upgraded_current = local_rules(
        module,
        manual_handoff_keywords="退款",
        order_placed_reply_enabled=True,
        order_placed_reply_mode="调用HTTP接口",
        order_placed_reply_text="接口失败兜底 {订单号}",
        order_placed_api_timeout_seconds=27,
        order_placed_reply_delay_seconds=0,
    )
    migrated = module.runtime_sync_auto_reply_rules(
        module.RuntimeAutoReplyRuleSyncInput(current_settings=upgraded_current),
        client={"id": 1},
    )

    assert migrated["settings_schema_version"] == 2
    assert migrated["revision"] == 2
    assert migrated["applied_revision"] == 1
    desired = migrated["desired_settings"]
    # Existing Web intent survives the schema migration.
    assert desired["manual_handoff_keywords"] == "退款,投诉"
    # Newly introduced fields are adopted from Windows actual local values.
    assert desired["order_placed_reply_enabled"] is True
    assert desired["order_placed_reply_mode"] == "调用HTTP接口"
    assert desired["order_placed_reply_text"] == "接口失败兜底 {订单号}"
    assert desired["order_placed_api_timeout_seconds"] == 27
    assert desired["order_placed_reply_delay_seconds"] == 0

    applied = module.runtime_sync_auto_reply_rules(
        module.RuntimeAutoReplyRuleSyncInput(current_settings=desired),
        client={"id": 1},
    )
    assert applied["applied_revision"] == 2
    assert module._snapshot(1)["order_rules_ready"] is True


@needs_server_deps
def test_v1_web_client_cannot_set_order_fields_before_windows_adoption(tmp_path):
    from fastapi import HTTPException

    module = load_module()
    prepare(module, tmp_path)
    module.runtime_sync_auto_reply_rules(
        module.RuntimeAutoReplyRuleSyncInput(current_settings=v1_rules(module)),
        client={"id": 1},
    )
    with pytest.raises(HTTPException) as blocked:
        module.put_auto_reply_rules(
            module.AutoReplyRuleSettingsInput(order_placed_reply_enabled=True),
            client={"id": 1},
        )
    assert blocked.value.status_code == 409
    assert "新版 Windows Bot" in blocked.value.detail


@needs_server_deps
def test_schema_column_is_added_to_existing_v1_database(tmp_path):
    module = load_module()
    cp = prepare_base_db(tmp_path)
    module._cp = cp
    module._console = FakeConsole()
    with cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE bot_web_auto_reply_rules (
                client_id INTEGER PRIMARY KEY,
                desired_settings_json TEXT NOT NULL DEFAULT '{}',
                current_settings_json TEXT NOT NULL DEFAULT '{}',
                revision INTEGER NOT NULL DEFAULT 0,
                applied_revision INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL
            );
            """
        )
    module.init_db()
    with cp.db() as conn:
        columns = {row[1] for row in conn.execute("PRAGMA table_info(bot_web_auto_reply_rules)")}
    assert "settings_schema_version" in columns


@needs_server_deps
def test_rule_state_is_isolated_by_client_id(tmp_path):
    module = load_module()
    prepare(module, tmp_path)
    one = local_rules(module, manual_handoff_keywords="店铺一", order_placed_reply_text="店铺一订单回复")
    two = local_rules(module, manual_handoff_keywords="店铺二", order_placed_reply_text="店铺二订单回复")
    module.runtime_sync_auto_reply_rules(module.RuntimeAutoReplyRuleSyncInput(current_settings=one), client={"id": 1})
    module.runtime_sync_auto_reply_rules(module.RuntimeAutoReplyRuleSyncInput(current_settings=two), client={"id": 2})
    assert module._snapshot(1)["desired"]["manual_handoff_keywords"] == "店铺一"
    assert module._snapshot(2)["desired"]["manual_handoff_keywords"] == "店铺二"
    assert module._snapshot(1)["desired"]["order_placed_reply_text"] == "店铺一订单回复"
    assert module._snapshot(2)["desired"]["order_placed_reply_text"] == "店铺二订单回复"


@needs_server_deps
def test_validation_rejects_bad_time_empty_fixed_reply_and_nonzero_order_delay(tmp_path):
    from fastapi import HTTPException

    module = load_module()
    prepare(module, tmp_path)
    current = local_rules(module)
    module.runtime_sync_auto_reply_rules(
        module.RuntimeAutoReplyRuleSyncInput(current_settings=current),
        client={"id": 1},
    )
    with pytest.raises(HTTPException) as bad_time:
        module.put_auto_reply_rules(
            module.AutoReplyRuleSettingsInput(work_start_time="99:00"),
            client={"id": 1},
        )
    assert bad_time.value.status_code == 422

    with pytest.raises(HTTPException) as empty_reply:
        module.put_auto_reply_rules(
            module.AutoReplyRuleSettingsInput(
                off_hours_reply_mode="固定预设答案",
                off_hours_fixed_text="",
            ),
            client={"id": 1},
        )
    assert empty_reply.value.status_code == 422

    with pytest.raises(HTTPException) as empty_order_reply:
        module.put_auto_reply_rules(
            module.AutoReplyRuleSettingsInput(
                order_placed_reply_enabled=True,
                order_placed_reply_mode="固定预设答案",
                order_placed_reply_text="",
            ),
            client={"id": 1},
        )
    assert empty_order_reply.value.status_code == 422

    bad_delay = dict(current)
    bad_delay["order_placed_reply_delay_seconds"] = 5
    with pytest.raises(HTTPException) as delay_error:
        module.runtime_sync_auto_reply_rules(
            module.RuntimeAutoReplyRuleSyncInput(current_settings=bad_delay),
            client={"id": 1},
        )
    assert delay_error.value.status_code == 422
    assert "delay 必须为 0" in delay_error.value.detail


def test_windows_sync_only_mutates_whitelisted_non_secret_rule_fields():
    text = WINDOWS_SYNC_PATH.read_text(encoding="utf-8-sig")
    props = WINDOWS_PROPS_PATH.read_text(encoding="utf-8-sig")
    assert "/api/runtime/v1/bot-web/auto-reply-rules/sync" in text
    assert "BuildCurrentSettings()" in text
    assert "BotFeatureStore.GetAutoReplyRules()" in text
    assert "BotFeatureStore.SaveAutoReplyRules(cfg)" in text
    assert "cfg.ManualKeywords =" in text
    assert "cfg.NoAutoReplyKeywords =" in text
    assert "cfg.EnableWorkHours =" in text
    assert "cfg.WorkStartTime =" in text
    assert "cfg.WorkEndTime =" in text
    assert "cfg.OffHoursReplyMode =" in text
    assert "cfg.OffHoursFixedText =" in text
    assert "cfg.EnableOrderPlacedReply =" in text
    assert "cfg.OrderPlacedReplyMode =" in text
    assert "cfg.OrderPlacedReplyText =" in text
    assert "cfg.OrderPlacedApiTimeoutSeconds =" in text
    assert "OrderPlacedReplyDelaySettings.GetSeconds()" in text
    assert "OrderPlacedReplyDelaySettings.SaveSeconds(0)" not in text
    assert "var currentValue = NormalizeOrderApiTimeout(cfg.OrderPlacedApiTimeoutSeconds);" in text
    assert "return Math.Max(3, Math.Min(60, value));" in text
    assert "ChromeNs\\BotWebAutoReplyRulesSyncService.cs" in props
    assert "cfg.WeChatWebhook =" not in text
    assert "cfg.SmtpPassword =" not in text
    assert "cfg.OrderPlacedApiUrl =" not in text
    assert "cfg.OrderPlacedApiToken =" not in text
    assert "cfg.FeishuWebhook =" not in text
    assert "cfg.DingTalkWebhook =" not in text


def test_mobile_ui_exposes_safe_order_rules_without_secret_inputs():
    text = WEB_JS_PATH.read_text(encoding="utf-8")
    assert "强制转人工关键词" in text
    assert "仅人工确认关键词" in text
    assert "启用工作时间判断" in text
    assert "下班回复方式" in text
    assert "买家下单后自动发送" in text
    assert "启用下单后自动回复" in text
    assert "固定预设 / HTTP 失败兜底文本" in text
    assert "HTTP 超时（秒）" in text
    assert "强制为 0 秒" in text
    assert "order_placed_reply_enabled" in text
    assert "order_placed_reply_mode" in text
    assert "order_placed_reply_text" in text
    assert "order_placed_api_timeout_seconds" in text
    assert "order_placed_reply_delay_seconds" in text
    assert "settings_schema_version" in text
    assert "order_rules_ready" in text
    assert "desired.order_placed_api_timeout_seconds || 3" in text
    assert "$(IDS.orderTimeout).value || 3" in text
    assert "/api/bot-web/auto-reply-rules" in text
    lowered = text.lower()
    for forbidden in (
        "smtp_password",
        "smtppassword",
        "orderplacedapitoken",
        "orderplacedapiurl",
        "authorization",
        "wechatwebhook",
        "feishuwebhook",
        "dingtalkwebhook",
        "clienttoken",
        "cookie",
    ):
        assert forbidden not in lowered

    loader = LOADER_JS_PATH.read_text(encoding="utf-8")
    assert "/static/bot-web-auto-reply-rules.js?v=2" in loader


def test_server_schema_versions_order_fields_and_never_exposes_order_secrets():
    text = MODULE_PATH.read_text(encoding="utf-8-sig")
    assert "RULE_SCHEMA_VERSION = 2" in text
    assert "settings_schema_version" in text
    assert "ORDER_RULE_KEYS" in text
    assert "order_placed_reply_enabled" in text
    assert "order_placed_reply_mode" in text
    assert "order_placed_reply_text" in text
    assert "order_placed_api_timeout_seconds" in text
    assert '"order_placed_api_timeout_seconds": 3' in text
    assert "order_placed_reply_delay_seconds" in text
    assert "OrderPlacedApiUrl" not in text
    assert "OrderPlacedApiToken" not in text


def test_bootstrap_and_container_package_rule_service():
    bootstrap = BOOTSTRAP_PATH.read_text(encoding="utf-8-sig")
    dockerfile = DOCKERFILE_PATH.read_text(encoding="utf-8")
    assert "import bot_web_auto_reply_rules" in bootstrap
    assert "bot_web_auto_reply_rules.install(control_plane, bot_web_console)" in bootstrap
    assert "bot_web_auto_reply_rules.init_db()" in bootstrap
    assert "bot_web_auto_reply_rules.py" in dockerfile
