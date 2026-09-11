from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_hwnd_safe_send_accepts_only_exact_verified_seller_root():
    source = read("src/Bot/ChromeNs/QNRpa.cs")
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    assert "_sendMessageButton.AsButton().Invoke()" not in source
    assert "GetWindowThreadProcessId(expectedRoot, out rootPid)" in native
    assert "rootPid == 0 || rootPid != expectedPid" in native
    assert "GetAncestor(target, GaRoot)" in native
    assert "if (root != expectedRoot)" in native
    assert "HWND安全发送已阻止跨根窗口点击" in native
    assert "GetWindowThreadProcessId(target, out targetPid)" in native
    assert "if (targetPid != expectedPid)" in native
    assert "HWND安全发送已验证千牛辅助进程子窗口" in native
    assert "允许同一千牛进程的独立根窗口" not in native

    root_owner = native.index("GetWindowThreadProcessId(expectedRoot, out rootPid)")
    root_guard = native.index("rootPid == 0 || rootPid != expectedPid", root_owner)
    target_root = native.index("GetAncestor(target, GaRoot)", root_guard)
    constrained = native.index("ResolveTargetInsideVerifiedSellerRoot(expectedRoot, screenPoint)", target_root)
    constrained_proof = native.index("constrainedRoot == expectedRoot", constrained)
    sibling_guard = native.index("if (root != expectedRoot)", constrained_proof)
    target_pid = native.index("GetWindowThreadProcessId(target, out targetPid)", sibling_guard)
    helper_pid = native.index("if (targetPid != expectedPid)", target_pid)
    post = native.index("PostMessage(target, WmLButtonDown", helper_pid)
    assert root_owner < root_guard < target_root < constrained < constrained_proof < sibling_guard < target_pid < helper_pid < post

    root_guard_block = native[root_guard:target_root]
    sibling_guard_block = native[sibling_guard:target_pid]
    assert "return false;" in root_guard_block
    assert "return false;" in sibling_guard_block


def test_unchanged_failed_bot_draft_does_not_become_fake_human_activity():
    rpa = read("src/Bot/ChromeNs/QNRpa.cs")
    queue = read("src/Bot/ChromeNs/NewOrderAttentionQueue.cs")
    assert "internal bool IsKnownBotOwnedDraftText(string currentText)" in rpa
    assert "EditorMatchesExpectedText(currentText, expected)" in rpa
    assert "!string.IsNullOrWhiteSpace(LastSendFailureReason)" in rpa
    assert "!LastSendWasCancelled" in rpa
    assert "internal async Task<bool> IsKnownBotOwnedDraftAsync()" in rpa
    first_guard = queue.split("if (!input.Empty)", 1)[1].split(
        "var current = await TryGetCurrentBuyerAsync", 1
    )[0]
    assert "IsKnownBotOwnedDraftAsync" in first_guard
    assert first_guard.index("IsKnownBotOwnedDraftAsync") < first_guard.index("MarkHumanInteraction")
    assert "不计为人工操作" in first_guard


def test_failed_generation_is_terminal_after_canonical_premerge_and_ai_paths():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")

    premerge = source.split("private async Task ProcessPreMergeAsync", 1)[1].split(
        "private bool HasPendingBuyerMessages", 1
    )[0]
    assert "CanonicalPreMergeOutcome.Failed" in premerge
    assert "BuyerSessionAgentState.Failed" in premerge
    assert "canonical_pre_merge_failed" in premerge
    assert "CanonicalPreMergeOutcome.Consumed" in premerge
    assert "canonical_pre_merge_consumed" in premerge

    finalizer = source.split("private void FinalizeReplyOutcome", 1)[1].split(
        "private void CompleteMergedAwayGenerations", 1
    )[0]
    assert "TryGetGenerationState" in finalizer
    assert "generationState == BuyerSessionAgentState.Failed" in finalizer
    failed_branch = finalizer.split("if (failed)", 1)[1].split("else if", 1)[0]
    assert "MarkCompleted" not in failed_branch
    assert "回复管线返回时会话已是Failed" in failed_branch
    assert "Dictionary<long, BuyerSessionAgentState> GenerationStates" in agent
    assert "SetGenerationStateLocked(state, generation, next)" in agent


def test_duplicate_conversation_change_cannot_take_outbound_cdp_route():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    client = read("src/Bot/ChromeNs/CDPClient.cs")
    assert "internal bool IsAuthoritativeSellerSession" in server
    switched = client.split("private void BuyerSwitched(string response)", 1)[1].split(
        "private void SellerSwitched", 1
    )[0]
    assert "server.IsAuthoritativeSellerSession(sellerNick, SessionId)" in switched
    assert "PreferRuntimeSession" in switched
    assert "不接管CDP命令路由" in switched