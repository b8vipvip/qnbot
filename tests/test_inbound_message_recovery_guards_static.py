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
    assert "var processingTask = ProcessIncomingMessageAsync(message);" in loop
    assert "processingTask != null && !processingTask.IsCompleted" in loop
    assert "_ = ObserveInboundProcessingAsync(processingTask);" in loop
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

    handoff = order[order.index("if (orderPlan.IsBuyerFollowUp)"): ]
    assert "accepted = true;" in order
    assert handoff.index("ResponseProgressTracker.ObserveNewBuyerTurn") < handoff.index("accepted = true;")
    assert handoff.index("accepted = true;") < handoff.index("return ProcessOrderPlacedReplyAsync(orderPlan);")
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


def test_remote_history_replay_does_not_hold_liveness_gate_for_async_order_reply():
    source = read("src/Bot/ChromeNs/BusinessInboundLivenessRecoveryBridge.cs")
    method = source.split("internal async Task ProbeActiveConversationForMissedInboundAsync()", 1)[1]

    assert "processingTask != null && !processingTask.IsCompleted" in method
    assert "_ = ObserveInboundProcessingAsync(processingTask);" in method
    assert "await processingTask.ConfigureAwait(false);" in method


def test_duplicate_buyer_transport_event_does_not_advance_session_watermark():
    source = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    method = source[
        source.index("public BuyerSessionAgentObservation ObserveBuyerMessage"):
        source.index("public BuyerSessionEventResult RecordEvent")
    ]

    duplicate = method.index("state.RecentMessageKeySet.Contains(messageKey)")
    watermark = method.index("state.LastObservedAt = observedAt")
    sort_watermark = method.index("state.LastBuyerEventSortValue = Math.Max")
    remember = method.index("RememberMessageKeyLocked(state, messageKey)")

    assert duplicate < watermark < sort_watermark < remember
    assert "A replayed copy is not a new buyer turn" in method


def test_background_recovery_bot_switches_are_read_under_the_exact_shop_scope():
    liveness = read("src/Bot/ChromeNs/BusinessInboundLivenessRecoveryBridge.cs")
    probe = liveness.split("internal async Task ProbeActiveConversationForMissedInboundAsync()", 1)[1]
    liveness_scope = probe.index("using (ShopSettingsScope.Enter(shop, false))")
    liveness_switch = probe.index("if (!Params.Robot.CanUseRobotReal) return;")
    assert liveness_scope < liveness_switch

    first = read("src/Bot/ChromeNs/FirstInquiryStreamingGuard.cs")
    schedule_first = first[
        first.index("internal void ScheduleFirstInquiryFastRecovery"):
        first.index("private bool IsFirstInquirySourceSessionCurrent")
    ]
    first_scope = schedule_first.index("using (ShopSettingsScope.Enter(shop, false))")
    first_switch = schedule_first.index("botEnabledForShop = Params.Robot.CanUseRobotReal;")
    assert first_scope < first_switch
    assert "if (!botEnabledForShop) return;" in schedule_first

    recovery = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    schedule_recovery = recovery[
        recovery.index("private void ScheduleBackgroundMessageRecovery"):
        recovery.index("private async Task<bool> RecoverMissedBuyerMessagesAsync")
    ]
    recovery_scope = schedule_recovery.index("using (ShopSettingsScope.Enter(shop, false))")
    recovery_switch = schedule_recovery.index("botEnabledForShop = Params.Robot.CanUseRobotReal;")
    assert recovery_scope < recovery_switch
    assert "if (!botEnabledForShop) return;" in schedule_recovery


def test_order_plan_master_switch_is_resolved_from_the_plan_shop_not_global_fallback():
    source = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    method = source[
        source.index("public static bool TryCreatePlan"):
        source.index("private static bool TryCreateBuyerFollowUpPlan")
    ]

    resolve_shop = method.index("ShopContextLocator.ResolveRuntimeByShopKey(shopKey)")
    scope = method.index("using (ShopSettingsScope.Enter(shop, false))")
    master_switch = method.index("botEnabledForShop = Params.Robot.CanUseRobotReal;")
    assert resolve_shop < scope < master_switch
    assert "if (!botEnabledForShop) return false;" in method


def test_delivery_watchdog_reads_send_switches_under_resolved_shop_scope():
    source = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    method = source[
        source.index("public static void ExpectDelivery("):
        source.index("public static string EnsurePending")
    ]

    resolve = method.index("var shop = ResolveShop(seller, shopKey);")
    scope = method.index("using (ShopSettingsScope.Enter(shop, false))")
    bot = method.index("botEnabledForShop = Params.Robot.CanUseRobotReal;")
    auto = method.index("autoReplyEnabledForShop = Params.Robot.GetIsAutoReply();")
    assert resolve < scope < bot < auto
    assert "if (!botEnabledForShop || (!force && !autoReplyEnabledForShop)) return;" in method


def test_configured_order_template_reads_bot_switch_inside_runtime_shop_scope():
    source = read("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")
    method = source[
        source.index("AutoReplyRuleConfig cfg;"):
        source.index("if (!ShouldOwnConfiguredTemplate")
    ]

    scope = method.index("using (ShopSettingsScope.Enter(runtimeShop, false))")
    config = method.index("cfg = BotFeatureStore.GetAutoReplyRules();")
    bot = method.index("botEnabledForShop = Params.Robot.CanUseRobotReal;")
    assert scope < config < bot
