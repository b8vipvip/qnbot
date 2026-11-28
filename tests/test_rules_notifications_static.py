from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_answer_source_badges_are_explicit():
    xaml = text("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml")
    code = text("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    qn = text("src/Bot/ChromeNs/QN.cs")
    tracker = text("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert "bdSource" in xaml
    assert "txtSourceSeparator" in xaml
    assert "answerSource" in code
    assert "ResolveAnswerSource" in qn
    assert "ReplyDeduplicationService.EnsureDistinct" in qn
    assert 'var source = deduplication.Regenerated' in qn
    assert ': "AI生成";' in qn
    assert "ResponseProgressTracker.SetAnswerReady" in qn
    assert "control.SetAnswer(answer, source, answerReadyAt)" in tracker


def test_history_scan_has_one_native_v2_navigation_entry():
    shell = text("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    smart = text("src/Bot/Knowledge/KnowledgeCenterV2AiToolsUi.cs")
    assert shell.count('Nav("历史聊天整理",') == 1
    assert "KnowledgeV2ChatHistoryPage" in shell
    assert "new ChatHistoryScanWindow" not in smart
    assert not (ROOT / "src/Bot/Knowledge/KnowledgeManagerControl.cs").exists()


def test_off_hours_rule_supports_ai_and_fixed_reply():
    source = text("src/Bot/Options/CtlRobotOptions.xaml.cs")
    ai = text("src/Bot/ChromeNs/MyOpenAI.cs")
    assert "WorkStartTime" in source
    assert "WorkEndTime" in source
    assert "AI告知下班时间" in source
    assert "固定预设答案" in source
    assert "EvaluateAutoReplyRule" in source
    assert "BuildOffHoursHandoffReply" in ai
    assert "AllowAutoReply" in ai


def test_handoff_notifications_cover_requested_channels():
    source = text("src/Bot/ChromeNs/HandoffNotificationService.cs")
    options = text("src/Bot/Options/CtlRobotOptions.xaml.cs")
    for value in ["微信", "QQ", "邮箱", "飞书", "钉钉"]:
        assert value in options or value in source
    assert "WeChatWebhook" in source
    assert "QQWebhook" in source
    assert "FeishuWebhook" in source
    assert "DingTalkWebhook" in source
    assert "SmtpClient" in source
    assert "NotificationCooldownMinutes" in source
