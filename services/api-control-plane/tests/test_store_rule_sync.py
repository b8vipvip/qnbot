from __future__ import annotations

import contextlib
import sqlite3

import pytest
from fastapi import HTTPException

import bot_web_console as core
import store_rule_sync as rules


class FakeControlPlane:
    def __init__(self, path):
        self.path = str(path)

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
        return "2026-08-11T00:00:00+00:00"


def setup_cp(tmp_path):
    cp = FakeControlPlane(tmp_path / "store-rules.db")
    core._cp = cp
    with cp.db() as conn:
        conn.executescript(
            """
            PRAGMA foreign_keys=ON;
            CREATE TABLE client_tokens(id INTEGER PRIMARY KEY);
            INSERT INTO client_tokens(id) VALUES(1);
            """
        )
    rules.init_db()
    return cp


def sample_profile(content="仅支持本店明确列出的服务范围"):
    return {
        "schemaVersion": 2,
        "rawInput": "原始店铺资料",
        "standardPrompt": "",
        "corePrompt": "所有回复必须遵守本店服务边界，不得自行扩大承诺。",
        "rules": [
            {
                "Id": "service-boundary",
                "Title": "服务范围",
                "Category": "链接范围",
                "Scope": "both",
                "Priority": 90,
                "Enabled": True,
                "Triggers": ["支持", "能不能"],
                "Content": content,
            }
        ],
    }


def test_store_rule_state_is_revisioned_per_client_token(tmp_path):
    setup_cp(tmp_path)
    first_hash = "a" * 64
    first = rules._save_state(1, sample_profile(), first_hash, "windows")
    assert first["revision"] == 1
    assert first["content_hash"] == first_hash

    second_hash = "b" * 64
    second = rules._save_state(
        1,
        sample_profile("售后问题只按本店规则回答"),
        second_hash,
        "windows",
    )
    assert second["revision"] == 2

    state = rules._state(1)
    assert state["revision"] == 2
    assert state["content_hash"] == second_hash
    assert state["profile"]["rules"][0]["Content"] == "售后问题只按本店规则回答"


def test_store_rule_state_rejects_invalid_hash_and_oversized_rule_set(tmp_path):
    setup_cp(tmp_path)
    with pytest.raises(HTTPException) as bad_hash:
        rules._save_state(1, sample_profile(), "not-a-sha256", "windows")
    assert bad_hash.value.status_code == 400

    profile = sample_profile()
    profile["rules"] = profile["rules"] * 81
    with pytest.raises(HTTPException) as too_many:
        rules._save_state(1, profile, "c" * 64, "windows")
    assert too_many.value.status_code == 400


def test_bot_web_store_rule_center_reads_and_updates_client_isolated_state(tmp_path):
    cp = setup_cp(tmp_path)
    with cp.db() as conn:
        conn.execute("INSERT INTO client_tokens(id) VALUES(2)")

    profile = sample_profile("售前只回答本店明确支持的服务")
    saved = rules.web_store_rules_update(
        rules.StoreRuleWebInput(profile=profile),
        client={"id": 1},
    )
    assert saved["ok"] is True
    assert saved["revision"] == 1
    assert saved["updated_by"] == "web"
    assert saved["content_hash"] == rules._profile_hash(profile)

    loaded = rules.web_store_rules(client={"id": 1})
    assert loaded["profile"]["rules"][0]["Content"] == "售前只回答本店明确支持的服务"
    assert loaded["revision"] == 1

    isolated = rules.web_store_rules(client={"id": 2})
    assert isolated["revision"] == 0
    assert isolated["profile"] == {}

    unchanged = rules.web_store_rules_update(
        rules.StoreRuleWebInput(profile=profile),
        client={"id": 1},
    )
    assert unchanged["revision"] == 1

    changed_profile = sample_profile("售后问题必须按本店规则回答")
    changed = rules.web_store_rules_update(
        rules.StoreRuleWebInput(profile=changed_profile),
        client={"id": 1},
    )
    assert changed["revision"] == 2
    assert changed["content_hash"] == rules._profile_hash(changed_profile)


def test_bot_web_store_rule_center_reuses_runtime_validation(tmp_path):
    setup_cp(tmp_path)
    invalid = sample_profile()
    invalid["rules"] = invalid["rules"] * 81
    with pytest.raises(HTTPException) as too_many:
        rules.web_store_rules_update(
            rules.StoreRuleWebInput(profile=invalid),
            client={"id": 1},
        )
    assert too_many.value.status_code == 400
