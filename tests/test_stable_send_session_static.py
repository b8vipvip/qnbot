from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_transient_empty_current_buyer_is_retried_without_relaxing_cross_buyer_guard():
    source = text("src/Bot/ChromeNs/QNRpa.cs")
    assert "for (var attempt = 0; attempt < 7; attempt++)" in source
    assert "会话确认暂时为空，等待稳定" in source
    assert "会话持续为空，重新打开目标买家后再次确认" in source
    assert "for (var attempt = 0; attempt < 5; attempt++)" in source
    assert "if (!string.IsNullOrWhiteSpace(currentNick))" in source
    assert "目标买家=" in source
    assert "BuyerIdentityAliasService.AreEquivalent" in source


def test_stale_answer_retry_is_cancelled_and_draft_is_cleared():
    qnrpa = text("src/Bot/ChromeNs/QNRpa.cs")
    runtime = text("src/Bot/ChromeNs/QN.RuntimeSafety.cs")
    assert "HasBuyerMessageAfter" in runtime
    assert "AnswerAttemptStartedAt" in qnrpa
    assert "VerifyAnswerFreshness" in qnrpa
    assert "旧答案发送/重试已取消" in qnrpa
    assert "发送前答案时效检查" in qnrpa
    assert "ClearExpectedDraftIfSafeAsync" in qnrpa
    assert 'SetSendCancellation(stage, "买家已发送更新消息，旧答案不会发送")' in qnrpa


def test_mandatory_order_preset_keeps_priority_when_buyer_sends_follow_up():
    qnrpa = text("src/Bot/ChromeNs/QNRpa.cs")
    tracker = text("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    order = text("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "internal static class ReliableSendAuthority" in order
    assert "BeginBusinessCritical(" in order
    assert "order_action_ledger" in order
    assert "ReliableSendAuthority.IsProtectedFromBuyerUpdate" in qnrpa
    assert "业务可靠发送权限受保护" in qnrpa
    assert qnrpa.index("ReliableSendAuthority.IsProtectedFromBuyerUpdate") < qnrpa.index("HasBuyerMessageAfter")
    assert "IsMandatoryOrderAnswer" not in tracker
    assert "ResponseProgressTracker.IsMandatoryOrderAnswer" not in qnrpa


def test_delivery_watchdog_starts_only_after_transaction_guards_and_is_shop_bound():
    qnrpa = text("src/Bot/ChromeNs/QNRpa.cs")
    native = text("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    watchdog = text("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    open_send = qnrpa[qnrpa.index("private async Task<bool> OpenAndSendText"):]
    pre_session = open_send.index('VerifyCurrentBuyerAsync(buyer, "写入前会话确认")')
    pre_refresh = open_send.index("RefreshChatControlsAsync(true)", pre_session)
    write_index = open_send.index("TrySetPlainTextByCdpAsync(buyer, text)", pre_refresh)
    post_session = open_send.index('VerifyCurrentBuyerAsync(buyer, "发送前会话确认")', write_index)
    draft_index = open_send.index("HasExpectedDraftFastAsync(text, 1200)", post_session)
    ensure_index = open_send.index("SendDeliveryWatchdog.EnsurePending", draft_index)
    send_index = open_send.index("sendResult = await TrySendTextNativeFirstAsync", ensure_index)
    assert pre_session < pre_refresh < write_index < post_session < draft_index < ensure_index < send_index
    assert "TrySendTextViaUiaAsync(buyer, text, sendStart)" in native
    assert "已准备本店真实发送回显监控（尚未开始计时）" in watchdog
    assert "Interlocked.CompareExchange(ref pending.Started, 1, 0)" in watchdog
    assert "pair.Value.Started == 0" in watchdog
    assert "pending.Shop.ShopKey" in watchdog


def test_post_write_session_guard_is_read_only_when_bot_owns_the_draft():
    qnrpa = text("src/Bot/ChromeNs/QNRpa.cs")
    verify_start = qnrpa.index("private async Task<bool> VerifyCurrentBuyerAsync")
    verify_end = qnrpa.index("private async Task ClearExpectedDraftIfSafeAsync", verify_start)
    verify = qnrpa[verify_start:verify_end]
    assert verify.index("if (HasLiveOwnedDraft())") < verify.index("for (var attempt = 0; attempt < 7; attempt++)")
    assert "VerifyCurrentBuyerWithoutNavigationAsync(buyer, stage)" in verify

    readonly_start = qnrpa.index("private async Task<bool> VerifyCurrentBuyerWithoutNavigationAsync")
    readonly_end = qnrpa.index("private async Task<bool> VerifyCurrentBuyerAsync", readonly_start)
    readonly_guard = qnrpa[readonly_start:readonly_end]
    assert "ReadCurrentBuyerNickAsync" in readonly_guard
    assert "OpenChat(" not in readonly_guard
    assert "Task.Delay(" not in readonly_guard
    assert "禁止重开/切换会话" in readonly_guard


def test_failed_bot_draft_cleanup_requires_same_buyer_exact_text_and_bot_ownership():
    qnrpa = text("src/Bot/ChromeNs/QNRpa.cs")
    cleanup_start = qnrpa.index("private async Task ClearExpectedDraftIfSafeAsync")
    cleanup_end = qnrpa.index("private async Task<bool> TrySetPlainTextByCdpAsync", cleanup_start)
    cleanup = qnrpa[cleanup_start:cleanup_end]
    assert "IsExpectedBuyer(buyer, currentBuyer)" in cleanup
    assert "EditorMatchesExpectedText(currentText, expected)" in cleanup
    assert "IsKnownBotOwnedDraftText(currentText)" in cleanup
    assert "buyerBeforeClear" in cleanup
    assert "IsExpectedBuyer(buyer, buyerBeforeClear)" in cleanup
    assert "PressCtrlA()" in cleanup
    assert "PressBackspace()" in cleanup


def test_send_path_drops_wpf_context_and_never_uses_enter_for_delivery():
    qnrpa = text("src/Bot/ChromeNs/QNRpa.cs")
    native = text("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    send = qnrpa[qnrpa.index("public async Task<bool> SendTextAsync"):qnrpa.index("private string SellerNick")]
    assert "Task.Delay(180).ConfigureAwait(false)" in send
    assert "OpenAndSendText(buyer, text).ConfigureAwait(false)" in send
    assert ".GetAwaiter().GetResult()" not in qnrpa
    assert "keybd_event(0x0D" not in qnrpa
    assert "PressEnter" not in qnrpa
    assert "TrySendTextNativeFirstAsync" in qnrpa
    assert "TrySendTextViaUiaAsync(buyer, text, sendStart)" in native
    assert "PressEnter" not in native


def test_late_seller_echo_recovers_original_reply_card():
    watchdog = text("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    tracker = text("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert "ResponseProgressTracker.MarkDeliveryConfirmed" in watchdog
    assert "DeliveryUi" in tracker
    assert "本店回复卡片已按卖家回显恢复为发送成功" in tracker
    assert "BotConnectionDiagnostics.RecordSendAttempt(true" in tracker


def test_send_diagnostic_timeout_is_non_fatal():
    source = text("src/Bot/ChromeNs/SendFailureAnomalyService.cs")
    assert "catch (TaskCanceledException)" in source
    assert "AI诊断超时，已保留本地规则诊断" in source
    assert "发送失败AI诊断超时，不影响Bot运行" in source
