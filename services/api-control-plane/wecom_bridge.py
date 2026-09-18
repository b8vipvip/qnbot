from __future__ import annotations

import base64
import hashlib
import json
import os
import re
import secrets
import sqlite3
import struct
import threading
import time
import xml.etree.ElementTree as ET
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, Optional, Tuple

from cryptography.hazmat.primitives import padding
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from curl_cffi import requests as curl_requests
from fastapi import APIRouter, Depends, HTTPException, Request, Response, status
from fastapi.responses import JSONResponse, PlainTextResponse
from pydantic import BaseModel, Field


DATA_DIR = Path(os.getenv("DATA_DIR", "/data")).resolve()
DATA_DIR.mkdir(parents=True, exist_ok=True)
DB_PATH = Path(os.getenv("DATABASE_PATH", str(DATA_DIR / "api-control-plane.db"))).resolve()
PUBLIC_BASE_URL = os.getenv("PUBLIC_BASE_URL", "").rstrip("/")

WECOM_CORP_ID = os.getenv("WECOM_CORP_ID", "").strip()
WECOM_APP_SECRET = os.getenv("WECOM_APP_SECRET", "").strip()
WECOM_AGENT_ID = os.getenv("WECOM_AGENT_ID", "").strip()
WECOM_TO_USERS = os.getenv("WECOM_TO_USERS", os.getenv("WECOM_TO_USER", "")).strip()
WECOM_CALLBACK_TOKEN = os.getenv("WECOM_CALLBACK_TOKEN", "").strip()
WECOM_CALLBACK_AES_KEY = os.getenv("WECOM_CALLBACK_AES_KEY", "").strip()
WECOM_ALLOWED_REPLY_USERS = os.getenv("WECOM_ALLOWED_REPLY_USERS", "").strip()
WECOM_TICKET_HOURS = max(1, min(168, int(os.getenv("WECOM_TICKET_HOURS", "24"))))
WECOM_ENABLED = os.getenv("WECOM_APP_ENABLED", "true").lower() in {"1", "true", "yes", "on"}

TOKEN_LOCK = threading.Lock()
TOKEN_CACHE: Dict[str, Any] = {"value": "", "expires_at": 0.0}
router = APIRouter()


def utcnow() -> datetime:
    return datetime.now(timezone.utc)


def iso_now() -> str:
    return utcnow().isoformat(timespec="seconds")


def safe_text(value: Any, limit: int = 500) -> str:
    text = str(value or "").replace("\r", " ").replace("\n", " ").strip()
    while "  " in text:
        text = text.replace("  ", " ")
    return text if len(text) <= limit else text[:limit] + "..."


def safe_buyer_message(value: Any, limit: int = 2000) -> str:
    text = str(value or "").replace("\r", "")
    text = re.sub(r"(?<!\d)1\d{10}(?!\d)", "[手机号]", text)
    text = re.sub(r"(?i)sk-[a-z0-9_-]{12,}", "[API_KEY]", text)
    lines = []
    for raw in text.split("\n"):
        line = re.sub(r"[ \t]+", " ", raw).strip()
        if line and line not in lines:
            lines.append(line[:500])
        if len(lines) >= 10:
            break
    result = "\n".join(lines) if lines else "[空白或未知消息]"
    return result if len(result) <= limit else result[:limit] + "..."


def split_users(value: str) -> Tuple[str, ...]:
    values = []
    for item in (value or "").replace(",", "|").replace("，", "|").replace(";", "|").replace("；", "|").split("|"):
        item = item.strip()
        if item and item not in values:
            values.append(item)
    return tuple(values)


def configured_recipients() -> Tuple[str, ...]:
    return split_users(WECOM_TO_USERS)


def allowed_reply_users() -> Tuple[str, ...]:
    explicit = split_users(WECOM_ALLOWED_REPLY_USERS)
    return explicit or configured_recipients()


