from __future__ import annotations

import re
import uuid
from datetime import datetime
from typing import Any, Dict, List
from urllib.parse import urlparse, urlunparse
from zoneinfo import ZoneInfo

from curl_cffi import requests as curl_requests
from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field

from wecom_settings import db, iso_now, require_admin, require_runtime_client


router = APIRouter()

PHONE_PATTERN = re.compile(r"^1\d{10}$")
CODE_PATTERN = re.compile(r"^\d{6}$")
ORDER_PATTERN = re.compile(r"^[A-Za-z0-9_-]{6,64}$")
SELECTOR_PATTERN = re.compile(r"^zh([1-9]|1[0-2])$")
OPERATION_PATTERN = re.compile(r"^[A-Za-z0-9_-]{8,80}$")
ALLOWED_PATHS = {
    "check_order": "/check_order_validity",
    "send_interval": "/check_send_interval",
    "send_code": "/sfyzm",
    "accounts": "/submit",
    "duplicate": "/check_recharge_duplicate",
    "recharge": "/api",
}


class ManualRechargeSettingsInput(BaseModel):
    enabled: bool = False
    base_url: str = Field(default="", max_length=1000)
    timeout_seconds: int = Field(default=20, ge=3, le=60)


class OrderInput(BaseModel):
    order_id: str = Field(min_length=6, max_length=64)


class PhoneInput(BaseModel):
    phone: str = Field(min_length=11, max_length=11)


class SendCodeInput(OrderInput, PhoneInput):
    pass


class AccountsInput(SendCodeInput):
    code: str = Field(min_length=6, max_length=6)
    operation_id: str = Field(min_length=8, max_length=80)


class DuplicateInput(AccountsInput):
    pass


class RechargeInput(AccountsInput):
    selector: str = Field(min_length=3, max_length=4)
    record_id: int = Field(ge=0)


