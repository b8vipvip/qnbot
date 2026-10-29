from __future__ import annotations

import json
import os
import threading
import time
from pathlib import Path
from typing import Any, Dict, Optional

from curl_cffi import requests as curl_requests
from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import JSONResponse

import bot_update_cache
import bot_update_progress
import bot_update_push


router = APIRouter()
_cp: Any = None

REPOSITORY = "b8vipvip/qnbot"
GITHUB_MASTER_API = f"https://api.github.com/repos/{REPOSITORY}/commits/master"
DATA_DIR = Path(os.getenv("DATA_DIR", "/data")).resolve()
SERVER_UPDATE_DIR = DATA_DIR / "server-update"
SERVER_UPDATE_REQUEST = SERVER_UPDATE_DIR / "request.json"
SERVER_UPDATE_STATUS = SERVER_UPDATE_DIR / "status.json"
SERVER_UPDATE_AGENT = SERVER_UPDATE_DIR / "agent.json"
SERVER_UPDATE_LOG = SERVER_UPDATE_DIR / "update.log"
SERVER_GITHUB_CACHE_SECONDS = max(15, min(600, int(os.getenv("SERVER_UPDATE_GITHUB_CACHE_SECONDS", "60"))))
SERVER_UPDATE_AGENT_STALE_SECONDS = max(5, min(120, int(os.getenv("SERVER_UPDATE_AGENT_STALE_SECONDS", "15"))))
SERVER_GITHUB_SYNC_ATTEMPTS = max(1, min(8, int(os.getenv("SERVER_UPDATE_GITHUB_SYNC_ATTEMPTS", "4"))))

_SERVER_CACHE_LOCK = threading.RLock()
_SERVER_CACHE: Optional[Dict[str, Any]] = None
_SERVER_CACHE_AT = 0.0


def install(control_plane: Any) -> None:
    global _cp
    _cp = control_plane
    SERVER_UPDATE_DIR.mkdir(parents=True, exist_ok=True)
    control_plane.app.include_router(router)


def _admin(request: Request) -> str:
    return _cp.require_admin(request)


def _safe(value: Any, limit: int = 500) -> str:
    text = str(value or "").replace("\r", " ").replace("\n", " ").strip()
    return text if len(text) <= limit else text[:limit] + "..."


def _headers() -> Dict[str, str]:
    return {
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "QianniuAiBot-VersionConsole/1.1",
    }


def _read_json(path: Path) -> Dict[str, Any]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
        return payload if isinstance(payload, dict) else {}
    except Exception:
        return {}


