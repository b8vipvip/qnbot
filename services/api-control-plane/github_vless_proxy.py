from __future__ import annotations

import json
import os
import socket
import subprocess
import threading
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional, Tuple
from urllib.parse import parse_qs, unquote, urlsplit

from curl_cffi import requests as curl_requests
from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import JSONResponse

import bot_update_progress


router = APIRouter()
_cp: Any = None

LISTEN_HOST = "127.0.0.1"
LISTEN_PORT = max(1024, min(65535, int(os.getenv("GITHUB_VLESS_PROXY_PORT", "11808"))))
LOCAL_PROXY = f"socks5h://{LISTEN_HOST}:{LISTEN_PORT}"
SING_BOX_BIN = os.getenv("SING_BOX_BIN", "/usr/local/bin/sing-box").strip()
CONFIG_PATH = Path(os.getenv("GITHUB_VLESS_PROXY_CONFIG", "/tmp/qianniu-github-vless-proxy.json"))
LOG_PATH = Path(os.getenv("GITHUB_VLESS_PROXY_LOG", "/tmp/qianniu-github-vless-proxy.log"))
GITHUB_TEST_URL = "https://api.github.com/repos/b8vipvip/qnbot/releases/latest"

_LOCK = threading.RLock()
_PROCESS: Optional[subprocess.Popen[Any]] = None
_LOG_HANDLE: Optional[Any] = None
_LAST_ERROR = ""
_LAST_TEST: Dict[str, Any] = {}


def install(control_plane: Any) -> None:
    global _cp
    _cp = control_plane
    control_plane.app.include_router(router)


def _admin(request: Request) -> str:
    return _cp.require_admin(request)


def _utcnow() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def _safe(value: Any, limit: int = 300) -> str:
    text = str(value or "").replace("\r", " ").replace("\n", " ").strip()
    return text if len(text) <= limit else text[:limit] + "..."


def _query_values(query: str) -> Dict[str, str]:
    raw = parse_qs(query, keep_blank_values=True)
    return {key: unquote(values[-1]) for key, values in raw.items() if values}


def _bool_value(value: str) -> bool:
    return str(value or "").strip().lower() in {"1", "true", "yes", "on"}


