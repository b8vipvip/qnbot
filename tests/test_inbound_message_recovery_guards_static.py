from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_failed_ingress_claim_can_be_released_without_stale_fifo_entry():
    source = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    dedupe = source[source.index("internal sealed class IncomingMessageDeduplicator"):]
    release = dedupe[dedupe.index("public bool TryRelease(string key)"):]

    assert "if (!_seen.Remove(key)) return false;" in release
    assert "_order.Where(existing => !string.Equals(existing, key, StringComparison.Ordinal))" in release
    assert "_order.Clear();" in release
    assert "foreach (var existing in retained) _order.Enqueue(existing);" in release


def test_live_receive_failure_does_not_abort_remaining_messages_in_same_payload():
    source = read("src/Bot/ChromeNs/QN.cs")
    method = source[
        source.index("private async void Cdp_EvRecieveNewMessage"):
        source.index("private void Cdp_EvMessageNotity")
    ]
    loop = method[method.index("foreach (var message in messages)"):]

    assert "try" in loop
    assert "await ProcessIncomingMessageAsync(message);" in loop
    assert "catch (Exception ex)" in loop
    assert "已隔离并继续处理同批次后续消息" in loop
    assert loop.index("catch (Exception ex)") < loop.index("await Task.Delay(30)")


def test_live_ingress_releases_only_a_failed_synchronous_business_claim():
    source = read("src/Bot/ChromeNs/QN.cs")
    method = source[
        source.index("private Task ProcessIncomingMessageAsync"):
        source.index("private async Task ProcessBuyerBurstAsync")
    ]

    assert "_handledBuyerMessageDeduplicator.TryAccept(messageKey)" in method
    assert "try" in method
    assert "_handledBuyerMessageDeduplicator.TryRelease(messageKey);" in method
    assert "throw;" in method
    assert method.index("_handledBuyerMessageDeduplicator.TryRelease(messageKey);") < method.index("throw;")


def test_recovered_buyer_replay_uses_exact_shop_scope_and_refreshes_timeline():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    method = source[
        source.index("private async Task ProcessRecoveredBuyerMessageAfterMissAsync"):
        source.index("private static bool IsPotentialRecoveredOrderCard")
    ]

    assert "ShopContextLocator.ResolveRuntimeBySeller(this)" in method
    assert "using (ShopSettingsScope.Enter(shop))" in method
    assert "ConversationContextStore.RefreshAndRecord(message, messageText, shopKey);" in method
    assert method.index("ConversationContextStore.RefreshAndRecord") < method.index("OrderPlacedAutoReplyService.TryCreatePlan")
    assert "var accepted = false;" in method
    assert "if (!accepted)" in method
    assert "_handledBuyerMessageDeduplicator.TryRelease(messageKey);" in method
    assert "await ProcessOrderPlacedReplyAsync(orderPlan).ConfigureAwait(false);" in method
    assert "_buyerMessageBurstCoordinator.Enqueue(new BuyerMessageBurstItem" in method
