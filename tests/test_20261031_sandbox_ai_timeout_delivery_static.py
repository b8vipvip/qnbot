"""Regression for sandbox AI response arriving after an obsolete 45-second deadline.

The real buyer reply pipeline intentionally retains separate real-send deadlines.
Only the read-only Knowledge V2 sandbox is allowed a longer AI preview budget.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_sandbox_ai_does_not_discard_valid_50_second_responses():
    ui = read("src/Bot/Knowledge/KnowledgeV2SandboxConversation.cs")
    ai = ui.split("private SandboxAiResult GenerateAiPreview(", 1)[1].split(
        "private async Task LoadHistoricalSampleAsync(", 1)[0]
    assert "private const int SandboxAiTimeoutSeconds = 180;" in ui
    assert "messages, 650, 0.2, SandboxAiTimeoutSeconds," in ai
    assert "CancellationToken.None, true" in ai
    assert "messages, 650, 0.2, 45," not in ai
    assert "Success = response != null && response.Success" in ai
    assert "Answer = response == null ? \"\" : (response.Answer ?? \"\").Trim()" in ai


def test_sandbox_timeout_is_visible_instead_of_losing_bot_turn():
    ui = read("src/Bot/Knowledge/KnowledgeV2SandboxConversation.cs")
    ai = ui.split("private SandboxAiResult GenerateAiPreview(", 1)[1].split(
        "private async Task LoadHistoricalSampleAsync(", 1)[0]
    run = ui.split("private async Task RunAsync(", 1)[1].split(
        "private sealed class PreviewResult", 1)[0]
    assert "catch (OperationCanceledException)" in ai
    assert "Success = false, TimedOut = true" in ai
    assert "AI响应超时" in ai
    assert 'ai.TimedOut ? "timeout" : (ai.Success ? "success" : "error")' in run
    assert '"AI生成失败：" + ai.Detail' in run
    assert 'AddBubble("Bot", result.Reply, result.Source' in run
    assert "await Task.Run(() => GenerateAiPreview(" in run


def test_sandbox_does_not_take_over_real_delivery_or_order_actions():
    ui = read("src/Bot/Knowledge/KnowledgeV2SandboxConversation.cs")
    for forbidden in ("SendTextWithRetryAsync(", "OrderEventHub.Publish(",
                      "MyOpenAI.GetAnswer(", "MarkDelivered("):
        assert forbidden not in ui
    assert 'ToolTip = "仅调用当前店铺 AI 生成模拟答案，不会给真实买家发送消息"' in ui
