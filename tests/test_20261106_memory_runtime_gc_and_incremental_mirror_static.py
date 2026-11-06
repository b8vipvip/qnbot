"""Regression contracts for client memory instrumentation and safe incremental UI.

Full-rebuild remains authoritative for transport changes, Bot echoes, withdrawals,
out-of-order history, and forced refresh. These tests are not a replacement for
Windows compilation and long-run memory profiling.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def src(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_uncooperative_ai_tasks_are_counted_until_they_actually_finish():
    pipeline = src("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    monitor = src("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    assert "UnfinishedAnswerTasksAfterCancel" in pipeline
    assert "AnswerTasksOutlivedCancellation" in pipeline
    assert "Interlocked.Increment(ref _unfinishedAnswerTasksAfterCancel)" in pipeline
    assert "Interlocked.Decrement(ref _unfinishedAnswerTasksAfterCancel)" in pipeline
    assert "TaskContinuationOptions.ExecuteSynchronously" in pipeline
    assert "task.ContinueWith(" in pipeline
    assert "token.ThrowIfCancellationRequested();" in pipeline
    assert "task.Wait(" not in pipeline
    assert "pendingCancelledAi=" in monitor
    assert "cancelledAiTasksTotal=" in monitor


def test_memory_sampling_reports_gc_generations_and_native_pressure():
    monitor = src("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    for marker in (
        "GC.CollectionCount(0)",
        "GC.CollectionCount(1)",
        "GC.CollectionCount(2)",
        "process.WorkingSet64",
        "process.PrivateMemorySize64",
        "process.Threads.Count",
        "GC.GetTotalMemory(false)",
        "gen2SinceLastSample=",
    ):
        assert marker in monitor
    assert "GC.Collect(" not in monitor
    assert "GetTotalMemory(true)" not in monitor


def test_buyer_only_append_preserves_bot_source_and_fallback_rendering():
    mirror = src("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert "private List<ConversationContextTurn> _messageMirrorRenderedTurns;" in mirror
    assert "TryAppendBuyerMessages(seller, buyer, turns, keepBottom)" in mirror
    assert "if (!force && !identityChanged" in mirror
    assert "if (!SameDisplayedTurn(displayed[i], turns[i])) return false;" in mirror
    assert 'string.Equals(turn.Role, "user", StringComparison.Ordinal)' in mirror
    assert "|| turn.Withdrawn" in mirror
    assert "stkDialog.Children.Add(BuildMessageMirrorBubble(turn, seller, buyer));" in mirror
    assert "RenderMessageMirror(seller, buyer, turns, keepBottom);" in mirror
    assert "BotConversationHistoryStore.RecordBotEcho(" in mirror
    assert "BotConversationHistoryStore.GetManualSource(" in mirror
    assert "BuildMessageMirrorBotContextMenu(" in mirror
    assert "BuildMessageMirrorBuyerContextMenu(" in mirror
    assert "BuildMessageMirrorDateSeparator(" in mirror
    assert "_messageMirrorRenderedTurns = null;" in mirror
    assert "scvBody.ScrollToEnd()" in mirror


def test_original_100_turn_history_is_not_truncated_by_append_optimization():
    source = src("src/Bot/ChromeNs/ConversationContextStore.cs")
    mirror = src("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert "GetMirrorTurns(seller, buyer, 100)" in mirror
    assert "Math.Min(100, maxTurns)" in source
    assert "eligible.Skip(Math.Max(0, eligible.Count - take))" in source


def test_gc_etw_collection_is_bounded_opt_in_and_does_not_change_processes():
    script = src("scripts/Collect-QnBotGcTrace.ps1")
    assert "DurationSeconds = 120" in script
    assert "ValidateRange(15, 600)" in script
    assert "MaxFileMiB = 64" in script
    assert "ValidateRange(16, 128)" in script
    assert "logman.exe" in script
    assert "start $session" in script
    assert "stop $session -ets" in script
    assert "finally" in script
    assert "Microsoft-Windows-DotNETRuntime" in script
    assert "0x1 0x4" in script
    assert "Stop-Process" not in script
    assert "GC]::Collect" not in script
    assert "Invoke-WebRequest" not in script
    assert "Upload" not in script