def bridge_configured(require_callback: bool = False) -> bool:
    outbound = bool(
        WECOM_ENABLED
        and WECOM_CORP_ID
        and WECOM_APP_SECRET
        and WECOM_AGENT_ID.isdigit()
        and configured_recipients()
    )
    if not require_callback:
        return outbound
    return bool(outbound and WECOM_CALLBACK_TOKEN and len(WECOM_CALLBACK_AES_KEY) == 43)


@contextmanager
def db() -> Iterable[sqlite3.Connection]:
    connection = sqlite3.connect(str(DB_PATH), timeout=30, check_same_thread=False)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA journal_mode=WAL")
    try:
        yield connection
        connection.commit()
    except Exception:
        connection.rollback()
        raise
    finally:
        connection.close()


def init_wecom_db() -> None:
    with db() as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS wecom_handoff_tickets (
                ticket_id TEXT PRIMARY KEY,
                client_id INTEGER NOT NULL,
                client_name TEXT NOT NULL,
                seller TEXT NOT NULL,
                buyer TEXT NOT NULL,
                question TEXT NOT NULL,
                reason TEXT NOT NULL,
                notified_users TEXT NOT NULL,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                last_error TEXT
            );

            CREATE TABLE IF NOT EXISTS wecom_handoff_commands (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ticket_id TEXT NOT NULL,
                wecom_msg_id TEXT NOT NULL UNIQUE,
                from_user TEXT NOT NULL,
                reply_text TEXT NOT NULL,
                status TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                claim_client_id INTEGER,
                claim_token TEXT,
                claim_until TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_error TEXT,
                FOREIGN KEY(ticket_id) REFERENCES wecom_handoff_tickets(ticket_id)
            );

            CREATE INDEX IF NOT EXISTS idx_wecom_commands_status
                ON wecom_handoff_commands(status, id);
            CREATE INDEX IF NOT EXISTS idx_wecom_tickets_client
                ON wecom_handoff_tickets(client_id, status, created_at);
            """
        )


def hash_token(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def bearer_token(request: Request) -> str:
    header = request.headers.get("authorization", "")
    if not header.lower().startswith("bearer "):
        return ""
    return header.split(" ", 1)[1].strip()


def require_client(request: Request) -> Dict[str, Any]:
    token = bearer_token(request)
    if not token:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="客户端令牌无效")
    with db() as conn:
        row = conn.execute(
            "SELECT * FROM client_tokens WHERE token_hash=? AND enabled=1",
            (hash_token(token),),
        ).fetchone()
        if not row:
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="客户端令牌无效")
        conn.execute("UPDATE client_tokens SET last_used_at=? WHERE id=?", (iso_now(), row["id"]))
    return dict(row)


def sha1_signature(token: str, timestamp: str, nonce: str, encrypted: str) -> str:
    parts = sorted([token or "", timestamp or "", nonce or "", encrypted or ""])
    return hashlib.sha1("".join(parts).encode("utf-8")).hexdigest()


def callback_key() -> bytes:
    try:
        key = base64.b64decode(WECOM_CALLBACK_AES_KEY + "=")
    except Exception as exc:
        raise ValueError("WECOM_CALLBACK_AES_KEY 不是有效的43位EncodingAESKey") from exc
    if len(key) != 32:
        raise ValueError("WECOM_CALLBACK_AES_KEY 解码后必须是32字节")
    return key


def decrypt_callback(encrypted: str) -> str:
    key = callback_key()
    raw = base64.b64decode(encrypted)
    decryptor = Cipher(algorithms.AES(key), modes.CBC(key[:16])).decryptor()
    padded = decryptor.update(raw) + decryptor.finalize()
    unpadder = padding.PKCS7(128).unpadder()
    plain = unpadder.update(padded) + unpadder.finalize()
    if len(plain) < 20:
        raise ValueError("企业微信回调明文长度无效")
    msg_len = struct.unpack("!I", plain[16:20])[0]
    message = plain[20 : 20 + msg_len]
    receive_id = plain[20 + msg_len :].decode("utf-8", errors="strict")
    if WECOM_CORP_ID and receive_id != WECOM_CORP_ID:
        raise ValueError("企业微信回调CorpID不匹配")
    return message.decode("utf-8", errors="strict")


def encrypt_callback(message: str) -> str:
    key = callback_key()
    message_bytes = (message or "").encode("utf-8")
    plain = secrets.token_bytes(16) + struct.pack("!I", len(message_bytes)) + message_bytes + WECOM_CORP_ID.encode("utf-8")
    padder = padding.PKCS7(128).padder()
    padded = padder.update(plain) + padder.finalize()
    encryptor = Cipher(algorithms.AES(key), modes.CBC(key[:16])).encryptor()
    encrypted = encryptor.update(padded) + encryptor.finalize()
    return base64.b64encode(encrypted).decode("ascii")


def xml_value(root: ET.Element, name: str) -> str:
    node = root.find(name)
    return (node.text or "").strip() if node is not None else ""


def encrypted_text_reply(to_user: str, content: str, timestamp: str, nonce: str) -> Response:
    inner = (
        "<xml>"
        f"<ToUserName><![CDATA[{to_user}]]></ToUserName>"
        f"<FromUserName><![CDATA[{WECOM_CORP_ID}]]></FromUserName>"
        f"<CreateTime>{int(time.time())}</CreateTime>"
        "<MsgType><![CDATA[text]]></MsgType>"
        f"<Content><![CDATA[{content}]]></Content>"
        "</xml>"
    )
    encrypted = encrypt_callback(inner)
    signature = sha1_signature(WECOM_CALLBACK_TOKEN, timestamp, nonce, encrypted)
    outer = (
        "<xml>"
        f"<Encrypt><![CDATA[{encrypted}]]></Encrypt>"
        f"<MsgSignature><![CDATA[{signature}]]></MsgSignature>"
        f"<TimeStamp>{timestamp}</TimeStamp>"
        f"<Nonce><![CDATA[{nonce}]]></Nonce>"
        "</xml>"
    )
    return Response(content=outer, media_type="application/xml; charset=utf-8")


def get_access_token() -> str:
    if not bridge_configured():
        raise RuntimeError("企业微信应用消息尚未完整配置")
    now = time.time()
    with TOKEN_LOCK:
        cached = str(TOKEN_CACHE.get("value") or "")
        if cached and float(TOKEN_CACHE.get("expires_at") or 0) > now + 60:
            return cached
        response = curl_requests.get(
            "https://qyapi.weixin.qq.com/cgi-bin/gettoken",
            params={"corpid": WECOM_CORP_ID, "corpsecret": WECOM_APP_SECRET},
            timeout=20,
            impersonate="chrome",
        )
        data = response.json()
        if response.status_code != 200 or int(data.get("errcode", -1)) != 0 or not data.get("access_token"):
            raise RuntimeError("获取企业微信access_token失败：" + safe_text(data.get("errmsg") or response.text, 200))
        token = str(data["access_token"])
        expires_in = int(data.get("expires_in") or 7200)
        TOKEN_CACHE["value"] = token
        TOKEN_CACHE["expires_at"] = now + max(300, expires_in - 120)
        return token


def send_app_text(users: Tuple[str, ...], content: str) -> Dict[str, Any]:
    token = get_access_token()
    payload = {
        "touser": "|".join(users),
        "msgtype": "text",
        "agentid": int(WECOM_AGENT_ID),
        "text": {"content": content},
        "safe": 0,
        "enable_id_trans": 0,
        "enable_duplicate_check": 1,
        "duplicate_check_interval": 1800,
    }
    response = curl_requests.post(
        "https://qyapi.weixin.qq.com/cgi-bin/message/send",
        params={"access_token": token},
        json=payload,
        timeout=20,
        impersonate="chrome",
    )
    data = response.json()
    if response.status_code != 200 or int(data.get("errcode", -1)) != 0:
        raise RuntimeError("发送企业微信应用消息失败：" + safe_text(data.get("errmsg") or response.text, 240))
    return data


def client_wecom_settings(client_id: int) -> Dict[str, Any]:
    import wecom_settings
    return wecom_settings.load_client_settings(int(client_id))

def client_bridge_configured(settings: Dict[str, Any]) -> bool:
    return bool(settings.get("enabled") and settings.get("corp_id") and settings.get("app_secret")
        and str(settings.get("agent_id") or "").isdigit() and split_users(str(settings.get("to_users") or "")))

def get_access_token_for(settings: Dict[str, Any]) -> str:
    if not client_bridge_configured(settings):
        raise RuntimeError("当前 Bot 客户端尚未完整配置企业微信应用消息")
    response=curl_requests.get("https://qyapi.weixin.qq.com/cgi-bin/gettoken",
        params={"corpid":str(settings.get("corp_id") or ""),"corpsecret":str(settings.get("app_secret") or "")},
        timeout=20,impersonate="chrome")
    data=response.json()
    if response.status_code!=200 or int(data.get("errcode",-1))!=0 or not data.get("access_token"):
        raise RuntimeError("获取企业微信access_token失败："+safe_text(data.get("errmsg") or response.text,200))
    return str(data["access_token"])

def send_app_text_for(settings: Dict[str, Any], users: Tuple[str, ...], content: str) -> Dict[str, Any]:
    token=get_access_token_for(settings)
    payload={"touser":"|".join(users),"msgtype":"text","agentid":int(settings["agent_id"]),
      "text":{"content":content},"safe":0,"enable_id_trans":0,"enable_duplicate_check":1,"duplicate_check_interval":1800}
    response=curl_requests.post("https://qyapi.weixin.qq.com/cgi-bin/message/send",
      params={"access_token":token},json=payload,timeout=20,impersonate="chrome")
    data=response.json()
    if response.status_code!=200 or int(data.get("errcode",-1))!=0:
        raise RuntimeError("发送企业微信应用消息失败："+safe_text(data.get("errmsg") or response.text,240))
    return data

def new_ticket_id() -> str:
    return "QN-" + secrets.token_hex(4).upper()


def parse_reply_command(content: str) -> Tuple[str, str]:
    text = (content or "").strip()
    if text.startswith("#"):
        text = text[1:].lstrip()
    parts = text.split(None, 1)
    if len(parts) != 2:
        return "", ""
    ticket_id = parts[0].strip().upper()
    reply = parts[1].strip()
    if not ticket_id.startswith("QN-") or len(ticket_id) != 11:
        return "", ""
    if any(ch not in "0123456789ABCDEF" for ch in ticket_id[3:]):
        return "", ""
    return ticket_id, reply[:1000]


class HandoffNotifyInput(BaseModel):
    seller: str = Field(default="", max_length=100)
    buyer: str = Field(default="", max_length=100)
    question: str = Field(default="", max_length=2000)
    reason: str = Field(default="", max_length=500)
    is_off_hours: bool = False
    test: bool = False


class CompleteInput(BaseModel):
    claim_token: str = Field(min_length=8, max_length=200)
    success: bool
    error: str = Field(default="", max_length=500)


def create_ticket(client: Dict[str, Any], data: HandoffNotifyInput) -> str:
    ticket_id = new_ticket_id()
    now = iso_now()
    settings=client_wecom_settings(int(client["id"]))
    expires_at = (utcnow() + timedelta(hours=max(1,min(168,int(settings.get("ticket_hours") or 24))))).isoformat(timespec="seconds")
    settings=client_wecom_settings(int(client["id"]))
    recipients=split_users(str(settings.get("to_users") or ""))
    with db() as conn:
        conn.execute(
            """
            INSERT INTO wecom_handoff_tickets(
                ticket_id,client_id,client_name,seller,buyer,question,reason,
                notified_users,status,created_at,updated_at,expires_at
            ) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)
            """,
            (
                ticket_id,
                int(client["id"]),
                str(client["name"]),
                safe_text(data.seller, 100),
                safe_text(data.buyer, 100),
                safe_buyer_message(data.question, 2000),
                safe_text(data.reason, 500),
                "|".join(recipients),
                "created",
                now,
                now,
                expires_at,
            ),
        )
    return ticket_id


def build_handoff_message(ticket_id: str, data: HandoffNotifyInput) -> str:
    title = "【千牛Bot企业微信应用测试】" if data.test else "【千牛Bot转人工提醒】"
    return (
        title
        + "\n工单：" + ticket_id
        + "\n时间：" + datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        + "\n客服：" + safe_text(data.seller or "测试客服", 80)
        + "\n买家：" + safe_text(data.buyer or "测试买家", 80)
        + "\n状态：" + ("人工客服下班" if data.is_off_hours else "等待人工处理")
        + "\n原因：" + safe_text(data.reason or "测试企业微信应用消息双向链路", 200)
        + "\n买家消息：\n" + safe_buyer_message(data.question or "这是一条测试通知", 1600)
        + "\n\n回复格式：" + ticket_id + " 这里填写给买家的回复"
        + "\n为防止多买家串单，回复时必须保留工单号。"
    )


@router.get("/api/runtime/v1/handoff/capabilities")
def handoff_capabilities(client: Dict[str, Any] = Depends(require_client)) -> Dict[str, Any]:
    import wecom_settings
    settings=client_wecom_settings(int(client["id"]))
    public=wecom_settings.public_client_settings(int(client["id"]),settings)
    return {
        "enabled": bool(public["outbound_configured"]),
        "callback_enabled": bool(public["callback_configured"]),
        "callback_url": public["callback_url"],
        "client": client["name"],
        "reply_format": "QN-XXXXXXXX 回复内容",
    }


@router.post("/api/runtime/v1/handoff/notify")
def handoff_notify(data: HandoffNotifyInput, client: Dict[str, Any] = Depends(require_client)) -> Dict[str, Any]:
    settings=client_wecom_settings(int(client["id"]))
    if not client_bridge_configured(settings):
        raise HTTPException(status_code=503, detail="当前 Bot 客户端尚未配置企业微信应用消息参数")
    if not data.test and (not data.seller.strip() or not data.buyer.strip() or not data.question.strip()):
        raise HTTPException(status_code=400, detail="seller、buyer、question不能为空")
    ticket_id = create_ticket(client, data)
    try:
        recipients=split_users(str(settings.get("to_users") or ""))
        result = send_app_text_for(settings, recipients, build_handoff_message(ticket_id, data))
        with db() as conn:
            conn.execute(
                "UPDATE wecom_handoff_tickets SET status='notified',updated_at=? WHERE ticket_id=?",
                (iso_now(), ticket_id),
            )
        return {"ok": True, "ticket_id": ticket_id, "msgid": result.get("msgid"), "message": "企业微信应用消息发送成功"}
    except Exception as exc:
        with db() as conn:
            conn.execute(
                "UPDATE wecom_handoff_tickets SET status='notify_failed',last_error=?,updated_at=? WHERE ticket_id=?",
                (safe_text(exc, 500), iso_now(), ticket_id),
            )
        raise HTTPException(status_code=502, detail=safe_text(exc, 500))


@router.get("/api/wecom/callback")
def verify_wecom_callback(
    msg_signature: str,
    timestamp: str,
    nonce: str,
    echostr: str,
) -> Response:
    if not bridge_configured(require_callback=True):
        raise HTTPException(status_code=503, detail="企业微信回调参数未配置")
    expected = sha1_signature(WECOM_CALLBACK_TOKEN, timestamp, nonce, echostr)
    if not secrets.compare_digest(expected, msg_signature):
        raise HTTPException(status_code=403, detail="企业微信回调签名无效")
    return PlainTextResponse(decrypt_callback(echostr))


@router.post("/api/wecom/callback")
async def receive_wecom_callback(request: Request) -> Response:
    if not bridge_configured(require_callback=True):
        raise HTTPException(status_code=503, detail="企业微信回调参数未配置")
    msg_signature = request.query_params.get("msg_signature", "")
    timestamp = request.query_params.get("timestamp", "")
    nonce = request.query_params.get("nonce", "")
    outer_text = (await request.body()).decode("utf-8", errors="strict")
    outer = ET.fromstring(outer_text)
    encrypted = xml_value(outer, "Encrypt")
    expected = sha1_signature(WECOM_CALLBACK_TOKEN, timestamp, nonce, encrypted)
    if not secrets.compare_digest(expected, msg_signature):
        raise HTTPException(status_code=403, detail="企业微信回调签名无效")

    inner = ET.fromstring(decrypt_callback(encrypted))
    from_user = xml_value(inner, "FromUserName")
    msg_type = xml_value(inner, "MsgType").lower()
    content = xml_value(inner, "Content")
    msg_id = xml_value(inner, "MsgId") or hashlib.sha256(
        (from_user + "|" + timestamp + "|" + content).encode("utf-8")
    ).hexdigest()

    if from_user not in allowed_reply_users():
        return encrypted_text_reply(from_user, "该成员未被授权处理千牛工单。", timestamp, nonce)
    if msg_type != "text":
        return encrypted_text_reply(from_user, "当前只支持文本回复，格式：QN-XXXXXXXX 回复内容", timestamp, nonce)

    ticket_id, reply_text = parse_reply_command(content)
    if not ticket_id or not reply_text:
        return encrypted_text_reply(from_user, "格式不正确，请回复：QN-XXXXXXXX 回复内容", timestamp, nonce)

    now = iso_now()
    with db() as conn:
        ticket = conn.execute(
            "SELECT * FROM wecom_handoff_tickets WHERE ticket_id=?",
            (ticket_id,),
        ).fetchone()
        if not ticket:
            return encrypted_text_reply(from_user, "未找到该工单，请核对工单号。", timestamp, nonce)
        if ticket["status"] in {"expired", "closed"} or ticket["expires_at"] < now:
            conn.execute(
                "UPDATE wecom_handoff_tickets SET status='expired',updated_at=? WHERE ticket_id=?",
                (now, ticket_id),
            )
            return encrypted_text_reply(from_user, "该工单已过期，请让买家重新触发转人工。", timestamp, nonce)
        notified_users = split_users(ticket["notified_users"])
        if notified_users and from_user not in notified_users:
            return encrypted_text_reply(from_user, "该工单未通知给当前成员，已拒绝转发。", timestamp, nonce)
        try:
            conn.execute(
                """
                INSERT INTO wecom_handoff_commands(
                    ticket_id,wecom_msg_id,from_user,reply_text,status,created_at,updated_at
                ) VALUES(?,?,?,?,?,?,?)
                """,
                (ticket_id, msg_id, from_user, reply_text, "pending", now, now),
            )
        except sqlite3.IntegrityError:
            return encrypted_text_reply(from_user, "这条回复已接收，请勿重复发送。", timestamp, nonce)
        conn.execute(
            "UPDATE wecom_handoff_tickets SET status='reply_queued',updated_at=? WHERE ticket_id=?",
            (now, ticket_id),
        )
    return encrypted_text_reply(from_user, "已接收工单 " + ticket_id + "，等待千牛Bot发送给买家。", timestamp, nonce)


@router.get("/api/runtime/v1/handoff/replies/next")
def next_handoff_reply(client: Dict[str, Any] = Depends(require_client)) -> Response:
    init_wecom_db()
    now = iso_now()
    claim_until = (utcnow() + timedelta(seconds=60)).isoformat(timespec="seconds")
    claim_token = secrets.token_urlsafe(24)
    with db() as conn:
        conn.execute("BEGIN IMMEDIATE")
        conn.execute(
            """
            UPDATE wecom_handoff_commands
            SET status='pending',claim_client_id=NULL,claim_token=NULL,claim_until=NULL,updated_at=?
            WHERE status='claimed' AND claim_until IS NOT NULL AND claim_until<?
            """,
            (now, now),
        )
        row = conn.execute(
            """
            SELECT c.*,t.client_id,t.client_name,t.seller,t.buyer,t.question,t.reason
            FROM wecom_handoff_commands c
            JOIN wecom_handoff_tickets t ON t.ticket_id=c.ticket_id
            WHERE c.status='pending' AND t.client_id=? AND t.expires_at>=?
            ORDER BY c.id ASC LIMIT 1
            """,
            (int(client["id"]), now),
        ).fetchone()
        if not row:
            return Response(status_code=204)
        updated = conn.execute(
            """
            UPDATE wecom_handoff_commands
            SET status='claimed',claim_client_id=?,claim_token=?,claim_until=?,attempts=attempts+1,updated_at=?
            WHERE id=? AND status='pending'
            """,
            (int(client["id"]), claim_token, claim_until, now, int(row["id"])),
        ).rowcount
        if updated != 1:
            return Response(status_code=204)
    return JSONResponse(
        {
            "id": int(row["id"]),
            "ticket_id": row["ticket_id"],
            "seller": row["seller"],
            "buyer": row["buyer"],
            "question": row["question"],
            "reason": row["reason"],
            "reply_text": row["reply_text"],
            "from_user": row["from_user"],
            "claim_token": claim_token,
            "claim_until": claim_until,
        }
    )


@router.post("/api/runtime/v1/handoff/replies/{command_id}/complete")
def complete_handoff_reply(
    command_id: int,
    data: CompleteInput,
    client: Dict[str, Any] = Depends(require_client),
) -> Dict[str, Any]:
    now = iso_now()
    with db() as conn:
        row = conn.execute(
            """
            SELECT c.*,t.client_id FROM wecom_handoff_commands c
            JOIN wecom_handoff_tickets t ON t.ticket_id=c.ticket_id
            WHERE c.id=?
            """,
            (command_id,),
        ).fetchone()
        if not row or int(row["client_id"]) != int(client["id"]):
            raise HTTPException(status_code=404, detail="人工回复任务不存在")
        if row["status"] != "claimed" or row["claim_token"] != data.claim_token:
            raise HTTPException(status_code=409, detail="人工回复任务租约无效或已过期")
        if data.success:
            conn.execute(
                """
                UPDATE wecom_handoff_commands
                SET status='sent',claim_client_id=NULL,claim_token=NULL,claim_until=NULL,last_error=NULL,updated_at=?
                WHERE id=?
                """,
                (now, command_id),
            )
            conn.execute(
                "UPDATE wecom_handoff_tickets SET status='delivered',updated_at=?,last_error=NULL WHERE ticket_id=?",
                (now, row["ticket_id"]),
            )
            return {"ok": True, "status": "sent"}

        next_status = "failed" if int(row["attempts"] or 0) >= 5 else "pending"
        error = safe_text(data.error or "Windows Bot发送失败", 500)
        conn.execute(
            """
            UPDATE wecom_handoff_commands
            SET status=?,claim_client_id=NULL,claim_token=NULL,claim_until=NULL,last_error=?,updated_at=?
            WHERE id=?
            """,
            (next_status, error, now, command_id),
        )
        conn.execute(
            "UPDATE wecom_handoff_tickets SET status=?,updated_at=?,last_error=? WHERE ticket_id=?",
            ("delivery_failed" if next_status == "failed" else "reply_queued", now, error, row["ticket_id"]),
        )
        return {"ok": True, "status": next_status}
