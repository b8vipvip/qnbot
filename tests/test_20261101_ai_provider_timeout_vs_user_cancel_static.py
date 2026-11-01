"""Timeouts from AI HTTP deadline must not appear as manual cancellations."""
from pathlib import Path
archive = (Path(__file__).resolve().parents[1] /
           "src/Bot/Knowledge/BuyerMessageArchiveService.cs").read_text(encoding="utf-8-sig")

def test_structured_ai_deadline_is_not_labeled_user_cancel():
    ask = archive.split("private static async Task<JObject> Ask(",1)[1].split(
        "internal static async Task<int> GroupByAi(",1)[0]
    assert "catch (OperationCanceledException ex)" in ask
    assert "if (ct.IsCancellationRequested) throw;" in ask
    assert 'throw new TimeoutException("AI上游请求超过 "' in ask
    assert 'MyOpenAI.CallStructuredChat(messages, maxTokens, 0.1, timeoutSeconds, ct)' in ask

def test_true_manual_cancel_preserves_cancellation_semantics():
    optimize = archive.split("internal static async Task<int> OptimizeTop(",1)[1].split(
        "// Strictly map an archived message",1)[0]
    assert "catch (OperationCanceledException) { throw; }" in optimize
    assert "catch (Exception ex)" in optimize
    assert "不能当作无知识命中" in optimize
