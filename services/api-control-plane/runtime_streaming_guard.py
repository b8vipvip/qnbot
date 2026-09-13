from __future__ import annotations

import asyncio
import json
import time
import uuid
from typing import Any, AsyncIterator, Dict, List, Sequence

import httpx
from fastapi import Request
from fastapi.responses import JSONResponse, StreamingResponse

import runtime_routing_guard


STREAM_ABORT_MARKER = "[[QN_STREAM_ABORTED]]"
STREAM_FIRST_EVENT_TIMEOUT_SECONDS = max(
    4,
    min(20, int(__import__("os").getenv("RUNTIME_STREAM_FIRST_EVENT_TIMEOUT_SECONDS", "8"))),
)
STREAM_IDLE_TIMEOUT_SECONDS = max(
    10,
    min(120, int(__import__("os").getenv("RUNTIME_STREAM_IDLE_TIMEOUT_SECONDS", "35"))),
)


def install(control_plane: Any) -> None:
    """Intercept OpenAI-compatible streaming chat requests before the normal JSON route.

    Normal non-streaming requests still use the existing FastAPI endpoint and bounded router.
    Streaming requests fail over across relay/provider/model chat routes only until the first
    real text delta arrives. After that point the selected upstream stream is committed and
    proxied to the Bot as SSE without buffering the full answer.
    """

    @control_plane.app.middleware("http")
    async def runtime_streaming_middleware(request: Request, call_next):
        accept = (request.headers.get("accept") or "").lower()
        if (
            request.method.upper() != "POST"
            or request.url.path != "/v1/chat/completions"
            or "text/event-stream" not in accept
        ):
            return await call_next(request)

        try:
            payload = await request.json()
        except Exception:
            return JSONResponse(status_code=400, content={"error": {"message": "请求JSON无效"}})

        if not bool(payload.get("stream")):
            return await call_next(request)

        token = control_plane.bearer_token(request)
        client = control_plane.authenticate_client_token(token)
        if not client:
            return JSONResponse(
                status_code=401,
                content={"error": {"message": "客户端令牌无效", "type": "authentication_error"}},
            )

        messages = payload.get("messages")
        if not isinstance(messages, list) or not messages:
            return JSONResponse(status_code=400, content={"error": {"message": "messages 不能为空"}})

        requested_model = str(payload.get("model") or "text-default")
        max_tokens = int(payload.get("max_tokens") or payload.get("max_completion_tokens") or 512)
        temperature = float(payload.get("temperature") if payload.get("temperature") is not None else 0.2)
        timeout = int(payload.get("timeout_seconds") or control_plane.REQUEST_TIMEOUT_SECONDS)

        return StreamingResponse(
            stream_chat(
                control_plane,
                request,
                client["name"],
                requested_model,
                messages,
                max(1, min(32000, max_tokens)),
                temperature,
                max(5, min(300, timeout)),
            ),
            media_type="text/event-stream",
            headers={
                "Cache-Control": "no-cache, no-transform",
                "X-Accel-Buffering": "no",
                "Connection": "keep-alive",
            },
        )


