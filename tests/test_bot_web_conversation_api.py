from __future__ import annotations

import base64
import importlib.util
import io
import sqlite3
import sys
import types
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
API = ROOT / "services" / "api-control-plane"


class FakeControlPlane:
    def __init__(self, path: Path):
        self.path = path

    @contextmanager
    def db(self):
        conn = sqlite3.connect(str(self.path))
        conn.row_factory = sqlite3.Row
        try:
            yield conn
            conn.commit()
        except Exception:
            conn.rollback()
            raise
        finally:
            conn.close()

    @staticmethod
    def iso_now() -> str:
        return datetime.now(timezone.utc).isoformat(timespec="seconds")


@contextmanager
def loaded_modules():
    module_names = ("fastapi", "fastapi.responses", "pydantic", "bot_web_console")
    previous = {name: sys.modules.get(name) for name in module_names}

    class DummyRouter:
        def get(self, *args, **kwargs):
            return lambda fn: fn

        post = put = delete = get

    class DummyStatus:
        def __getattr__(self, name):
            return 200

    fastapi = types.ModuleType("fastapi")
    fastapi.__path__ = []
    fastapi.APIRouter = DummyRouter
    fastapi.Depends = lambda *args, **kwargs: None
    fastapi.HTTPException = type("HTTPException", (Exception,), {})
    fastapi.Query = lambda default=None, **kwargs: default
    fastapi.Request = type("Request", (), {})
    fastapi.status = DummyStatus()

    responses = types.ModuleType("fastapi.responses")
    responses.FileResponse = type("FileResponse", (), {})
    responses.RedirectResponse = type("RedirectResponse", (), {})
    pydantic = types.ModuleType("pydantic")
    pydantic.BaseModel = type("BaseModel", (), {})
    pydantic.Field = lambda default=None, **kwargs: default

    sys.modules["fastapi"] = fastapi
    sys.modules["fastapi.responses"] = responses
    sys.modules["pydantic"] = pydantic

    try:
        core_spec = importlib.util.spec_from_file_location(
            "bot_web_console", API / "bot_web_console.py"
        )
        core = importlib.util.module_from_spec(core_spec)
        assert core_spec and core_spec.loader
        sys.modules["bot_web_console"] = core
        core_spec.loader.exec_module(core)

        conversation_spec = importlib.util.spec_from_file_location(
            "bot_web_conversation_knowledge_under_test",
            API / "bot_web_conversation_knowledge.py",
        )
        conversations = importlib.util.module_from_spec(conversation_spec)
        assert conversation_spec and conversation_spec.loader
        conversation_spec.loader.exec_module(conversations)
        yield core, conversations
    finally:
        for name, module in previous.items():
            if module is None:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = module