def _parse_vless_url(vless_url: str) -> Tuple[Dict[str, Any], Dict[str, Any]]:
    value = str(vless_url or "").strip()
    if not value.lower().startswith("vless://"):
        raise ValueError("节点链接必须以 vless:// 开头")
    try:
        parsed = urlsplit(value)
        server = parsed.hostname or ""
        port = int(parsed.port or 443)
    except Exception as exc:
        raise ValueError("VLESS 节点地址或端口无效") from exc
    user_id = unquote(parsed.username or "").strip()
    if not server or not user_id:
        raise ValueError("VLESS 节点缺少服务器地址或 UUID")
    try:
        uuid.UUID(user_id)
    except Exception as exc:
        raise ValueError("VLESS UUID 格式无效") from exc
    if port < 1 or port > 65535:
        raise ValueError("VLESS 端口范围无效")

    q = _query_values(parsed.query)

    def get(*names: str, default: str = "") -> str:
        for name in names:
            if name in q and str(q[name]).strip() != "":
                return str(q[name]).strip()
        return default

    outbound: Dict[str, Any] = {
        "type": "vless",
        "tag": "vless-out",
        "server": server,
        "server_port": port,
        "uuid": user_id,
    }

    flow = get("flow")
    if flow and flow not in {"xtls-rprx-vision"}:
        raise ValueError(f"暂不支持此 VLESS flow：{flow}")
    if flow:
        outbound["flow"] = flow

    packet_encoding = get("packetEncoding", "packet_encoding").lower()
    if packet_encoding:
        if packet_encoding not in {"xudp", "packetaddr"}:
            raise ValueError(f"暂不支持此 VLESS packetEncoding：{packet_encoding}")
        outbound["packet_encoding"] = packet_encoding

    security = get("security", default="none").lower()
    if security not in {"none", "tls", "reality"}:
        raise ValueError(f"暂不支持此 VLESS security：{security}")
    if security in {"tls", "reality"}:
        tls: Dict[str, Any] = {
            "enabled": True,
            "server_name": get("sni", default=server),
        }
        fingerprint = get("fp")
        if fingerprint:
            tls["utls"] = {"enabled": True, "fingerprint": fingerprint}
        alpn = get("alpn")
        if alpn:
            tls["alpn"] = [part.strip() for part in alpn.split(",") if part.strip()]
        if _bool_value(get("allowInsecure", "insecure")):
            tls["insecure"] = True
        if security == "reality":
            public_key = get("pbk", "publicKey")
            short_id = get("sid", "shortId")
            if not public_key:
                raise ValueError("Reality 节点缺少 pbk/publicKey")
            reality: Dict[str, Any] = {"enabled": True, "public_key": public_key}
            if short_id:
                reality["short_id"] = short_id
            tls["reality"] = reality
        outbound["tls"] = tls

    transport_type = get("type", default="tcp").lower()
    if transport_type in {"tcp", "none", "raw"}:
        header_type = get("headerType", "header_type", default="none").lower()
        if header_type not in {"", "none"}:
            raise ValueError(f"sing-box 不支持此 TCP headerType：{header_type}")
    elif transport_type in {"ws", "websocket"}:
        transport: Dict[str, Any] = {"type": "ws", "path": get("path", default="/")}
        host = get("host")
        if host:
            transport["headers"] = {"Host": host}
        early_data = get("ed", "max_early_data")
        if early_data:
            try:
                transport["max_early_data"] = max(0, int(early_data))
            except ValueError as exc:
                raise ValueError("WebSocket early-data 参数无效") from exc
            if int(transport["max_early_data"]) > 0:
                transport["early_data_header_name"] = get("eh", "early_data_header_name", default="Sec-WebSocket-Protocol")
        outbound["transport"] = transport
        transport_type = "ws"
    elif transport_type == "grpc":
        outbound["transport"] = {
            "type": "grpc",
            "service_name": get("serviceName", "service_name"),
        }
    elif transport_type in {"httpupgrade", "http-upgrade"}:
        transport = {"type": "httpupgrade", "path": get("path", default="/")}
        host = get("host")
        if host:
            transport["host"] = host
        outbound["transport"] = transport
        transport_type = "httpupgrade"
    elif transport_type in {"http", "h2"}:
        transport = {"type": "http", "path": get("path", default="/")}
        host = get("host")
        if host:
            transport["host"] = [part.strip() for part in host.split(",") if part.strip()]
        outbound["transport"] = transport
        transport_type = "http"
    elif transport_type == "quic":
        outbound["transport"] = {"type": "quic"}
    else:
        raise ValueError(f"暂不支持此 VLESS 传输类型：{transport_type}")

    summary = {
        "name": unquote(parsed.fragment or "").strip(),
        "server": server,
        "port": port,
        "security": security,
        "transport": transport_type,
        "flow": flow,
    }
    return outbound, summary


def _sing_box_config(vless_url: str) -> Tuple[Dict[str, Any], Dict[str, Any]]:
    outbound, summary = _parse_vless_url(vless_url)
    return {
        "log": {"level": "error", "timestamp": True},
        "inbounds": [
            {
                "type": "socks",
                "tag": "github-socks",
                "listen": LISTEN_HOST,
                "listen_port": LISTEN_PORT,
            }
        ],
        "outbounds": [outbound],
        "route": {"final": "vless-out"},
    }, summary


