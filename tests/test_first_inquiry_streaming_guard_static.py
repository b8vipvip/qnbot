from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def canonical_block() -> str:
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    start = coordinator.index("internal static class CanonicalPreMergeDecisionService")
    end = coordinator.index("internal sealed class BuyerMessageBurstCoordinator", start)
    return coordinator[start:end]


def test_canonical_reply_policy_runs_before_merge_and_ai_dispatch():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    canonical = canonical_block()

    decision = coordinator.index("CanonicalPreMergeDecisionService.HandleAsync(")
    merge = coordinator.index("EnqueueForMerge(item)", decision)
    legacy_gate = coordinator.index("LegacyAiConfigurationGate.WaitAsync", merge)
    assert decision < merge < legacy_gate
    assert "不检查AI接口" in canonical
    assert "MyOpenAI" not in canonical
    assert "AiEndpointStore" not in canonical
    assert "SemaphoreSlim" not in canonical


def test_shop_scoped_reply_dispatch_is_inside_the_same_buyer_worker():
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    worker = coordinator.index("private async Task RunAsync")
    decision = coordinator.index("CanonicalPreMergeDecisionService.HandleAsync(", worker)
    merge = coordinator.index("EnqueueForMerge(item)", decision)
    dispatch = coordinator.index("await DispatchScopedAsync", merge)
    assert worker < decision < merge < dispatch
    assert "state.PendingRules.Enqueue(item)" in coordinator
    assert "if (!state.WorkerRunning)" in coordinator


def test_first_inquiry_is_sent_locally_and_committed_only_after_real_send():
    canonical = canonical_block()

    resolve = canonical.index("FirstInquiryFixedReplyService.TryResolve(")
    invoke_send = canonical.index("var firstOk = await SendFixedAsync(", resolve)
    success = canonical.index("if (firstOk)", invoke_send)
    mark = canonical.index("FirstInquiryFixedReplyService.MarkDelivered(", success)
    failure = canonical.index("else", mark)
    release = canonical.index("FirstInquiryFixedReplyService.ReleaseReservation(", failure)
    local_short = canonical.index("if (allowLocalShortReply)", release)
    failure_block = canonical[failure:local_short]
    sender = canonical.index("qn.SendTextWithRetryAsync(")

    assert resolve < invoke_send < success < mark < failure < release < local_short
    assert sender > release
    assert "首条咨询固定回复" in canonical
    assert "ReleaseReservation(" in failure_block
    assert "CanonicalPreMergeOutcome.Failed" in failure_block


def test_off_hours_reply_is_a_fixed_local_rule_not_ai_dependent():
    canonical = canonical_block()

    assert "cfg.EnableWorkHours" in canonical
    assert "cfg.OffHoursFixedText" in canonical
    assert "IsInsideWorkHours" in canonical
    assert '"下班自动回复"' in canonical
    off_hours_start = canonical.index("private static bool TryResolveOffHours")
    off_hours_end = canonical.index("private static bool TryParseClock", off_hours_start)
    off_hours_block = canonical[off_hours_start:off_hours_end]
    assert "EvaluateAutoReplyRule" not in off_hours_block
    assert "MyOpenAI" not in canonical


def test_old_reflection_guard_no_longer_rewraps_runtime_handler():
    guard = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")

    assert "_firstInquiryStreamingGuardBootstrap" in guard
    assert "BuyerMessageBurstCoordinator" in guard
    assert "new Timer" not in guard
    assert "BindingFlags" not in guard
    assert '"_handler"' not in guard
    assert "handlerField.SetValue" not in guard
    assert "不再动态重包消息handler" in guard