async def stream_chat(
    control_plane: Any,
    request: Request,
    client_name: str,
    requested_model: str,
    messages: Sequence[Dict[str, Any]],
    max_tokens: int,
    temperature: float,
    timeout: int,
) -> AsyncIterator[bytes]:
    routes = [
        route
        for route in runtime_routing_guard._build_routes(control_plane, requested_model, messages)
        if route[2] == "chat"
    ]
    requested_budget = max(5, int(timeout or runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS))
    total_budget = min(requested_budget, runtime_routing_guard.RUNTIME_TOTAL_BUDGET_SECONDS)
    deadline = time.monotonic() + total_budget
    failures: List[Dict[str, Any]] = []

    timeout_config = httpx.Timeout(
        connect=min(10.0, float(STREAM_FIRST_EVENT_TIMEOUT_SECONDS)),
        read=None,
        write=min(10.0, float(STREAM_FIRST_EVENT_TIMEOUT_SECONDS)),
        pool=min(10.0, float(STREAM_FIRST_EVENT_TIMEOUT_SECONDS)),
    )

    async with httpx.AsyncClient(timeout=timeout_config, follow_redirects=True) as http:
        for provider, model, protocol in routes:
            if await request.is_disconnected():
                return
            remaining = deadline - time.monotonic()
            if remaining < 2:
                break

            urls = control_plane.upstream_url_candidates(
                provider,
                model,
                protocol,
                control_plane.messages_have_image(messages),
            )[: runtime_routing_guard.RUNTIME_MAX_URLS_PER_ROUTE]

            for url in urls:
                if await request.is_disconnected():
                    return
                remaining = deadline - time.monotonic()
                if remaining < 2:
                    break

                attempt_started = time.monotonic()
                committed = False
                upstream_payload = {
                    "model": model,
                    "messages": list(messages),
                    "max_tokens": max_tokens,
                    "temperature": temperature,
                    "stream": True,
                }
                attempt = {
                    "provider_id": provider["id"],
                    "provider_name": provider["name"],
                    "model": model,
                    "protocol": "chat-stream",
                    "url": url,
                    "success": False,
                    "latency_ms": 0,
                }

                try:
                    headers = {
                        "Authorization": f"Bearer {provider['api_key']}",
                        "Content-Type": "application/json",
                        "Accept": "text/event-stream, application/json",
                        "User-Agent": "qianniu-api-control-plane/stream",
                    }
                    async with http.stream(
                        "POST",
                        url,
                        headers=headers,
                        json=upstream_payload,
                    ) as response:
                        attempt["status_code"] = response.status_code
                        if not (200 <= response.status_code < 300):
                            body = (await response.aread()).decode("utf-8", errors="replace")
                            attempt["error"] = control_plane.safe_text(body, 500) or f"HTTP {response.status_code}"
                            attempt["latency_ms"] = int((time.monotonic() - attempt_started) * 1000)
                            failures.append(dict(attempt))
                            control_plane.log_request(client_name, "text-stream", requested_model, attempt)
                            continue

                        content_type = (response.headers.get("content-type") or "").lower()
                        if "text/event-stream" not in content_type:
                            body = (await response.aread()).decode("utf-8", errors="replace")
                            answer = _extract_json_answer(control_plane, body)
                            if not answer:
                                attempt["error"] = "上游未返回SSE，普通JSON中也未解析到答案"
                                attempt["latency_ms"] = int((time.monotonic() - attempt_started) * 1000)
                                failures.append(dict(attempt))
                                control_plane.log_request(client_name, "text-stream", requested_model, attempt)
                                continue

                            attempt["success"] = True
                            attempt["latency_ms"] = int((time.monotonic() - attempt_started) * 1000)
                            control_plane.log_request(client_name, "text-stream-fallback", requested_model, attempt)
                            yield _synthetic_chunk(model, answer)
                            yield b"data: [DONE]\n\n"
                            return

                        first_text_seen = False
                        clean_finish_seen = False
                        iterator = response.aiter_lines().__aiter__()

                        while True:
                            if await request.is_disconnected():
                                return
                            try:
                                wait_seconds = (
                                    min(float(STREAM_FIRST_EVENT_TIMEOUT_SECONDS), max(1.0, deadline - time.monotonic()))
                                    if not first_text_seen
                                    else float(STREAM_IDLE_TIMEOUT_SECONDS)
                                )
                                line = await asyncio.wait_for(iterator.__anext__(), timeout=wait_seconds)
                            except StopAsyncIteration:
                                break
                            except asyncio.TimeoutError:
                                attempt["error"] = (
                                    "等待首个流式文本片段超时"
                                    if not first_text_seen
                                    else "流式输出空闲超时"
                                )
                                break

                            if not line:
                                if first_text_seen:
                                    yield b"\n"
                                continue

                            normalized = line.strip()
                            if not normalized.startswith("data:"):
                                continue

                            data = normalized[5:].strip()
                            if data == "[DONE]":
                                if first_text_seen:
                                    yield b"data: [DONE]\n\n"
                                    return
                                attempt["error"] = "上游流结束但没有文本内容"
                                break

                            delta = _extract_delta(data)
                            if _has_finish_reason(data):
                                clean_finish_seen = True
                            if not first_text_seen and delta:
                                first_text_seen = True
                                committed = True
                                attempt["success"] = True
                                attempt["latency_ms"] = int((time.monotonic() - attempt_started) * 1000)
                                control_plane.log_request(client_name, "text-stream", requested_model, attempt)

                            if first_text_seen:
                                yield (line + "\n\n").encode("utf-8")

                        if first_text_seen:
                            if clean_finish_seen:
                                yield b"data: [DONE]\n\n"
                            else:
                                # Bytes have already been sent, so switching to another model would mix answers.
                                # Emit a private abort marker; the Bot recognizes it at the final outbound formatter
                                # and blocks the partial answer from being delivered to the buyer.
                                yield _synthetic_chunk(model, STREAM_ABORT_MARKER)
                                yield b"data: [DONE]\n\n"
                            return

                        attempt["latency_ms"] = int((time.monotonic() - attempt_started) * 1000)
                        if not attempt.get("error"):
                            attempt["error"] = "上游流结束但没有有效文本片段"
                        failures.append(dict(attempt))
                        control_plane.log_request(client_name, "text-stream", requested_model, attempt)

                except asyncio.CancelledError:
                    raise
                except Exception as exc:
                    attempt["latency_ms"] = int((time.monotonic() - attempt_started) * 1000)
                    attempt["error"] = control_plane.safe_text(exc, 500)
                    if committed:
                        yield _synthetic_chunk(model, STREAM_ABORT_MARKER)
                        yield b"data: [DONE]\n\n"
                        return
                    failures.append(dict(attempt))
                    control_plane.log_request(client_name, "text-stream", requested_model, attempt)
                    continue

    if not await request.is_disconnected():
        remaining = int(deadline - time.monotonic())
        if remaining >= 5:
            # The realtime buyer lane owns a Chat Completions protocol contract. A final
            # non-stream rescue may reuse the same bounded router, but it must not escape
            # into Responses/legacy where request identity and continuation semantics differ.
            dispatched = await asyncio.to_thread(
                runtime_routing_guard.dispatch_chat,
                control_plane,
                client_name,
                requested_model,
                messages,
                max_tokens,
                temperature,
                remaining,
                allowed_protocols={"chat"},
            )
            if dispatched.get("success"):
                attempt = dispatched["attempt"]
                yield _synthetic_chunk(str(attempt.get("model") or requested_model), str(attempt.get("answer") or ""))
                yield b"data: [DONE]\n\n"
                return
            failures.extend(
                dict(attempt)
                for attempt in dispatched.get("attempts", [])
                if isinstance(attempt, dict)
            )

    error_payload = {
        "error": {
            "message": "所有可用流式路由均失败",
            "type": "stream_upstream_exhausted",
            "attempts": failures,
        }
    }
    yield ("data: " + json.dumps(error_payload, ensure_ascii=False) + "\n\n").encode("utf-8")
    yield b"data: [DONE]\n\n"


