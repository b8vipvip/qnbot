from __future__ import annotations

import contextlib
import sqlite3

import pytest
from fastapi import HTTPException

import bot_client_shop_binding as binding
import bot_web_console as core
import knowledge_v2_sync as v2sync


class FakeControlPlane:
    def __init__(self, path):
        self.path = str(path)

    @contextlib.contextmanager
    def db(self):
        conn = sqlite3.connect(self.path)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA foreign_keys=ON")
        try:
            yield conn
            conn.commit()
        finally:
            conn.close()

    @staticmethod
    def iso_now():
        return "2026-08-11T00:00:00+00:00"


def setup_cp(tmp_path):
    cp = FakeControlPlane(tmp_path / "binding.db")
    binding._cp = cp
    core._cp = cp
    with cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE client_tokens(id INTEGER PRIMARY KEY);
            INSERT INTO client_tokens(id) VALUES(1);
            CREATE TABLE bot_messages(client_id INTEGER);
            CREATE TABLE bot_commands(client_id INTEGER);
            CREATE TABLE bot_client_state(client_id INTEGER);
            CREATE TABLE bot_conversation_reads(client_id INTEGER);
            CREATE TABLE bot_web_image_knowledge(client_id INTEGER,shop_key TEXT,id TEXT);
            CREATE TABLE bot_web_image_knowledge_sync_state(client_id INTEGER,shop_key TEXT,revision INTEGER);
            CREATE TABLE bot_web_image_knowledge_tombstones(client_id INTEGER,shop_key TEXT,id TEXT);
            CREATE TABLE bot_knowledge_state(client_id INTEGER);
            CREATE TABLE bot_knowledge_v2_state(
                client_id INTEGER PRIMARY KEY,
                revision INTEGER NOT NULL DEFAULT 0,
                records_json TEXT NOT NULL DEFAULT '[]'
            );
            CREATE TABLE bot_knowledge_backups(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_id INTEGER NOT NULL,
                revision INTEGER NOT NULL,
                items_json TEXT NOT NULL DEFAULT '[]',
                content_hash TEXT NOT NULL DEFAULT '',
                reason TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL
            );
            CREATE TABLE bot_web_settings_ack(
                client_id INTEGER PRIMARY KEY,
                desired_fingerprint TEXT NOT NULL DEFAULT '',
                revision INTEGER NOT NULL DEFAULT 1,
                applied_revision INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL
            );
            CREATE TABLE bot_web_auto_reply_rules(
                client_id INTEGER PRIMARY KEY,
                desired_settings_json TEXT NOT NULL DEFAULT '{}',
                current_settings_json TEXT NOT NULL DEFAULT '{}',
                revision INTEGER NOT NULL DEFAULT 0,
                applied_revision INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NOT NULL DEFAULT '',
                settings_schema_version INTEGER NOT NULL DEFAULT 1,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE bot_web_ai_model_settings(
                client_id INTEGER PRIMARY KEY,
                desired_settings_json TEXT NOT NULL DEFAULT '{}',
                current_settings_json TEXT NOT NULL DEFAULT '{}',
                revision INTEGER NOT NULL DEFAULT 0,
                applied_revision INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL
            );
            CREATE TABLE wecom_client_settings(
                client_id INTEGER PRIMARY KEY,
                enabled INTEGER NOT NULL DEFAULT 0,
                corp_id TEXT NOT NULL DEFAULT '',
                app_secret_cipher TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL,
                FOREIGN KEY(client_id) REFERENCES client_tokens(id)
            );
            CREATE TABLE wecom_handoff_tickets(
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
            CREATE TABLE wecom_handoff_commands(
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
            CREATE TABLE bot_store_rule_state(client_id INTEGER);
            CREATE TABLE bot_message_processing_traces(client_id INTEGER);
            CREATE TABLE bot_client_bot_enabled(client_id INTEGER PRIMARY KEY,shop_key TEXT);
            CREATE TABLE bot_client_data_backups(client_id INTEGER);
            """
        )
    binding.init_db()
    return cp


def test_first_claim_binds_token_and_second_shop_gets_structured_409(tmp_path):
    cp = setup_cp(tmp_path)
    result = binding.ensure_binding(1, "shop_a", False, "seller-a")
    assert result["ok"] is True
    assert result["shop_key"] == "shop_a"
    assert result["rebound"] is False

    # Runtime sync middleware sends ShopKey but not seller; this must not erase
    # the seller identity established by the explicit Windows token-binding claim.
    binding.ensure_binding(1, "shop_a", False, "")

    with pytest.raises(HTTPException) as caught:
        binding.ensure_binding(1, "shop_b", False, "seller-b")
    assert caught.value.status_code == 409
    assert caught.value.detail["code"] == "token_bound_to_other_shop"
    assert caught.value.detail["bound_shop_key"] == "shop_a"

    with cp.db() as conn:
        row = conn.execute(
            "SELECT shop_key,seller FROM bot_client_shop_binding WHERE client_id=1"
        ).fetchone()
    assert row["shop_key"] == "shop_a"
    assert row["seller"] == "seller-a"


def test_force_rebind_clears_old_token_scoped_server_state_before_switch(tmp_path):
    cp = setup_cp(tmp_path)
    binding.ensure_binding(1, "shop_a", False, "seller-a")

    with cp.db() as conn:
        for table in (
            "bot_messages",
            "bot_commands",
            "bot_client_state",
            "bot_conversation_reads",
            "bot_knowledge_state",
            "bot_knowledge_v2_state",
            "bot_store_rule_state",
            "bot_message_processing_traces",
            "bot_client_data_backups",
        ):
            conn.execute(f"INSERT INTO {table}(client_id) VALUES(1)")
        conn.execute("INSERT INTO bot_web_image_knowledge(client_id,shop_key,id) VALUES(1,'shop_a','image-a')")
        conn.execute("INSERT INTO bot_web_image_knowledge_sync_state(client_id,shop_key,revision) VALUES(1,'shop_a',12)")
        conn.execute("INSERT INTO bot_web_image_knowledge_tombstones(client_id,shop_key,id) VALUES(1,'shop_a','deleted-a')")
        conn.execute("INSERT INTO bot_knowledge_backups(client_id,revision,created_at) VALUES(1,1,'2026-08-11T00:00:00+00:00')")
        for table in ("bot_web_settings_ack", "bot_web_auto_reply_rules", "bot_web_ai_model_settings"):
            conn.execute(f"INSERT INTO {table}(client_id,updated_at) VALUES(1,'2026-08-11T00:00:00+00:00')")
        conn.execute("INSERT INTO wecom_client_settings(client_id,updated_at) VALUES(1,'2026-08-11T00:00:00+00:00')")
        conn.execute("""INSERT INTO wecom_handoff_tickets(
            ticket_id,client_id,client_name,seller,buyer,question,reason,notified_users,status,created_at,updated_at,expires_at
        ) VALUES('ticket-a',1,'Bot A','seller-a','buyer-a','question-a','reason-a','user-a','reply_queued','2026-08-11','2026-08-11','2026-08-12')""")
        conn.execute("""INSERT INTO wecom_handoff_commands(
            ticket_id,wecom_msg_id,from_user,reply_text,status,created_at,updated_at
        ) VALUES('ticket-a','msg-a','user-a','old-shop-reply','pending','2026-08-11','2026-08-11')""")
        conn.execute(
            "INSERT OR REPLACE INTO bot_client_bot_enabled(client_id,shop_key) VALUES(1,'shop_a')"
        )

    result = binding.ensure_binding(1, "shop_b", True, "seller-b")
    assert result["ok"] is True
    assert result["shop_key"] == "shop_b"
    assert result["rebound"] is True
    assert result["server_state_reset"] is True

    with cp.db() as conn:
        for table in (
            "bot_messages",
            "bot_commands",
            "bot_client_state",
            "bot_conversation_reads",
            "bot_knowledge_state",
            "bot_knowledge_v2_state",
            "bot_knowledge_backups",
            "bot_web_settings_ack",
            "bot_web_auto_reply_rules",
            "bot_web_ai_model_settings",
            "wecom_client_settings",
            "bot_store_rule_state",
            "bot_message_processing_traces",
            "bot_client_bot_enabled",
            "bot_client_data_backups",
        ):
            count = conn.execute(
                f"SELECT COUNT(*) FROM {table} WHERE client_id=1"
            ).fetchone()[0]
            assert count == 0
        for table in ("wecom_handoff_tickets", "wecom_handoff_commands"):
            count = conn.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]
            assert count == 0
        # Image knowledge is explicitly ShopKey-scoped, so rebinding this token
        # hides the old shop's rows without deleting them; rebinding back can recover them.
        assert conn.execute("SELECT shop_key FROM bot_web_image_knowledge WHERE client_id=1").fetchone()[0] == "shop_a"
        assert conn.execute("SELECT revision FROM bot_web_image_knowledge_sync_state WHERE client_id=1 AND shop_key='shop_a'").fetchone()[0] == 12
        assert conn.execute("SELECT id FROM bot_web_image_knowledge_tombstones WHERE client_id=1 AND shop_key='shop_a'").fetchone()[0] == "deleted-a"
        row = conn.execute(
            "SELECT shop_key,seller FROM bot_client_shop_binding WHERE client_id=1"
        ).fetchone()
    assert row["shop_key"] == "shop_b"
    assert row["seller"] == "seller-b"


def test_force_rebind_rolls_back_when_wecom_handoff_cleanup_fails(tmp_path):
    cp = setup_cp(tmp_path)
    binding.ensure_binding(1, "shop_a", False, "seller-a")

    with cp.db() as conn:
        conn.execute("INSERT INTO wecom_client_settings(client_id,updated_at) VALUES(1,'2026-08-11')")
        conn.execute("""INSERT INTO wecom_handoff_tickets(
            ticket_id,client_id,client_name,seller,buyer,question,reason,notified_users,status,created_at,updated_at,expires_at
        ) VALUES('ticket-a',1,'Bot A','seller-a','buyer-a','question-a','reason-a','user-a','reply_queued','2026-08-11','2026-08-11','2026-08-12')""")
        conn.execute("""INSERT INTO wecom_handoff_commands(
            ticket_id,wecom_msg_id,from_user,reply_text,status,created_at,updated_at
        ) VALUES('ticket-a','msg-a','user-a','old-shop-reply','pending','2026-08-11','2026-08-11')""")
        conn.execute("""
            CREATE TRIGGER deny_wecom_handoff_cleanup
            BEFORE DELETE ON wecom_handoff_commands
            BEGIN
                SELECT RAISE(ABORT, 'wecom cleanup blocked');
            END;
        """)

    with pytest.raises(sqlite3.IntegrityError, match="wecom cleanup blocked"):
        binding.ensure_binding(1, "shop_b", True, "seller-b")

    with cp.db() as conn:
        row = conn.execute(
            "SELECT shop_key,seller FROM bot_client_shop_binding WHERE client_id=1"
        ).fetchone()
        tickets = conn.execute("SELECT COUNT(*) FROM wecom_handoff_tickets WHERE client_id=1").fetchone()[0]
        commands = conn.execute("SELECT COUNT(*) FROM wecom_handoff_commands").fetchone()[0]
        settings = conn.execute("SELECT COUNT(*) FROM wecom_client_settings WHERE client_id=1").fetchone()[0]

    assert row["shop_key"] == "shop_a"
    assert row["seller"] == "seller-a"
    assert tickets == 1
    assert commands == 1
    assert settings == 1


def test_force_rebind_rolls_back_when_old_shop_state_cleanup_fails(tmp_path):
    cp = setup_cp(tmp_path)
    binding.ensure_binding(1, "shop_a", False, "seller-a")

    with cp.db() as conn:
        conn.execute("INSERT INTO bot_messages(client_id) VALUES(1)")
        conn.execute(
            """
            CREATE TRIGGER deny_bot_message_cleanup
            BEFORE DELETE ON bot_messages
            BEGIN
                SELECT RAISE(ABORT, 'cleanup blocked');
            END;
            """
        )

    with pytest.raises(sqlite3.IntegrityError, match="cleanup blocked"):
        binding.ensure_binding(1, "shop_b", True, "seller-b")

    with cp.db() as conn:
        row = conn.execute(
            "SELECT shop_key,seller FROM bot_client_shop_binding WHERE client_id=1"
        ).fetchone()
        count = conn.execute(
            "SELECT COUNT(*) FROM bot_messages WHERE client_id=1"
        ).fetchone()[0]

    assert row["shop_key"] == "shop_a"
    assert row["seller"] == "seller-a"
    assert count == 1


def test_force_rebind_fails_closed_when_existing_state_table_has_no_client_id(tmp_path):
    cp = setup_cp(tmp_path)
    binding.ensure_binding(1, "shop_a", False, "seller-a")

    with cp.db() as conn:
        conn.execute("DROP TABLE bot_client_data_backups")
        conn.execute("CREATE TABLE bot_client_data_backups(id INTEGER PRIMARY KEY)")

    with pytest.raises(RuntimeError, match="has no client_id column"):
        binding.ensure_binding(1, "shop_b", True, "seller-b")

    with cp.db() as conn:
        row = conn.execute(
            "SELECT shop_key,seller FROM bot_client_shop_binding WHERE client_id=1"
        ).fetchone()

    assert row["shop_key"] == "shop_a"
    assert row["seller"] == "seller-a"


def setup_v2_revision_cp(tmp_path):
    cp = FakeControlPlane(tmp_path / "knowledge-v2-revision.db")
    with cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE client_tokens(id INTEGER PRIMARY KEY);
            INSERT INTO client_tokens(id) VALUES(1);
            CREATE TABLE bot_commands(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_id INTEGER NOT NULL,
                command_type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                status TEXT NOT NULL,
                result_json TEXT NOT NULL,
                error TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """
        )
    v2sync._cp = cp
    v2sync.bot_web_console = core
    v2sync.init_db()
    return cp


def v2_record(answer):
    return {
        "Id": "same-id",
        "Type": "general",
        "Title": "same question",
        "Intent": "general",
        "Subject": "device",
        "Predicate": "compatibility",
        "Entities": [],
        "Aliases": [],
        "Answer": answer,
        "ShortAnswer": "",
        "Conditions": [],
        "Exclusions": [],
        "RequiredContext": [],
        "ProductIds": [],
        "RiskLevel": "normal",
        "SourceType": "web_manual",
        "SourceId": "",
        "Authority": 0.92,
        "Confidence": 0.92,
        "Enabled": True,
        "Status": "active",
    }


def test_knowledge_v2_web_put_rejects_stale_editor_revision(tmp_path):
    cp = setup_v2_revision_cp(tmp_path)
    first = v2sync._save(1, [v2_record("Windows answer")], "windows", expected_revision=0)
    assert first["revision"] == 1

    with pytest.raises(HTTPException) as caught:
        v2sync.web_put(
            v2sync.KnowledgeV2WebInput(
                records=[v2_record("stale web answer")],
                expected_revision=0,
            ),
            client={"id": 1},
        )
    assert caught.value.status_code == 409
    assert caught.value.detail["code"] == "revision_conflict"
    assert caught.value.detail["current_revision"] == 1
    assert v2sync._state(1)["records"][0]["Answer"] == "Windows answer"

    updated = v2sync.web_put(
        v2sync.KnowledgeV2WebInput(
            records=[v2_record("fresh web answer")],
            expected_revision=1,
        ),
        client={"id": 1},
    )
    assert updated["revision"] == 2
    assert updated["records"][0]["Answer"] == "fresh web answer"


def test_knowledge_v2_stale_windows_revision_cannot_overwrite_web_edit(tmp_path):
    setup_v2_revision_cp(tmp_path)
    v2sync._save(1, [v2_record("Windows answer")], "windows", expected_revision=0)
    v2sync._save(
        1,
        [v2_record("web answer")],
        "web",
        expected_revision=1,
        raise_on_conflict=True,
    )

    stale = v2sync._save(1, [v2_record("stale Windows answer")], "windows", expected_revision=1)
    assert stale["revision"] == 2
    assert stale["records"][0]["Answer"] == "web answer"
    assert v2sync._state(1)["records"][0]["Answer"] == "web answer"


@pytest.mark.parametrize(
    "field,value",
    [
        ("direct_threshold", float("nan")),
        ("direct_threshold", float("inf")),
        ("direct_threshold", float("-inf")),
        ("min_confidence", float("nan")),
        ("min_confidence", float("inf")),
        ("min_confidence", float("-inf")),
    ],
)
def test_knowledge_v2_runtime_settings_reject_non_finite_thresholds(tmp_path, field, value):
    setup_v2_revision_cp(tmp_path)
    values = {
        "enabled": True,
        "mode": "production",
        "direct_threshold": 0.82,
        "min_confidence": 0.72,
    }
    values[field] = value
    payload = v2sync.KnowledgeV2RuntimeSettingsInput(**values)

    with pytest.raises(HTTPException) as caught:
        v2sync.web_v2_runtime_settings_save(payload, client={"id": 1})

    assert caught.value.status_code == 400
    assert "有限数值" in caught.value.detail
    with v2sync._cp.db() as conn:
        queued = conn.execute("SELECT COUNT(*) FROM bot_commands").fetchone()[0]
    assert queued == 0
