from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "Bot" / "ChromeNs" / "KnowledgeLearningService.cs"


def source_text():
    return SOURCE.read_text(encoding="utf-8-sig")


def test_manual_comparison_malformed_structured_output_is_retryable_and_does_not_write():
    text = source_text()
    compare = text.split("private static async Task CompareManualAnswerAsync(", 1)[1].split(
        "private static bool ContainsUnsafeManualLearning(", 1
    )[0]
    queue = text.split("private static void QueueManualAnswerComparison(", 1)[1].split(
        "private static async Task CompareManualAnswerAsync(", 1
    )[0]

    assert "TryParseObject(result.Answer, out parsed)" in compare
    assert "AI对比结果不是可恢复的结构化JSON，等待自动重试。" in compare
    assert "for (var attempt = 0; attempt < 3; attempt++)" in queue
    assert "ManualComparisons.TryRemove(comparisonKey, out ignoredComparison);" in queue


def test_json_recovery_supports_wrappers_fences_arrays_and_encoded_strings():
    text = source_text()
    assert "```(?:json)?" in text
    assert "ExtractBalancedJsonObjects" in text
    assert "token as JArray" in text
    assert "token.Type != JTokenType.String" in text
    assert "depth > 5" in text


def test_json_recovery_is_string_aware_and_not_first_last_brace_slicing():
    text = source_text()
    assert "var inString = false;" in text
    assert "var escaped = false;" in text
    assert "text.IndexOf('{')" not in text
    assert "text.LastIndexOf('}')" not in text
