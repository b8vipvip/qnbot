from __future__ import annotations

import time
import uuid
from typing import Any, Dict

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import JSONResponse
from starlette.concurrency import run_in_threadpool

import bot_client_shop_binding
import bot_web_console as core


router = APIRouter()
_cp: Any = None


def install(control_plane: Any) -> None:
    global _cp
    _cp = control_plane
    control_plane.app.include_router(router)


def _safe_shop_key(value: str) -> str:
    value = (value or "").strip()
    if not value or len(value) > 160:
        raise HTTPException(status_code=400, detail="ShopKey 无效")
    return value


@router.post("/api/runtime/v1/ai-proxy/{shop_key}/chat/completions")
async def runtime_shop_chat(shop_key: str, request: Request) -> JSONResponse:
    client = core._runtime_client(request)
    shop_key = _safe_shop_key(shop_key)
    bot_client_shop_binding.ensure_binding(int(client["id"]), shop_key, False, "")

    payload = await request.json()
    messages = payload.get("messages")
    if not isinstance(messages, list) or not messages:
        raise HTTPException(status_code=400, detail="messages 不能为空")

    requested_model = str(payload.get("model") or "text-default")
    max_tokens = int(payload.get("max_tokens") or payload.get("max_completion_tokens") or 512)
    temperature = float(payload.get("temperature") if payload.get("temperature") is not None else 0.2)
    timeout = int(payload.get("timeout_seconds") or _cp.REQUEST_TIMEOUT_SECONDS)

    dispatched = await run_in_threadpool(
        _cp.dispatch_chat,
        client["name"],
        requested_model,
        messages,
        max(1, min(32000, max_tokens)),
        temperature,
        max(5, min(300, timeout)),
        int(client["id"]),
        shop_key,
        "/api/runtime/v1/ai-proxy/chat/completions",
    )
    if not dispatched["success"]:
        return JSONResponse(
            status_code=502,
            content={
                "error": {
                    "message": "所有供应商、模型和请求协议均调用失败",
                    "type": "upstream_exhausted",
                    "attempts": dispatched["attempts"],
                },
                "qianniu_shop_key": shop_key,
            },
        )

    attempt = dispatched["attempt"]
    answer = attempt["answer"]
    return JSONResponse(
        content={
            "id": "chatcmpl_" + uuid.uuid4().hex,
            "object": "chat.completion",
            "created": int(time.time()),
            "model": attempt["model"],
            "choices": [
                {
                    "index": 0,
                    "message": {"role": "assistant", "content": answer},
                    "finish_reason": "stop",
                }
            ],
            "usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0},
            "qianniu_routing": {
                "provider": attempt["provider_name"],
                "protocol": attempt["protocol"],
                "latency_ms": attempt["latency_ms"],
                "fallback_attempts": len(dispatched["attempts"]) - 1,
                "shop_key": shop_key,
            },
        }
    )