def _init_db() -> None:
    with _cp.db() as conn:
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS version_update_proxy_settings(
              id INTEGER PRIMARY KEY CHECK(id=1),
              enabled INTEGER NOT NULL DEFAULT 0,
              vless_url_enc TEXT NOT NULL DEFAULT '',
              updated_at TEXT NOT NULL DEFAULT ''
            )
            """
        )
        conn.commit()


def _read_row() -> Optional[Dict[str, Any]]:
    with _cp.db() as conn:
        row = conn.execute(
            "SELECT enabled,vless_url_enc,updated_at FROM version_update_proxy_settings WHERE id=1"
        ).fetchone()
    return dict(row) if row is not None else None


def _decrypt_url(row: Optional[Dict[str, Any]]) -> str:
    if not row or not str(row.get("vless_url_enc") or ""):
        return ""
    try:
        return str(_cp.decrypt_secret(str(row["vless_url_enc"]))).strip()
    except Exception as exc:
        raise RuntimeError("已保存的 VLESS 节点无法解密，请重新保存节点") from exc


def _persist(enabled: bool, vless_url: str) -> None:
    encrypted = _cp.encrypt_secret(vless_url) if vless_url else ""
    with _cp.db() as conn:
        conn.execute(
            """
            INSERT INTO version_update_proxy_settings(id,enabled,vless_url_enc,updated_at)
            VALUES(1,?,?,?)
            ON CONFLICT(id) DO UPDATE SET
              enabled=excluded.enabled,
              vless_url_enc=excluded.vless_url_enc,
              updated_at=excluded.updated_at
            """,
            (1 if enabled else 0, encrypted, _utcnow()),
        )
        conn.commit()


def _stop_process_locked(remove_config: bool = True) -> None:
    global _PROCESS, _LOG_HANDLE
    process = _PROCESS
    _PROCESS = None
    if process is not None and process.poll() is None:
        try:
            process.terminate()
            process.wait(timeout=3)
        except Exception:
            try:
                process.kill()
                process.wait(timeout=2)
            except Exception:
                pass
    if _LOG_HANDLE is not None:
        try:
            _LOG_HANDLE.close()
        except Exception:
            pass
        _LOG_HANDLE = None
    if remove_config:
        CONFIG_PATH.unlink(missing_ok=True)


def _wait_listener(process: subprocess.Popen[Any], timeout_seconds: float = 6.0) -> None:
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"sing-box 启动失败，退出码 {process.returncode}")
        try:
            with socket.create_connection((LISTEN_HOST, LISTEN_PORT), timeout=0.25):
                return
        except OSError:
            time.sleep(0.1)
    raise RuntimeError("sing-box 本地 SOCKS 监听启动超时")


def _start_process_locked(vless_url: str) -> Dict[str, Any]:
    global _PROCESS, _LOG_HANDLE
    config, summary = _sing_box_config(vless_url)
    if not Path(SING_BOX_BIN).is_file():
        raise RuntimeError("控制面镜像缺少 sing-box，需先更新服务端版本")
    _stop_process_locked(remove_config=False)
    CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
    CONFIG_PATH.write_text(json.dumps(config, ensure_ascii=False, indent=2), encoding="utf-8")
    os.chmod(CONFIG_PATH, 0o600)
    check = subprocess.run(
        [SING_BOX_BIN, "check", "-c", str(CONFIG_PATH)],
        capture_output=True,
        text=True,
        timeout=12,
        check=False,
    )
    if check.returncode != 0:
        CONFIG_PATH.unlink(missing_ok=True)
        detail = _safe(check.stderr or check.stdout or "配置校验失败", 500)
        raise RuntimeError("VLESS 节点配置校验失败：" + detail)
    LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
    _LOG_HANDLE = LOG_PATH.open("a", encoding="utf-8")
    _PROCESS = subprocess.Popen(
        [SING_BOX_BIN, "run", "-c", str(CONFIG_PATH)],
        stdin=subprocess.DEVNULL,
        stdout=_LOG_HANDLE,
        stderr=subprocess.STDOUT,
        close_fds=True,
    )
    try:
        _wait_listener(_PROCESS)
    except Exception:
        _stop_process_locked()
        raise
    return summary


def _process_running() -> bool:
    process = _PROCESS
    return bool(process is not None and process.poll() is None)


def _apply_saved_locked() -> None:
    global _LAST_ERROR
    row = _read_row()
    if row is None:
        _stop_process_locked()
        bot_update_progress.set_github_proxy(None)
        _LAST_ERROR = ""
        return
    if not bool(row.get("enabled")):
        _stop_process_locked()
        bot_update_progress.set_github_proxy("")
        _LAST_ERROR = ""
        return
    try:
        vless_url = _decrypt_url(row)
        if not vless_url:
            raise RuntimeError("已启用 GitHub 下载代理，但尚未保存 VLESS 节点")
        _start_process_locked(vless_url)
        bot_update_progress.set_github_proxy(LOCAL_PROXY)
        _LAST_ERROR = ""
    except Exception as exc:
        _stop_process_locked()
        bot_update_progress.set_github_proxy("")
        _LAST_ERROR = _safe(exc, 500)
        raise


def _status_locked() -> Dict[str, Any]:
    row = _read_row()
    configured = bool(row and str(row.get("vless_url_enc") or ""))
    enabled = bool(row and row.get("enabled"))
    summary: Dict[str, Any] = {}
    decrypt_error = ""
    if configured:
        try:
            _, summary = _parse_vless_url(_decrypt_url(row))
        except Exception as exc:
            decrypt_error = _safe(exc, 300)
    error = _LAST_ERROR or decrypt_error
    return {
        "enabled": enabled,
        "configured": configured,
        "running": bool(enabled and _process_running() and bot_update_progress.github_proxy() == LOCAL_PROXY),
        "node": summary,
        "updated_at": str(row.get("updated_at") or "") if row else "",
        "last_error": error,
        "last_test": dict(_LAST_TEST),
        "scope": "control-plane-and-host-git-github-https",
        "isolation": {
            "listen": f"{LISTEN_HOST}:{LISTEN_PORT}",
            "tun": False,
            "system_proxy": False,
            "server_route_changed": False,
        },
    }


def init_github_vless_proxy() -> None:
    global _LAST_ERROR
    with _LOCK:
        _init_db()
        try:
            _apply_saved_locked()
        except Exception as exc:
            _LAST_ERROR = _safe(exc, 500)


def stop_github_vless_proxy() -> None:
    with _LOCK:
        _stop_process_locked()


@router.get("/api/admin/version-update/proxy")
def get_proxy_settings(request: Request, _: str = Depends(_admin)) -> JSONResponse:
    with _LOCK:
        return JSONResponse(_status_locked(), headers={"Cache-Control": "no-store"})


@router.put("/api/admin/version-update/proxy")
async def save_proxy_settings(request: Request, _: str = Depends(_admin)) -> JSONResponse:
    global _LAST_ERROR
    try:
        payload = await request.json()
    except Exception as exc:
        raise HTTPException(status_code=400, detail="请求 JSON 无效") from exc
    if not isinstance(payload, dict):
        raise HTTPException(status_code=400, detail="请求 JSON 无效")
    clear = bool(payload.get("clear"))
    enabled = bool(payload.get("enabled"))
    supplied = str(payload.get("vless_url") or "").strip()
    with _LOCK:
        _init_db()
        row = _read_row()
        current = ""
        if row and str(row.get("vless_url_enc") or ""):
            try:
                current = _decrypt_url(row)
            except Exception:
                current = ""
        if clear:
            _persist(False, "")
            _stop_process_locked()
            bot_update_progress.set_github_proxy("")
            _LAST_ERROR = ""
            return JSONResponse(_status_locked(), headers={"Cache-Control": "no-store"})
        candidate = supplied or current
        if supplied:
            try:
                _parse_vless_url(supplied)
            except ValueError as exc:
                raise HTTPException(status_code=422, detail=str(exc)) from exc
        if enabled and not candidate:
            raise HTTPException(status_code=422, detail="启用代理前请先填写 vless:// 节点链接")
        _persist(enabled, candidate)
        try:
            _apply_saved_locked()
        except Exception as exc:
            raise HTTPException(status_code=502, detail="VLESS 代理启动失败：" + _safe(exc, 300)) from exc
        return JSONResponse(_status_locked(), headers={"Cache-Control": "no-store"})


@router.post("/api/admin/version-update/proxy/test")
def test_proxy_settings(request: Request, _: str = Depends(_admin)) -> JSONResponse:
    global _LAST_TEST, _LAST_ERROR
    with _LOCK:
        status = _status_locked()
        if not status.get("enabled") or not status.get("configured"):
            raise HTTPException(status_code=409, detail="请先保存并启用 VLESS GitHub 下载代理")
        if not status.get("running"):
            try:
                _apply_saved_locked()
            except Exception as exc:
                raise HTTPException(status_code=502, detail="VLESS 代理未运行：" + _safe(exc, 260)) from exc
    started = time.monotonic()
    response = None
    try:
        response = curl_requests.get(
            GITHUB_TEST_URL,
            headers={"Accept": "application/vnd.github+json", "User-Agent": "QianniuAiBot-ProxyTest/1.0"},
            timeout=(10, 20),
            allow_redirects=True,
            impersonate="chrome",
            proxies={"http": LOCAL_PROXY, "https": LOCAL_PROXY},
        )
        if response.status_code < 200 or response.status_code >= 300:
            raise RuntimeError(f"GitHub 返回 HTTP {response.status_code}")
        elapsed_ms = int((time.monotonic() - started) * 1000)
        with _LOCK:
            _LAST_TEST = {"ok": True, "latency_ms": elapsed_ms, "tested_at": _utcnow()}
            _LAST_ERROR = ""
            snapshot = _status_locked()
        return JSONResponse(snapshot, headers={"Cache-Control": "no-store"})
    except Exception as exc:
        elapsed_ms = int((time.monotonic() - started) * 1000)
        with _LOCK:
            _LAST_TEST = {"ok": False, "latency_ms": elapsed_ms, "tested_at": _utcnow(), "error": _safe(exc, 240)}
            _LAST_ERROR = "代理测试失败：" + _safe(exc, 300)
        raise HTTPException(status_code=502, detail=_LAST_ERROR) from exc
    finally:
        if response is not None:
            try:
                response.close()
            except Exception:
                pass
