from __future__ import annotations

import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
CONTROL = ROOT / "services" / "api-control-plane"
if str(CONTROL) not in sys.path:
    sys.path.insert(0, str(CONTROL))

import bot_web_bot_qa_logic


def message(
    item_id: int,
    role: str,
    text: str,
    occurred_at: str,
    message_type: str = "text",
):
    return {
        "id": item_id,
        "client_id": 1,
        "message_key": f"m{item_id}",
        "seller": "seller01",
        "buyer": "buyer01",
        "role": role,
        "text": text,
        "message_type": message_type,
        "occurred_at": occurred_at,
        "created_at": occurred_at,
    }


def test_only_bot_qa_is_returned_and_echo_duplicates_are_collapsed():
    rows = [
        message(1, "assistant", "欢迎您光临本店", "2026-08-02T04:35:31+00:00"),
        message(2, "user", "https://item.taobao.com/item.htm?id=1057505313937", "2026-08-02T04:35:42+00:00"),
        message(3, "user", "https://item.taobao.com/item.htm?id=1057505313937", "2026-08-02T04:35:43+00:00"),
        message(4, "assistant", "我看到您发来的商品链接了，想咨询哪方面呢？ [AI]", "2026-08-02T04:35:47+00:00"),
        message(5, "assistant", "我看到您发来的商品链接了，想咨询哪方面呢？ [AI]", "2026-08-02T04:35:47+00:00"),
        message(6, "user", "这个是冲到自己账号上吗", "2026-08-02T04:35:50+00:00"),
        message(7, "assistant", "充你手机号", "2026-08-02T04:36:02+00:00"),
        message(8, "user", "ok", "2026-08-02T04:36:08+00:00"),
    ]

    result = bot_web_bot_qa_logic.build_bot_qa_messages(rows)

    assert [item["message_type"] for item in result] == ["bot_question", "bot_answer"]
    assert result[0]["text"] == "https://item.taobao.com/item.htm?id=1057505313937"
    assert result[1]["text"] == "我看到您发来的商品链接了，想咨询哪方面呢？"
    assert all("欢迎您光临本店" not in item["text"] for item in result)
    assert all("充你手机号" not in item["text"] for item in result)
    assert all(item["text"] != "ok" for item in result)


def test_distinct_question_burst_is_kept_for_one_bot_answer():
    rows = [
        message(10, "user", "电视端怎么绑定？", "2026-08-02T04:40:00+00:00"),
        message(11, "user", "我用的是雷鸟电视", "2026-08-02T04:40:10+00:00"),
        message(12, "assistant", "请按雷鸟电视教程绑定酷狗账号。 [AI]", "2026-08-02T04:40:15+00:00"),
    ]

    result = bot_web_bot_qa_logic.build_bot_qa_messages(rows)

    assert [item["text"] for item in result] == [
        "电视端怎么绑定？",
        "我用的是雷鸟电视",
        "请按雷鸟电视教程绑定酷狗账号。",
    ]


def test_identical_bot_qa_after_duplicate_window_is_not_lost():
    rows = [
        message(20, "user", "多久到账", "2026-08-02T04:00:00+00:00"),
        message(21, "assistant", "一般八分钟左右到账。 [AI]", "2026-08-02T04:00:05+00:00"),
        message(22, "user", "多久到账", "2026-08-02T04:05:00+00:00"),
        message(23, "assistant", "一般八分钟左右到账。 [AI]", "2026-08-02T04:05:05+00:00"),
    ]

    result = bot_web_bot_qa_logic.build_bot_qa_messages(rows)

    assert len(result) == 4
    assert [item["message_type"] for item in result] == [
        "bot_question",
        "bot_answer",
        "bot_question",
        "bot_answer",
    ]


def test_bot_answer_message_type_is_supported_without_ai_suffix():
    rows = [
        message(30, "user", "支持车机吗", "2026-08-02T05:00:00+00:00"),
        message(31, "assistant", "请发设备酷狗账号界面确认。", "2026-08-02T05:00:03+00:00", "bot_answer"),
    ]

    result = bot_web_bot_qa_logic.build_bot_qa_messages(rows)

    assert len(result) == 2
    assert result[-1]["text"] == "请发设备酷狗账号界面确认。"


def test_mobile_inbox_preserves_the_full_desktop_message_timeline():
    rows = [
        message(40, "assistant", "您好，我是人工客服。", "2026-08-02T06:00:00+00:00"),
        message(41, "user", "这个功能怎么使用？", "2026-08-02T06:00:05+00:00"),
        message(42, "assistant", "请在设置页面开启同步。[A]", "2026-08-02T06:00:10+00:00"),
        message(43, "assistant", "这条消息后来撤回。[A]", "2026-08-02T06:00:15+00:00"),
    ]
    rows[3]["withdrawn"] = 1

    result = [bot_web_bot_qa_logic.project_conversation_message(row) for row in rows]

    assert len(result) == 4
    assert result[0]["text"] == "您好，我是人工客服。"
    assert result[0]["is_bot"] is False
    assert result[2]["text"] == "请在设置页面开启同步。"
    assert result[2]["is_bot"] is True
    assert result[3]["withdrawn"] is True
    assert result[3]["is_bot"] is False


def test_mobile_inbox_api_does_not_filter_out_manual_seller_messages():
    source = (CONTROL / "bot_web_bot_qa.py").read_text(encoding="utf-8-sig")
    assert "build_bot_qa_messages" not in source
    assert 'record_scope": "conversation"' in source
    assert "ORDER BY occurred_at ASC,id ASC" in source



def test_bootstrap_registers_bot_qa_routes_before_legacy_conversation_routes():
    bootstrap = (CONTROL / "bootstrap.py").read_text(encoding="utf-8-sig")
    assert bootstrap.index("bot_web_bot_qa.install(control_plane)") < bootstrap.index(
        "bot_web_conversation_knowledge.install(control_plane)"
    )
    dockerfile = (CONTROL / "Dockerfile").read_text(encoding="utf-8-sig")
    assert "bot_web_bot_qa.py" in dockerfile
    assert "bot_web_bot_qa_logic.py" in dockerfile


def test_web_page_uses_buyer_message_inbox_labels():
    page = (CONTROL / "static" / "bot-web.html").read_text(encoding="utf-8-sig")
    assert "买家消息" in page
    assert "仅显示买家问题与 Bot 自动回答" not in page
    assert "同步 Windows Bot 已采集的买家与客服消息" in page
