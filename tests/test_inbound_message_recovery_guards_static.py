from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_failed_ingress_claim_can_be_released_without_stale_fifo_entry():
    source = read("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    dedupe = source[source.index("internal sealed class IncomingMessageDeduplicator"):]
    release = dedupe[dedupe.index("public bool TryRelease(string key)"):]

    assert "if (!_seen.Remove(key)) return false;" in release
    assert "Where(existing => !string.Equals(existing, key, StringComparison.Ordinal))" in release
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
    business = method[method.index("if (!_handledBuyerMessageDeduplicator.TryAccept(messageKey))"):]
    assert "try" in business
    assert "_handledBuyerMessageDeduplicator.TryRelease(messageKey);" in business
    assert "throw;" in business
    assert business.index("_handledBuyerMessageDeduplicator.TryRelease(messageKey);") < business.index("throw;")


def test_recovered_buyer_replay_uses_exact_shop_scope_and_refreshes_timeline():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    method = source[
        source.index("private Task ProcessRecoveredBuyerMessageAfterMissAsync"):
        source.index("private static bool IsPotentialRecoveredOrderCard")
    ]

    assert "ShopContextLocator.ResolveRuntimeBySeller(this)" in method
    assert "using (ShopSettingsScope.Enter(shop))" in method
    assert "ConversationContextStore.RefreshAndRecord(message, messageText, shopKey);" in method
    assert method.index("ConversationContextStore.RefreshAndRecord") < method.index("OrderPlacedAutoReplyService.TryCreatePlan")
    assert "var accepted = false;" in method
    assert "if (!accepted)" in method
    assert "_handledBuyerMessageDeduplicator.TryRelease(messageKey);" in method
    assert "return ProcessOrderPlacedReplyAsync(orderPlan);" in method
    assert "_buyerMessageBurstCoordinator.Enqueue(new BuyerMessageBurstItem" in method


def test_failed_seller_echo_recording_releases_transport_claim_for_history_replay():
    source = read("src/Bot/ChromeNs/QN.cs")
    method = source[
        source.index("private Task ProcessIncomingMessageAsync"):
        source.index("private async Task ProcessBuyerBurstAsync")
    ]
    seller = method[
        method.index("if (IsSellerMessage(message))"):
        method.index("if (!IsBuyerMessage(message))")
    ]

    assert "_incomingMessageDeduplicator.TryAccept(messageKey)" in seller
    assert "ConversationContextStore.RefreshAndRecord(message, messageText);" in seller
    assert "RecordSellerEcho(message.toid.nick, messageText);" in seller
    assert "_incomingMessageDeduplicator.TryRelease(messageKey);" in seller
    assert "catch (Exception ex)" in seller


def test_recovered_order_handoff_keeps_claim_after_durable_order_ledger_owns_it():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    method = source[
        source.index("private Task ProcessRecoveredBuyerMessageAfterMissAsync"):
        source.index("private static bool IsPotentialRecoveredOrderCard")
    ]
    order = method[
        method.index("if (OrderPlacedAutoReplyService.TryCreatePlan("):
        method.index("var decision = IncomingMessageSafety.Evaluate")
    ]

    assert "accepted = true;" in order
    assert order.index("ResponseProgressTracker.ObserveNewBuyerTurn") < order.index("accepted = true;")
    assert order.index("accepted = true;") < order.index("return ProcessOrderPlacedReplyAsync(orderPlan);")
    assert "if (!accepted)" in method


def test_long_order_reply_task_does_not_hold_shared_inbound_receive_gate():
    source = read("src/Bot/ChromeNs/QN.cs")
    receive = source[
        source.index("private async void Cdp_EvRecieveNewMessage"):
        source.index("private async Task ObserveInboundProcessingAsync")
    ]
    observer = source[
        source.index("private async Task ObserveInboundProcessingAsync"):
        source.index("private void Cdp_EvMessageNotity")
    ]

    assert "var processingTask = ProcessIncomingMessageAsync(message);" in receive
    assert "processingTask != null && !processingTask.IsCompleted" in receive
    assert "_ = ObserveInboundProcessingAsync(processingTask);" in receive
    assert "await processingTask;" in receive
    assert "await processingTask.ConfigureAwait(false);" in observer
    assert "catch (Exception ex)" in observer
