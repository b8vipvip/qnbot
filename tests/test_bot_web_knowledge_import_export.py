from __future__ import annotations

import importlib.util
import sqlite3
import sys
import types
from contextlib import contextmanager
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "services" / "api-control-plane" / "bot_web_conversation_knowledge.py"
HAS_FASTAPI = importlib.util.find_spec("fastapi") is not None
needs_server_deps = pytest.mark.skipif(not HAS_FASTAPI, reason="server dependencies are not installed in Windows static CI")


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


def load_module(tmp_path: Path):
    core = types.ModuleType("bot_web_console")
    core._cp = FakeControlPlane(tmp_path / "knowledge.db")
    core._now = lambda: "2026-09-18T05:00:00+00:00"
    core._settings_for = lambda client_id: {"current": {"knowledge_cloud_sync_enabled": True}}
    core._web_client = lambda request=None: {"id": 1}
    core._runtime_client = lambda request=None: {"id": 1}
    sys.modules["bot_web_console"] = core

    name = "bot_web_conversation_knowledge_under_test"
    spec = importlib.util.spec_from_file_location(name, MODULE_PATH)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    sys.modules[name] = module
    spec.loader.exec_module(module)

    with core._cp.db() as conn:
        conn.executescript(
            """
            CREATE TABLE client_tokens (id INTEGER PRIMARY KEY);
            INSERT INTO client_tokens(id) VALUES(1),(2);
            """
        )
    module.init_db()
    return module, core


def item(item_id: str, title: str, answer: str):
    return {
        "Id": item_id,
        "Category": "通用",
        "Title": title,
        "Answer": answer,
        "Keywords": "",
        "Enabled": True,
        "SourceType": "test",
        "CreatedAt": "2026-09-18 12:00:00",
        "UpdatedAt": "2026-09-18 12:00:00",
    }


@needs_server_deps
def test_export_merge_replace_and_backups_are_client_isolated(tmp_path):
    from fastapi import HTTPException

    module, core = load_module(tmp_path)
    module._save_knowledge(1, [item("a", "问题A", "答案A")], "windows")
    exported = module.web_knowledge_export(client={"id": 1})
    assert exported["format"] == "qianniu-bot-knowledge"
    assert exported["format_version"] == 1
    assert exported["revision"] == 1
    assert exported["items"][0]["Title"] == "问题A"

    merged = module.web_knowledge_import(
        module.KnowledgeImportInput(
            mode="merge",
            items=[
                item("a", "问题A", "答案A-更新"),
                item("b", "问题B", "答案B"),
            ],
        ),
        client={"id": 1},
    )
    assert merged["revision"] == 2
    assert merged["updated"] == 1
    assert merged["added"] == 1
    assert merged["skipped"] == 0
    state = module._knowledge_row(1)
    assert {x["Id"] for x in state["items"]} == {"a", "b"}
    assert next(x for x in state["items"] if x["Id"] == "a")["Answer"] == "答案A-更新"

    backups = module.web_knowledge_backups(limit=20, client={"id": 1})["backups"]
    assert len(backups) == 1
    assert backups[0]["revision"] == 1
    assert backups[0]["reason"] == "web-import-merge"

    with pytest.raises(HTTPException) as blocked:
        module.web_knowledge_import(
            module.KnowledgeImportInput(mode="replace", items=[item("c", "问题C", "答案C")]),
            client={"id": 1},
        )
    assert blocked.value.status_code == 409
    assert "二次确认" in blocked.value.detail

    replaced = module.web_knowledge_import(
        module.KnowledgeImportInput(
            mode="replace",
            replace_confirmed=True,
            items=[item("c", "问题C", "答案C")],
        ),
        client={"id": 1},
    )
    assert replaced["revision"] == 3
    assert module._knowledge_row(1)["items"][0]["Id"] == "c"
    assert len(module.web_knowledge_backups(limit=20, client={"id": 1})["backups"]) == 2

    module._save_knowledge(2, [item("z", "店铺二", "答案")], "windows")
    assert module.web_knowledge_export(client={"id": 2})["items"][0]["Id"] == "z"
    assert module.web_knowledge_export(client={"id": 1})["items"][0]["Id"] == "c"


@needs_server_deps
def test_merge_skips_same_title_with_different_id_and_rejects_duplicate_import_file(tmp_path):
    from fastapi import HTTPException

    module, _ = load_module(tmp_path)
    module._save_knowledge(1, [item("a", "相同问题", "原答案")], "windows")
    merged = module.web_knowledge_import(
        module.KnowledgeImportInput(
            mode="merge",
            items=[item("b", "相同问题", "另一个答案")],
        ),
        client={"id": 1},
    )
    assert merged["skipped"] == 1
    assert len(module._knowledge_row(1)["items"]) == 1

    with pytest.raises(HTTPException) as duplicate:
        module.web_knowledge_import(
            module.KnowledgeImportInput(
                mode="merge",
                items=[
                    item("x", "问题X", "答案1"),
                    item("x", "问题Y", "答案2"),
                ],
            ),
            client={"id": 1},
        )
    assert duplicate.value.status_code == 422
    assert "重复" in duplicate.value.detail
