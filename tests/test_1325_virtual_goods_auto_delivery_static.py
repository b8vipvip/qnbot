from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_unified_settings_exposes_dedicated_auto_delivery_page():
    wnd = text("src/Bot/Options/WndOption.xaml.cs")
    options = text("src/Bot/Options/IOptions.cs")
    page = text("src/Bot/Options/AutoDeliveryOptionsControl.cs")

    assert "AutoDelivery" in options
    assert 'AddPage("订单自动化", "自动发货"' in wnd
    assert "new AutoDeliveryOptionsControl(Seller)" in wnd
    assert 'Content = "启用自动发货（无需物流）"' in page
    assert "DefaultDelayMinutes = 10" in page
    assert "MinDelayMinutes = 0" in page
    assert "MaxDelayMinutes = 1440" in page
    assert 'EnabledKey = "auto_delivery.enabled"' in page
    assert 'DelayKey = "auto_delivery.delay_minutes"' in page
    assert "AutoDeliveryOrderEventSeed.MarkConfiguration(targetSeller, enabled);" in page
    assert "AutoDeliveryCoordinator.ReconfigureSeller(targetSeller, enabled, delay);" in page


def test_auto_delivery_runtime_is_exact_order_and_pending_state_gated():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")

    assert "JsonConvert.SerializeObject((orderId ?? string.Empty).Trim())" in runtime
    assert "if (!before.Pending)" in runtime
    assert "if (!before.ShipButton)" in runtime
    assert "doClick&&pending&&ship" in runtime
    assert "var ship=exact(best,'发货')" in runtime
    assert "var no=exact(modal,'无需物流')" in runtime
    assert "ok=exact(modal,'确认发货')" in runtime
    assert 'ReadModalStateAsync("confirm")' in runtime
    assert "去发货" not in runtime


def test_auto_delivery_never_declares_success_without_post_action_state_evidence():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")

    confirm_index = runtime.index('var confirm = await ReadModalStateAsync("confirm")')
    verify_index = runtime.index("for (var attempt = 0; attempt < 16; attempt++)", confirm_index)
    completed_index = runtime.index("return AutoDeliveryAttemptResult.Completed", verify_index)
    assert confirm_index < verify_index < completed_index
    assert "ConfirmationUncertain" in runtime
    assert "ConfirmationUncertainCount >= 5" in runtime
    assert "已点击确认发货，但 8 秒内尚未从订单面板读到确定的新状态" in runtime


def test_auto_delivery_respects_shared_human_and_bot_activity_guards():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")

    assert "_sendGate.CurrentCount < 1" in runtime
    assert "_incomingMessageGate.CurrentCount < 1" in runtime
    assert "_backgroundRecoveryGate.CurrentCount < 1" in runtime
    assert "BotActivityCoordinator.IsSafeToAutoFocus" in runtime
    assert "TryGetInputboxEmptyAsync" in runtime
    assert "IsKnownBotOwnedDraftAsync" in runtime
    assert "await _sendGate.WaitAsync()" in runtime
    assert 'BotActivityCoordinator.Begin("虚拟商品自动发货"' in runtime


def test_delayed_jobs_and_order_event_cursor_are_durable_and_non_retroactive():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    seed = text("src/Bot/ChromeNs/AutoDeliveryOrderEventSeed.cs")
    bootstrap = text("src/Bot/AutoDeliveryAppBootstrap.cs")

    assert '"virtual-goods-auto-delivery-queue.json"' in runtime
    assert "ExpiresAt = now.Add(MaxPendingAge)" in runtime
    assert '"order-event-state.json"' in seed
    assert '"virtual-goods-auto-delivery-event-cursor.json"' in seed
    assert "cursor.LastSeenAt = DateTime.Now;" in seed
    assert "OrderEventType.Created || x.Snapshot.EventType == OrderEventType.Paid" in seed
    assert "AutoDeliveryCoordinator.Enqueue(entry.Snapshot);" in seed
    assert "AutoDeliveryCoordinator.Initialize();" in bootstrap
    assert "AutoDeliveryOrderEventSeed.Initialize();" in bootstrap


def test_auto_delivery_sources_are_included_in_normal_and_wpf_temp_builds():
    props = text("src/Bot/Directory.Build.props")

    for path in (
        "Options\\AutoDeliveryOptionsControl.cs",
        "ChromeNs\\VirtualGoodsAutoDelivery.cs",
        "ChromeNs\\AutoDeliveryOrderEventSeed.cs",
        "AutoDeliveryAppBootstrap.cs",
    ):
        assert path in props
