"""Regression contracts for the 2026-11-06 12:14–12:48 stale-draft stall.

Field log: the Bot successfully wrote a preset reply by CDP, then
VerifyAnswerFreshness rejected it, and synchronous UIA cleanup inside
SetSendFailure held QN._sendGate for approximately 34m 37s.

Fail closed on all uncertain editor ownership; never let an abandoned COM
worker delete a newer operator draft or open a second concurrent mutation.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_stale_send_failure_setter_cannot_synchronously_enter_uia():
    source = read("src/Bot/ChromeNs/QNRpa.ReliableSend.cs")
    setter = source.split("internal void SetSendFailure(", 1)[1].split(
        "private static bool IsNonRetryableStaleAnswer", 1
    )[0]
    assert "LastSendWasCancelled = IsNonRetryableStaleAnswer(stage, detail);" in setter
    assert "ClearCancelledExactBotDraftIfPresent" not in setter
    assert "ClearCancelledExactBotDraftIfPresent" not in source
    assert "TryGetEditorText(" not in setter
    assert "FocusEditor(" not in setter
    assert "PressCtrlA(" not in setter
    assert "PressBackspace(" not in setter
    assert "草稿清理由有界发送收尾执行" in setter


def test_stale_draft_cleanup_is_bounded_and_owner_checked_after_focus():
    source = read("src/Bot/ChromeNs/QNRpa.cs")
    start = source.index("private async Task ClearExpectedDraftIfSafeAsync")
    end = source.index("private async Task<bool> ClearStaleComposerBeforeNewDraftAsync", start)
    method = source[start:end]
    assert "HasOwnedRecentDraft(expected)" in method
    assert "ReadCurrentBuyerForDraftCleanupAsync" in method
    assert "Task.WhenAny(read, Task.Delay(1500))" in method
    assert "IsExpectedBuyer(buyer, currentBuyer)" in method
    assert "IsExpectedBuyer(buyer, buyerBeforeClear)" in method
    assert "cleanupDeadlineUtc" in method
    assert "RunUiMutationAsync(() =>" in method
    assert '}, "发送失败精确草稿有界清理", false)' in method
    first_text = method.index("IsOwnedDraftForBuyer(buyer, currentText)")
    focus = method.index("!FocusEditor()", first_text)
    after_focus = method.index("IsOwnedDraftForBuyer(buyer, focusedText)", focus)
    ctrl_a = method.index("PressCtrlA();", after_focus)
    backspace = method.index("PressBackspace();", ctrl_a)
    assert first_text < focus < after_focus < ctrl_a < backspace
    assert "DateTime.UtcNow >= cleanupDeadlineUtc" in method
    assert "IsKnownBotOwnedDraftText" not in method  # cancelled replies set LastSendWasCancelled=true
    assert "DispatcherEx.xInvoke(" not in method


def test_ui_mutation_cleanup_preserves_original_stale_cancellation_reason():
    rpa = read("src/Bot/ChromeNs/QNRpa.cs")
    method = rpa.split("private async Task<bool> RunUiMutationAsync", 1)[1].split(
        "private async Task<bool> HasExpectedDraftFastAsync", 1
    )[0]
    assert "bool updateSendFailure = true" in method
    assert "if (updateSendFailure) SetSendFailure(stage," in method
    assert "Task.Delay(UiMutationTimeoutMs)" in method
    assert "_activeUiMutationTask != null && !_activeUiMutationTask.IsCompleted" in method
    assert "ReferenceEquals(_activeUiMutationTask, worker)" in method
    assert "原任务保持独占租约直到安全退出" in method
    assert "Thread.Abort" not in method


def test_send_gate_is_released_and_wait_and_hold_are_diagnosed():
    qn = read("src/Bot/ChromeNs/QN.cs")
    region = qn.split("public async Task<bool> SendTextWithRetryAsync(", 1)[1].split(
        "private async Task<bool> WaitForSellerEchoGraceAsync", 1
    )[0]
    assert "await _sendGate.WaitAsync(cancellationToken)" in region
    assert "sendGateAcquiredAt = DateTime.UtcNow" in region
    assert "可靠发送等待锁耗时较长" in region
    assert "finally" in region
    assert "_sendGate.Release();" in region
    assert "可靠发送锁已释放" in region
    assert "rpa.LastSendWasCancelled" in region


def test_cancellation_keeps_real_send_and_buyer_integrity_checks():
    rpa = read("src/Bot/ChromeNs/QNRpa.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")
    assert 'VerifyAnswerFreshness(buyer, text, attemptStartedAt, "发送前答案时效检查")' in rpa
    assert "await ClearExpectedDraftIfSafeAsync(buyer, text, GetSendFailureReason())" in rpa
    assert "SendDeliveryWatchdog.CancelPending(SellerNick, buyer, text" in rpa
    assert "if (!ok && rpa.LastSendWasCancelled)" in qn
    assert "禁止重试" in qn
