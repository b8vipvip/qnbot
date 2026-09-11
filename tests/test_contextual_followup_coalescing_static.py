from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_contextual_followups_keep_substantive_anchor_and_support_ellipsis():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "SemanticContinuationWindowSeconds = 180" in source
    assert "AnchorText" in source and "LatestGeneration" in source
    assert "IsPunctuationOnlySemanticNudge" in source
    assert '可以|可以吗|可以不' in source
    assert '能|能吗|能用|能用吗|能不能' in source
    assert '多久|什么时候|多少钱|在哪|哪里' in source
    assert "semantic_continuation_superseded" in source
    assert "previous.LatestGeneration" in source
    assert "MarkContextualContinuationMerged" in source
    assert "最近商品/图片/订单上下文" in source


def test_model_question_is_used_by_both_text_reasoning_paths():
    streaming = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    legacy = read("src/Bot/ChromeNs/QN.cs")
    assert "string.IsNullOrWhiteSpace(burst.ModelQuestion) ? burst.CombinedQuestion : burst.ModelQuestion" in streaming
    assert "string.IsNullOrWhiteSpace(burst.ModelQuestion) ? burst.CombinedQuestion : burst.ModelQuestion" in legacy


def test_premerge_has_one_same_buyer_owner_and_no_late_send_ai_race():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "_preMergeRuleGates" not in coordinator
    assert "PreMergeRuleExecutionDeadlineMilliseconds" not in coordinator
    assert "Task.WhenAny(rulesTask, deadlineTask)" not in coordinator
    assert "PendingRules" in coordinator
    assert "CanonicalPreMergeDecisionService.HandleAsync(" in coordinator
    assert "CanonicalPreMergeOutcome" in coordinator
    assert "single_owner_lane" in coordinator
    assert "single_owner_dispatch" in coordinator

    enqueue_method = coordinator.split("public void Enqueue(BuyerMessageBurstItem item)", 1)[1].split(
        "private async Task RunAsync", 1
    )[0]
    worker = coordinator.split("private async Task RunAsync", 1)[1].split(
        "private void StartOwnedDispatch", 1
    )[0]
    owned = coordinator.split("private void StartOwnedDispatch", 1)[1].split(
        "private async Task ProcessPreMergeAsync", 1
    )[0]
    premerge = coordinator.split("private async Task ProcessPreMergeAsync", 1)[1].split(
        "private bool HasPendingBuyerMessages", 1
    )[0]
    assert "state.PendingRules.Enqueue(item)" in enqueue_method
    assert "await ProcessPreMergeAsync(key, state, ruleItem)" in worker
    assert "StartOwnedDispatch(key, state, burst, lease);" in worker
    assert "DispatchScopedAsync(burst, lease)" in owned
    assert "Task.WhenAny(dispatchTask, cancelledTask)" in owned
    assert "CanonicalPreMergeDecisionService.HandleAsync(" in premerge
    assert "EnqueueForMerge(item)" in premerge

    canonical_start = coordinator.index("internal static class CanonicalPreMergeDecisionService")
    canonical_end = coordinator.index("internal sealed class BuyerMessageBurstCoordinator", canonical_start)
    canonical = coordinator[canonical_start:canonical_end]
    assert "SemaphoreSlim" not in canonical
    assert "BuyerSessionAgent" not in canonical
    assert "BotActivityCoordinator.Begin" not in canonical
    assert "TryTransition(" not in canonical


def test_non_buyer_runtime_probe_is_guarded_before_success_correction():
    monitor = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    first_guard = monitor.index('RejectNonBuyerProbe(qn, seller, first, currentNick, "first_read")')
    same = monitor.index("AreSameBuyer(seller, currentNick, firstNick)", first_guard)
    second_guard = monitor.index('RejectNonBuyerProbe(qn, seller, second, currentNick, "stable_read")')
    corrected = monitor.index('"当前买家由主动探测修正', second_guard)
    assert first_guard < same
    assert second_guard < corrected
    assert "保持已验证buyer不变" in monitor


def test_pending_progress_card_can_be_terminally_folded_into_contextual_followup():
    progress = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    assert "public static void MarkContextualContinuationMerged" in progress
    assert "本条已合并到最新问题语义中" in progress
    assert "entry.AnswerReadyAt != DateTime.MinValue" in progress