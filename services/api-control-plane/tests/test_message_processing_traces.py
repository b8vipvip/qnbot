from __future__ import annotations

import contextlib
import sqlite3
from datetime import datetime, timedelta, timezone

from fastapi import FastAPI, HTTPException
from fastapi.testclient import TestClient

import bot_client_shop_binding
import bot_web_console
import message_processing_traces


class FakeControlPlane:
    def __init__(self, path):
        self.path = str(path)
        self.app = FastAPI()

    @contextlib.contextmanager
    def db(self):
        conn = sqlite3.connect(self.path)
        conn.row_factory = sqlite3.Row
        try:
            yield conn
            conn.commit()
        finally:
            conn.close()

    @staticmethod
    def iso_now():
        return datetime.now(timezone.utc).replace(microsecond=0).isoformat()

    @staticmethod
    def require_admin(request):
        if request.headers.get("x-test-admin") != "yes":
            raise HTTPException(status_code=401, detail="管理员未登录")
        return "admin"


def test_runtime_batch_can_be_queried_grouped_and_exported_by_authenticated_admin(tmp_path, monkeypatch):
    cp = FakeControlPlane(tmp_path / "message-traces.db")
    with cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE client_tokens(id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO client_tokens(id,name) VALUES(1,'测试客户端');
            """
        )

    monkeypatch.setattr(bot_web_console, "_runtime_client", lambda request: {"id": 1})
    monkeypatch.setattr(
        bot_client_shop_binding,
        "ensure_binding",
        lambda client_id, shop_key, force, seller: {
            "ok": True,
            "shop_key": shop_key,
        },
    )
    message_processing_traces.install(cp)
    message_processing_traces.init_db()

    base = datetime.now(timezone.utc).replace(microsecond=0) - timedelta(minutes=15)
    timestamps = [(base + timedelta(minutes=i)).isoformat() for i in range(3)]
    beijing_day = (base + timedelta(hours=8)).strftime("%Y-%m-%d")

    with TestClient(cp.app) as client:
        uploaded = client.post(
            "/api/runtime/v1/message-processing-traces/batch",
            headers={"X-Shop-Key": "shop_test"},
            json={
                "events": [
                    {
                        "event_id": "event-1",
                        "trace_id": "trace-1",
                        "seller": "seller-a",
                        "buyer": "buyer-a",
                        "stage": "message_received",
                        "status": "processing",
                        "summary": "已识别买家消息",
                        "detail": "测试消息",
                        "occurred_at": timestamps[0],
                    },
                    {
                        "event_id": "event-2",
                        "trace_id": "trace-1",
                        "seller": "seller-a",
                        "buyer": "buyer-a",
                        "stage": "answer_ready",
                        "status": "ready",
                        "summary": "本地知识答案已就绪",
                        "detail": "未调用AI",
                        "duration_ms": 23,
                        "occurred_at": timestamps[1],
                    },
                    {
                        "event_id": "event-3",
                        "trace_id": "trace-1",
                        "seller": "seller-a",
                        "buyer": "buyer-a",
                        "stage": "delivery_confirmed",
                        "status": "success",
                        "summary": "已确认发送",
                        "detail": "真实回显已确认",
                        "duration_ms": 45,
                        "occurred_at": timestamps[2],
                    },
                ]
            },
        )
        assert uploaded.status_code == 200
        assert uploaded.json()["saved"] == 3

        unauthenticated = client.get("/api/admin/message-processing-traces")
        assert unauthenticated.status_code == 401

        queried = client.get(
            "/api/admin/message-processing-traces",
            headers={"X-Test-Admin": "yes"},
        )
        assert queried.status_code == 200
        rows = queried.json()
        assert len(rows) == 3
        assert rows[0]["shop_key"] == "shop_test"
        assert rows[0]["trace_id"] == "trace-1"

        grouped = client.get(
            "/api/admin/message-processing-conversations",
            headers={"X-Test-Admin": "yes"},
        )
        assert grouped.status_code == 200
        conversations = grouped.json()
        assert len(conversations) == 1
        assert conversations[0]["buyer"] == "buyer-a"
        assert conversations[0]["conversation_date"] == beijing_day
        assert conversations[0]["event_count"] == 3
        assert conversations[0]["trace_count"] == 1
        assert conversations[0]["success_count"] == 1
        assert conversations[0]["failed_count"] == 0
        assert conversations[0]["latest_status"] == "success"

        detail = client.get(
            "/api/admin/message-processing-conversations/detail",
            headers={"X-Test-Admin": "yes"},
            params={
                "client_id": 1,
                "shop_key": "shop_test",
                "seller": "seller-a",
                "buyer": "buyer-a",
                "conversation_date": beijing_day,
            },
        )
        assert detail.status_code == 200
        detail_events = detail.json()["events"]
        assert [event["event_id"] for event in detail_events] == ["event-1", "event-2", "event-3"]

        exported = client.get(
            "/api/admin/message-processing-conversations/export?window=2h",
            headers={"X-Test-Admin": "yes"},
        )
        assert exported.status_code == 200
        assert exported.headers["content-type"].startswith("text/csv")
        assert "buyer-a" in exported.text
        assert "本地知识答案已就绪" in exported.text
        assert "真实回显已确认" in exported.text