def init_manual_recharge_db() -> None:
    with db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS manual_recharge_settings (
                id INTEGER PRIMARY KEY CHECK(id=1),
                enabled INTEGER NOT NULL DEFAULT 0,
                base_url TEXT NOT NULL DEFAULT '',
                timeout_seconds INTEGER NOT NULL DEFAULT 20,
                updated_at TEXT NOT NULL
            );
            """
        )


def _normalize_base_url(value: str) -> str:
    raw = (value or "").strip().rstrip("/")
    if not raw:
        return ""
    parsed = urlparse(raw)
    if parsed.scheme.lower() != "https" or not parsed.netloc:
        raise HTTPException(status_code=400, detail="代充接口只允许 HTTPS 根地址")
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise HTTPException(status_code=400, detail="代充接口根地址不能包含账号、查询参数或片段")
    if parsed.path not in {"", "/"}:
        raise HTTPException(status_code=400, detail="代充白名单只能配置 HTTPS 主机根地址")
    if parsed.hostname in {"localhost", "127.0.0.1", "::1"}:
        raise HTTPException(status_code=400, detail="代充白名单不能指向本机回环地址")
    return urlunparse(("https", parsed.netloc, "", "", "", ""))


def _load_settings() -> Dict[str, Any]:
    init_manual_recharge_db()
    with db() as conn:
        row = conn.execute("SELECT * FROM manual_recharge_settings WHERE id=1").fetchone()
    if not row:
        return {
            "exists": False,
            "enabled": False,
            "base_url": "",
            "allowed_host": "",
            "timeout_seconds": 20,
            "updated_at": None,
        }
    base_url = _normalize_base_url(str(row["base_url"] or "")) if row["base_url"] else ""
    return {
        "exists": True,
        "enabled": bool(row["enabled"]),
        "base_url": base_url,
        "allowed_host": urlparse(base_url).hostname or "" if base_url else "",
        "timeout_seconds": max(3, min(60, int(row["timeout_seconds"] or 20))),
        "updated_at": row["updated_at"],
    }


def _save_settings(data: ManualRechargeSettingsInput) -> Dict[str, Any]:
    base_url = _normalize_base_url(data.base_url)
    if data.enabled and not base_url:
        raise HTTPException(status_code=400, detail="启用代充前必须配置固定 HTTPS 白名单根地址")
    now = iso_now()
    with db() as conn:
        conn.execute(
            """
            INSERT INTO manual_recharge_settings(id,enabled,base_url,timeout_seconds,updated_at)
            VALUES(1,?,?,?,?)
            ON CONFLICT(id) DO UPDATE SET
                enabled=excluded.enabled,
                base_url=excluded.base_url,
                timeout_seconds=excluded.timeout_seconds,
                updated_at=excluded.updated_at
            """,
            (1 if data.enabled else 0, base_url, max(3, min(60, data.timeout_seconds)), now),
        )
    return _load_settings()


def _require_enabled() -> Dict[str, Any]:
    settings = _load_settings()
    if not settings.get("enabled"):
        raise HTTPException(status_code=409, detail="manual_recharge_disabled")
    if not settings.get("base_url"):
        raise HTTPException(status_code=503, detail="代充服务未配置固定白名单地址")
    return settings


def _upstream_url(settings: Dict[str, Any], operation: str) -> str:
    if operation not in ALLOWED_PATHS:
        raise HTTPException(status_code=400, detail="不支持的代充操作")
    base = _normalize_base_url(str(settings.get("base_url") or ""))
    if not base:
        raise HTTPException(status_code=503, detail="代充服务未配置固定白名单地址")
    return base + ALLOWED_PATHS[operation]


def _validate_phone(phone: str) -> str:
    value = (phone or "").strip()
    if not PHONE_PATTERN.fullmatch(value):
        raise HTTPException(status_code=400, detail="手机号格式无效")
    return value


def _validate_code(code: str) -> str:
    value = (code or "").strip()
    if not CODE_PATTERN.fullmatch(value):
        raise HTTPException(status_code=400, detail="验证码格式无效")
    return value


def _validate_order(order_id: str) -> str:
    value = (order_id or "").strip()
    if not ORDER_PATTERN.fullmatch(value):
        raise HTTPException(status_code=400, detail="兑换码格式无效")
    return value


def _validate_operation(operation_id: str) -> str:
    value = (operation_id or "").strip()
    if not OPERATION_PATTERN.fullmatch(value):
        raise HTTPException(status_code=400, detail="操作幂等标识无效")
    return value


def _request_json(
    settings: Dict[str, Any],
    operation: str,
    *,
    payload: Dict[str, Any] | None = None,
    params: Dict[str, Any] | None = None,
    idempotency_key: str = "",
) -> Dict[str, Any]:
    timeout = max(3, min(60, int(settings.get("timeout_seconds") or 20)))
    headers = {
        "Accept": "application/json,*/*;q=0.8",
        "User-Agent": "qianniu-bot-manual-recharge/1.0",
    }
    if idempotency_key:
        headers["X-Idempotency-Key"] = idempotency_key
    try:
        if operation == "send_interval":
            response = curl_requests.get(
                _upstream_url(settings, operation),
                params=params or {},
                headers=headers,
                timeout=timeout,
                allow_redirects=False,
            )
        else:
            response = curl_requests.post(
                _upstream_url(settings, operation),
                json=payload or {},
                headers=headers,
                timeout=timeout,
                allow_redirects=False,
            )
    except Exception as exc:
        raise HTTPException(
            status_code=502,
            detail="代充上游连接失败（" + type(exc).__name__ + "）",
        )
    if response.status_code < 200 or response.status_code >= 300:
        raise HTTPException(
            status_code=502,
            detail="代充上游返回 HTTP " + str(response.status_code),
        )
    try:
        body = response.json()
    except Exception:
        raise HTTPException(status_code=502, detail="代充上游未返回 JSON")
    if not isinstance(body, dict):
        raise HTTPException(status_code=502, detail="代充上游返回结构无效")
    return body


def _public_accounts(payload: Dict[str, Any]) -> Dict[str, Any]:
    status = str(payload.get("status") or "").strip().lower()
    if status == "code_error":
        return {"status": "code_error", "record_id": 0, "accounts": []}
    if status == "new_user":
        return {"status": "new_user", "record_id": int(payload.get("record_id") or 0), "accounts": []}
    if status == "error":
        raise HTTPException(status_code=502, detail="账号查询接口返回错误")
    if status not in {"single", "multiple"}:
        raise HTTPException(status_code=502, detail="账号查询接口返回未知状态")

    record_id = int(payload.get("record_id") or 0)
    accounts: List[Dict[str, Any]] = []
    if status == "single":
        accounts.append(
            {
                "nickname": str(payload.get("nickname") or "").strip()[:120],
                "userid": str(payload.get("userid") or "").strip()[:120],
                "selector": "zh1",
            }
        )
    else:
        raw = payload.get("accounts")
        if not isinstance(raw, list):
            raise HTTPException(status_code=502, detail="账号列表结构无效")
        for index, item in enumerate(raw[:12], start=1):
            if not isinstance(item, dict):
                continue
            accounts.append(
                {
                    "nickname": str(item.get("nickname") or "").strip()[:120],
                    "userid": str(item.get("userid") or "").strip()[:120],
                    "selector": "zh" + str(index),
                }
            )
    accounts = [x for x in accounts if x["nickname"] or x["userid"]]
    if not accounts:
        raise HTTPException(status_code=502, detail="账号查询接口未返回可确认账号")
    return {"status": status, "record_id": record_id, "accounts": accounts}


def _duplicate_flag(payload: Dict[str, Any]) -> bool:
    if payload.get("duplicate") is True:
        return True
    status = str(payload.get("status") or "").strip().lower()
    return status in {"duplicate", "duplicated", "重复", "重复订单", "已提交"}


def _accepted_recharge(payload: Dict[str, Any]) -> bool:
    if payload.get("success") is True:
        return True
    status = str(payload.get("status") or "").strip().lower()
    if status in {"success", "ok", "submitted", "accepted", "chongzhi", "充值中", "等待", "已提交"}:
        return True
    code = payload.get("code")
    return code in {0, 200, "0", "200"}


def _beijing_qdzhb() -> str:
    now = datetime.now(ZoneInfo("Asia/Shanghai"))
    minutes = now.hour * 60 + now.minute
    return "1" if 8 * 60 <= minutes <= 18 * 60 + 30 else "0"


@router.get("/api/admin/manual-recharge/settings")
def admin_get_manual_recharge_settings(_: str = Depends(require_admin)) -> Dict[str, Any]:
    return _load_settings()


@router.put("/api/admin/manual-recharge/settings")
def admin_put_manual_recharge_settings(
    data: ManualRechargeSettingsInput,
    _: str = Depends(require_admin),
) -> Dict[str, Any]:
    return _save_settings(data)


@router.get("/api/runtime/v1/manual-recharge/config")
def runtime_manual_recharge_config(
    _: Dict[str, Any] = Depends(require_runtime_client),
) -> Dict[str, Any]:
    settings = _load_settings()
    return {
        "enabled": bool(settings.get("enabled")),
        "allowed_host": settings.get("allowed_host") if settings.get("enabled") else "",
        "updated_at": settings.get("updated_at"),
    }


@router.post("/api/runtime/v1/manual-recharge/check-order")
def runtime_manual_recharge_check_order(
    data: OrderInput,
    _: Dict[str, Any] = Depends(require_runtime_client),
) -> Dict[str, Any]:
    settings = _require_enabled()
    order_id = _validate_order(data.order_id)
    result = _request_json(settings, "check_order", payload={"order_id": order_id})
    valid = result.get("valid")
    status = str(result.get("status") or "").strip().lower()
    if valid is False or status in {"invalid", "error", "failed", "无效"}:
        return {"valid": False}
    if valid is True or status in {"valid", "success", "ok"}:
        return {"valid": True}
    raise HTTPException(status_code=502, detail="兑换码校验接口返回未知结构")


@router.post("/api/runtime/v1/manual-recharge/check-send-interval")
def runtime_manual_recharge_check_send_interval(
    data: PhoneInput,
    _: Dict[str, Any] = Depends(require_runtime_client),
) -> Dict[str, Any]:
    settings = _require_enabled()
    phone = _validate_phone(data.phone)
    result = _request_json(settings, "send_interval", params={"phone": phone})
    allowed = result.get("allowed")
    if allowed is False:
        return {"allowed": False, "retry_after_seconds": int(result.get("retry_after_seconds") or 0)}
    status = str(result.get("status") or "").strip().lower()
    if allowed is True or status in {"ok", "success", "allowed", "ready"}:
        return {"allowed": True, "retry_after_seconds": 0}
    if status in {"wait", "too_soon", "blocked", "rate_limited"}:
        return {"allowed": False, "retry_after_seconds": int(result.get("retry_after_seconds") or 0)}
    raise HTTPException(status_code=502, detail="验证码发送间隔接口返回未知结构")


@router.post("/api/runtime/v1/manual-recharge/send-code")
def runtime_manual_recharge_send_code(
    data: SendCodeInput,
    _: Dict[str, Any] = Depends(require_runtime_client),
) -> Dict[str, Any]:
    settings = _require_enabled()
    phone = _validate_phone(data.phone)
    order_id = _validate_order(data.order_id)
    payload = {
        "token": phone,
        "UrlsID": "",
        "orderID": order_id,
        "zhanghu": "no",
        "huiyuanguize": "0",
        "lingqu3": "0",
        "shougong": "0",
        "qdzhb": "0",
        "applogin": "0",
        "weblog": "0",
        "init": "1",
        "yzm_status": "1",
        "type": "fasong",
    }
    result = _request_json(settings, "send_code", payload=payload)
    if result.get("success") is False or str(result.get("status") or "").lower() in {"error", "failed"}:
        raise HTTPException(status_code=502, detail="验证码发送接口未确认成功")
    return {"sent": True}


@router.post("/api/runtime/v1/manual-recharge/accounts")
def runtime_manual_recharge_accounts(
    data: AccountsInput,
    _: Dict[str, Any] = Depends(require_runtime_client),
) -> Dict[str, Any]:
    settings = _require_enabled()
    phone = _validate_phone(data.phone)
    code = _validate_code(data.code)
    order_id = _validate_order(data.order_id)
    operation_id = _validate_operation(data.operation_id)
    result = _request_json(
        settings,
        "accounts",
        payload={"phone": phone, "code": code, "order_id": order_id},
        idempotency_key=operation_id + "-accounts",
    )
    return _public_accounts(result)


@router.post("/api/runtime/v1/manual-recharge/duplicate-check")
def runtime_manual_recharge_duplicate_check(
    data: DuplicateInput,
    _: Dict[str, Any] = Depends(require_runtime_client),
) -> Dict[str, Any]:
    settings = _require_enabled()
    phone = _validate_phone(data.phone)
    code = _validate_code(data.code)
    order_id = _validate_order(data.order_id)
    operation_id = _validate_operation(data.operation_id)
    result = _request_json(
        settings,
        "duplicate",
        payload={"phone": phone, "code": code, "order_id": order_id, "source": "tel"},
        idempotency_key=operation_id + "-duplicate",
    )
    return {"duplicate": _duplicate_flag(result)}


@router.post("/api/runtime/v1/manual-recharge/submit")
def runtime_manual_recharge_submit(
    data: RechargeInput,
    _: Dict[str, Any] = Depends(require_runtime_client),
) -> Dict[str, Any]:
    settings = _require_enabled()
    phone = _validate_phone(data.phone)
    code = _validate_code(data.code)
    order_id = _validate_order(data.order_id)
    operation_id = _validate_operation(data.operation_id)
    selector = (data.selector or "").strip().lower()
    if not SELECTOR_PATTERN.fullmatch(selector):
        raise HTTPException(status_code=400, detail="账号选择值无效")

    duplicate = _request_json(
        settings,
        "duplicate",
        payload={"phone": phone, "code": code, "order_id": order_id, "source": "tel"},
        idempotency_key=operation_id + "-duplicate-final",
    )
    if _duplicate_flag(duplicate):
        return {"submitted": False, "duplicate": True}

    payload = {
        "token": phone,
        "UrlsID": code,
        "orderID": order_id,
        "zhanghu": selector,
        "huiyuanguize": "0",
        "qdzhb": _beijing_qdzhb(),
        "shougong": "0",
        "lingqu3": "1",
        "applogin": "1",
        "weblog": "1",
        "init": "0",
        "yzm_status": "3",
        "type": "chongzhi",
        "record_id": data.record_id,
    }
    result = _request_json(
        settings,
        "recharge",
        payload=payload,
        idempotency_key=operation_id + "-recharge",
    )
    if not _accepted_recharge(result):
        raise HTTPException(status_code=502, detail="充值接口未返回可确认的提交状态")
    return {"submitted": True, "duplicate": False, "operation_id": operation_id}
