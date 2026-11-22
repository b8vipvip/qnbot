from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def between(text: str, start: str, end: str) -> str:
    i = text.index(start)
    j = text.index(end, i)
    return text[i:j]


def test_reliable_send_uses_stable_automation_ids_and_exact_draft_check():
    source = read("src/Bot/ChromeNs/QNRpa.ReliableSend.cs")
    qnrpa = read("src/Bot/ChromeNs/QNRpa.cs")
    assert "chatInputArea.plainTextEdit" in source
    assert "enterAreaKeyWidget.sendMsg" in source
    assert "RefreshChatControlsAsync" in source
    assert "EditorMatchesExpectedText" in source
    assert "TryGetEditorText" in source
    assert "LastSendFailureReason" in source
    assert "await RefreshChatControlsAsync(true)" in qnrpa
    assert "SetSendFailure" in qnrpa


def test_background_notification_only_recovers_when_detailed_event_is_missing():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")
    assert "BackgroundRecoveryInitialDelayMs = 1000" in source
    assert "Task.Delay(BackgroundRecoveryInitialDelayMs)" in source
    assert "_latestBuyerMessageObserved" in source
    assert "im.singlemsg.GetRemoteHisMsg" in source
    assert "_sendGate.WaitAsync(BackgroundRecoverySendGateWaitMs)" in source
    assert "_backgroundRecoveryGate.WaitAsync(BackgroundRecoveryGateWaitMs)" in source
    assert "OpenChat(buyer);" in source
    assert "await ProcessIncomingMessageAsync(message).ConfigureAwait(false)" in source
    assert "await _sendGate.WaitAsync();" not in source
    assert "await _backgroundRecoveryGate.WaitAsync();" not in source
    assert "ScheduleBackgroundMessageRecovery(e)" in qn
    assert "MarkBuyerMessageObserved(sellerNick, buyerNick)" in qn


def test_generic_message_center_notification_uses_buyer_correlation_not_current_chat_guess():
    source = read("src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs")
    handler = between(source, "private static async void OnMessageNotify", "private static JToken ParseExpanded")

    assert "OrderAutomationCoordinator.ObserveGenericPaymentSignal" in handler
    assert "ProcessDirectOrderMessageAsync" in handler
    assert "ScheduleVerifiedCurrentBuyerPanelRecovery" not in source
    assert "PanelProbes" not in source

    coordinator = between(source, "internal static class OrderAutomationCoordinator", "internal static class OrderPaymentNotificationFallback")
    assert "GenericWakes" in coordinator
    assert "RecentBuyerSignals" in coordinator
    assert "BackwardBuyerCorrelation" in coordinator
    assert "EvShopRobotReceriveNewMessage" in coordinator
    assert "EvBuyerSwitched" in coordinator
    assert "ScheduleCorrelatedProbe" in coordinator
    assert "BotActivityCoordinator.IsSafeToAutoFocus" in coordinator
    assert "TryRecoverVisibleOrderPanelForCoordinatorAsync" in coordinator
    assert "CorrelatedProbeDelaysMs" in coordinator
    assert "180000" in coordinator


def test_generic_payment_wake_never_binds_directly_to_current_conversation():
    source = read("src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs")
    observe = between(source, "internal static void ObserveGenericPaymentSignal", "private static void OnShopRobotNewMessage")
    assert "GetCurrentConversationID" not in observe
    assert "OpenChat" not in observe
    assert "GenericWakes.AddOrUpdate" in observe
    assert "RecentBuyerSignals" in observe


def test_correlated_panel_probe_has_strict_event_time_floor_and_single_order_hub_sink():
    source = read("src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs")
    probe = between(source, "internal async Task<bool> TryRecoverVisibleOrderPanelForCoordinatorAsync", "    }\n}")
    assert "probeStartedAt" in probe
    assert "AddSeconds(-20)" in probe
    assert "eventTime.Value < freshFloor" in probe
    assert "VisiblePanelUnsupportedStatuses" in probe
    assert "OrderEventHub.Publish(snapshot)" in probe
    assert "ProcessOrderPlacedReplyAsync" not in probe
    assert "SendTextWithRetryAsync" not in probe


