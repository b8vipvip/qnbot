from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_text_send_fails_closed_without_exact_draft_or_target_buyer():
    qnrpa = read("src/Bot/ChromeNs/QNRpa.cs")
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    reliable = read("src/Bot/ChromeNs/QNRpa.ReliableSend.cs")

    assert "VerifyCurrentBuyerAsync" in qnrpa
    assert '"写入前会话确认"' in qnrpa
    assert '"发送前会话确认"' in qnrpa
    assert '"发送前文本确认"' in qnrpa
    assert "HasExpectedDraftFastAsync" in qnrpa
    assert "输入框内容已变化或无法确认，已阻止发送" in qnrpa

    # A stale composer may only be mutated after target-buyer proof and exact Bot ownership proof.
    # Unknown/manual content is preserved fail-closed. Caller wait is bounded, while a timed-out
    # COM/UIA worker retains an exclusive lease until it exits so a second mutation cannot race it.
    cleanup = qnrpa.index("private async Task<bool> ClearStaleComposerBeforeNewDraftAsync")
    cleanup_end = qnrpa.index("private async Task<bool> TrySetPlainTextByCdpAsync", cleanup)
    block = qnrpa[cleanup:cleanup_end]
    buyer_read = block.index("ReadCurrentBuyerNickAsync()")
    buyer_guard = block.index("IsExpectedBuyer(buyer, currentBuyer)", buyer_read)
    exact_draft = block.index("EditorMatchesExpectedText(observedText, expected)", buyer_guard)
    ownership = block.index("IsOwnedDraftForBuyer(buyer, observedText)", exact_draft)
    clear = block.index("检测到同一买家的Bot历史残留草稿", ownership)
    mutation = block.index("RunUiMutationAsync", clear)
    reread = block.index("TryGetEditorText(out afterClear)", mutation)
    buyer_after = block.index("buyerAfterClear", reread)
    cdp_after = block.index("残留草稿清理后确认", buyer_after)
    assert buyer_read < buyer_guard < exact_draft < ownership < clear < mutation < reread < buyer_after < cdp_after
    assert "输入框存在所有权无法证明的内容，已保留并阻止覆盖/追加发送" in block
    assert "清空后CDP未确认输入框为空，禁止盲目追加写入" in block

    mutation_helper = qnrpa[qnrpa.index("private async Task<bool> RunUiMutationAsync"):qnrpa.index("private async Task<bool> HasExpectedDraftFastAsync")]
    assert "Task.WhenAny" in mutation_helper
    assert "Task.Delay(UiMutationTimeoutMs)" in mutation_helper
    assert "_activeUiMutationTask != null && !_activeUiMutationTask.IsCompleted" in mutation_helper
    assert "原任务保持独占租约直到安全退出" in mutation_helper
    assert "Thread.Abort" not in mutation_helper

    assert "UIA写入确认" in qnrpa
    open_send = qnrpa[qnrpa.index("private async Task<bool> OpenAndSendText"):]
    assert "SetPlainText(text)" not in open_send
    assert "TrySendTextNativeFirstAsync" in open_send
    assert "TrySendTextViaUiaAsync(\n                buyer, text, sendStart, manualTakeoverCheck, preSendGuard)" in native
    assert native.count("HasExpectedDraftFastAsync(text") >= 4
    assert "if (string.IsNullOrEmpty(expected))" in reliable


def test_order_auto_reply_cannot_bypass_bot_master_switch():
    source = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    master_switch = source.index("if (!Params.Robot.CanUseRobotReal) return false;")
    config_read = source.index("BotFeatureStore.GetAutoReplyRules()")
    assert master_switch < config_read


def test_real_flow_test_is_bound_to_the_original_shopkey_not_just_seller_nickname():
    source = read("src/Bot/ChromeNs/BotFlowTestService.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")
    recovery = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    assert "QN.FindExistingByShopKey(shopKey)" in source
    assert "QN.FindExistingBySellerNick(candidate.Seller)" not in source
    assert "ShopKey = shopKey" in source
    assert "ShopKey = shop.ShopKey" in source
    assert "candidate.ShopKey" in source
    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in source
    assert "BotFlowTestService.RecordCandidate(sellerNick, buyerNick, displayQuestion, detectedAt, shopKey);" in qn
    assert "BotFlowTestService.RecordCandidate(sellerNick, buyerNick, displayQuestion, detectedAt, shopKey);" in recovery
    assert "测试问题缺少可信ShopKey" in source
    assert "目标店铺身份与测试问题来源不一致" in source


def test_progress_tracker_does_not_delete_newer_buyer_message_state():
    source = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert "AnswerReadyAt" in source
    assert "ConcurrentDictionary<string, string> CurrentTurns" in source
    assert "AsyncLocal<string> OperationTurnKey" in source
    assert "TurnKey(string seller, string buyer, DateTime detectedAt, string shopKey = null)" in source
    assert "ResolveTerminalTurnKey" in source
    assert "PromoteCurrentTurn" in source
    assert "ConsolidatePendingBurstEntries" in source
    assert "ShouldDeferUnsupportedMediaCard" in source
    # Terminal operations remove the AsyncLocal-bound turn, not whichever newer turn is current.
    assert "var turnKey = ResolveTerminalTurnKey(seller, buyer, shopKey);" in source
    assert "TryRemoveTurn(turnKey" in source
