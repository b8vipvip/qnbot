from __future__ import annotations

import inspect

import pytest
from fastapi import HTTPException

import manual_recharge


def test_default_settings_are_disabled_and_runtime_config_hides_base_url(monkeypatch):
    monkeypatch.setattr(
        manual_recharge,
        "_load_settings",
        lambda: {
            "enabled": False,
            "base_url": "https://example.com",
            "allowed_host": "example.com",
            "timeout_seconds": 20,
            "updated_at": None,
        },
    )
    result = manual_recharge.runtime_manual_recharge_config(_={"id": 1})
    assert result["enabled"] is False
    assert result["allowed_host"] == ""
    assert "base_url" not in result


def test_whitelist_requires_https_host_root_only():
    assert manual_recharge._normalize_base_url("https://recharge.example.com/") == "https://recharge.example.com"
    for value in (
        "http://recharge.example.com",
        "https://recharge.example.com/path",
        "https://user:pass@recharge.example.com",
        "https://recharge.example.com/?x=1",
        "https://127.0.0.1",
    ):
        with pytest.raises(HTTPException):
            manual_recharge._normalize_base_url(value)


def test_runtime_never_accepts_client_supplied_upstream_url():
    source = inspect.getsource(manual_recharge)
    assert "ALLOWED_PATHS" in source
    assert '"/check_order_validity"' in source
    assert '"/check_send_interval"' in source
    assert '"/sfyzm"' in source
    assert '"/submit"' in source
    assert '"/check_recharge_duplicate"' in source
    assert '"/api"' in source
    for model in (
        manual_recharge.OrderInput,
        manual_recharge.PhoneInput,
        manual_recharge.SendCodeInput,
        manual_recharge.AccountsInput,
        manual_recharge.DuplicateInput,
        manual_recharge.RechargeInput,
    ):
        assert "url" not in model.model_fields
        assert "base_url" not in model.model_fields


def test_accounts_response_is_minimal_and_preserves_exact_matching_fields():
    result = manual_recharge._public_accounts(
        {
            "status": "multiple",
            "record_id": 88,
            "accounts": [
                {"nickname": "Alpha", "userid": "10001", "pic": "https://private/pic1"},
                {"nickname": "Beta", "userid": "10002", "pic": "https://private/pic2"},
            ],
        }
    )
    assert result == {
        "status": "multiple",
        "record_id": 88,
        "accounts": [
            {"nickname": "Alpha", "userid": "10001", "selector": "zh1"},
            {"nickname": "Beta", "userid": "10002", "selector": "zh2"},
        ],
    }
    assert "pic" not in str(result)


def test_unknown_account_structure_fails_closed():
    with pytest.raises(HTTPException):
        manual_recharge._public_accounts({"status": "surprise", "record_id": 1})


def test_duplicate_and_recharge_submission_use_stable_operation_id(monkeypatch):
    calls = []

    monkeypatch.setattr(
        manual_recharge,
        "_require_enabled",
        lambda: {
            "enabled": True,
            "base_url": "https://recharge.example.com",
            "timeout_seconds": 20,
        },
    )

    def fake_request(settings, operation, *, payload=None, params=None, idempotency_key=""):
        calls.append((operation, payload, idempotency_key))
        if operation == "duplicate":
            return {"duplicate": False}
        if operation == "recharge":
            return {"success": True}
        raise AssertionError(operation)

    monkeypatch.setattr(manual_recharge, "_request_json", fake_request)
    data = manual_recharge.RechargeInput(
        phone="13800138000",
        code="123456",
        order_id="ABCDEF123456",
        operation_id="op_123456789",
        selector="zh2",
        record_id=99,
    )
    result = manual_recharge.runtime_manual_recharge_submit(data, _={"id": 1})
    assert result["submitted"] is True
    assert calls[0][0] == "duplicate"
    assert calls[0][2] == "op_123456789-duplicate-final"
    assert calls[1][0] == "recharge"
    assert calls[1][2] == "op_123456789-recharge"
    assert calls[1][1]["zhanghu"] == "zh2"
    assert calls[1][1]["record_id"] == 99


def test_final_submit_stops_on_duplicate(monkeypatch):
    monkeypatch.setattr(
        manual_recharge,
        "_require_enabled",
        lambda: {
            "enabled": True,
            "base_url": "https://recharge.example.com",
            "timeout_seconds": 20,
        },
    )
    monkeypatch.setattr(
        manual_recharge,
        "_request_json",
        lambda *args, **kwargs: {"duplicate": True},
    )
    data = manual_recharge.RechargeInput(
        phone="13800138000",
        code="123456",
        order_id="ABCDEF123456",
        operation_id="op_123456789",
        selector="zh1",
        record_id=99,
    )
    result = manual_recharge.runtime_manual_recharge_submit(data, _={"id": 1})
    assert result == {"submitted": False, "duplicate": True}


def test_sensitive_values_are_not_embedded_in_error_details():
    source = inspect.getsource(manual_recharge._request_json)
    assert "response.text" not in source
    assert "payload" not in inspect.getsource(manual_recharge.runtime_manual_recharge_config)
    assert "phone" not in inspect.getsource(manual_recharge.runtime_manual_recharge_config)
    assert "code" not in inspect.getsource(manual_recharge.runtime_manual_recharge_config)
