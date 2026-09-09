from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_recent_image_caption_is_opt_in_and_rejects_incident_messages():
    src = (ROOT / "src/Bot/ChromeNs/VisionFollowUpContextPipeline.cs").read_text(encoding="utf-8")
    assert "internal static bool IsLikelyImageCaption" in src
    assert r'@"^\\d{5,20}$"' in src
    for token in ["充值", "充这个", "人工", "投诉", "不满意"]:
        assert token in src
    for anchor in ["截图", "页面", "界面", "显示", "提示", "报错"]:
        assert anchor in src


def test_v2_local_direct_requires_current_message_grounding():
    src = (ROOT / "src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs").read_text(encoding="utf-8")
    assert "IsSafeStandaloneLocalDirect(query, message)" in src
    assert "query.WorkingMemoryReason" in src
    for token in ["人工", "客服", "投诉", "不满意", "充没", "到账没"]:
        assert token in src
    assert "&& standaloneSafe" in src


def test_service_attitude_warning_is_fail_closed_not_auto_confirmed():
    src = (ROOT / "src/Bot/ChromeNs/QNRpa.PlatformSendGuard.cs").read_text(encoding="utf-8")
    assert "不会自动点击“继续发送”" in src
    assert "安全策略禁止Bot自动点击“继续发送”" in src
    assert "result.Continued = false;" in src
    assert ".AsButton().Invoke()" not in src


def test_field_incident_messages_are_explicitly_covered():
    vision = (ROOT / "src/Bot/ChromeNs/VisionFollowUpContextPipeline.cs").read_text(encoding="utf-8")
    knowledge = (ROOT / "src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs").read_text(encoding="utf-8")
    assert "手机号" in vision and "号码" in vision
    for token in ["充没", "充上没", "人工", "不满意", "有病"]:
        assert token in knowledge
