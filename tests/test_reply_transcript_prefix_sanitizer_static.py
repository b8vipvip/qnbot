from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_streaming_reply_strips_internal_timeline_prefix_before_ui_send_and_learning():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    sanitize = source.index("var sanitizedAnswer = ReplyTranscriptSanitizer.Sanitize(answer);")
    dedupe = source.index("ReplyDeduplicationService.EnsureDistinct(")
    answer_ready = source.index("ResponseProgressTracker.SetAnswerReady(")
    send = source.index("burst.BuyerNick, answer, 1, lease.CancellationToken")
    delivered = source.index("ReplyDeduplicationService.RememberDelivered(", send)

    assert sanitize < dedupe < answer_ready < send < delivered
    assert "if (sendOk)" in source[send:delivered]
    assert "KnowledgeLearningService.QueueLearn(" not in source
    assert "已移除AI回复中的内部时间线前缀" in source


def test_model_prompt_explicitly_forbids_copying_internal_timeline_labels():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    assert "dynamicSystemPrompt += ReplyTranscriptSanitizer.PromptGuard;" in source
    assert "历史消息中的 [yyyy-MM-dd HH:mm:ss 客服]" in source
    assert "只是内部时间线标签" in source
    assert "禁止把内部日期、时间、客服/买家/assistant/user 说话人标签复制到回复开头" in source


def test_sanitizer_handles_timestamp_role_current_message_and_bracket_variants_without_touching_ai_marker():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    assert "BracketedTimelinePrefix" in source
    assert "PlainTimelinePrefix" in source
    assert "(?:当前消息\\s*)?" in source
    assert "(?:客服|买家|assistant|user)" in source
    assert "[\\]】］]" in source
    assert "for (var i = 0; i < 3; i++)" in source
    assert "[AI]" not in source[source.index("internal static class ReplyTranscriptSanitizer"):source.index("internal static class StreamingBuyerAnswerService")]


def test_stream_and_nonstream_fallback_are_sanitized_before_return():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    assert "var sanitized = ReplyTranscriptSanitizer.Sanitize(result.Answer);" in source
    assert "var sanitized = ReplyTranscriptSanitizer.Sanitize(fallback.Answer);" in source
    assert "模型仅返回了内部时间线标签，已丢弃" in source
    assert "非流式兜底仅返回了内部时间线标签，已丢弃" in source