def test_parseable_message_center_order_still_uses_structured_order_pipeline():
    source = read("src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs")
    handler = between(source, "private static async void OnMessageNotify", "private static JToken ParseExpanded")

    assert "ResolveOrderId(flat, combined)" in handler
    assert "ResolveBuyer(flat, seller, orderId)" in handler
    assert "ProcessDirectOrderMessageAsync" in handler
    assert "messageCenterNotify统一兼容解析" in handler
    assert "有订单号但缺少买家身份" in handler


def test_dead_uncompiled_order_recovery_file_is_removed():
    assert not (ROOT / "src/Bot/ChromeNs/FirstInquiryDeliveryBridge.cs").exists()
    targets = read("src/Directory.Build.targets")
    assert "FirstInquiryDeliveryBridge.cs" not in targets


def test_settings_have_separate_notification_tab_and_order_section():
    source = read("src/Bot/Options/CtlRobotOptions.xaml.cs")
    assert 'Header = "消息通知"' in source
    rules = between(source, "private UIElement BuildRulesTab()", "private UIElement BuildNotificationTab()")
    notifications = between(source, "private UIElement BuildNotificationTab()", "private TextBlock SectionTitle")
    assert "买家下单后自动发送" in rules
    assert "人工客服工作时间与下班回复" not in rules
    assert "转人工通知" not in rules
    assert "人工客服工作时间与下班回复" in notifications
    assert "转人工通知" in notifications
    assert "调用HTTP接口" in rules
    assert "OrderPlacedReplyText" in source
    assert "OrderPlacedApiUrl" in source
    assert "OrderPlacedApiToken" in source


def test_order_reply_detects_order_card_and_supports_http_contract():
    source = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")
    assert "buyer_order_created" in source
    assert 'payload["orderId"]' not in source  # object initializer is used, not mutable shared payload
    assert '["orderId"] = plan.OrderId' in source
    assert 'token["reply"]' in source
    assert 'token["answer"]' in source
    assert 'token["message"]' in source
    assert "{订单号}" in source
    assert "OrderPlacedAutoReplyService.TryCreatePlan" in qn
    order_index = qn.index("OrderPlacedAutoReplyService.TryCreatePlan")
    safety_index = qn.index("IncomingMessageSafety.Evaluate", order_index)
    assert order_index < safety_index


def test_new_partial_files_are_in_windows_build():
    targets = read("src/Directory.Build.targets")
    assert "QNRpa.ReliableSend.cs" in targets
    assert "QN.MessageRecovery.cs" in targets
    assert "OrderPlacedAutoReplyService.cs" in targets
    assert "OrderPaymentNotificationFallback.cs" in targets

def test_order_signal_cleanup_uses_compare_and_remove_for_replaced_entries():
    source = read("src/Bot/ChromeNs/OrderPaymentNotificationFallback.cs")
    coordinator = between(
        source, "internal static class OrderAutomationCoordinator",
        "internal static class OrderPaymentNotificationFallback"
    )
    cleanup = between(coordinator, "private static void Cleanup()", "private static string Normalize")
    probe = between(coordinator, "private static void ScheduleCorrelatedProbe", "private static bool TryActivateTargetIfSafeAsync")

    assert "ICollection<KeyValuePair<string, GenericWake>>" in cleanup
    assert "exactWakes.Remove(pair);" in cleanup
    assert "ICollection<KeyValuePair<string, BuyerSignal>>" in cleanup
    assert "exactSignals.Remove(pair);" in cleanup
    assert "GenericWakes.TryRemove(pair.Key" not in cleanup
    assert "RecentBuyerSignals.TryRemove(pair.Key" not in cleanup
    assert "ICollection<KeyValuePair<string, ProbeState>>" in probe
    assert ".Remove(new KeyValuePair<string, ProbeState>(key, state))" in probe
    assert "Probes.TryRemove(key" not in probe


def test_direct_order_raw_reservation_cleanup_does_not_remove_a_renewed_key():
    source = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    cleanup = between(source, "private static void CleanupReservations()", "    }\n\n    internal static class DirectOrderIdentityResolver")
    assert "ICollection<KeyValuePair<string, DateTime>>" in cleanup
    assert "exactReservations.Remove(pair);" in cleanup
    assert "RawReservations.TryRemove(pair.Key" not in cleanup
