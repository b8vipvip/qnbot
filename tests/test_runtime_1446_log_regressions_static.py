from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_product_link_preset_is_consumed_by_canonical_owner_before_first_inquiry_or_ai():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    canonical = source[source.index("internal static class CanonicalPreMergeDecisionService"):source.index("internal sealed class BuyerMessageBurstCoordinator")]

    offhours = canonical.index("TryResolveOffHours(out offHoursReply)")
    product = canonical.index("ConversationContextStore.TryTakeProductLinkReply(")
    first = canonical.index("FirstInquiryFixedReplyService.TryResolve(")
    local = canonical.index("LocalShortReplyService.TryResolve(")
    assert offhours < product < first < local
    assert '"商品链接预设回复"' in canonical[product:first]
    assert "CanonicalPreMergeOutcome.Consumed" in canonical[product:first]
    assert "aiCalled=false" in canonical[product:first]


def test_one_buyer_queue_owner_tracks_parallel_dispatched_generations_without_head_of_line_blocking():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    lane = source[source.index("private async Task RunAsync"):source.index("private async Task ProcessPreMergeAsync")]
    owned = source[source.index("private void StartOwnedDispatch"):source.index("private async Task ProcessPreMergeAsync")]

    assert "StartOwnedDispatch(key, state, burst, lease);" in lane
    assert "await DispatchScopedAsync(burst, lease)" not in lane
    assert "HashSet<Task> InFlightDispatches" in source
    assert "Task.WhenAny(dispatchTask, cancelledTask)" in owned
    assert "RetireStateLocked" in owned
    assert "state.Retired" in source


def test_local_short_rule_does_not_consume_ack_while_an_older_generation_is_still_in_flight():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    premerge = source[source.index("private async Task ProcessPreMergeAsync"):source.index("private bool HasPendingBuyerMessages")]
    assert "state.InFlightDispatches.Count == 0" in premerge


def test_cancelled_non_cooperative_provider_task_loses_owner_immediately():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    dispatch = source[source.index("private async Task DispatchOwnedAsync"):source.index("private void OnOwnedDispatchCompleted")]
    assert "Task.Delay(Timeout.Infinite, lease.CancellationToken)" in dispatch
    assert "Task.WhenAny(dispatchTask, cancelledTask)" in dispatch
    assert "return;" in dispatch
    assert "底层非协作任务如继续运行也不再拥有发送/终态资格" in dispatch
