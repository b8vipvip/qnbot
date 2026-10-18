from __future__ import annotations

import contextlib
import csv
import io
import json
import sqlite3

from fastapi import FastAPI, HTTPException, Request
from fastapi.testclient import TestClient

import ai_request_records


class FakeControlPlane:
    def __init__(self, database):
        self.database = str(database)
        self.app = FastAPI()

    @contextlib.contextmanager
    def db(self):
        conn = sqlite3.connect(self.database)
        conn.row_factory = sqlite3.Row
        try:
            yield conn
            conn.commit()
        finally:
            conn.close()

    @staticmethod
    def require_admin(request: Request):
        if request.headers.get("x-test-admin") != "yes":
            raise HTTPException(status_code=401, detail="admin required")
        return "admin"


def prepare(tmp_path):
    cp = FakeControlPlane(tmp_path / "ai-records.db")
    ai_request_records.install(cp)
    ai_request_records.init_db()
    return cp


def test_auth_and_request_response_detail_and_export(tmp_path):
    cp = prepare(tmp_path)
    secret = "qnb_" + "a" * 40
    messages = [
        {"role": "system", "content": "请回答"},
        {"role": "user", "content": "设备怎么安装？", "authorization": "Bearer " + secret,
         "metadata": {"api_key": "sk-" + "x" * 20}},
        {"role": "user", "content": [
            {"type": "image_url", "image_url": {"url": "data:image/png;base64,AAAAA"}},
            {"type": "text", "text": "额外信息"}]},
    ]
    ai_request_records.record(
        client_id=2, client_name="01", shop_key="qn_test", endpoint="/api/runtime/v1/ai-proxy/chat/completions",
        requested_model="text-default", messages=messages,
        dispatched={"success": True, "attempt": {
            "provider_name": "upstream", "model": "gpt-5.5", "protocol": "chat",
            "answer": "在设置中完成安装", "latency_ms": 30,
        }, "attempts": [{"provider_name": "upstream", "model": "gpt-5.5",
                         "protocol": "chat", "success": True, "latency_ms": 30,
                         "url": "https://secret.example/key"}]},
        duration_ms=45,
    )
    ai_request_records.record(
        client_id=3, client_name="客服02", shop_key="qn_other", endpoint="/v1/responses",
        requested_model="gpt-5.5", messages=[{"role": "user", "content": "=SUM(1,2)"}],
        dispatched={"success": False, "attempts": [
            {"model": "gpt-5.5", "provider_name": "upstream", "protocol": "chat",
             "success": False, "error": "Bearer " + secret, "status_code": 503}]},
        duration_ms=101,
    )
    with TestClient(cp.app) as client:
        assert client.get("/api/admin/ai-requests").status_code == 401
        assert client.get("/api/admin/ai-requests/1").status_code == 401
        assert client.get("/api/admin/ai-requests/export").status_code == 401
        admin = {"x-test-admin": "yes"}
        response = client.get("/api/admin/ai-requests?limit=1", headers=admin)
        assert response.status_code == 200
        assert response.json()["total"] == 2
        assert len(response.json()["items"]) == 1
        assert "prompt" not in response.text  # summary never exposes full prompts
        assert response.json()["items"][0]["success"] == 0
        filtered = client.get("/api/admin/ai-requests?shop_key=qn_test", headers=admin).json()
        assert filtered["total"] == 1
        assert filtered["items"][0]["client_id"] == 2
        detail = client.get("/api/admin/ai-requests/" + str(filtered["items"][0]["id"]), headers=admin).json()
        assert detail["response_text"] == "在设置中完成安装"
        assert detail["prompt"][0]["content"] == "请回答"
        assert detail["prompt"][1]["authorization"] == "[REDACTED]"
        assert detail["prompt"][1]["metadata"]["api_key"] == "[REDACTED]"
        assert detail["prompt"][2]["content"][0]["image_url"] == "[媒体内容已省略]"
        assert detail["attempts"][0]["provider_name"] == "upstream"
        assert "url" not in detail["attempts"][0]
        for private in (secret, "sk-" + "x" * 20, "secret.example", "AAAAA"):
            assert private not in json.dumps(detail, ensure_ascii=False)
        export = client.get("/api/admin/ai-requests/export?count=1&format=json", headers=admin)
        assert export.status_code == 200
        assert export.headers["cache-control"] == "no-store"
        assert export.json()["exported_count"] == 1
        assert export.json()["records"][0]["client_id"] == 3
        csv_resp = client.get("/api/admin/ai-requests/export?count=2&format=csv", headers=admin)
        assert csv_resp.status_code == 200
        rows = list(csv.DictReader(io.StringIO(csv_resp.text.lstrip("\ufeff"))))
        assert len(rows) == 2
        assert rows[0]["prompt"]
        assert csv_resp.headers["content-disposition"].endswith('.csv"')
        assert client.get("/api/admin/ai-requests/export?count=5001", headers=admin).status_code == 422
        assert client.get("/api/admin/ai-requests?status=nothing", headers=admin).status_code == 422
        assert client.get("/api/admin/ai-requests/999999", headers=admin).status_code == 404