def create_message_schema(cp: FakeControlPlane) -> None:
    with cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE bot_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_id INTEGER NOT NULL,
                message_key TEXT NOT NULL,
                seller TEXT NOT NULL DEFAULT '',
                buyer TEXT NOT NULL DEFAULT '',
                role TEXT NOT NULL DEFAULT 'system',
                text TEXT NOT NULL DEFAULT '',
                message_type TEXT NOT NULL DEFAULT 'text',
                is_bot INTEGER NOT NULL DEFAULT 0,
                withdrawn INTEGER NOT NULL DEFAULT 0,
                attachment_mime TEXT,
                attachment_name TEXT,
                attachment_sha256 TEXT,
                attachment_bytes BLOB,
                occurred_at TEXT NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(client_id, message_key)
            );
            CREATE INDEX idx_bot_messages_conversation
                ON bot_messages(client_id, seller, buyer, id DESC);
            CREATE TABLE bot_conversation_reads (
                client_id INTEGER NOT NULL,
                seller TEXT NOT NULL,
                buyer TEXT NOT NULL,
                last_read_message_id INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(client_id, seller, buyer)
            );
            """
        )


def test_server_retention_keeps_the_full_visible_buyer_history_window(tmp_path):
    with loaded_modules() as (core, _):
        cp = FakeControlPlane(tmp_path / "messages.db")
        core._cp = cp
        create_message_schema(cp)

        now = datetime.now(timezone.utc).isoformat(timespec="seconds")
        with cp.db() as conn:
            conn.executemany(
                """
                INSERT INTO bot_messages(
                    client_id,message_key,seller,buyer,role,text,message_type,
                    is_bot,withdrawn,occurred_at,created_at
                ) VALUES(1,?,?,?,?,?,'text',0,0,?,?)
                """,
                [
                    (f"message-{i}", "seller", f"buyer-{i % 600}", "user",
                     f"message {i}", now, now)
                    for i in range(60001)
                ],
            )

        core._cleanup_messages(1, 7)

        with cp.db() as conn:
            row = conn.execute(
                "SELECT COUNT(*) AS count, MIN(id) AS oldest, MAX(id) AS newest "
                "FROM bot_messages WHERE client_id=1"
            ).fetchone()
        assert core.MAX_MESSAGES_PER_CLIENT == 60000
        assert row["count"] == 60000
        assert row["oldest"] == 2
        assert row["newest"] == 60001


def test_buyer_inbox_and_message_history_api_page_without_dropping_messages(tmp_path):
    with loaded_modules() as (core, conversations):
        cp = FakeControlPlane(tmp_path / "conversation-api.db")
        core._cp = cp
        create_message_schema(cp)

        start = datetime.now(timezone.utc) - timedelta(minutes=5)
        rows = []
        for i in range(130):
            occurred = (start + timedelta(seconds=i)).isoformat(timespec="seconds")
            role = "user" if i % 2 == 0 else "assistant"
            rows.append(
                (1, f"buyer-a-{i}", "seller", "buyer-a", role,
                 f"buyer A message {i}", "text", 0, 0, occurred, occurred)
            )
        occurred = (start + timedelta(minutes=10)).isoformat(timespec="seconds")
        rows.append(
            (1, "buyer-b-1", "seller", "buyer-b", "user",
             "buyer B latest message", "text", 0, 0, occurred, occurred)
        )
        with cp.db() as conn:
            conn.executemany(
                """
                INSERT INTO bot_messages(
                    client_id,message_key,seller,buyer,role,text,message_type,
                    is_bot,withdrawn,occurred_at,created_at
                ) VALUES(?,?,?,?,?,?,?,?,?,?,?)
                """,
                rows,
            )
            conn.execute(
                """
                INSERT INTO bot_conversation_reads(
                    client_id,seller,buyer,last_read_message_id,updated_at
                ) VALUES(1,'seller','buyer-a',120,?)
                """,
                (cp.iso_now(),),
            )

        client = {"id": 1}
        inbox = conversations.web_conversations(query="", limit=600, client=client)
        assert {item["buyer"] for item in inbox["conversations"]} == {"buyer-a", "buyer-b"}
        buyer_a = next(x for x in inbox["conversations"] if x["buyer"] == "buyer-a")
        assert buyer_a["text"] == "buyer A message 129"
        assert buyer_a["unread_count"] == 5

        latest = conversations.web_conversation_messages(
            seller="seller", buyer="buyer-a", before_id=0, limit=120, client=client
        )
        assert latest["has_more"] is True
        assert len(latest["messages"]) == 120
        assert latest["messages"][0]["text"] == "buyer A message 10"
        assert latest["messages"][-1]["text"] == "buyer A message 129"

        older = conversations.web_conversation_messages(
            seller="seller",
            buyer="buyer-a",
            before_id=latest["messages"][0]["id"],
            limit=120,
            client=client,
        )
        assert older["has_more"] is False
        assert len(older["messages"]) == 10
        merged_ids = [x["id"] for x in older["messages"] + latest["messages"]]
        assert len(merged_ids) == 130
        assert len(set(merged_ids)) == 130



def test_original_buyer_image_attachment_is_persisted_served_and_removed_on_withdrawal(tmp_path):
    with loaded_modules() as (core, _):
        cp = FakeControlPlane(tmp_path / "message-attachments.db")
        core._cp = cp
        create_message_schema(cp)

        buffer = io.BytesIO()
        Image.new("RGB", (2, 2), (240, 30, 20)).save(buffer, format="PNG")
        raw = buffer.getvalue()
        digest = __import__("hashlib").sha256(raw).hexdigest()
        core._save_message(1, {
            "message_key": "buyer-image-1",
            "seller": "seller",
            "buyer": "buyer",
            "role": "user",
            "text": "[图片]",
            "message_type": "image",
            "attachment": {
                "mime_type": "image/png",
                "file_name": "buyer.png",
                "sha256": digest,
                "data_base64": base64.b64encode(raw).decode("ascii"),
            },
        })

        with cp.db() as conn:
            row = conn.execute(
                "SELECT id,message_type,attachment_sha256,attachment_bytes FROM bot_messages WHERE message_key='buyer-image-1'"
            ).fetchone()
        assert row["message_type"] == "image"
        assert row["attachment_sha256"] == digest
        assert bytes(row["attachment_bytes"]) == raw

        result = core.bot_web_conversation_message_attachment(
            message_id=row["id"], seller="seller", buyer="buyer", client={"id": 1}
        )
        assert result["mime_type"] == "image/png"
        assert base64.b64decode(result["image_base64"]) == raw

        core._save_message(1, {
            "message_key": "buyer-image-1",
            "seller": "seller",
            "buyer": "buyer",
            "role": "user",
            "text": "已撤回一条消息",
            "message_type": "image",
            "withdrawn": True,
        })
        with cp.db() as conn:
            withdrawn = conn.execute(
                "SELECT withdrawn,attachment_bytes,attachment_sha256 FROM bot_messages WHERE message_key='buyer-image-1'"
            ).fetchone()
        assert withdrawn["withdrawn"] == 1
        assert withdrawn["attachment_bytes"] is None
        assert withdrawn["attachment_sha256"] is None
