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
    assert "AutoDeliveryCoordinator.ReconfigureSeller(targetSeller, enabled, delay);" in page


def test_enable_is_cursor_first_and_disable_is_setting_first():
    page = text("src/Bot/Options/AutoDeliveryOptionsControl.cs")

    enabled_branch = page.index("if (enabled)")
    cursor_true = page.index("AutoDeliveryOrderEventSeed.MarkConfiguration(targetSeller, true);", enabled_branch)
    setting_true = page.index("AutoDeliverySettings.Save(targetSeller, true, delay);", enabled_branch)
    else_branch = page.index("else", setting_true)
    setting_false = page.index("AutoDeliverySettings.Save(targetSeller, false, delay);", else_branch)
    cursor_false = page.index("AutoDeliveryOrderEventSeed.MarkConfiguration(targetSeller, false);", setting_false)
    assert cursor_true < setting_true < else_branch < setting_false < cursor_false


def test_auto_delivery_runtime_is_exact_order_and_pending_state_gated():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")

    assert "AutoDeliveryConfirmationLedger.IsSafeOrderId(snapshot.OrderId)" in runtime
    assert "JsonConvert.SerializeObject((orderId ?? string.Empty).Trim())" in runtime
    assert "if (!before.Pending)" in runtime
    assert "if (!before.ShipButton)" in runtime
    assert "doClick&&pending&&ship" in runtime
    assert "uniqueTargetOrder" in runtime
    assert "keys.length===1&&keys[0]===target" in runtime
    assert "var ship=uniqueAction(best,'发货')" in runtime
    assert "var no=uniqueExact(modal,'无需物流')" in runtime
    assert "ok=uniqueExact(modal,'确认发货')" in runtime
    assert 'ReadModalStateAsync("confirm")' in runtime
    assert "去发货" not in runtime


def test_no_logistics_must_be_selected_before_confirm_permission_is_consumed():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")

    selected = runtime.index('var selected = await ReadModalStateAsync("verify")')
    selected_gate = runtime.index("!selected.Selected", selected)
    barrier = runtime.index("AutoDeliveryCoordinator.TryPersistConfirmationIntent", selected_gate)
    confirm = runtime.index('var confirm = await ReadModalStateAsync("confirm")', barrier)
    assert selected < selected_gate < barrier < confirm
    assert "if(ok&&selectedNo)clicked=clickNode(ok)" in runtime
    assert "selected:selectedNo" in runtime


def test_long_lived_confirmation_ledger_blocks_reenqueue_and_repeat_confirm():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    ledger = text("src/Bot/ChromeNs/AutoDeliveryConfirmationLedger.cs")

    assert '"virtual-goods-auto-delivery-confirmation-ledger.json"' in ledger
    assert "TimeSpan.FromDays(365)" in ledger
    assert "public static bool IsOperational()" in ledger
    assert "if (_failClosed) return true;" in ledger
    assert "if (_failClosed) return false;" in ledger
    assert "if (_state.Records.Any" in ledger
    assert "MigratePendingConfirmationIntentsLocked" in ledger
    assert 'token["ConfirmationIntentAt"]' in ledger

    enqueue = runtime.index("public static void Enqueue")
    has_intent = runtime.index("AutoDeliveryConfirmationLedger.HasIntent", enqueue)
    queue_lookup = runtime.index("var existing = _state.Pending.FirstOrDefault", has_intent)
    assert enqueue < has_intent < queue_lookup

    persist = runtime.index("public static bool TryPersistConfirmationIntent")
    local_consumed = runtime.index("live.ConfirmationIntentAt.HasValue", persist)
    durable_write = runtime.index("AutoDeliveryConfirmationLedger.TryRecordIntent", local_consumed)
    assert persist < local_consumed < durable_write
    assert "AutoDeliveryConfirmationLedger.Remove" not in runtime
    assert "AutoDeliveryConfirmationLedger.HasIntent(seller, orderId)" in runtime[persist:durable_write]


