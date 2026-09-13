from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PIPELINE = ROOT / "src" / "Bot" / "ChromeNs" / "BuyerStreamingReplyPipeline.cs"


def _source() -> str:
    return PIPELINE.read_text(encoding="utf-8-sig")


def test_ai_total_budget_remains_bounded_below_generation_watchdog():
    source = _source()
    assert "internal const int TotalAiBudgetSeconds = 120;" in source
    assert "generationCts.CancelAfter(TimeSpan.FromSeconds(TotalAiBudgetSeconds));" in source
    assert "StreamPhaseBudgetSeconds" not in source
    assert "StructuredFallbackSeconds" not in source
    assert "timeoutCts" not in source


def test_invalid_ai_answer_fails_before_answer_ready_transition():
    source = _source()
    failure_gate = source.index('if (string.IsNullOrWhiteSpace(answer)\n                || answer.StartsWith("错误：", StringComparison.Ordinal))')
    fail_call = source.index("ResponseProgressTracker.Fail(", failure_gate)
    answer_ready = source.index("ResponseProgressTracker.SetAnswerReady(", failure_gate)

    assert failure_gate < fail_call < answer_ready
    assert 'conversationCtl.SetProcessing("AI未生成可用答案");' in source
    assert "保持失败态且不进入答案就绪/完成" in source


def test_error_answer_is_not_completed_or_sent():
    source = _source()
    failure_gate = source.index('if (string.IsNullOrWhiteSpace(answer)\n                || answer.StartsWith("错误：", StringComparison.Ordinal))')
    failure_block_end = source.index("var deduplication = ReplyDeduplicationService.EnsureDistinct", failure_gate)
    failure_block = source[failure_gate:failure_block_end]

    assert "ResponseProgressTracker.Fail(" in failure_block
    assert "ResponseProgressTracker.Complete(" not in failure_block
    assert "SetAnswerReady(" not in failure_block
    assert "SendTextWithRetryAsync(" not in failure_block


def test_no_late_ai_error_complete_branch_remains_after_answer_ready():
    source = _source()
    answer_ready = source.index("ResponseProgressTracker.SetAnswerReady(")
    auto_send = source.index("if (!autoSend)", answer_ready)
    send_call = source.index("SendTextWithRetryAsync", auto_send)
    between = source[auto_send:send_call]

    assert 'answer.StartsWith("错误：", StringComparison.Ordinal)' not in between
    assert 'SetSendResult(false, "未发送：AI错误")' not in between
