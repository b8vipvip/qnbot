import base64
import hashlib
import sqlite3
import time
import xml.etree.ElementTree as ET

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

import wecom_bridge
import wecom_crypto


def make_aes_key() -> str:
    return base64.b64encode(bytes(range(32))).decode("ascii").rstrip("=")


def create_client_table(path) -> None:
    with sqlite3.connect(str(path)) as conn:
        conn.execute(
            """
            CREATE TABLE client_tokens (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                token_hash TEXT NOT NULL UNIQUE,
                token_prefix TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL,
                last_used_at TEXT
            )
            """
        )


@pytest.fixture()
def bridge_client(tmp_path, monkeypatch):
    database = tmp_path / "bridge.db"
    create_client_table(database)
    monkeypatch.setattr(wecom_bridge, "DB_PATH", database)
    monkeypatch.setattr(wecom_bridge, "WECOM_ENABLED", True)
    monkeypatch.setattr(wecom_bridge, "WECOM_CORP_ID", "ww-test-corp")
    monkeypatch.setattr(wecom_bridge, "WECOM_APP_SECRET", "app-secret")
    monkeypatch.setattr(wecom_bridge, "WECOM_AGENT_ID", "1000002")
    monkeypatch.setattr(wecom_bridge, "WECOM_TO_USERS", "operator1")
    monkeypatch.setattr(wecom_bridge, "WECOM_ALLOWED_REPLY_USERS", "operator1")
    monkeypatch.setattr(wecom_bridge, "WECOM_CALLBACK_TOKEN", "callback-token")
    monkeypatch.setattr(wecom_bridge, "WECOM_CALLBACK_AES_KEY", make_aes_key())
    wecom_crypto.install_on_bridge(wecom_bridge)
    monkeypatch.setattr(wecom_bridge, "client_wecom_settings", lambda client_id: {
        "enabled": True, "corp_id": "ww-test-corp", "app_secret": "app-secret",
        "agent_id": "1000002", "to_users": "operator1", "allowed_reply_users": "operator1",
        "ticket_hours": 24,
    })
    wecom_bridge.init_wecom_db()

    raw_token = "qnb_test_client_token"
    with sqlite3.connect(str(database)) as conn:
        conn.execute(
            "INSERT INTO client_tokens(name,token_hash,token_prefix,enabled,created_at) VALUES(?,?,?,?,?)",
            (
                "test-bot",
                hashlib.sha256(raw_token.encode("utf-8")).hexdigest(),
                raw_token[:12],
                1,
                wecom_bridge.iso_now(),
            ),
        )

    app = FastAPI()
    app.include_router(wecom_bridge.router)
    return TestClient(app), database, {"Authorization": "Bearer " + raw_token}


def test_wecom_crypto_uses_32_byte_padding_and_round_trips():
    key = make_aes_key()
    corp_id = "ww-test-corp"
    message = "<xml><Content><![CDATA[中文人工回复]]></Content></xml>"
    padded = wecom_crypto.pkcs7_pad_32(message.encode("utf-8"))
    assert len(padded) % 32 == 0
    assert wecom_crypto.pkcs7_unpad_32(padded) == message.encode("utf-8")
    encrypted = wecom_crypto.encrypt_message(message, key, corp_id)
    assert wecom_crypto.decrypt_message(encrypted, key, corp_id) == message