def _write_json_atomic(path: Path, payload: Dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    temp.replace(path)


def _fetch_server_github(force: bool = False) -> Dict[str, Any]:
    global _SERVER_CACHE, _SERVER_CACHE_AT
    now = time.time()
    with _SERVER_CACHE_LOCK:
        if not force and _SERVER_CACHE is not None and now - _SERVER_CACHE_AT <= SERVER_GITHUB_CACHE_SECONDS:
            return dict(_SERVER_CACHE)
        last_error: Optional[Exception] = None
        for attempt in range(1, SERVER_GITHUB_SYNC_ATTEMPTS + 1):
            response = None
            try:
                response = curl_requests.get(
                    GITHUB_MASTER_API,
                    headers=_headers(),
                    timeout=(bot_update_progress.DOWNLOAD_CONNECT_TIMEOUT_SECONDS, 60),
                    allow_redirects=True,
                    impersonate="chrome",
                    **bot_update_progress._request_proxy_kwargs(),
                )
                if response.status_code < 200 or response.status_code >= 300:
                    raise RuntimeError(f"GitHub master 返回 HTTP {response.status_code}")
                payload = response.json()
                if not isinstance(payload, dict) or not payload.get("sha"):
                    raise RuntimeError("GitHub master 返回格式无效")
                commit = payload.get("commit") if isinstance(payload.get("commit"), dict) else {}
                author = commit.get("author") if isinstance(commit.get("author"), dict) else {}
                item = {
                    "sha": str(payload.get("sha") or ""),
                    "short_sha": str(payload.get("sha") or "")[:12],
                    "message": str(commit.get("message") or "").split("\n", 1)[0],
                    "committed_at": str(author.get("date") or ""),
                    "html_url": str(payload.get("html_url") or ""),
                    "branch": "master",
                    "transport": "https-proxy" if bot_update_progress.github_proxy() else "https-direct",
                    "attempt": attempt,
                }
                _SERVER_CACHE = dict(item)
                _SERVER_CACHE_AT = now
                return item
            except Exception as exc:
                last_error = exc
                if attempt < SERVER_GITHUB_SYNC_ATTEMPTS:
                    time.sleep(min(8, bot_update_progress._retry_delay(attempt)))
            finally:
                if response is not None:
                    try:
                        response.close()
                    except Exception:
                        pass
        raise RuntimeError(f"GitHub master 连续同步失败 {SERVER_GITHUB_SYNC_ATTEMPTS} 次：{last_error}")


def _agent_status() -> Dict[str, Any]:
    item = _read_json(SERVER_UPDATE_AGENT)
    try:
        age = max(0.0, time.time() - SERVER_UPDATE_AGENT.stat().st_mtime)
    except Exception:
        age = 999999.0
    item["online"] = bool(item and age <= SERVER_UPDATE_AGENT_STALE_SECONDS)
    item["age_seconds"] = int(age) if age < 999999 else None
    item.setdefault("git_transport", "ssh-443")
    return item


def _server_update_status() -> Dict[str, Any]:
    status = _read_json(SERVER_UPDATE_STATUS)
    if not status:
        status = {
            "state": "idle",
            "phase": "等待更新",
            "progress_percent": 0,
            "message": "尚未执行服务端在线更新",
        }
    status["agent"] = _agent_status()
    now = time.time()
    status_stamp = float(status.get("updated_at_unix") or 0)
    status["status_age_seconds"] = max(0, int(now - status_stamp)) if status_stamp else None
    try:
        last_build_output = SERVER_UPDATE_LOG.stat().st_mtime
    except OSError:
        last_build_output = 0
    status["log_age_seconds"] = max(0, int(now - last_build_output)) if last_build_output else None
    status["log_size_bytes"] = SERVER_UPDATE_LOG.stat().st_size if last_build_output else 0
    state = str(status.get("state") or "")
    phase = str(status.get("phase") or "")
    building = state == "running" and ("构建" in phase or int(status.get("progress_percent") or 0) == 35)
    silent = status["log_age_seconds"]
    status["build_suspected_stalled"] = bool(building and silent is not None and silent >= 300)
    status["build_phase_not_percentage"] = building
    if state in {"queued", "running"} and not status["agent"].get("online"):
        status["diagnostic"] = "更新代理离线，请检查 systemd 服务；不可据此推断旧服务已经停止。"
    elif status["build_suspected_stalled"]:
        status["diagnostic"] = "构建日志已超过 5 分钟无输出，检查网络/磁盘/BuildKit 或构建超时；旧服务在构建阶段继续运行。"
    elif building:
        status["diagnostic"] = "35% 表示处于 Docker 镜像构建阶段，不是已经完成了 35% 的构建。"
    else:
        status["diagnostic"] = ""
    return status


def _runtime_build_commit() -> str:
    """Revision baked into THIS process's image; never infer it from host Git."""
    value = str(os.getenv("CONTROL_PLANE_BUILD_COMMIT", "")).strip()
    if len(value) == 40 and all(ch in "0123456789abcdef" for ch in value.lower()):
        return value.lower()
    return ""


def _current_server_commit(status: Dict[str, Any]) -> str:
    # Prefer an immutable image stamp. The host updater can checkout new code
    # while the old container is still serving production traffic.
    return _runtime_build_commit() or str(status.get("current_commit") or "").strip()


def _client_release(request: Request, refresh: bool = False) -> Dict[str, Any]:
    metadata = bot_update_cache.refresh_latest_metadata() if refresh else bot_update_cache.get_latest_metadata()
    package = bot_update_cache.get_cached_package_status(metadata)
    public = bot_update_cache._public_metadata(metadata, request)
    return {
        "version": public.get("version") or "",
        "tag": public.get("tag") or "",
        "name": public.get("name") or "",
        "notes": public.get("notes") or "",
        "published_at": public.get("published_at") or "",
        "commit": public.get("commit") or "",
        "sha256": public.get("sha256") or "",
        "size": int(public.get("size") or 0),
        "html_url": public.get("html_url") or "",
        "mirror_url": public.get("mirror_url") or "",
        "package": package,
        "push": bot_update_push.get_push_status(),
        "network": {
            "transport": "https-range-resume",
            "proxy_enabled": bool(bot_update_progress.github_proxy()),
            "max_attempts": bot_update_progress.DOWNLOAD_MAX_ATTEMPTS,
            "connect_timeout_seconds": bot_update_progress.DOWNLOAD_CONNECT_TIMEOUT_SECONDS,
            "read_timeout_seconds": bot_update_progress.DOWNLOAD_READ_TIMEOUT_SECONDS,
        },
    }


def _snapshot(request: Request, refresh: bool = False) -> Dict[str, Any]:
    server_status = _server_update_status()
    server_error = ""
    github: Dict[str, Any] = {}
    try:
        github = _fetch_server_github(force=refresh)
    except Exception as exc:
        server_error = _safe(exc, 240)
    running_image_commit = _runtime_build_commit()
    recorded_commit = str(server_status.get("current_commit") or "").strip()
    current = _current_server_commit(server_status)
    latest = str(github.get("sha") or "")
    update_available = bool(current and latest and current != latest)
    client_error = ""
    client: Dict[str, Any] = {}
    try:
        client = _client_release(request, refresh=refresh)
    except Exception as exc:
        client_error = _safe(exc, 240)
    return {
        "server": {
            "current_commit": current,
            "current_short_sha": current[:12] if current else "",
            "running_image_commit": running_image_commit,
            "recorded_commit": recorded_commit,
            "version_source": "running_image" if running_image_commit else "updater_status",
            "github": github,
            "update_available": update_available,
            "sync_error": server_error,
            "update": server_status,
            "network": {
                "git_transport": str(server_status.get("agent", {}).get("git_transport") or "ssh-443"),
                "git_fetch_attempts": int(server_status.get("agent", {}).get("git_fetch_attempts") or 5),
            },
        },
        "client": {**client, "sync_error": client_error},
        "synced_at_unix": time.time(),
    }


@router.get("/api/admin/version-update/status")
def version_update_status(request: Request, _: str = Depends(_admin)) -> JSONResponse:
    return JSONResponse(_snapshot(request, refresh=False), headers={"Cache-Control": "no-store"})


@router.post("/api/admin/version-update/sync")
def version_update_sync(request: Request, _: str = Depends(_admin)) -> JSONResponse:
    return JSONResponse(_snapshot(request, refresh=True), headers={"Cache-Control": "no-store"})


@router.post("/api/admin/version-update/server/start")
def version_update_server_start(request: Request, _: str = Depends(_admin)) -> JSONResponse:
    agent = _agent_status()
    if not agent.get("online"):
        raise HTTPException(status_code=409, detail="主机更新代理未运行。首次启用请在服务器执行 scripts/install-api-control-plane-update-agent.sh")
    current_status = _server_update_status()
    if str(current_status.get("state") or "") in {"queued", "running"}:
        raise HTTPException(status_code=409, detail="服务端更新任务正在执行")
    try:
        github = _fetch_server_github(force=True)
    except Exception as exc:
        raise HTTPException(status_code=503, detail="同步 GitHub master 失败：" + _safe(exc, 200))
    current = _current_server_commit(current_status)
    target = str(github.get("sha") or "")
    if current and current == target:
        return JSONResponse({"ok": True, "already_latest": True, "status": current_status})
    request_id = f"web-{int(time.time())}"
    payload = {
        "request_id": request_id,
        "requested_at_unix": time.time(),
        "requested_commit": target,
        "branch": "master",
        "source": "web-console",
    }
    _write_json_atomic(SERVER_UPDATE_REQUEST, payload)
    queued = {
        "state": "queued",
        "phase": "等待主机更新代理",
        "progress_percent": 1,
        "message": "更新请求已提交，正在等待主机代理接管",
        "request_id": request_id,
        "current_commit": current,
        "target_commit": target,
        "updated_at_unix": time.time(),
    }
    _write_json_atomic(SERVER_UPDATE_STATUS, queued)
    return JSONResponse({"ok": True, "already_latest": False, "status": queued}, status_code=202)


@router.post("/api/admin/version-update/client/start")
def version_update_client_start(request: Request, _: str = Depends(_admin)) -> JSONResponse:
    try:
        metadata = bot_update_cache.refresh_latest_metadata()
        state = bot_update_cache.start_cached_package(metadata)
    except Exception as exc:
        raise HTTPException(status_code=502, detail="客户端正式版同步失败：" + _safe(exc, 240))
    state["version"] = str(metadata.get("version") or "")
    state["tag"] = str(metadata.get("tag") or "")
    state["sha256"] = str(metadata.get("sha256") or "")
    state["size"] = int(metadata.get("size") or 0)
    state["mirror_url"] = bot_update_cache._absolute_mirror_url(request, str(metadata.get("tag") or ""))
    state["push"] = bot_update_push.get_push_status()
    return JSONResponse(state, status_code=200 if state.get("ready") else 202, headers={"Cache-Control": "no-store"})