def test_auto_delivery_only_declares_success_on_explicit_done_status():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")

    confirm_index = runtime.index('var confirm = await ReadModalStateAsync("confirm")')
    verify_index = runtime.index("for (var attempt = 0; attempt < 16; attempt++)", confirm_index)
    done_gate = runtime.index("if (after.Found && after.Done)", verify_index)
    completed_index = runtime.index("return AutoDeliveryAttemptResult.Completed", done_gate)
    assert confirm_index < verify_index < done_gate < completed_index
    assert "done=(st==='已发货'||st==='交易成功'||st==='已完成')" in runtime
    assert "ConfirmationUncertainCount >= 5" in runtime
    assert "长期账本保证后续只读复核且绝不重复确认" in runtime


def test_auto_delivery_respects_shared_human_and_bot_activity_guards():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")

    assert "_sendGate.CurrentCount < 1" in runtime
    assert "_incomingMessageGate.CurrentCount < 1" in runtime
    assert "_backgroundRecoveryGate.CurrentCount < 1" in runtime
    assert "BotActivityCoordinator.IsSafeToAutoFocus" in runtime
    assert "TryGetInputboxEmptyAsync" in runtime
    assert "IsKnownBotOwnedDraftAsync" in runtime
    assert "await _sendGate.WaitAsync()" in runtime
    assert 'using (BotActivityCoordinator.Begin("虚拟商品自动发货", seller, expectedBuyer))' in runtime

    # Read-only verification and non-candidates must terminate before foreground focus/navigation.
    method = runtime[runtime.index("internal async Task<AutoDeliveryAttemptResult> TryExecuteVirtualGoodsAutoDeliveryAsync"):
                     runtime.index("private async Task<AutoDeliveryPreflightResult> TrySilentAutoDeliveryPreflightAsync")]
    verification_stop = method.index("if (verificationOnly)")
    focus = method.index("BotActivityCoordinator.IsSafeToAutoFocus", verification_stop)
    open_chat = method.index("OpenChat(expectedBuyer)", focus)
    assert verification_stop < focus < open_chat


def test_delayed_jobs_and_order_event_cursor_are_durable_and_non_retroactive():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    seed = text("src/Bot/ChromeNs/AutoDeliveryOrderEventSeed.cs")
    bootstrap = text("src/Bot/AutoDeliveryAppBootstrap.cs")

    assert '"virtual-goods-auto-delivery-queue.json"' in runtime
    assert "ExpiresAt = now.Add(MaxPendingAge)" in runtime
    assert '"order-event-state.json"' in seed
    assert '"virtual-goods-auto-delivery-event-cursor.json"' in seed
    assert "cursor.LastSeenAt = DateTime.Now;" in seed
    assert "OrderEventType.Created" in seed and "OrderEventType.Paid" in seed
    assert "AutoDeliveryCoordinator.Enqueue(entry.Snapshot);" in seed
    assert "AutoDeliveryConfirmationLedger.IsOperational()" in seed
    assert "暂停消费新订单事件且不推进游标" in seed
    assert "File.Replace(temp, path, null, true)" in seed
    assert 'Guid.NewGuid().ToString("N") + ".tmp"' in seed
    assert "AutoDeliveryCoordinator.Initialize();" in bootstrap
    assert "AutoDeliveryOrderEventSeed.Initialize();" in bootstrap


def test_auto_delivery_sources_are_included_in_normal_and_wpf_temp_builds():
    props = text("src/Bot/Directory.Build.props")

    for path in (
        "Options\\AutoDeliveryOptionsControl.cs",
        "ChromeNs\\VirtualGoodsAutoDelivery.cs",
        "ChromeNs\\AutoDeliveryOrderEventSeed.cs",
        "ChromeNs\\AutoDeliveryConfirmationLedger.cs",
        "AutoDeliveryAppBootstrap.cs",
    ):
        assert path in props