def test_ticket_reply_is_queued_claimed_and_completed(bridge_client, monkeypatch):
    client, database, headers = bridge_client
    monkeypatch.setattr(
        wecom_bridge,
        "send_app_text_for",
        lambda settings, users, content: {"errcode": 0, "errmsg": "ok", "msgid": "outbound-1"},
    )

    notify = client.post(
        "/api/runtime/v1/handoff/notify",
        headers=headers,
        json={
            "seller": "seller-a",
            "buyer": "buyer-a",
            "question": "买家申请退款怎么办？",
            "reason": "命中退款关键词",
            "is_off_hours": False,
        },
    )
    assert notify.status_code == 200
    ticket_id = notify.json()["ticket_id"]
    assert ticket_id.startswith("QN-")

    content = ticket_id + " 已为您提交人工退款核查，请稍候。"
    inner = (
        "<xml>"
        "<ToUserName><![CDATA[ww-test-corp]]></ToUserName>"
        "<FromUserName><![CDATA[operator1]]></FromUserName>"
        "<CreateTime>1700000000</CreateTime>"
        "<MsgType><![CDATA[text]]></MsgType>"
        f"<Content><![CDATA[{content}]]></Content>"
        "<MsgId>inbound-1</MsgId>"
        "</xml>"
    )
    encrypted = wecom_bridge.encrypt_callback(inner)
    timestamp = str(int(time.time()))
    nonce = "nonce-1"
    signature = wecom_bridge.sha1_signature(
        wecom_bridge.WECOM_CALLBACK_TOKEN,
        timestamp,
        nonce,
        encrypted,
    )
    outer = f"<xml><Encrypt><![CDATA[{encrypted}]]></Encrypt></xml>"
    callback = client.post(
        "/api/wecom/callback",
        params={"msg_signature": signature, "timestamp": timestamp, "nonce": nonce},
        content=outer.encode("utf-8"),
        headers={"Content-Type": "application/xml"},
    )
    assert callback.status_code == 200
    response_outer = ET.fromstring(callback.text)
    response_encrypted = response_outer.findtext("Encrypt")
    response_inner = wecom_bridge.decrypt_callback(response_encrypted)
    assert "已接收工单" in response_inner

    next_reply = client.get(
        "/api/runtime/v1/handoff/replies/next",
        headers=headers,
    )
    assert next_reply.status_code == 200
    task = next_reply.json()
    assert task["ticket_id"] == ticket_id
    assert task["seller"] == "seller-a"
    assert task["buyer"] == "buyer-a"
    assert task["question"] == "买家申请退款怎么办？"
    assert task["reply_text"] == "已为您提交人工退款核查，请稍候。"

    complete = client.post(
        f"/api/runtime/v1/handoff/replies/{task['id']}/complete",
        headers=headers,
        json={"claim_token": task["claim_token"], "success": True, "error": ""},
    )
    assert complete.status_code == 200
    assert complete.json()["status"] == "sent"

    with sqlite3.connect(str(database)) as conn:
        command_status = conn.execute(
            "SELECT status FROM wecom_handoff_commands WHERE id=?",
            (task["id"],),
        ).fetchone()[0]
        ticket_status = conn.execute(
            "SELECT status FROM wecom_handoff_tickets WHERE ticket_id=?",
            (ticket_id,),
        ).fetchone()[0]
    assert command_status == "sent"
    assert ticket_status == "delivered"


def test_reply_requires_ticket_and_authorized_member(bridge_client, monkeypatch):
    client, _, headers = bridge_client
    monkeypatch.setattr(
        wecom_bridge,
        "send_app_text_for",
        lambda settings, users, content: {"errcode": 0, "errmsg": "ok", "msgid": "outbound-2"},
    )
    notify = client.post(
        "/api/runtime/v1/handoff/notify",
        headers=headers,
        json={"seller": "s", "buyer": "b", "question": "q", "reason": "r"},
    )
    assert notify.status_code == 200

    inner = (
        "<xml>"
        "<FromUserName><![CDATA[unauthorized-user]]></FromUserName>"
        "<MsgType><![CDATA[text]]></MsgType>"
        "<Content><![CDATA[没有工单号的回复]]></Content>"
        "<MsgId>inbound-unauthorized</MsgId>"
        "</xml>"
    )
    encrypted = wecom_bridge.encrypt_callback(inner)
    timestamp = str(int(time.time()))
    nonce = "nonce-2"
    signature = wecom_bridge.sha1_signature(
        wecom_bridge.WECOM_CALLBACK_TOKEN,
        timestamp,
        nonce,
        encrypted,
    )
    callback = client.post(
        "/api/wecom/callback",
        params={"msg_signature": signature, "timestamp": timestamp, "nonce": nonce},
        content=f"<xml><Encrypt><![CDATA[{encrypted}]]></Encrypt></xml>",
    )
    assert callback.status_code == 200
    response_outer = ET.fromstring(callback.text)
    response_inner = wecom_bridge.decrypt_callback(response_outer.findtext("Encrypt"))
    assert "未被授权" in response_inner