def _extract_delta(data: str) -> str:
    try:
        obj = json.loads(data)
        choices = obj.get("choices") or []
        if not choices:
            return ""
        choice = choices[0] or {}
        delta = choice.get("delta") or {}
        content = delta.get("content")
        if content is None:
            content = choice.get("text")
        return content if isinstance(content, str) else ""
    except Exception:
        return ""


def _has_finish_reason(data: str) -> bool:
    try:
        obj = json.loads(data)
        choices = obj.get("choices") or []
        if not choices:
            return False
        reason = (choices[0] or {}).get("finish_reason")
        return reason is not None and str(reason).strip() != ""
    except Exception:
        return False


def _extract_json_answer(control_plane: Any, body: str) -> str:
    try:
        data = json.loads(body)
    except Exception:
        return ""
    return control_plane.extract_chat_text(data)


def _synthetic_chunk(model: str, answer: str) -> bytes:
    body = {
        "id": "chatcmpl_" + uuid.uuid4().hex,
        "object": "chat.completion.chunk",
        "created": int(time.time()),
        "model": model,
        "choices": [
            {
                "index": 0,
                "delta": {"content": answer},
                "finish_reason": None,
            }
        ],
    }
    return ("data: " + json.dumps(body, ensure_ascii=False, separators=(",", ":")) + "\n\n").encode("utf-8")
