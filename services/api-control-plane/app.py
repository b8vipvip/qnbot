from __future__ import annotations

import base64
import hashlib
import hmac
import io
import json
import os
import re
import secrets
import sqlite3
import threading
import time
import traceback
import uuid
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

from cryptography.fernet import Fernet, InvalidToken
from curl_cffi import requests as curl_requests
from fastapi import BackgroundTasks, Depends, FastAPI, HTTPException, Request, Response, status
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse, PlainTextResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field
from starlette.middleware.sessions import SessionMiddleware
from starlette.concurrency import run_in_threadpool
from PIL import Image, ImageDraw, ImageFont


APP_DIR = Path(__file__).resolve().parent
STATIC_DIR = APP_DIR / "static"
DATA_DIR = Path(os.getenv("DATA_DIR", "/data")).resolve()
DATA_DIR.mkdir(parents=True, exist_ok=True)
DB_PATH = Path(os.getenv("DATABASE_PATH", str(DATA_DIR / "api-control-plane.db"))).resolve()
APP_SECRET = os.getenv("APP_SECRET", "change-me-in-production")
ADMIN_USERNAME = os.getenv("ADMIN_USERNAME", "admin")
ADMIN_PASSWORD = os.getenv("ADMIN_PASSWORD", "change-me")
PUBLIC_BASE_URL = os.getenv("PUBLIC_BASE_URL", "").rstrip("/")
REQUEST_TIMEOUT_SECONDS = int(os.getenv("REQUEST_TIMEOUT_SECONDS", "45"))
SCHEDULER_POLL_SECONDS = int(os.getenv("SCHEDULER_POLL_SECONDS", "60"))
VISION_CODE = "VISION-7382"
LOG_LOCK = threading.Lock()
TEST_LOCKS: Dict[int, threading.Lock] = {}
TEST_LOCKS_GUARD = threading.Lock()
SCHEDULER_STOP = threading.Event()


def utcnow() -> datetime:
    return datetime.now(timezone.utc)


def iso_now() -> str:
    return utcnow().isoformat(timespec="seconds")


def parse_json(value: Optional[str], default: Any) -> Any:
    if not value:
        return default
    try:
        return json.loads(value)
    except Exception:
        return default


