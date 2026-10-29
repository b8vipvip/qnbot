"""Bounded private service/runtime logs; administrative access only.

This is application logging, not a Docker socket or command-execution endpoint.
The host update agent writes its deployment log to the same /data volume.
"""
from __future__ import annotations

import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import re
import time
from typing import Any

from fastapi import APIRouter, Depends, HTTPException, Query, Request
from fastapi.responses import JSONResponse, Response

router = APIRouter()
_cp: Any = None
DATA_DIR = Path(os.getenv("DATA_DIR", "/data")).resolve()
RUNTIME_DIR = DATA_DIR / "server-runtime"
RUNTIME_FILE = RUNTIME_DIR / "runtime.log"
UPDATE_FILE = DATA_DIR / "server-update" / "update.log"
MAX_LINES = 1000
MAX_READ_BYTES = 512 * 1024

_SENSITIVE = [
    (re.compile(r"(?i)(authorization\s*[:=]\s*bearer\s+)[^\s\"']+"), r"\1[REDACTED]"),
    (re.compile(r"(?i)\b(api[_-]?key|access[_-]?token|client[_-]?secret|password|cookie|vless)\s*[:=]\s*[^\s,;]+"), r"\1=[REDACTED]"),
    (re.compile(r"(?<!\d)1[3-9]\d{9}(?!\d)"), "[PHONE]"),
    (re.compile(r"(?i)(?:验证码|兑换码)\s*[:：=]\s*[\w-]{4,}"), "[SENSITIVE_CODE]"),
    (re.compile(r"(?i)(https?://[^\s?#]+)\?[^ \r\n]+"), r"\1?[QUERY_REDACTED]"),
]


def redact(value: str) -> str:
    text = str(value or "")
    for pattern, substitute in _SENSITIVE:
        text = pattern.sub(substitute, text)
    return text


class PrivateLogFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        return redact(super().format(record))


def _admin(request: Request) -> str:
    return _cp.require_admin(request)


def _source_path(source: str) -> Path:
    if source == "runtime":
        return RUNTIME_FILE
    if source in {"update", "build"}:
        return UPDATE_FILE
    raise HTTPException(status_code=400, detail="不支持的日志类别")


def _tail(path: Path, lines: int) -> list[str]:
    if not path.is_file():
        return []
    # Read only the last bounded bytes; never download the whole runtime log.
    with path.open("rb") as handle:
        handle.seek(0, 2)
        size = handle.tell()
        handle.seek(max(0, size - MAX_READ_BYTES))
        data = handle.read(MAX_READ_BYTES)
    if size > MAX_READ_BYTES:
        data = data.split(b"\n", 1)[-1]
    return data.decode("utf-8", errors="replace").splitlines()[-lines:]


def _read(source: str, lines: int) -> dict[str, Any]:
    path = _source_path(source)
    raw = _tail(path, lines if source != "build" else MAX_LINES)
    if source == "build":
        # The agent's update.log captures Docker Compose's plain build output.
        raw = [v for v in raw if
               "构建" in v or "Dockerfile" in v or "pip " in v
               or "apt-get" in v or "error" in v.lower() or "failed" in v.lower()
               or "#" in v or "timeout" in v.lower()][-lines:]
    stat = path.stat() if path.is_file() else None
    return {
        "source": source,
        "lines": [redact(s)[:1500] for s in raw],
        "available": bool(stat),
        "size_bytes": stat.st_size if stat else 0,
        "modified_at_unix": stat.st_mtime if stat else None,
        "truncated_to_last_bytes": MAX_READ_BYTES,
    }


def install(control_plane: Any) -> None:
    global _cp
    _cp = control_plane
    # Diagnostics must never prevent the customer-facing API from booting.
    # Some old deployments bind-mount /data with restrictive ownership.
    try:
        RUNTIME_DIR.mkdir(parents=True, exist_ok=True)
        handler = RotatingFileHandler(
            str(RUNTIME_FILE), maxBytes=3 * 1024 * 1024, backupCount=5, encoding="utf-8"
        )
        try:
            os.chmod(RUNTIME_FILE, 0o600)
        except OSError:
            pass
        handler.setLevel(logging.INFO)
        handler.setFormatter(PrivateLogFormatter(
            "%(asctime)s %(levelname)s [%(name)s] %(message)s"))
        # Capture Python/uvicorn logging, not raw access URLs with query strings.
        for name in ("", "uvicorn.error"):
            logger = logging.getLogger(name)
            logger.addHandler(handler)
            if name == "":
                logger.setLevel(min(logger.level, logging.INFO) if logger.level else logging.INFO)
    except OSError as exc:
        logging.getLogger("qnbot.runtime").warning(
            "private runtime log unavailable: %s", type(exc).__name__)
    control_plane.app.include_router(router)

    @control_plane.app.middleware("http")
    async def record_runtime_status(request: Request, call_next):
        started = time.monotonic()
        route_path = request.url.path
        if route_path.startswith("/api/admin/server-runtime/logs") or route_path == "/healthz":
            return await call_next(request)
        try:
            result = await call_next(request)
            elapsed = int((time.monotonic() - started) * 1000)
            # No query parameters, request bodies, customer messages or tokens.
            endpoint = request.scope.get("route")
            template = getattr(endpoint, "path", None) or "/"
            if len(template) > 140:
                template = template[:140] + "..."
            logging.getLogger("qnbot.runtime").info(
                "HTTP %s %s status=%s latency_ms=%s", request.method,
                template, result.status_code, elapsed)
            return result
        except Exception as exc:
            logging.getLogger("qnbot.runtime").error(
                "HTTP %s failed type=%s latency_ms=%s", request.method,
                type(exc).__name__, int((time.monotonic() - started) * 1000))
            raise


@router.get("/api/admin/server-runtime/logs")
def get_server_runtime_logs(
    request: Request, source: str = Query("runtime"),
    lines: int = Query(200, ge=10, le=MAX_LINES),
    _: str = Depends(_admin),
) -> JSONResponse:
    return JSONResponse(_read(source, lines), headers={"Cache-Control": "no-store"})


@router.get("/api/admin/server-runtime/logs/download")
def download_server_runtime_logs(
    request: Request, source: str = Query("runtime"),
    lines: int = Query(MAX_LINES, ge=10, le=MAX_LINES),
    _: str = Depends(_admin),
) -> Response:
    data = _read(source, lines)
    content = "\n".join(data["lines"]) + "\n"
    return Response(
        content=content, media_type="text/plain; charset=utf-8",
        headers={
            "Cache-Control": "no-store",
            "Content-Disposition": f'attachment; filename="qnbot-{source}-logs.txt"',
            "X-Content-Type-Options": "nosniff",
        },
    )