def test_bounded_content_and_expiry(tmp_path, monkeypatch):
    cp = prepare(tmp_path)
    ai_request_records.record(
        client_id=9, client_name="test", shop_key="test", endpoint="/v1/chat/completions",
        requested_model="x", messages=[{"role": "user", "content": "a" * 95000}],
        dispatched={"success": True, "attempt": {
            "answer": "b" * 95000, "model": "x", "provider_name": "p", "protocol": "chat"},
            "attempts": []},
        duration_ms=2,
    )
    with cp.db() as conn:
        row = conn.execute("SELECT * FROM ai_request_records").fetchone()
        assert row["truncated"] == 1
        assert len(row["response_text"]) == 48000
        assert len(row["prompt_json"]) < 49000
        conn.execute("UPDATE ai_request_records SET created_at='2001-01-01T00:00:00+00:00'")
    monkeypatch.setattr(ai_request_records, "_LAST_CLEANUP", float("-inf"))
    ai_request_records._maybe_cleanup()
    with cp.db() as conn:
        assert conn.execute("SELECT count(*) FROM ai_request_records").fetchone()[0] == 0


def test_hooked_all_runtime_routes_and_frontend_navigation():
    from pathlib import Path
    root = Path(__file__).resolve().parents[1]
    router = (root / "runtime_routing_guard.py").read_text(encoding="utf-8")
    streaming = (root / "runtime_streaming_guard.py").read_text(encoding="utf-8")
    proxy = (root / "runtime_shop_ai_proxy.py").read_text(encoding="utf-8")
    app = (root / "app.py").read_text(encoding="utf-8")
    bootstrap = (root / "bootstrap.py").read_text(encoding="utf-8")
    html = (root / "static/index.html").read_text(encoding="utf-8")
    frontend = (root / "static/ai-requests.js").read_text(encoding="utf-8")
    assert "ai_request_records.record(" in router
    assert '"/v1/responses"' in app
    assert '"shop_key"' not in proxy.split('max(5, min(300, timeout)),')[1][:80]  # positional shop key, not an untrusted header
    assert "ai_request_records.record," in streaming
    assert "ai_request_records.init_db()" in bootstrap
    assert 'data-page="ai-requests"' in html
    assert 'id="aiRequestExportCount"' in html
    assert 'onclick="viewAiRequest(' in frontend


def test_synonym_business_format_precheck_is_not_http_success():
    base = [{"role": "system", "content": "你负责为淘宝客服固定自动回复生成同义改写"},
            {"role": "user", "content": "来源：下班自动回复\\n原文：时间[[T0]]"}]
    purpose, status = ai_request_records._classify_business(
        base, '["只有一条"]', True
    )
    assert purpose == "固定回复同义编译 · 下班自动回复"
    assert status == "格式初筛失败：不是10条唯一文本"
    ok = json.dumps(["话术 %d [[T0]]" % n for n in range(10)], ensure_ascii=False)
    assert ai_request_records._classify_business(base, ok, True)[1] == "格式初筛通过，客户端验收未知"
    assert ai_request_records._classify_business(base, ok, False)[1] == "上游失败，客户端未验收"
    assert ai_request_records._classify_business(base, "not json", True)[1] == "格式初筛失败：不是JSON数组"
    assert ai_request_records._classify_business(base, json.dumps(["abc"]*10), True)[1] == "格式初筛失败：不是10条唯一文本"


def test_request_history_purpose_persisted_for_admin_and_export(tmp_path):
    cp = prepare(tmp_path)
    ai_request_records.record(
        client_id=1, client_name="one", shop_key="qn_test", endpoint="/v1/chat/completions",
        requested_model="text-default",
        messages=[{"role": "system", "content": "固定自动回复生成同义改写"},
                  {"role": "user", "content": "来源：下班自动回复\\n原文：下班[[T0]]"}],
        dispatched={"success": True, "attempt": {"answer": '["一条"]',
                                                  "model": "test", "provider_name": "upstream"}},
        duration_ms=1,
    )
    with TestClient(cp.app) as client:
        admin = {"x-test-admin": "yes"}
        record = client.get("/api/admin/ai-requests", headers=admin).json()["items"][0]
        assert record["purpose"].startswith("固定回复同义编译")
        assert record["business_result"].startswith("格式初筛失败")
        detail = client.get("/api/admin/ai-requests/1", headers=admin).json()
        assert detail["business_result"] == record["business_result"]
        export = client.get("/api/admin/ai-requests/export?count=1", headers=admin).json()
        assert export["records"][0]["purpose"] == record["purpose"]