def json_text(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def safe_text(value: Any, limit: int = 500) -> str:
    text = str(value or "").replace("\r", " ").replace("\n", " ").strip()
    while "  " in text:
        text = text.replace("  ", " ")
    return text if len(text) <= limit else text[:limit] + "..."


def mask_key(value: str) -> str:
    value = value or ""
    if not value:
        return ""
    if len(value) <= 8:
        return "*" * len(value)
    return value[:4] + "*" * (len(value) - 8) + value[-4:]


def normalize_base_url(url: str) -> str:
    url = (url or "").strip().rstrip("/")
    if not url:
        return ""
    if not url.startswith(("http://", "https://")):
        url = "https://" + url
    if url.endswith("/chat/completions"):
        url = url[: -len("/chat/completions")].rstrip("/")
    elif url.endswith("/responses"):
        url = url[: -len("/responses")].rstrip("/")
    elif url.endswith("/completions"):
        url = url[: -len("/completions")].rstrip("/")
    return url


def get_api_roots(base_url: str, include_v1_root: bool = True, include_root: bool = True) -> List[str]:
    base_url = normalize_base_url(base_url)
    if not base_url:
        return []
    roots: List[str] = []
    if base_url.endswith("/v1"):
        if include_v1_root:
            roots.append(base_url)
        if include_root:
            roots.append(base_url[:-3].rstrip("/"))
    else:
        if include_v1_root:
            roots.append(base_url + "/v1")
        if include_root:
            roots.append(base_url)
    return list(dict.fromkeys(roots))


def derive_fernet_key() -> bytes:
    explicit = os.getenv("API_KEY_ENCRYPTION_KEY", "").strip()
    if explicit:
        try:
            Fernet(explicit.encode("ascii"))
            return explicit.encode("ascii")
        except Exception as exc:
            raise RuntimeError("API_KEY_ENCRYPTION_KEY 必须是有效的 Fernet key") from exc
    digest = hashlib.sha256(APP_SECRET.encode("utf-8")).digest()
    return base64.urlsafe_b64encode(digest)


FERNET = Fernet(derive_fernet_key())


def encrypt_secret(value: str) -> str:
    if not value:
        return ""
    return FERNET.encrypt(value.encode("utf-8")).decode("ascii")


def decrypt_secret(value: str) -> str:
    if not value:
        return ""
    try:
        return FERNET.decrypt(value.encode("ascii")).decode("utf-8")
    except InvalidToken as exc:
        raise RuntimeError("无法解密上游 ApiKey，请确认 API_KEY_ENCRYPTION_KEY 未变化") from exc


@contextmanager
def db() -> Iterable[sqlite3.Connection]:
    connection = sqlite3.connect(str(DB_PATH), timeout=30, check_same_thread=False)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA journal_mode=WAL")
    connection.execute("PRAGMA foreign_keys=ON")
    try:
        yield connection
        connection.commit()
    except Exception:
        connection.rollback()
        raise
    finally:
        connection.close()


def init_db() -> None:
    with db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS providers (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                base_url TEXT NOT NULL,
                api_key_cipher TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                priority INTEGER NOT NULL DEFAULT 100,
                main_text_model TEXT NOT NULL DEFAULT '',
                backup_text_models_json TEXT NOT NULL DEFAULT '[]',
                main_vision_model TEXT NOT NULL DEFAULT '',
                backup_vision_models_json TEXT NOT NULL DEFAULT '[]',
                protocol_order_json TEXT NOT NULL DEFAULT '["responses","chat","legacy"]',
                model_capabilities_json TEXT NOT NULL DEFAULT '{}',
                auto_test_enabled INTEGER NOT NULL DEFAULT 0,
                auto_test_interval_hours INTEGER NOT NULL DEFAULT 12,
                auto_test_options_json TEXT NOT NULL DEFAULT '{}',
                last_test_at TEXT,
                next_test_at TEXT,
                last_status TEXT NOT NULL DEFAULT '未测试',
                last_latency_ms INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS client_tokens (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                token_hash TEXT NOT NULL UNIQUE,
                token_prefix TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL,
                last_used_at TEXT
            );

            CREATE TABLE IF NOT EXISTS test_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                provider_id INTEGER NOT NULL,
                mode TEXT NOT NULL,
                status TEXT NOT NULL,
                options_json TEXT NOT NULL,
                result_json TEXT,
                analysis_markdown TEXT,
                started_at TEXT,
                finished_at TEXT,
                error TEXT,
                created_at TEXT NOT NULL,
                FOREIGN KEY(provider_id) REFERENCES providers(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS request_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_name TEXT,
                request_kind TEXT NOT NULL,
                requested_model TEXT,
                provider_id INTEGER,
                provider_name TEXT,
                resolved_model TEXT,
                protocol TEXT,
                success INTEGER NOT NULL,
                latency_ms INTEGER NOT NULL,
                error TEXT,
                created_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_test_runs_provider ON test_runs(provider_id, id DESC);
            CREATE INDEX IF NOT EXISTS idx_request_logs_created ON request_logs(id DESC);
            """
        )


def row_to_provider(row: sqlite3.Row, include_secret: bool = False) -> Dict[str, Any]:
    provider = dict(row)
    provider["enabled"] = bool(provider["enabled"])
    provider["auto_test_enabled"] = bool(provider["auto_test_enabled"])
    provider["backup_text_models"] = parse_json(provider.pop("backup_text_models_json"), [])
    provider["backup_vision_models"] = parse_json(provider.pop("backup_vision_models_json"), [])
    provider["protocol_order"] = parse_json(provider.pop("protocol_order_json"), ["responses", "chat", "legacy"])
    provider["model_capabilities"] = parse_json(provider.pop("model_capabilities_json"), {})
    provider["auto_test_options"] = parse_json(provider.pop("auto_test_options_json"), default_deep_test_options())
    cipher = provider.pop("api_key_cipher")
    provider["api_key_masked"] = mask_key(decrypt_secret(cipher)) if cipher else ""
    if include_secret:
        provider["api_key"] = decrypt_secret(cipher)
    return provider


def get_provider(provider_id: int, include_secret: bool = False) -> Dict[str, Any]:
    with db() as conn:
        row = conn.execute("SELECT * FROM providers WHERE id=?", (provider_id,)).fetchone()
    if not row:
        raise HTTPException(status_code=404, detail="供应商不存在")
    return row_to_provider(row, include_secret=include_secret)


def list_providers(include_secret: bool = False, enabled_only: bool = False) -> List[Dict[str, Any]]:
    query = "SELECT * FROM providers"
    params: Tuple[Any, ...] = ()
    if enabled_only:
        query += " WHERE enabled=1"
    query += " ORDER BY priority ASC, id ASC"
    with db() as conn:
        rows = conn.execute(query, params).fetchall()
    return [row_to_provider(row, include_secret=include_secret) for row in rows]


def hash_token(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def authenticate_client_token(token: str) -> Optional[Dict[str, Any]]:
    if not token:
        return None
    digest = hash_token(token)
    with db() as conn:
        row = conn.execute(
            "SELECT * FROM client_tokens WHERE token_hash=? AND enabled=1",
            (digest,),
        ).fetchone()
        if not row:
            return None
        conn.execute("UPDATE client_tokens SET last_used_at=? WHERE id=?", (iso_now(), row["id"]))
    return dict(row)


def bearer_token(request: Request) -> str:
    header = request.headers.get("authorization", "")
    if not header.lower().startswith("bearer "):
        return ""
    return header.split(" ", 1)[1].strip()


def require_client(request: Request) -> Dict[str, Any]:
    client = authenticate_client_token(bearer_token(request))
    if not client:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="客户端令牌无效")
    return client


def require_admin(request: Request) -> str:
    username = request.session.get("admin_username")
    if not username:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="管理员未登录")
    return str(username)


def default_deep_test_options() -> Dict[str, Any]:
    return {
        "discover_models": True,
        "test_all_discovered_models": True,
        "selected_models": [],
        "include_v1_root": True,
        "include_root": True,
        "responses_text": True,
        "chat_text": True,
        "legacy_text": True,
        "responses_vision": True,
        "chat_vision": True,
        "require_vision_for_full": False,
        "auto_apply_results": True,
        "timeout_seconds": REQUEST_TIMEOUT_SECONDS,
    }


def build_headers(api_key: str) -> Dict[str, str]:
    return {
        "Authorization": f"Bearer {api_key}",
        "Content-Type": "application/json",
        "Accept": "application/json",
        "User-Agent": (
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
            "AppleWebKit/537.36 (KHTML, like Gecko) "
            "Chrome/150.0.0.0 Safari/537.36"
        ),
    }


def body_preview(response: Any, max_length: int = 500) -> str:
    try:
        text = (response.text or "").strip()
    except Exception:
        return ""
    return text if len(text) <= max_length else text[:max_length] + "..."


def safe_json(response: Any) -> Any:
    try:
        return response.json()
    except Exception:
        return None


def response_error(response: Any) -> str:
    data = safe_json(response)
    if isinstance(data, dict):
        error = data.get("error")
        if isinstance(error, dict):
            return str(error.get("message") or error.get("type") or json_text(error))
        if isinstance(error, str):
            return error
        if data.get("message"):
            return str(data["message"])
    return body_preview(response) or "服务器未返回错误内容"


def do_request(method: str, url: str, api_key: str, payload: Any = None, timeout: int = REQUEST_TIMEOUT_SECONDS) -> Dict[str, Any]:
    started = time.perf_counter()
    try:
        response = curl_requests.request(
            method=method,
            url=url,
            headers=build_headers(api_key),
            json=payload,
            timeout=max(5, min(300, int(timeout or REQUEST_TIMEOUT_SECONDS))),
            impersonate="chrome",
            allow_redirects=True,
        )
        return {
            "network_success": True,
            "response": response,
            "elapsed": round(time.perf_counter() - started, 3),
        }
    except Exception as exc:
        return {
            "network_success": False,
            "response": None,
            "elapsed": round(time.perf_counter() - started, 3),
            "error": safe_text(exc, 800),
        }


def extract_models(data: Any) -> List[str]:
    models: List[str] = []
    candidates: Any = None
    if isinstance(data, dict):
        if isinstance(data.get("data"), list):
            candidates = data["data"]
        elif isinstance(data.get("models"), list):
            candidates = data["models"]
    elif isinstance(data, list):
        candidates = data
    if isinstance(candidates, list):
        for item in candidates:
            if isinstance(item, str):
                models.append(item)
            elif isinstance(item, dict):
                model_id = item.get("id") or item.get("name") or item.get("model")
                if model_id:
                    models.append(str(model_id))
    return sorted(set(x.strip() for x in models if str(x).strip()))


def discover_models(base_url: str, api_key: str, options: Dict[str, Any]) -> Dict[str, Any]:
    attempts: List[Dict[str, Any]] = []
    urls = [
        root.rstrip("/") + "/models"
        for root in get_api_roots(
            base_url,
            include_v1_root=bool(options.get("include_v1_root", True)),
            include_root=bool(options.get("include_root", True)),
        )
    ]
    for url in list(dict.fromkeys(urls)):
        result = do_request("GET", url, api_key, timeout=int(options.get("timeout_seconds", REQUEST_TIMEOUT_SECONDS)))
        attempt: Dict[str, Any] = {
            "url": url,
            "network_success": result["network_success"],
            "elapsed": result["elapsed"],
        }
        if not result["network_success"]:
            attempt["error"] = result["error"]
            attempts.append(attempt)
            continue
        response = result["response"]
        attempt["status_code"] = response.status_code
        attempt["content_type"] = response.headers.get("content-type", "")
        if response.status_code != 200:
            attempt["error"] = response_error(response)
            attempts.append(attempt)
            continue
        data = safe_json(response)
        if data is None:
            attempt["error"] = "HTTP 200，但返回内容不是 JSON"
            attempt["response_preview"] = body_preview(response)
            attempts.append(attempt)
            continue
        models = extract_models(data)
        if models:
            attempt["success"] = True
            attempt["models_count"] = len(models)
            attempts.append(attempt)
            return {"success": True, "url": url, "models": models, "attempts": attempts}
        attempt["error"] = "返回 JSON，但未识别到模型列表"
        attempts.append(attempt)
    return {"success": False, "models": [], "attempts": attempts}


def validate_responses(data: Any) -> bool:
    return isinstance(data, dict) and (
        "output" in data or "output_text" in data or data.get("object") == "response"
    )


def validate_chat(data: Any) -> bool:
    return isinstance(data, dict) and isinstance(data.get("choices"), list) and bool(data["choices"])


def extract_chat_text(data: Any) -> str:
    try:
        content = data["choices"][0]["message"]["content"]
        if isinstance(content, str):
            return content.strip()
        if isinstance(content, list):
            parts = []
            for item in content:
                if isinstance(item, dict) and item.get("text"):
                    parts.append(str(item["text"]))
            return "\n".join(parts).strip()
    except Exception:
        return ""
    return ""


def extract_responses_text(data: Any) -> str:
    if not isinstance(data, dict):
        return ""
    if isinstance(data.get("output_text"), str):
        return data["output_text"].strip()
    output = data.get("output")
    if not isinstance(output, list):
        return ""
    parts: List[str] = []
    for item in output:
        if not isinstance(item, dict):
            continue
        content = item.get("content")
        if not isinstance(content, list):
            continue
        for part in content:
            if isinstance(part, dict) and isinstance(part.get("text"), str):
                parts.append(part["text"])
    return "\n".join(parts).strip()


def test_endpoint(api_type: str, kind: str, url: str, api_key: str, payload: Dict[str, Any], timeout: int) -> Dict[str, Any]:
    result = do_request("POST", url, api_key, payload, timeout=timeout)
    output: Dict[str, Any] = {
        "api_type": api_type,
        "kind": kind,
        "method": "POST",
        "url": url,
        "elapsed": result["elapsed"],
        "success": False,
    }
    if not result["network_success"]:
        output["status_code"] = None
        output["error"] = result["error"]
        return output
    response = result["response"]
    output["status_code"] = response.status_code
    output["content_type"] = response.headers.get("content-type", "")
    if not (200 <= response.status_code < 300):
        output["error"] = response_error(response)
        return output
    data = safe_json(response)
    if data is None:
        output["error"] = "HTTP 返回成功，但内容不是 JSON，可能是网页、WAF 页面或登录页面。"
        output["response_preview"] = body_preview(response)
        return output
    valid = validate_responses(data) if kind == "responses" else validate_chat(data)
    if not valid:
        output["error"] = "返回 JSON，但结构不符合该 API 的正常模型响应格式。"
        output["response_preview"] = safe_text(json_text(data), 500)
        return output
    text = extract_responses_text(data) if kind == "responses" else extract_chat_text(data)
    output["success"] = True
    output["model_answer"] = text
    return output


def get_test_font(size: int = 58) -> Any:
    for candidate in (
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
    ):
        try:
            return ImageFont.truetype(candidate, size)
        except Exception:
            pass
    return ImageFont.load_default()


def create_vision_test_data_url() -> str:
    image = Image.new("RGB", (900, 420), "white")
    draw = ImageDraw.Draw(image)
    draw.rectangle((70, 70, 250, 250), fill=(50, 110, 230))
    draw.ellipse((650, 70, 830, 250), fill=(230, 70, 70))
    draw.text((285, 85), "VISION TEST", fill=(0, 0, 0), font=get_test_font(32))
    draw.text((250, 200), VISION_CODE, fill=(0, 0, 0), font=get_test_font(62))
    draw.text((245, 300), "Read the code in this image", fill=(0, 0, 0), font=get_test_font(32))
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return "data:image/png;base64," + base64.b64encode(buffer.getvalue()).decode("ascii")


def test_vision(kind: str, url: str, api_key: str, model: str, image_data_url: str, timeout: int) -> Dict[str, Any]:
    if kind == "responses":
        payload = {
            "model": model,
            "input": [
                {
                    "role": "user",
                    "content": [
                        {"type": "input_text", "text": "请读取图片中的验证码。只回复验证码本身，不要解释。"},
                        {"type": "input_image", "image_url": image_data_url},
                    ],
                }
            ],
            "max_output_tokens": 50,
        }
    else:
        payload = {
            "model": model,
            "messages": [
                {
                    "role": "user",
                    "content": [
                        {"type": "text", "text": "请读取图片中的验证码。只回复验证码本身，不要解释。"},
                        {"type": "image_url", "image_url": {"url": image_data_url}},
                    ],
                }
            ],
            "max_tokens": 50,
            "temperature": 0,
        }
    result = do_request("POST", url, api_key, payload, timeout=timeout)
    output: Dict[str, Any] = {
        "success": False,
        "kind": kind,
        "api_type": "OpenAI Responses Vision" if kind == "responses" else "OpenAI Chat Completions Vision",
        "method": "POST",
        "url": url,
        "elapsed": result["elapsed"],
        "expected": VISION_CODE,
    }
    if not result["network_success"]:
        output["status_code"] = None
        output["error"] = result["error"]
        return output
    response = result["response"]
    output["status_code"] = response.status_code
    if not (200 <= response.status_code < 300):
        output["error"] = response_error(response)
        return output
    data = safe_json(response)
    if data is None:
        output["error"] = "视觉请求返回的内容不是 JSON。"
        output["response_preview"] = body_preview(response)
        return output
    text = extract_responses_text(data) if kind == "responses" else extract_chat_text(data)
    output["model_answer"] = text
    normalized = text.upper().replace(" ", "").replace("\n", "").replace("`", "")
    if VISION_CODE in normalized:
        output["success"] = True
    else:
        output["error"] = f"接口接受了图片，但未正确识别验证码。期望：{VISION_CODE}，实际：{text!r}"
    return output


def test_model(provider: Dict[str, Any], model: str, options: Dict[str, Any], image_data_url: str) -> Dict[str, Any]:
    text_results: List[Dict[str, Any]] = []
    vision_results: List[Dict[str, Any]] = []
    timeout = int(options.get("timeout_seconds", REQUEST_TIMEOUT_SECONDS))
    roots = get_api_roots(
        provider["base_url"],
        include_v1_root=bool(options.get("include_v1_root", True)),
        include_root=bool(options.get("include_root", True)),
    )
    for root in roots:
        tests: List[Tuple[str, str, str, Dict[str, Any]]] = []
        if options.get("responses_text", True):
            tests.append(
                (
                    "OpenAI Responses API",
                    "responses",
                    root.rstrip("/") + "/responses",
                    {"model": model, "input": "只回复 OK", "max_output_tokens": 16},
                )
            )
        if options.get("chat_text", True):
            tests.append(
                (
                    "OpenAI Chat Completions",
                    "chat",
                    root.rstrip("/") + "/chat/completions",
                    {
                        "model": model,
                        "messages": [{"role": "user", "content": "只回复 OK"}],
                        "max_tokens": 16,
                        "temperature": 0,
                    },
                )
            )
        if options.get("legacy_text", True):
            tests.append(
                (
                    "Legacy Completions",
                    "legacy",
                    root.rstrip("/") + "/completions",
                    {"model": model, "prompt": "Reply only OK", "max_tokens": 16, "temperature": 0},
                )
            )
        for api_type, kind, url, payload in tests:
            text_result = test_endpoint(api_type, kind, url, provider["api_key"], payload, timeout)
            text_results.append(text_result)
            if not text_result["success"]:
                continue
            if kind == "responses" and options.get("responses_vision", True):
                vision_results.append(test_vision(kind, url, provider["api_key"], model, image_data_url, timeout))
            elif kind == "chat" and options.get("chat_vision", True):
                vision_results.append(test_vision(kind, url, provider["api_key"], model, image_data_url, timeout))
    successful_text = [x for x in text_results if x["success"]]
    successful_vision = [x for x in vision_results if x["success"]]
    return {
        "model": model,
        "text_available": bool(successful_text),
        "vision_available": bool(successful_vision),
        "successful_text_methods": successful_text,
        "successful_vision_methods": successful_vision,
        "all_text_tests": text_results,
        "all_vision_tests": vision_results,
    }


def classify_failure(reason: str) -> str:
    text = (reason or "").lower()
    if "10054" in text or "connection reset" in text or "远程主机强迫关闭" in text:
        return "连接被远端重置"
    if "401" in text or "unauthorized" in text or "invalid api key" in text:
        return "鉴权失败"
    if "403" in text or "forbidden" in text:
        return "访问被拒绝/WAF拦截"
    if "404" in text or "not found" in text:
        return "接口路径不存在"
    if "429" in text or "rate limit" in text or "too many requests" in text:
        return "限流或额度不足"
    if "timeout" in text or "timed out" in text or "超时" in text:
        return "请求超时"
    if "不是 json" in text or "waf 页面" in text or "登录页面" in text:
        return "返回网页而非 API JSON"
    if "未正确识别验证码" in text:
        return "视觉理解失败"
    if "model" in text and ("not found" in text or "does not exist" in text or "unsupported" in text):
        return "模型不存在或不支持"
    return "其他错误"


def protocol_names(model_result: Dict[str, Any]) -> List[str]:
    names: List[str] = []
    for item in model_result.get("successful_text_methods", []):
        kind = item.get("kind")
        display = {"responses": "OpenAI Responses API", "chat": "OpenAI Chat Completions", "legacy": "Legacy Completions"}.get(kind)
        if display and display not in names:
            names.append(display)
    return names


def generate_analysis(provider: Dict[str, Any], result: Dict[str, Any]) -> str:
    models = result.get("model_results", [])
    text_ok = [x for x in models if x.get("text_available")]
    vision_ok = [x for x in models if x.get("vision_available")]
    lines = [
        "# AI 中转站模型/API 测试中文分析报告",
        "",
        f"- 测试开始：{result.get('started_at')}",
        f"- 测试结束：{result.get('finished_at')}",
        f"- 中转站：{provider.get('name')}",
        f"- 地址：`{provider.get('base_url')}`",
        f"- 自动发现模型：{len(result.get('discovery', {}).get('models', []))}",
        f"- 实际测试模型：{len(models)}",
        f"- 文本调用成功：{len(text_ok)}",
        f"- 视觉理解通过：{len(vision_ok)}",
        "",
        "## 模型能力",
        "",
        "| 模型 | 文本调用 | 视觉理解 | 可用请求方式 |",
        "|---|---|---|---|",
    ]
    for model in models:
        lines.append(
            f"| `{model.get('model','')}` | {'可用' if model.get('text_available') else '不可用/未确认'} "
            f"| {'可用' if model.get('vision_available') else '不支持/未通过'} "
            f"| {'、'.join(protocol_names(model)) or '无'} |"
        )
    failures: List[str] = []
    for model in models:
        for item in model.get("all_text_tests", []) + model.get("all_vision_tests", []):
            if not item.get("success") and item.get("error"):
                failures.append(str(item["error"]))
    counts: Dict[str, int] = {}
    for failure in failures:
        category = classify_failure(failure)
        counts[category] = counts.get(category, 0) + 1
    lines += ["", "## 主要失败原因", ""]
    for name, count in sorted(counts.items(), key=lambda item: item[1], reverse=True):
        lines.append(f"- {name}：{count} 次")
    lines += [
        "",
        "## 自动应用结果",
        "",
        f"- 当前主文本模型：`{result.get('applied', {}).get('main_text_model', provider.get('main_text_model',''))}`",
        f"- 文本备用模型：{', '.join(result.get('applied', {}).get('backup_text_models', [])) or '无'}",
        f"- 当前主视觉模型：`{result.get('applied', {}).get('main_vision_model', provider.get('main_vision_model',''))}`",
        f"- 视觉备用模型：{', '.join(result.get('applied', {}).get('backup_vision_models', [])) or '无'}",
        "",
        "判定“文本可用”要求返回符合对应 API 结构的 JSON；仅 HTTP 200 但返回 HTML 不视为成功。",
        "判定“视觉可用”要求模型正确读取测试图片中的验证码。",
    ]
    return "\n".join(lines)


def version_key(model: str) -> Tuple[int, ...]:
    """Extract a comparable numeric model version without pretending suffixes have chronology."""
    model = (model or "").lower()
    matches = re.findall(r"(\d+(?:\.\d+)+|\d+)", model)
    values: List[int] = []
    if matches:
        for part in matches[0].split("."):
            try:
                values.append(int(part))
            except ValueError:
                values.append(0)
    while len(values) < 4:
        values.append(0)
    return tuple(values[:4])


def sort_model_results_newest(items: Sequence[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """Prefer the highest numeric version; for equal versions prefer later discovery order."""
    indexed = list(enumerate(items))
    indexed.sort(
        key=lambda pair: (version_key(str(pair[1].get("model", ""))), pair[0]),
        reverse=True,
    )
    return [item for _, item in indexed]


def successful_protocols(model_result: Dict[str, Any], vision: bool = False) -> List[str]:
    items = model_result.get("successful_vision_methods" if vision else "successful_text_methods", [])
    protocols: List[str] = []
    for item in items:
        kind = item.get("kind")
        if not kind:
            api_type = str(item.get("api_type", "")).lower()
            if "responses" in api_type:
                kind = "responses"
            elif "chat" in api_type:
                kind = "chat"
            elif "legacy" in api_type:
                kind = "legacy"
        if kind and kind not in protocols:
            protocols.append(kind)
    return protocols


def successful_endpoint_urls(model_result: Dict[str, Any], vision: bool = False) -> Dict[str, List[str]]:
    items = model_result.get("successful_vision_methods" if vision else "successful_text_methods", [])
    output: Dict[str, List[str]] = {}
    for item in items:
        kind = item.get("kind")
        url = str(item.get("url") or "").strip()
        if kind in {"responses", "chat", "legacy"} and url:
            output.setdefault(kind, [])
            if url not in output[kind]:
                output[kind].append(url)
    return output


def apply_deep_test_result(provider_id: int, provider: Dict[str, Any], result: Dict[str, Any], options: Dict[str, Any]) -> Dict[str, Any]:
    model_results = result.get("model_results", [])
    text_available = [x for x in model_results if x.get("text_available")]
    vision_available = [x for x in model_results if x.get("vision_available")]
    require_vision = bool(options.get("require_vision_for_full", False))
    full_available = [x for x in text_available if x.get("vision_available")] if require_vision else text_available
    text_sorted = sort_model_results_newest(text_available)
    vision_sorted = sort_model_results_newest(vision_available)
    full_sorted = sort_model_results_newest(full_available)

    current_main = provider.get("main_text_model", "")
    current_result = next((x for x in text_available if x.get("model") == current_main), None)
    if current_result:
        main_text = current_main
    elif full_sorted:
        main_text = full_sorted[0]["model"]
    elif text_sorted:
        main_text = text_sorted[0]["model"]
    else:
        main_text = current_main

    backup_text = [x["model"] for x in text_sorted if x["model"] != main_text]

    current_vision = provider.get("main_vision_model", "")
    if any(x.get("model") == current_vision for x in vision_available):
        main_vision = current_vision
    elif vision_sorted:
        main_vision = vision_sorted[0]["model"]
    else:
        main_vision = current_vision
    backup_vision = [x["model"] for x in vision_sorted if x["model"] != main_vision]

    capabilities: Dict[str, Any] = {}
    for item in model_results:
        capabilities[item["model"]] = {
            "text_available": bool(item.get("text_available")),
            "vision_available": bool(item.get("vision_available")),
            "text_protocols": successful_protocols(item, vision=False),
            "vision_protocols": successful_protocols(item, vision=True),
            "text_urls": successful_endpoint_urls(item, vision=False),
            "vision_urls": successful_endpoint_urls(item, vision=True),
            "last_test_at": result.get("finished_at"),
            "latency_ms": min(
                [int(float(x.get("elapsed", 0)) * 1000) for x in item.get("successful_text_methods", [])] or [0]
            ),
        }

    main_result = next((x for x in model_results if x.get("model") == main_text), None)
    protocol_order = successful_protocols(main_result or {}, vision=False) or provider.get("protocol_order", ["responses", "chat", "legacy"])

    status_text = (
        f"可用：文本模型 {len(text_available)} 个，视觉模型 {len(vision_available)} 个"
        if text_available
        else "不可用：没有模型通过文本调用测试"
    )

    with db() as conn:
        conn.execute(
            """
            UPDATE providers
            SET main_text_model=?, backup_text_models_json=?,
                main_vision_model=?, backup_vision_models_json=?,
                protocol_order_json=?, model_capabilities_json=?,
                last_test_at=?, next_test_at=?, last_status=?, updated_at=?
            WHERE id=?
            """,
            (
                main_text,
                json_text(backup_text),
                main_vision,
                json_text(backup_vision),
                json_text(protocol_order),
                json_text(capabilities),
                result.get("finished_at"),
                next_test_time(provider, result.get("finished_at")),
                status_text,
                iso_now(),
                provider_id,
            ),
        )
    applied = {
        "main_text_model": main_text,
        "backup_text_models": backup_text,
        "main_vision_model": main_vision,
        "backup_vision_models": backup_vision,
        "protocol_order": protocol_order,
        "model_capabilities": capabilities,
    }
    result["applied"] = applied
    return applied


def next_test_time(provider: Dict[str, Any], from_time: Optional[str] = None) -> Optional[str]:
    if not provider.get("auto_test_enabled"):
        return None
    hours = max(1, min(720, int(provider.get("auto_test_interval_hours") or 12)))
    base = utcnow()
    if from_time:
        try:
            base = datetime.fromisoformat(from_time.replace("Z", "+00:00"))
        except Exception:
            pass
    return (base + timedelta(hours=hours)).isoformat(timespec="seconds")


def run_provider_test(provider_id: int, mode: str, options: Dict[str, Any], run_id: Optional[int] = None) -> Dict[str, Any]:
    provider = get_provider(provider_id, include_secret=True)
    lock = provider_test_lock(provider_id)
    if not lock.acquire(blocking=False):
        raise RuntimeError("该供应商已有测试正在执行")
    try:
        started_at = iso_now()
        if run_id:
            with db() as conn:
                conn.execute(
                    "UPDATE test_runs SET status='running', started_at=? WHERE id=?",
                    (started_at, run_id),
                )
        merged = default_deep_test_options()
        merged.update(options or {})
        if mode == "ordinary":
            merged["discover_models"] = False
            merged["test_all_discovered_models"] = False
            selected = merged.get("selected_models") or [provider.get("main_text_model")]
            merged["selected_models"] = [x for x in selected if x]
            merged["auto_apply_results"] = False

        discovery = (
            discover_models(provider["base_url"], provider["api_key"], merged)
            if merged.get("discover_models")
            else {"success": False, "models": [], "attempts": [], "skipped": True}
        )
        configured_models = [
            provider.get("main_text_model", ""),
            *provider.get("backup_text_models", []),
            provider.get("main_vision_model", ""),
            *provider.get("backup_vision_models", []),
        ]
        configured_models = list(dict.fromkeys(x for x in configured_models if x))
        selected_models = [str(x).strip() for x in merged.get("selected_models", []) if str(x).strip()]
        if mode == "deep" and merged.get("test_all_discovered_models", True) and discovery.get("models"):
            models = discovery["models"]
        elif selected_models:
            models = selected_models
        elif discovery.get("models"):
            models = discovery["models"]
        else:
            models = configured_models
        models = list(dict.fromkeys(models))
        if not models:
            raise RuntimeError("未发现或配置任何可测试模型")

        image_data_url = create_vision_test_data_url()
        model_results = [test_model(provider, model, merged, image_data_url) for model in models]
        finished_at = iso_now()
        result: Dict[str, Any] = {
            "started_at": started_at,
            "finished_at": finished_at,
            "mode": mode,
            "provider": {
                "id": provider["id"],
                "name": provider["name"],
                "base_url": provider["base_url"],
                "api_key_masked": provider["api_key_masked"],
            },
            "discovery": discovery,
            "configured_models": configured_models,
            "tested_models": models,
            "model_results": model_results,
        }
        if merged.get("auto_apply_results", mode == "deep"):
            apply_deep_test_result(provider_id, provider, result, merged)
        analysis = generate_analysis(provider, result)
        if run_id:
            with db() as conn:
                conn.execute(
                    """
                    UPDATE test_runs
                    SET status='completed', result_json=?, analysis_markdown=?,
                        finished_at=?, error=NULL
                    WHERE id=?
                    """,
                    (json_text(result), analysis, finished_at, run_id),
                )
        return result
    except Exception as exc:
        if run_id:
            with db() as conn:
                conn.execute(
                    "UPDATE test_runs SET status='failed', error=?, finished_at=? WHERE id=?",
                    (safe_text(exc, 2000), iso_now(), run_id),
                )
        raise
    finally:
        lock.release()


def provider_test_lock(provider_id: int) -> threading.Lock:
    with TEST_LOCKS_GUARD:
        return TEST_LOCKS.setdefault(provider_id, threading.Lock())


def create_test_run(provider_id: int, mode: str, options: Dict[str, Any]) -> int:
    with db() as conn:
        cursor = conn.execute(
            """
            INSERT INTO test_runs(provider_id, mode, status, options_json, created_at)
            VALUES(?, ?, 'queued', ?, ?)
            """,
            (provider_id, mode, json_text(options), iso_now()),
        )
        return int(cursor.lastrowid)


def test_worker(provider_id: int, mode: str, options: Dict[str, Any], run_id: int) -> None:
    try:
        run_provider_test(provider_id, mode, options, run_id=run_id)
    except Exception:
        traceback.print_exc()


def model_candidates(provider: Dict[str, Any], requested_model: str, vision: bool) -> List[str]:
    pool: List[str] = []
    logical = {"", "text-default", "vision-default", "auto", "default"}
    if requested_model and requested_model not in logical:
        pool.append(requested_model)
    if vision:
        pool += [provider.get("main_vision_model", "")] + provider.get("backup_vision_models", [])
    else:
        pool += [provider.get("main_text_model", "")] + provider.get("backup_text_models", [])
    return list(dict.fromkeys(x for x in pool if x))


def protocol_candidates(provider: Dict[str, Any], model: str, vision: bool) -> List[str]:
    capabilities = provider.get("model_capabilities", {})
    model_caps = capabilities.get(model, {}) if isinstance(capabilities, dict) else {}
    key = "vision_protocols" if vision else "text_protocols"
    discovered = [x for x in model_caps.get(key, []) if x in {"responses", "chat", "legacy"}]
    configured = [x for x in provider.get("protocol_order", []) if x in {"responses", "chat", "legacy"}]
    protocols = discovered + configured + ["responses", "chat", "legacy"]
    if vision:
        protocols = [x for x in protocols if x in {"responses", "chat"}]
    return list(dict.fromkeys(protocols))


def messages_have_image(messages: Sequence[Dict[str, Any]]) -> bool:
    for message in messages:
        content = message.get("content")
        if isinstance(content, list):
            for item in content:
                if isinstance(item, dict) and item.get("type") in {"image_url", "input_image"}:
                    return True
    return False


def responses_input_have_image(input_value: Any) -> bool:
    if isinstance(input_value, list):
        for message in input_value:
            if isinstance(message, dict):
                content = message.get("content")
                if isinstance(content, list):
                    for item in content:
                        if isinstance(item, dict) and item.get("type") in {"input_image", "image_url"}:
                            return True
    return False


def convert_chat_to_responses_input(messages: Sequence[Dict[str, Any]]) -> List[Dict[str, Any]]:
    output: List[Dict[str, Any]] = []
    for message in messages:
        role = message.get("role", "user")
        content = message.get("content", "")
        if isinstance(content, str):
            output.append({"role": role, "content": [{"type": "input_text", "text": content}]})
            continue
        parts: List[Dict[str, Any]] = []
        for item in content if isinstance(content, list) else []:
            if not isinstance(item, dict):
                continue
            item_type = item.get("type")
            if item_type in {"text", "input_text"}:
                parts.append({"type": "input_text", "text": item.get("text", "")})
            elif item_type in {"image_url", "input_image"}:
                image_url = item.get("image_url")
                if isinstance(image_url, dict):
                    image_url = image_url.get("url")
                parts.append({"type": "input_image", "image_url": image_url})
        output.append({"role": role, "content": parts})
    return output


def convert_responses_input_to_chat(input_value: Any) -> List[Dict[str, Any]]:
    if isinstance(input_value, str):
        return [{"role": "user", "content": input_value}]
    messages: List[Dict[str, Any]] = []
    if not isinstance(input_value, list):
        return messages
    for message in input_value:
        if not isinstance(message, dict):
            continue
        role = message.get("role", "user")
        content = message.get("content", "")
        if isinstance(content, str):
            messages.append({"role": role, "content": content})
            continue
        parts: List[Dict[str, Any]] = []
        for item in content if isinstance(content, list) else []:
            if not isinstance(item, dict):
                continue
            item_type = item.get("type")
            if item_type in {"input_text", "text"}:
                parts.append({"type": "text", "text": item.get("text", "")})
            elif item_type in {"input_image", "image_url"}:
                image_url = item.get("image_url")
                if isinstance(image_url, dict):
                    image_url = image_url.get("url")
                parts.append({"type": "image_url", "image_url": {"url": image_url}})
        messages.append({"role": role, "content": parts})
    return messages


def flatten_prompt(messages: Sequence[Dict[str, Any]]) -> str:
    lines: List[str] = []
    for message in messages:
        content = message.get("content", "")
        if isinstance(content, str):
            lines.append(f"{message.get('role','user')}: {content}")
    return "\n".join(lines) + "\nassistant:"


def upstream_url_candidates(provider: Dict[str, Any], model: str, protocol: str, vision: bool) -> List[str]:
    capabilities = provider.get("model_capabilities", {})
    model_caps = capabilities.get(model, {}) if isinstance(capabilities, dict) else {}
    url_key = "vision_urls" if vision else "text_urls"
    tested_urls = model_caps.get(url_key, {}) if isinstance(model_caps, dict) else {}
    candidates = list(tested_urls.get(protocol, [])) if isinstance(tested_urls, dict) else []
    suffix = {"responses": "/responses", "legacy": "/completions", "chat": "/chat/completions"}[protocol]
    for root in get_api_roots(provider["base_url"], include_v1_root=True, include_root=True):
        url = root.rstrip("/") + suffix
        if url not in candidates:
            candidates.append(url)
    return candidates


def upstream_call(
    provider: Dict[str, Any],
    model: str,
    protocol: str,
    messages: Sequence[Dict[str, Any]],
    max_tokens: int,
    temperature: float,
    timeout: int,
) -> Dict[str, Any]:
    if protocol == "responses":
        payload = {
            "model": model,
            "input": convert_chat_to_responses_input(messages),
            "max_output_tokens": max_tokens,
        }
        if temperature is not None:
            payload["temperature"] = temperature
    elif protocol == "legacy":
        payload = {
            "model": model,
            "prompt": flatten_prompt(messages),
            "max_tokens": max_tokens,
            "temperature": temperature,
        }
    else:
        payload = {
            "model": model,
            "messages": list(messages),
            "max_tokens": max_tokens,
            "temperature": temperature,
            "stream": False,
        }

    url_attempts: List[Dict[str, Any]] = []
    final_attempt: Optional[Dict[str, Any]] = None
    for url in upstream_url_candidates(provider, model, protocol, messages_have_image(messages)):
        result = do_request("POST", url, provider["api_key"], payload, timeout=timeout)
        attempt: Dict[str, Any] = {
            "provider_id": provider["id"],
            "provider_name": provider["name"],
            "model": model,
            "protocol": protocol,
            "url": url,
            "latency_ms": int(result["elapsed"] * 1000),
            "success": False,
        }
        if not result["network_success"]:
            attempt["error"] = result["error"]
            url_attempts.append({k: v for k, v in attempt.items() if k != "raw"})
            final_attempt = attempt
            continue
        response = result["response"]
        attempt["status_code"] = response.status_code
        if not (200 <= response.status_code < 300):
            attempt["error"] = response_error(response)
            url_attempts.append({k: v for k, v in attempt.items() if k != "raw"})
            final_attempt = attempt
            continue
        data = safe_json(response)
        if data is None:
            attempt["error"] = "HTTP成功但返回内容不是JSON"
            attempt["response_preview"] = body_preview(response)
            url_attempts.append({k: v for k, v in attempt.items() if k != "raw"})
            final_attempt = attempt
            continue
        text = extract_responses_text(data) if protocol == "responses" else extract_chat_text(data)
        if not text and protocol == "legacy":
            try:
                text = str(data["choices"][0]["text"]).strip()
            except Exception:
                text = ""
        if not text:
            attempt["error"] = "未解析到模型回复文本"
            attempt["response_preview"] = safe_text(json_text(data), 500)
            url_attempts.append({k: v for k, v in attempt.items() if k != "raw"})
            final_attempt = attempt
            continue
        attempt["success"] = True
        attempt["answer"] = text
        attempt["raw"] = data
        attempt["url_attempts"] = url_attempts + [{k: v for k, v in attempt.items() if k not in {"raw", "answer", "url_attempts"}}]
        return attempt

    if final_attempt is None:
        final_attempt = {
            "provider_id": provider["id"],
            "provider_name": provider["name"],
            "model": model,
            "protocol": protocol,
            "url": "",
            "latency_ms": 0,
            "success": False,
            "error": "没有可用的请求地址",
        }
    final_attempt["url_attempts"] = url_attempts
    return final_attempt


def log_request(
    client_name: str,
    kind: str,
    requested_model: str,
    attempt: Dict[str, Any],
) -> None:
    with db() as conn:
        conn.execute(
            """
            INSERT INTO request_logs(
                client_name, request_kind, requested_model, provider_id, provider_name,
                resolved_model, protocol, success, latency_ms, error, created_at
            ) VALUES(?,?,?,?,?,?,?,?,?,?,?)
            """,
            (
                client_name,
                kind,
                requested_model,
                attempt.get("provider_id"),
                attempt.get("provider_name"),
                attempt.get("model"),
                attempt.get("protocol"),
                1 if attempt.get("success") else 0,
                int(attempt.get("latency_ms") or 0),
                safe_text(attempt.get("error"), 1000),
                iso_now(),
            ),
        )


def dispatch_chat(
    client_name: str,
    requested_model: str,
    messages: Sequence[Dict[str, Any]],
    max_tokens: int,
    temperature: float,
    timeout: int,
) -> Dict[str, Any]:
    vision = messages_have_image(messages)
    attempts: List[Dict[str, Any]] = []
    for provider in list_providers(include_secret=True, enabled_only=True):
        for model in model_candidates(provider, requested_model, vision):
            for protocol in protocol_candidates(provider, model, vision):
                attempt = upstream_call(provider, model, protocol, messages, max_tokens, temperature, timeout)
                attempts.append({k: v for k, v in attempt.items() if k != "raw"})
                log_request(client_name, "vision" if vision else "text", requested_model, attempt)
                if attempt.get("success"):
                    return {"success": True, "attempt": attempt, "attempts": attempts, "vision": vision}
    return {"success": False, "attempts": attempts, "vision": vision}


def dashboard_data() -> Dict[str, Any]:
    providers = list_providers()
    with db() as conn:
        test_rows = conn.execute(
            """
            SELECT t.*, p.name provider_name
            FROM test_runs t JOIN providers p ON p.id=t.provider_id
            ORDER BY t.id DESC LIMIT 12
            """
        ).fetchall()
        log_rows = conn.execute("SELECT * FROM request_logs ORDER BY id DESC LIMIT 20").fetchall()
        client_count = conn.execute("SELECT COUNT(*) c FROM client_tokens WHERE enabled=1").fetchone()["c"]
    return {
        "providers": providers,
        "recent_tests": [dict(row) for row in test_rows],
        "recent_requests": [dict(row) for row in log_rows],
        "enabled_provider_count": sum(1 for p in providers if p["enabled"]),
        "healthy_provider_count": sum(1 for p in providers if p["last_status"].startswith("可用")),
        "client_count": client_count,
    }


def scheduler_loop() -> None:
    while not SCHEDULER_STOP.wait(max(10, SCHEDULER_POLL_SECONDS)):
        try:
            now = utcnow()
            for provider in list_providers():
                if not provider.get("enabled") or not provider.get("auto_test_enabled"):
                    continue
                next_at = provider.get("next_test_at")
                due = not next_at
                if next_at:
                    try:
                        due = datetime.fromisoformat(next_at.replace("Z", "+00:00")) <= now
                    except Exception:
                        due = True
                if not due:
                    continue
                lock = provider_test_lock(provider["id"])
                if lock.locked():
                    continue
                options = default_deep_test_options()
                options.update(provider.get("auto_test_options") or {})
                options["auto_apply_results"] = True
                run_id = create_test_run(provider["id"], "scheduled", options)
                thread = threading.Thread(
                    target=test_worker,
                    args=(provider["id"], "deep", options, run_id),
                    daemon=True,
                    name=f"provider-test-{provider['id']}",
                )
                thread.start()
                with db() as conn:
                    conn.execute(
                        "UPDATE providers SET next_test_at=?, updated_at=? WHERE id=?",
                        (next_test_time(provider), iso_now(), provider["id"]),
                    )
        except Exception:
            traceback.print_exc()


class LoginInput(BaseModel):
    username: str
    password: str


class ProviderInput(BaseModel):
    name: str = Field(min_length=1, max_length=100)
    base_url: str = Field(min_length=1, max_length=500)
    api_key: Optional[str] = ""
    enabled: bool = True
    priority: int = 100
    main_text_model: str = ""
    backup_text_models: List[str] = Field(default_factory=list)
    main_vision_model: str = ""
    backup_vision_models: List[str] = Field(default_factory=list)
    protocol_order: List[str] = Field(default_factory=lambda: ["responses", "chat", "legacy"])
    auto_test_enabled: bool = False
    auto_test_interval_hours: int = 12
    auto_test_options: Dict[str, Any] = Field(default_factory=dict)


class TestInput(BaseModel):
    mode: str = "ordinary"
    options: Dict[str, Any] = Field(default_factory=dict)


class ClientInput(BaseModel):
    name: str = Field(min_length=1, max_length=100)


app = FastAPI(title="Qianniu AI API Control Plane", version="1.0.0")
app.add_middleware(
    SessionMiddleware,
    secret_key=APP_SECRET,
    same_site="lax",
    https_only=os.getenv("COOKIE_SECURE", "true").lower() in {"1", "true", "yes", "on"},
    max_age=60 * 60 * 12,
)
app.mount("/static", StaticFiles(directory=str(STATIC_DIR)), name="static")

@app.on_event("startup")
def on_startup() -> None:
    init_db()
    if os.getenv("DISABLE_SCHEDULER", "false").lower() not in {"1", "true", "yes"}:
        thread = threading.Thread(target=scheduler_loop, daemon=True, name="deep-test-scheduler")
        thread.start()


@app.on_event("shutdown")
def on_shutdown() -> None:
    SCHEDULER_STOP.set()


@app.get("/", response_class=HTMLResponse)
def index() -> FileResponse:
    return FileResponse(STATIC_DIR / "index.html")


@app.get("/healthz")
def healthz() -> Dict[str, Any]:
    return {"status": "ok", "time": iso_now(), "service": "qianniu-api-control-plane"}


@app.post("/api/admin/login")
def admin_login(data: LoginInput, request: Request) -> Dict[str, Any]:
    if not (
        hmac.compare_digest(data.username, ADMIN_USERNAME)
        and hmac.compare_digest(data.password, ADMIN_PASSWORD)
    ):
        raise HTTPException(status_code=401, detail="用户名或密码错误")
    request.session["admin_username"] = data.username
    return {"ok": True, "username": data.username}


@app.post("/api/admin/logout")
def admin_logout(request: Request) -> Dict[str, Any]:
    request.session.clear()
    return {"ok": True}


@app.get("/api/admin/me")
def admin_me(username: str = Depends(require_admin)) -> Dict[str, Any]:
    return {"username": username}


@app.get("/api/admin/dashboard")
def admin_dashboard(_: str = Depends(require_admin)) -> Dict[str, Any]:
    return dashboard_data()


@app.get("/api/admin/providers")
def admin_list_providers(_: str = Depends(require_admin)) -> List[Dict[str, Any]]:
    return list_providers()


@app.post("/api/admin/providers")
def admin_create_provider(data: ProviderInput, _: str = Depends(require_admin)) -> Dict[str, Any]:
    base_url = normalize_base_url(data.base_url)
    if not base_url.startswith(("http://", "https://")):
        raise HTTPException(status_code=400, detail="BaseUrl 无效")
    options = default_deep_test_options()
    options.update(data.auto_test_options or {})
    now = iso_now()
    with db() as conn:
        cursor = conn.execute(
            """
            INSERT INTO providers(
                name, base_url, api_key_cipher, enabled, priority,
                main_text_model, backup_text_models_json,
                main_vision_model, backup_vision_models_json,
                protocol_order_json, model_capabilities_json,
                auto_test_enabled, auto_test_interval_hours, auto_test_options_json,
                next_test_at, created_at, updated_at
            ) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            """,
            (
                data.name.strip(),
                base_url,
                encrypt_secret((data.api_key or "").strip()),
                1 if data.enabled else 0,
                max(1, data.priority),
                data.main_text_model.strip(),
                json_text(list(dict.fromkeys(x.strip() for x in data.backup_text_models if x.strip()))),
                data.main_vision_model.strip(),
                json_text(list(dict.fromkeys(x.strip() for x in data.backup_vision_models if x.strip()))),
                json_text([x for x in data.protocol_order if x in {"responses", "chat", "legacy"}] or ["responses", "chat", "legacy"]),
                "{}",
                1 if data.auto_test_enabled else 0,
                max(1, min(720, data.auto_test_interval_hours)),
                json_text(options),
                (utcnow() + timedelta(minutes=2)).isoformat(timespec="seconds") if data.auto_test_enabled else None,
                now,
                now,
            ),
        )
        provider_id = int(cursor.lastrowid)
    return get_provider(provider_id)


@app.put("/api/admin/providers/{provider_id}")
def admin_update_provider(provider_id: int, data: ProviderInput, _: str = Depends(require_admin)) -> Dict[str, Any]:
    existing = get_provider(provider_id, include_secret=True)
    base_url = normalize_base_url(data.base_url)
    if not base_url.startswith(("http://", "https://")):
        raise HTTPException(status_code=400, detail="BaseUrl 无效")
    api_key = (data.api_key or "").strip() or existing["api_key"]
    options = default_deep_test_options()
    options.update(data.auto_test_options or {})
    provider_for_next = {
        **existing,
        "auto_test_enabled": data.auto_test_enabled,
        "auto_test_interval_hours": data.auto_test_interval_hours,
    }
    with db() as conn:
        conn.execute(
            """
            UPDATE providers SET
                name=?, base_url=?, api_key_cipher=?, enabled=?, priority=?,
                main_text_model=?, backup_text_models_json=?,
                main_vision_model=?, backup_vision_models_json=?,
                protocol_order_json=?, auto_test_enabled=?,
                auto_test_interval_hours=?, auto_test_options_json=?,
                next_test_at=?, updated_at=?
            WHERE id=?
            """,
            (
                data.name.strip(),
                base_url,
                encrypt_secret(api_key),
                1 if data.enabled else 0,
                max(1, data.priority),
                data.main_text_model.strip(),
                json_text(list(dict.fromkeys(x.strip() for x in data.backup_text_models if x.strip()))),
                data.main_vision_model.strip(),
                json_text(list(dict.fromkeys(x.strip() for x in data.backup_vision_models if x.strip()))),
                json_text([x for x in data.protocol_order if x in {"responses", "chat", "legacy"}] or ["responses", "chat", "legacy"]),
                1 if data.auto_test_enabled else 0,
                max(1, min(720, data.auto_test_interval_hours)),
                json_text(options),
                next_test_time(provider_for_next) if data.auto_test_enabled else None,
                iso_now(),
                provider_id,
            ),
        )
    return get_provider(provider_id)


@app.delete("/api/admin/providers/{provider_id}")
def admin_delete_provider(provider_id: int, _: str = Depends(require_admin)) -> Dict[str, Any]:
    get_provider(provider_id)
    with db() as conn:
        conn.execute("DELETE FROM providers WHERE id=?", (provider_id,))
    return {"ok": True}


@app.post("/api/admin/providers/{provider_id}/tests")
def admin_start_test(
    provider_id: int,
    data: TestInput,
    background_tasks: BackgroundTasks,
    _: str = Depends(require_admin),
) -> Dict[str, Any]:
    get_provider(provider_id)
    mode = "deep" if data.mode == "deep" else "ordinary"
    options = default_deep_test_options()
    options.update(data.options or {})
    if mode == "ordinary":
        options["discover_models"] = False
        options["test_all_discovered_models"] = False
        options["auto_apply_results"] = False
    run_id = create_test_run(provider_id, mode, options)
    background_tasks.add_task(test_worker, provider_id, mode, options, run_id)
    return {"run_id": run_id, "status": "queued"}


@app.get("/api/admin/tests")
def admin_tests(_: str = Depends(require_admin)) -> List[Dict[str, Any]]:
    with db() as conn:
        rows = conn.execute(
            """
            SELECT t.*, p.name provider_name
            FROM test_runs t JOIN providers p ON p.id=t.provider_id
            ORDER BY t.id DESC LIMIT 100
            """
        ).fetchall()
    output: List[Dict[str, Any]] = []
    for row in rows:
        item = dict(row)
        item["options"] = parse_json(item.pop("options_json"), {})
        item.pop("result_json", None)
        item.pop("analysis_markdown", None)
        output.append(item)
    return output


@app.get("/api/admin/tests/{run_id}")
def admin_test(run_id: int, _: str = Depends(require_admin)) -> Dict[str, Any]:
    with db() as conn:
        row = conn.execute(
            """
            SELECT t.*, p.name provider_name
            FROM test_runs t JOIN providers p ON p.id=t.provider_id
            WHERE t.id=?
            """,
            (run_id,),
        ).fetchone()
    if not row:
        raise HTTPException(status_code=404, detail="测试记录不存在")
    item = dict(row)
    item["options"] = parse_json(item.pop("options_json"), {})
    item["result"] = parse_json(item.pop("result_json"), None)
    return item


@app.get("/api/admin/tests/{run_id}/raw")
def admin_test_raw(run_id: int, _: str = Depends(require_admin)) -> Response:
    with db() as conn:
        row = conn.execute("SELECT result_json FROM test_runs WHERE id=?", (run_id,)).fetchone()
    result = parse_json(row["result_json"], None) if row else None
    if not result:
        raise HTTPException(status_code=404, detail="测试结果尚未生成")
    return Response(
        json.dumps(result, ensure_ascii=False, indent=2),
        media_type="application/json",
        headers={"Content-Disposition": f'attachment; filename="api_test_raw_{run_id}.json"'},
    )


@app.get("/api/admin/tests/{run_id}/report")
def admin_test_report(run_id: int, _: str = Depends(require_admin)) -> Response:
    with db() as conn:
        row = conn.execute("SELECT analysis_markdown FROM test_runs WHERE id=?", (run_id,)).fetchone()
    if not row or not row["analysis_markdown"]:
        raise HTTPException(status_code=404, detail="分析报告尚未生成")
    return Response(
        row["analysis_markdown"],
        media_type="text/markdown; charset=utf-8",
        headers={"Content-Disposition": f'attachment; filename="api_test_analysis_cn_{run_id}.md"'},
    )

@app.get("/api/admin/clients")
def admin_clients(_: str = Depends(require_admin)) -> List[Dict[str, Any]]:
    with db() as conn:
        rows = conn.execute("SELECT id,name,token_prefix,enabled,created_at,last_used_at FROM client_tokens ORDER BY id DESC").fetchall()
    return [{**dict(row), "enabled": bool(row["enabled"])} for row in rows]


@app.post("/api/admin/clients")
def admin_create_client(data: ClientInput, _: str = Depends(require_admin)) -> Dict[str, Any]:
    token = "qnb_" + secrets.token_urlsafe(32)
    with db() as conn:
        cursor = conn.execute(
            """
            INSERT INTO client_tokens(name, token_hash, token_prefix, enabled, created_at)
            VALUES(?,?,?,?,?)
            """,
            (data.name.strip(), hash_token(token), token[:12], 1, iso_now()),
        )
        client_id = int(cursor.lastrowid)
    return {"id": client_id, "name": data.name.strip(), "token": token, "warning": "令牌只显示这一次，请立即保存到 Bot。"}


@app.post("/api/admin/clients/{client_id}/toggle")
def admin_toggle_client(client_id: int, _: str = Depends(require_admin)) -> Dict[str, Any]:
    with db() as conn:
        row = conn.execute("SELECT enabled FROM client_tokens WHERE id=?", (client_id,)).fetchone()
        if not row:
            raise HTTPException(status_code=404, detail="客户端不存在")
        enabled = 0 if row["enabled"] else 1
        conn.execute("UPDATE client_tokens SET enabled=? WHERE id=?", (enabled, client_id))
    return {"ok": True, "enabled": bool(enabled)}


@app.delete("/api/admin/clients/{client_id}")
def admin_delete_client(client_id: int, _: str = Depends(require_admin)) -> Dict[str, Any]:
    with db() as conn:
        conn.execute("DELETE FROM client_tokens WHERE id=?", (client_id,))
    return {"ok": True}


@app.get("/api/runtime/v1/config")
def runtime_config(client: Dict[str, Any] = Depends(require_client)) -> Dict[str, Any]:
    providers = list_providers(enabled_only=True)
    return {
        "service": "qianniu-api-control-plane",
        "client": client["name"],
        "text_route": "text-default",
        "vision_route": "vision-default",
        "providers": [
            {
                "name": p["name"],
                "status": p["last_status"],
                "main_text_model": p["main_text_model"],
                "main_vision_model": p["main_vision_model"],
                "protocol_order": p["protocol_order"],
            }
            for p in providers
        ],
    }


@app.post("/v1/chat/completions")
async def runtime_chat(request: Request, client: Dict[str, Any] = Depends(require_client)) -> Response:
    payload = await request.json()
    messages = payload.get("messages")
    if not isinstance(messages, list) or not messages:
        raise HTTPException(status_code=400, detail="messages 不能为空")
    requested_model = str(payload.get("model") or "text-default")
    max_tokens = int(payload.get("max_tokens") or payload.get("max_completion_tokens") or 512)
    temperature = float(payload.get("temperature") if payload.get("temperature") is not None else 0.2)
    timeout = int(payload.get("timeout_seconds") or REQUEST_TIMEOUT_SECONDS)
    dispatched = await run_in_threadpool(
        dispatch_chat,
        client["name"],
        requested_model,
        messages,
        max(1, min(32000, max_tokens)),
        temperature,
        max(5, min(300, timeout)),
        int(client["id"]),
        "",
        "/v1/chat/completions",
    )
    if not dispatched["success"]:
        return JSONResponse(
            status_code=502,
            content={"error": {"message": "所有供应商、模型和请求协议均调用失败", "type": "upstream_exhausted", "attempts": dispatched["attempts"]}},
        )
    attempt = dispatched["attempt"]
    answer = attempt["answer"]
    body = {
        "id": "chatcmpl_" + uuid.uuid4().hex,
        "object": "chat.completion",
        "created": int(time.time()),
        "model": attempt["model"],
        "choices": [{"index": 0, "message": {"role": "assistant", "content": answer}, "finish_reason": "stop"}],
        "usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0},
        "qianniu_routing": {
            "provider": attempt["provider_name"],
            "protocol": attempt["protocol"],
            "latency_ms": attempt["latency_ms"],
            "fallback_attempts": len(dispatched["attempts"]) - 1,
        },
    }
    return JSONResponse(content=body)


@app.post("/v1/responses")
async def runtime_responses(request: Request, client: Dict[str, Any] = Depends(require_client)) -> Response:
    payload = await request.json()
    input_value = payload.get("input")
    messages = convert_responses_input_to_chat(input_value)
    if not messages:
        raise HTTPException(status_code=400, detail="input 不能为空")
    requested_model = str(payload.get("model") or "text-default")
    max_tokens = int(payload.get("max_output_tokens") or 512)
    temperature = float(payload.get("temperature") if payload.get("temperature") is not None else 0.2)
    dispatched = await run_in_threadpool(
        dispatch_chat,
        client["name"],
        requested_model,
        messages,
        max(1, min(32000, max_tokens)),
        temperature,
        REQUEST_TIMEOUT_SECONDS,
        int(client["id"]),
        "",
        "/v1/responses",
    )
    if not dispatched["success"]:
        return JSONResponse(
            status_code=502,
            content={"error": {"message": "所有供应商、模型和请求协议均调用失败", "type": "upstream_exhausted", "attempts": dispatched["attempts"]}},
        )
    attempt = dispatched["attempt"]
    answer = attempt["answer"]
    body = {
        "id": "resp_" + uuid.uuid4().hex,
        "object": "response",
        "created_at": int(time.time()),
        "status": "completed",
        "model": attempt["model"],
        "output_text": answer,
        "output": [
            {
                "id": "msg_" + uuid.uuid4().hex,
                "type": "message",
                "status": "completed",
                "role": "assistant",
                "content": [{"type": "output_text", "text": answer, "annotations": []}],
            }
        ],
        "qianniu_routing": {
            "provider": attempt["provider_name"],
            "protocol": attempt["protocol"],
            "latency_ms": attempt["latency_ms"],
            "fallback_attempts": len(dispatched["attempts"]) - 1,
        },
    }
    return JSONResponse(content=body)


if __name__ == "__main__":
    import uvicorn

    uvicorn.run("app:app", host="0.0.0.0", port=int(os.getenv("PORT", "8080")), reload=False)
