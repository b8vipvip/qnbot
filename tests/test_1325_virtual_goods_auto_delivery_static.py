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
    assert 'ReadModalStateAsync(sourceClient, "confirm")' in runtime
    assert "去发货" not in runtime


def test_no_logistics_must_be_selected_before_confirm_permission_is_consumed():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")

    selected = runtime.index('var selected = await ReadModalStateAsync(sourceClient, "verify")')
    selected_gate = runtime.index("!selected.Selected", selected)
    barrier = runtime.index("AutoDeliveryCoordinator.TryPersistConfirmationIntent", selected_gate)
    confirm = runtime.index('var confirm = await ReadModalStateAsync(sourceClient, "confirm")', barrier)
    assert selected < selected_gate < barrier < confirm
    assert "if(ok&&selectedNo)clicked=clickNode(ok)" in runtime
    assert "selected:selectedNo" in runtime


def test_long_lived_confirmation_ledger_blocks_reenqueue_and_repeat_confirm():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    ledger = text("src/Bot/ChromeNs/AutoDeliveryConfirmationLedger.cs")

    assert '"virtual-goods-auto-delivery-confirmation-ledger.json"' in ledger
    assert "TimeSpan.FromDays(365)" in ledger
    assert "public static bool IsOperational()" in ledger
    assert "if (_failClosed) return false;" in ledger
    assert "if (!TryReadIntent(shopKey, seller, orderId, out hasIntent, out atCapacity)) return true;" in ledger
    assert "if (_state.Records.Any" in ledger
    assert "MigratePendingConfirmationIntentsLocked" in ledger
    assert 'token["ConfirmationIntentAt"]' in ledger

    enqueue = runtime.index("public static bool Enqueue")
    has_intent = runtime.index("AutoDeliveryConfirmationLedger.HasIntent", enqueue)
    queue_lookup = runtime.index("var existing = _state.Pending.FirstOrDefault", has_intent)
    assert enqueue < has_intent < queue_lookup

    persist = runtime.index("public static bool TryPersistConfirmationIntent")
    local_consumed = runtime.index("live.ConfirmationIntentAt.HasValue", persist)
    durable_write = runtime.index("AutoDeliveryConfirmationLedger.TryRecordIntent", local_consumed)
    assert persist < local_consumed < durable_write
    assert "AutoDeliveryConfirmationLedger.Remove" not in runtime
    assert "AutoDeliveryConfirmationLedger.TryGetIntent(shopKey, seller, orderId, out existingIntent)" in runtime[persist:durable_write]
    assert "if (!AutoDeliveryConfirmationLedger.IsOperational())" in runtime[persist:durable_write]
    assert "if (AutoDeliveryConfirmationLedger.TryGetIntent(shopKey, seller, orderId, out racedIntent)" in runtime[persist:]


def test_auto_delivery_only_declares_success_on_explicit_done_status():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")

    confirm_index = runtime.index('var confirm = await ReadModalStateAsync(sourceClient, "confirm")')
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
    assert 'using (BotActivityCoordinator.Begin("虚拟商品自动发货", seller, expectedBuyer, snapshot.ShopKey))' in runtime

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
    assert "if (!AutoDeliveryCoordinator.Enqueue(entry.Snapshot)) return;" in seed
    assert "AutoDeliveryConfirmationLedger.IsOperational()" in seed
    assert "暂停消费新订单事件且不推进游标" in seed
    assert "CrossProcessAtomicStateFile.WriteAllTextAtomic" in seed
    atomic = text("src/Bot/ChromeNs/CrossProcessAtomicStateFile.cs")
    assert "FileOptions.WriteThrough" in atomic
    assert "stream.Flush(true)" in atomic
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


def test_auto_delivery_queue_read_failure_fails_closed_without_overwriting_pending_tasks():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    initialize = runtime[runtime.index("public static void Initialize"):runtime.index("public static bool IsOperational")]
    loader = runtime[runtime.index("private static bool ReloadStateForMutationLocked"):runtime.index("private static bool SaveStateLocked")]

    assert "private static volatile bool _stateLoadFailed;" in runtime
    assert "ReloadStateForMutationLocked(true)" in initialize
    assert "if (!ReloadStateForMutationLocked(true))" in initialize
    assert "if (!SaveStateLocked())" in initialize
    failed_branch = initialize[initialize.index("if (!ReloadStateForMutationLocked(true))"):]
    assert "EnsureRecoveryTimerLocked()" in failed_branch
    assert failed_branch.index("EnsureRecoveryTimerLocked()") < failed_branch.index("return;")
    assert "private static void EnsureRecoveryTimerLocked()" in runtime
    assert "new Timer(_ => OnTimerTick()" in runtime
    assert "保留原状态文件并暂停自动发货" in initialize
    assert "LoadState(out loadedSuccessfully)" in loader
    assert "private static StateDocument LoadState(out bool loadedSuccessfully)" in loader
    assert "(loaded.Schema != 1 && loaded.Schema != 2) || loaded.Pending == null" in loader
    assert "loadedSuccessfully = false;" in loader
    assert "if (_stateLoadFailed) return;" in runtime
    assert "if (_stateLoadFailed) return false;" in runtime


def test_auto_delivery_initializers_publish_ready_state_only_after_locked_first_load():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    runtime_init = runtime[runtime.index("public static void Initialize"):runtime.index("public static bool Enqueue")]
    seed = text("src/Bot/ChromeNs/AutoDeliveryOrderEventSeed.cs")
    seed_init = seed[seed.index("public static void Initialize"):seed.index("public static void MarkConfiguration")]

    for initializer in (runtime_init, seed_init):
        assert "lock (Sync)" in initializer
        assert "if (_initialized != 0) return;" in initializer
        assert "Interlocked.Exchange(ref _initialized" not in initializer
        assert initializer.index("if (_initialized != 0) return;") < initializer.index("_initialized = 1;")
    assert runtime_init.index("ReloadStateForMutationLocked(true)") < runtime_init.index("_initialized = 1;")
    assert "EnsureRecoveryTimerLocked()" in runtime_init
    assert seed_init.index("_cursors = LoadCursors(out loadedSuccessfully);") < seed_init.index("_initialized = 1;")
    assert "EnsureRecoveryTimerLocked()" in seed_init
    assert "private static void EnsureRecoveryTimerLocked()" in seed
    assert "private static void EnsureRecoveryTimerLocked()" in runtime


def test_event_cursor_read_or_write_failure_pauses_without_advancing_or_overwriting():
    seed = text("src/Bot/ChromeNs/AutoDeliveryOrderEventSeed.cs")
    initialize = seed[seed.index("public static void Initialize"):seed.index("public static void MarkConfiguration")]
    loader = seed[seed.index("private static CursorState LoadCursors"):seed.index("private static bool SaveCursorsLocked")]
    save = seed[seed.index("private static bool SaveCursorsLocked"):seed.index("private static string DataRoot")]
    tick = seed[seed.index("private static void Tick"):seed.index("private static SellerCursor GetCursorLocked")]

    assert "LoadCursors(out loadedSuccessfully)" in initialize
    assert "if (!loadedSuccessfully)" in initialize
    failed_branch = initialize[initialize.index("if (!loadedSuccessfully)"):]
    assert "EnsureRecoveryTimerLocked()" in failed_branch
    assert failed_branch.index("EnsureRecoveryTimerLocked()") < failed_branch.index("return;")
    assert "private static void OnTimerTick()" in seed
    assert "TryRecoverCursorState()" in seed
    assert "private static volatile bool _cursorStateUnavailable;" in seed
    assert "state == null || (state.Schema != 1 && state.Schema != 2) || state.Sellers == null" in loader
    assert "loadedSuccessfully = false;" in loader
    assert "return new CursorState();" in loader
    assert "if (_cursorStateUnavailable) return;" in tick
    assert "if (!SaveCursorsLocked()) return;" in tick
    assert "if (!AutoDeliveryCoordinator.Enqueue(entry.Snapshot)) return;" in tick
    assert "暂停事件消费" in loader
    assert "_cursorStateUnavailable = true;" in save


def test_event_cursor_does_not_advance_when_queue_persistence_is_unavailable():
    coordinator = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    operational = coordinator[coordinator.index("public static bool IsOperational"):coordinator.index("public static bool Enqueue")]
    enqueue = coordinator[coordinator.index("public static bool Enqueue"):coordinator.index("public static void ReconfigureSeller")]
    seed = text("src/Bot/ChromeNs/AutoDeliveryOrderEventSeed.cs")
    tick = seed[seed.index("private static void Tick"):seed.index("private static SellerCursor GetCursorLocked")]

    assert "public static bool IsOperational()" in operational
    assert "return queueReady && AutoDeliveryConfirmationLedger.IsOperational();" in operational
    assert "public static bool Enqueue(OrderSnapshot snapshot)" in enqueue
    assert "if (!SaveStateLocked()) return false;" in enqueue
    assert "if (cfg == null) return false;" in enqueue
    assert "if (!cfg.Enabled) return true;" in enqueue
    assert "return AutoDeliveryConfirmationLedger.IsOperational();" in enqueue
    assert "|| !AutoDeliveryCoordinator.IsOperational()" in tick
    assert "if (!AutoDeliveryCoordinator.Enqueue(entry.Snapshot)) return;" in tick
    assert tick.index("AutoDeliveryCoordinator.Enqueue(entry.Snapshot)") < tick.index("cursor.LastSeenAt = latest;")


def test_auto_delivery_settings_never_fall_back_to_global_on_ambiguous_shop_identity():
    page = text("src/Bot/Options/AutoDeliveryOptionsControl.cs")
    load = page[page.index("public static AutoDeliveryConfig Load"):
                page.index("public static void Save")]
    resolver = page[page.index("private static ShopScopedSettingsStore TryCreateShopStore"):
                    page.index("private static bool ParseBool")]

    assert "ShopContextLocator.ResolveCurrentOrBySellerNick(seller)" in resolver
    assert "ShopContextLocator.ResolveBySellerNick(seller)" not in resolver
    assert "catch" not in resolver
    assert "var store = TryCreateShopStore(seller);" in load
    assert "catch" not in load
    assert "只有一个明确为空的 seller" not in load
    assert "Only an explicitly empty seller may use the pre-ShopKey global" in load
    assert "已阻止回退到全局设置" in resolver



def test_auto_delivery_queue_cursor_and_confirmation_ledger_are_shopkey_isolated():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    seed = text("src/Bot/ChromeNs/AutoDeliveryOrderEventSeed.cs")
    ledger = text("src/Bot/ChromeNs/AutoDeliveryConfirmationLedger.cs")

    assert "snapshot.ShopKey = ShopContextLocator.ResolveRuntimeBySellerNick(snapshot.Seller).ShopKey" in runtime
    assert "BuildKey(snapshot.ShopKey, snapshot.Seller, snapshot.OrderId)" in runtime
    assert "ShopContextLocator.FindConnectedQnByShopKey(snapshot.ShopKey)" in runtime
    assert "runtimeShop.ShopKey, snapshot.ShopKey" in runtime
    assert "loaded.Schema = 2;" in runtime
    assert "ResolveRuntimeBySellerNick(snapshot.Seller)" in runtime and "旧版任务等待唯一ShopKey恢复" in runtime

    assert "GetCursorLocked(string shopKey, string seller)" in seed
    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in seed
    assert "x.Snapshot.ShopKey" in seed
    assert "state.Schema = 2;" in seed

    assert "HasIntent(string shopKey, string seller, string orderId)" in ledger
    assert "TryRecordIntent(string shopKey, string seller, string orderId" in ledger
    assert "MarkResolved(string shopKey, string seller, string orderId" in ledger
    assert "BuildLegacyKey(seller, orderId)" in ledger
    assert "loaded.Schema == 1" in ledger



def test_disabling_auto_delivery_preserves_consumed_confirmation_for_read_only_recovery():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    reconfigure = runtime[
        runtime.index("public static void ReconfigureSeller"):
        runtime.index("public static bool TryPersistConfirmationIntent")
    ]
    process = runtime[
        runtime.index("private static async Task ProcessRecordAsync"):
        runtime.index("private static bool IsSilentPreflightReason")
    ]
    execute = runtime[
        runtime.index("internal async Task<AutoDeliveryAttemptResult> TryExecuteVirtualGoodsAutoDeliveryAsync"):
        runtime.index("private async Task<AutoDeliveryPreflightResult> TrySilentAutoDeliveryPreflightAsync")
    ]
    cleanup = runtime[
        runtime.index("private static void CleanupExpiredLocked"):
        runtime.index("private static DateTime ResolveAnchor")
    ]

    assert "if (HasConfirmationIntent(record))" in reconfigure
    assert "preservedForVerification++;" in reconfigure
    assert "if (enabled || preservedForVerification > 0) ScheduleWorker();" in reconfigure
    assert "if (!verificationOnly && (cfg == null || !cfg.Enabled))" in process
    assert "if (AutoDeliveryConfirmationLedger.HasIntent(snapshot.ShopKey, seller, snapshot.OrderId))" in execute
    assert "if (!verificationOnly && !AutoDeliverySettings.Load(seller).Enabled)" in execute
    verification_stop = execute.index("if (verificationOnly)")
    focus = execute.index("BotActivityCoordinator.IsSafeToAutoFocus", verification_stop)
    assert verification_stop < focus
    assert '"manual_review_required"' in cleanup
    assert "只读复核任务超过24小时，已转人工复核" in cleanup



def test_confirmation_ledger_capacity_never_evicts_existing_at_most_once_barriers():
    ledger = text("src/Bot/ChromeNs/AutoDeliveryConfirmationLedger.cs")
    operational = ledger[
        ledger.index("public static bool IsOperational"):
        ledger.index("public static bool IsSafeOrderId")
    ]
    has_intent = ledger[
        ledger.index("public static bool HasIntent"):
        ledger.index("public static bool TryRecordIntent")
    ]
    record_intent = ledger[
        ledger.index("public static bool TryRecordIntent"):
        ledger.index("public static bool MarkResolved")
    ]

    # Capacity is a fail-closed admission limit, never a license to discard an
    # older irreversible-confirmation barrier.
    assert "return !_failClosed && !IsAtCapacityLocked();" in operational
    assert "if (!TryReadIntent(shopKey, seller, orderId, out hasIntent, out atCapacity)) return true;" in has_intent
    assert "return hasIntent || atCapacity;" in has_intent
    read_intent = ledger[
        ledger.index("private static bool TryReadIntent"):
        ledger.index("public static bool TryRecordIntent")
    ]
    assert "hasIntent = _state.Records.Any" in read_intent
    assert "atCapacity = IsAtCapacityLocked();" in read_intent
    assert "if (IsAtCapacityLocked())" in record_intent
    assert "拒绝写入新确认意图" in record_intent
    assert "private static bool IsAtCapacityLocked()" in ledger
    assert "TrimLocked" not in ledger
    assert ".Take(MaxRecords)" not in ledger

    migration = ledger[
        ledger.index("private static bool MigratePendingConfirmationIntentsLocked"):
        ledger.index("private static LedgerState LoadState")
    ]
    assert "KeepUntil = DateTime.Now.Add(Retention)" in migration
    assert "TrimLocked" not in migration



def test_confirmation_ledger_serializes_cross_process_barrier_transactions():
    ledger = text("src/Bot/ChromeNs/AutoDeliveryConfirmationLedger.cs")

    for start, end in (
        ("public static void Initialize", "public static bool IsOperational"),
        ("public static bool IsOperational", "public static bool IsSafeOrderId"),
        ("private static bool TryReadIntent", "public static bool TryRecordIntent"),
        ("public static bool TryRecordIntent", "public static bool MarkResolved"),
        ("public static bool MarkResolved", "private static bool MigratePendingConfirmationIntentsLocked"),
    ):
        block = ledger[ledger.index(start):ledger.index(end, ledger.index(start))]
        assert 'CrossProcessAtomicStateFile.Acquire(path, "AutoDeliveryConfirmationLedger", 3000)' in block

    for start, end in (
        ("public static bool IsOperational", "public static bool IsSafeOrderId"),
        ("private static bool TryReadIntent", "public static bool TryRecordIntent"),
        ("public static bool TryRecordIntent", "public static bool MarkResolved"),
        ("public static bool MarkResolved", "private static bool MigratePendingConfirmationIntentsLocked"),
    ):
        block = ledger[ledger.index(start):ledger.index(end, ledger.index(start))]
        assert "ReloadStateForMutationLocked()" in block

    assert "private static bool ReloadStateForMutationLocked()" in ledger
    assert "Caller must hold the named cross-process mutex" in ledger
    reload = ledger[
        ledger.index("private static bool ReloadStateForMutationLocked"):
        ledger.index("private static void CleanupLocked")
    ]
    assert "if (!File.Exists(path))" in reload
    assert "禁止新的自动确认" in reload



def test_auto_delivery_worker_never_executes_without_a_durable_queue_checkpoint():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    worker = runtime[
        runtime.index("private static async Task RunWorkerAsync"):
        runtime.index("private static async Task ProcessRecordAsync")
    ]
    save = runtime[
        runtime.index("private static bool SaveStateLocked"):
        runtime.index("private static OrderSnapshot CloneSnapshot")
    ]

    reserve = worker.index("record.NextAttemptAt = now.Add(WorkerClaimLease);")
    persist = worker.index("if (!SaveStateLocked()) return;", reserve)
    execute = worker.index("await ProcessRecordAsync(record).ConfigureAwait(false);", persist)
    assert reserve < persist < execute
    assert "if (_stateLoadFailed) return;" in worker
    assert "_stateLoadFailed = true;" in save
    assert "已暂停自动发货" in save


def test_corrupt_or_unreadable_auto_delivery_queue_never_starts_worker_or_overwrites_source():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    initialize = runtime[
        runtime.index("public static void Initialize()"):
        runtime.index("public static bool IsOperational()")
    ]
    loader = runtime[
        runtime.index("private static bool ReloadStateForMutationLocked"):
        runtime.index("private static bool SaveStateLocked")
    ]

    assert "ReloadStateForMutationLocked(true)" in initialize
    assert "_stateLoadFailed = true;" in initialize
    failed_branch = initialize[initialize.index("if (!ReloadStateForMutationLocked(true))"):]
    return_index = failed_branch.index("return;")
    assert return_index < failed_branch.index("CleanupExpiredLocked")
    assert failed_branch.index("EnsureRecoveryTimerLocked()") < return_index
    assert "EnsureRecoveryTimerLocked()" in initialize
    assert "LoadState(out loadedSuccessfully)" in loader
    assert "拒绝以空队列覆盖原任务状态" in loader
    assert "loadedSuccessfully = true;" not in loader[loader.index("catch (Exception ex)"):]


def test_uncertain_auto_delivery_outcome_restarts_as_read_only_verification_never_second_confirm():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    execute = runtime[
        runtime.index("internal async Task<AutoDeliveryAttemptResult> TryExecuteVirtualGoodsAutoDeliveryAsync"):
        runtime.index("private async Task<AutoDeliveryPreflightResult> TrySilentAutoDeliveryPreflightAsync")
    ]
    process = runtime[
        runtime.index("private static async Task ProcessRecordAsync"):
        runtime.index("private static bool IsSilentPreflightReason")
    ]
    uncertain = process[
        process.index("case AutoDeliveryAttemptOutcome.ConfirmationUncertain:"):
        process.index("default:", process.index("case AutoDeliveryAttemptOutcome.ConfirmationUncertain:"))
    ]

    assert "if (AutoDeliveryConfirmationLedger.HasIntent(snapshot.ShopKey, seller, snapshot.OrderId))" in execute
    assert execute.index("if (AutoDeliveryConfirmationLedger.HasIntent") < execute.index("if (!verificationOnly")
    assert execute.index("TrySilentAutoDeliveryPreflightAsync(snapshot)") < execute.index("if (verificationOnly)")
    assert "保持只读，不切换前台买家且绝不再次确认发货" in execute
    assert execute.index("if (verificationOnly)") < execute.index("BotActivityCoordinator.IsSafeToAutoFocus")
    assert "AutoDeliveryCoordinator.TryPersistConfirmationIntent(" in execute
    assert "snapshot.ShopKey, snapshot.Seller, snapshot.OrderId, workerClaimId" in execute
    assert execute.index("TryPersistConfirmationIntent") < execute.index("RenewWorkerClaim", execute.index("TryPersistConfirmationIntent"))
    assert execute.index("RenewWorkerClaim", execute.index("TryPersistConfirmationIntent")) < execute.index('ReadModalStateAsync(sourceClient, "confirm")')
    assert "repeatConfirm=false" in execute
    assert "live.ConfirmationUncertainCount++;" in uncertain
    assert "live.NextAttemptAt = DateTime.Now.Add(UncertainRetryDelay);" in uncertain
    assert 'TryResolveConfirmationIntent(record, "manual_review_required")' in uncertain

def test_auto_delivery_queue_retains_read_only_barrier_until_ledger_resolution_is_durable():
    ledger = text("src/Bot/ChromeNs/AutoDeliveryConfirmationLedger.cs")
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    resolve = ledger[
        ledger.index("public static bool MarkResolved"):
        ledger.index("private static bool MigratePendingConfirmationIntentsLocked")
    ]
    cleanup = runtime[
        runtime.index("private static void CleanupExpiredLocked"):
        runtime.index("private static DateTime ResolveAnchor")
    ]
    process = runtime[
        runtime.index("private static async Task ProcessRecordAsync"):
        runtime.index("private static bool IsSilentPreflightReason")
    ]

    assert "public static bool MarkResolved(" in resolve
    assert "if (!lease.Acquired || !ReloadStateForMutationLocked()) return false;" in resolve
    assert "if (records.Count == 0) return false;" in resolve
    assert "if (!SaveLocked())" in resolve
    assert "return true;" in resolve
    assert "TryResolveConfirmationIntent(item, \"manual_review_required\")" in cleanup
    assert "保留只读队列屏障" in cleanup
    assert "TryResolveConfirmationIntent(record, \"completed\")" in process
    assert "TryResolveConfirmationIntent(record, \"terminal:\" + result.Reason)" in process
    assert "人工复核状态未能持久化，保留只读防重屏障" in process
    assert "_state.Pending.Remove(live);" not in process[
        process.index("case AutoDeliveryAttemptOutcome.ConfirmationUncertain:"):
        process.index("default:", process.index("case AutoDeliveryAttemptOutcome.ConfirmationUncertain:"))
    ]


def test_read_only_auto_delivery_recovery_failures_count_toward_manual_review():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    execute = runtime[
        runtime.index("internal async Task<AutoDeliveryAttemptResult> TryExecuteVirtualGoodsAutoDeliveryAsync"):
        runtime.index("private async Task<AutoDeliveryPreflightResult> TrySilentAutoDeliveryPreflightAsync")
    ]
    uncertain = execute[execute.index("catch (Exception ex)", execute.index('ReadModalStateAsync(sourceClient, "confirm")')):]

    assert 'AutoDeliveryAttemptResult.Uncertain("只读复核无法确认当前客服与任务店铺身份一致")' in execute
    assert 'AutoDeliveryAttemptResult.Uncertain("只读复核无法验证当前千牛会话ShopKey："' in execute
    assert 'AutoDeliveryAttemptResult.Uncertain("只读复核发现当前千牛会话ShopKey与订单任务不一致")' in execute
    assert 'AutoDeliveryAttemptResult.Uncertain("只读复核异常：" + ex.Message)' in uncertain
    assert 'verificationOnly=" + verificationOnly' in uncertain

def test_auto_delivery_cursor_uses_immutable_event_acceptance_time_not_duplicate_seen_time():
    seed = text("src/Bot/ChromeNs/AutoDeliveryOrderEventSeed.cs")
    hub = text("src/Bot/ChromeNs/OrderEventHub.cs")
    event = seed[
        seed.index("private sealed class HubEvent"):
        seed.index("private sealed class HubState")
    ]
    helper = seed[
        seed.index("private static DateTime GetAcceptedAt"):
        seed.index("private static HubState LoadHubState")
    ]
    tick = seed[
        seed.index("private static void Tick"):
        seed.index("private static SellerCursor GetCursorLocked")
    ]
    publish = hub[
        hub.index("public static OrderEventPublishResult Publish"):
        hub.index("private static StateMutexLease AcquireStateMutex")
    ]

    assert "public DateTime AcceptedAt { get; set; }" in event
    assert "entry.AcceptedAt == DateTime.MinValue ? entry.SeenAt : entry.AcceptedAt" in helper
    assert ".Where(x => GetAcceptedAt(x) > floor)" in tick
    assert ".OrderBy(x => GetAcceptedAt(x))" in tick
    assert "var acceptedAt = GetAcceptedAt(entry);" in tick
    assert "if (acceptedAt > latest) latest = acceptedAt;" in tick
    assert "existing.SeenAt = now;" in publish
    assert "existing.AcceptedAt" in publish
    assert ".Where(x => x.SeenAt > floor)" not in tick

def test_preflight_completed_or_terminal_order_does_not_require_a_nonexistent_confirmation_intent():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    ledger = text("src/Bot/ChromeNs/AutoDeliveryConfirmationLedger.cs")
    resolve = runtime[
        runtime.index("private static bool TryResolveConfirmationIntent("):
        runtime.index("private static PendingRecord FindLiveRecordLocked")
    ]
    process = runtime[
        runtime.index("private static async Task ProcessRecordAsync"):
        runtime.index("private static bool IsSilentPreflightReason")
    ]
    completed = process[
        process.index("case AutoDeliveryAttemptOutcome.Completed:"):
        process.index("case AutoDeliveryAttemptOutcome.TerminalNoAction:")
    ]
    terminal = process[
        process.index("case AutoDeliveryAttemptOutcome.TerminalNoAction:"):
        process.index("case AutoDeliveryAttemptOutcome.ConfirmationUncertain:")
    ]
    try_get = ledger[
        ledger.index("public static bool TryGetIntent"):
        ledger.index("private static bool TryReadIntent")
    ]
    has_intent = ledger[
        ledger.index("public static bool HasIntent"):
        ledger.index("public static bool TryGetIntent")
    ]

    assert "record.ConfirmationIntentAt.HasValue" in resolve
    assert "AutoDeliveryConfirmationLedger.TryGetIntent(" in resolve
    assert "if (!hasIntent) return true;" in resolve
    assert "AutoDeliveryConfirmationLedger.MarkResolved(" in resolve
    assert "TryResolveConfirmationIntent(record, \"completed\")" in completed
    assert "TryResolveConfirmationIntent(record, \"terminal:\" + result.Reason)" in terminal
    assert "hasIntent = false;" in try_get or "out bool hasIntent" in try_get
    assert "return hasIntent || atCapacity;" in has_intent
    assert "if (!TryReadIntent(shopKey, seller, orderId, out hasIntent, out atCapacity)) return true;" in has_intent

def test_full_confirmation_ledger_never_fabricates_per_order_intent_on_capacity():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    persist = runtime[
        runtime.index("public static bool TryPersistConfirmationIntent"):
        runtime.index("private static void ScheduleWorker")
    ]

    exact_read = persist.index("AutoDeliveryConfirmationLedger.TryGetIntent(shopKey, seller, orderId, out existingIntent)")
    actual_intent = persist.index("if (existingIntent)", exact_read)
    capacity_gate = persist.index("if (!AutoDeliveryConfirmationLedger.IsOperational())", actual_intent)
    record = persist.index("AutoDeliveryConfirmationLedger.TryRecordIntent", capacity_gate)
    race_read = persist.index("AutoDeliveryConfirmationLedger.TryGetIntent(shopKey, seller, orderId, out racedIntent)", record)

    assert exact_read < actual_intent < capacity_gate < record < race_read
    assert "if (AutoDeliveryConfirmationLedger.HasIntent(shopKey, seller, orderId))" not in persist
    assert "不伪造订单确认屏障" in persist

def test_read_only_delivery_recovery_never_rebinds_or_focuses_another_shop():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    process = runtime[
        runtime.index("private static async Task ProcessRecordAsync"):
        runtime.index("private static bool IsSilentPreflightReason")
    ]
    execute = runtime[
        runtime.index("internal async Task<AutoDeliveryAttemptResult> TryExecuteVirtualGoodsAutoDeliveryAsync"):
        runtime.index("private bool IsAutoDeliverySourceSessionCurrent")
    ]

    assert "using (ShopSettingsScope.Enter(shop, false))" in process
    assert "using (ShopSettingsScope.Enter(shop))" not in process
    verification_stop = execute.index("if (verificationOnly)")
    bind = execute.index("MultiShopRuntimeSessionCoordinator.EnsureShopBinding(runtimeShop)")
    focus = execute.index("BotActivityCoordinator.IsSafeToAutoFocus", bind)
    assert verification_stop < bind < focus
    assert "if (!MultiShopRuntimeSessionCoordinator.EnsureShopBinding(runtimeShop))" in execute
    assert "静默预检确认订单仍未发货，但确认权限此前已消费" in execute
    assert "保持只读，不切换前台买家且绝不再次确认发货" in execute


def test_legacy_delivery_queue_retries_unique_shop_recovery_after_restart():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    process = runtime[
        runtime.index("private static async Task ProcessRecordAsync"):
        runtime.index("private static bool IsSilentPreflightReason")
    ]
    ledger = text("src/Bot/ChromeNs/AutoDeliveryConfirmationLedger.cs")
    resolve = ledger[
        ledger.index("public static bool MarkResolved"):
        ledger.index("private static bool MigratePendingConfirmationIntentsLocked")
    ]

    assert "ResolveRuntimeBySellerNick(snapshot.Seller)" in process
    assert "string.Equals(shop.SellerNick, snapshot.Seller, StringComparison.Ordinal)" in process
    assert "var resolvedKey = BuildKey(shop.ShopKey, snapshot.Seller, snapshot.OrderId);" in process
    assert "live.Snapshot.ShopKey = shop.ShopKey;" in process
    assert "if (!SaveStateLocked())" in process
    assert "collision" in process
    assert "BuildLegacyKey(seller, orderId)" in resolve
    assert "string.Equals(x.Key, legacyKey, StringComparison.Ordinal)" in resolve
    assert "foreach (var record in records)" in resolve
    # Migrated legacy barriers remain present after resolution, so at-most-once protection survives.
    assert "KeepUntil" in resolve
    assert "Resolution" in resolve


def test_auto_delivery_queue_claims_and_every_mutation_are_cross_process_serialized():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    lock_call = 'CrossProcessAtomicStateFile.Acquire(path, "VirtualGoodsAutoDeliveryQueue", 3000)'

    blocks = [
        runtime[runtime.index("public static void Initialize"):runtime.index("public static bool IsOperational")],
        runtime[runtime.index("public static bool Enqueue"):runtime.index("public static void ReconfigureSeller")],
        runtime[runtime.index("public static void ReconfigureSeller"):runtime.index("public static bool TryPersistConfirmationIntent")],
        runtime[runtime.index("public static bool TryPersistConfirmationIntent"):runtime.index("private static void ScheduleWorker")],
        runtime[runtime.index("private static async Task RunWorkerAsync"):runtime.index("private static async Task ProcessRecordAsync")],
        runtime[runtime.index("private static void DeferSellerNavigationRecords"):runtime.index("private static void DeferRecord")],
        runtime[runtime.index("private static void DeferRecord"):runtime.index("private static void RemoveRecord")],
        runtime[runtime.index("private static void RemoveRecord"):runtime.index("private static bool HasConfirmationIntent")],
    ]
    for block in blocks:
        assert lock_call in block
        assert "ReloadStateForMutationLocked(" in block

    process = runtime[
        runtime.index("private static async Task ProcessRecordAsync"):
        runtime.index("private static bool IsSilentPreflightReason")
    ]
    uncertain = process[
        process.index("case AutoDeliveryAttemptOutcome.ConfirmationUncertain:"):
        process.index("default:", process.index("case AutoDeliveryAttemptOutcome.ConfirmationUncertain:"))
    ]
    legacy = process[
        process.index("var legacyKey = record.Key;"):
        process.index("if (collision)", process.index("var legacyKey = record.Key;"))
    ]
    assert lock_call in uncertain and "ReloadStateForMutationLocked(false)" in uncertain
    assert lock_call in legacy and "ReloadStateForMutationLocked(false)" in legacy

    helper = runtime[
        runtime.index("private static bool ReloadStateForMutationLocked"):
        runtime.index("private static StateDocument LoadState")
    ]
    assert "if (_stateLoadFailed) return false;" in helper
    assert "if (!allowMissing)" in helper
    assert "拒绝以空队列覆盖原任务状态" in helper
    assert "_state = loaded;" in helper

    assert "WorkerClaimLease = TimeSpan.FromMinutes(5)" in runtime
    worker = runtime[
        runtime.index("private static async Task RunWorkerAsync"):
        runtime.index("private static async Task ProcessRecordAsync")
    ]
    claim = worker.index("record.NextAttemptAt = now.Add(WorkerClaimLease);")
    persist = worker.index("if (!SaveStateLocked()) return;", claim)
    execute = worker.index("await ProcessRecordAsync(record).ConfigureAwait(false);", persist)
    assert claim < persist < execute
    assert "暂停执行以避免重复领取任务" in worker


def test_auto_delivery_worker_claim_token_fences_stale_processes_before_irreversible_confirm():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    coordinator = runtime[
        runtime.index("internal static class AutoDeliveryCoordinator"):
        runtime.index("public partial class QN")
    ]
    process = coordinator[
        coordinator.index("private static async Task ProcessRecordAsync"):
        coordinator.index("private static bool IsSilentPreflightReason")
    ]
    renew = coordinator[
        coordinator.index("public static bool RenewWorkerClaim"):
        coordinator.index("private static bool IsWorkerClaimCurrent")
    ]
    execute = runtime[
        runtime.index("internal async Task<AutoDeliveryAttemptResult> TryExecuteVirtualGoodsAutoDeliveryAsync"):
        runtime.index("private bool IsAutoDeliverySourceSessionCurrent")
    ]

    assert "public string WorkerClaimId { get; set; }" in coordinator
    assert 'record.WorkerClaimId = Guid.NewGuid().ToString("N");' in coordinator
    assert "Guid.TryParseExact(workerClaimId, \"N\", out claimGuid)" in renew
    assert "live.NextAttemptAt <= now" in renew
    assert "if (!IsWorkerClaimCurrent(record))" in process
    assert "if (!IsSameWorkerClaim(live, record)) return;" in process
    assert "if (!IsSameWorkerClaim(live, record)) return;" in coordinator[
        coordinator.index("private static void DeferRecord"):
        coordinator.index("private static void RemoveRecord")
    ]
    assert "if (!IsSameWorkerClaim(live, record)) return;" in coordinator[
        coordinator.index("private static void RemoveRecord"):
        coordinator.index("private static bool HasConfirmationIntent")
    ]
    assert "AutoDeliveryCoordinator.RenewWorkerClaim(" in execute
    assert "snapshot.ShopKey, snapshot.Seller, snapshot.OrderId, workerClaimId" in execute
    preflight = execute.index("TrySilentAutoDeliveryPreflightAsync(snapshot)")
    renew_after_preflight = execute.index("RenewWorkerClaim(", preflight)
    focus_after_preflight = execute.index("BotActivityCoordinator.IsSafeToAutoFocus", preflight)
    send_lock = execute.index("await _sendGate.WaitAsync()")
    renew_after_send_lock = execute.index("RenewWorkerClaim(", send_lock)
    assert preflight < renew_after_preflight < focus_after_preflight
    assert send_lock < renew_after_send_lock
    persist = execute.index("TryPersistConfirmationIntent(")
    renew_before_click = execute.index("RenewWorkerClaim(", persist)
    click = execute.index('ReadModalStateAsync(sourceClient, "confirm")', renew_before_click)
    assert persist < renew_before_click < click



def test_auto_delivery_queue_retries_startup_recovery_without_enabling_unreadable_state():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    tick = runtime[runtime.index("private static void OnTimerTick"):runtime.index("private static bool TryRecoverQueueState")]
    recover = runtime[runtime.index("private static bool TryRecoverQueueState"):runtime.index("public static bool IsOperational")]
    assert "if (_stateLoadFailed && !TryRecoverQueueState()) return;" in tick
    assert "ReloadStateForMutationLocked(true)" in recover
    assert "if (!ReloadStateForMutationLocked(true))" in recover
    assert "if (!SaveStateLocked()) return false;" in recover
    assert "_initialized = 1;" in recover
    assert "EnsureRecoveryTimerLocked();" in recover
    assert "ScheduleWorker();" in tick


def test_auto_delivery_event_cursor_retries_startup_recovery_before_consuming_events():
    seed = text("src/Bot/ChromeNs/AutoDeliveryOrderEventSeed.cs")
    tick = seed[seed.index("private static void OnTimerTick"):seed.index("private static bool TryRecoverCursorState")]
    recover = seed[seed.index("private static bool TryRecoverCursorState"):seed.index("private static void Tick")]
    assert "if (_cursorStateUnavailable && !TryRecoverCursorState()) return;" in tick
    assert "if (!_cursorStateUnavailable) Tick();" in tick
    assert "LoadCursors(out loadedSuccessfully)" in recover
    assert "if (!loadedSuccessfully)" in recover
    assert "if (_cursorStateDirty && !SaveCursorsLocked()) return false;" in recover
    assert "_initialized = 1;" in recover
    assert "EnsureRecoveryTimerLocked();" in recover



def test_auto_delivery_queue_loss_after_restart_is_detected_by_durable_marker():
    runtime = text("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")
    marker = runtime[runtime.index("private static string StateMarkerPath"):runtime.index("private static bool ReloadStateForMutationLocked")]
    reload = runtime[
        runtime.index("private static bool ReloadStateForMutationLocked"):
        runtime.index("private static StateDocument LoadState")
    ]
    save = runtime[
        runtime.index("private static bool EnsureStateMarkerLocked"):
        runtime.index("private static bool SaveStateLocked")
    ]
    assert 'return StatePath() + ".initialized";' in marker
    assert "private static bool TryGetFilePresence" in marker
    assert "File.GetAttributes(path);" in marker
    assert "catch (FileNotFoundException)" in marker
    assert "catch (DirectoryNotFoundException)" in marker
    assert "StateMarkerPath(), \"schema=1\"" in save
    missing = reload[reload.index("if (!queueExists)"):reload.index("bool loadedSuccessfully;")]
    assert "TryGetFilePresence(StateMarkerPath(), out markerExists)" in missing
    assert "if (!TryGetFilePresence(StateMarkerPath(), out markerExists) || markerExists)" in missing
    assert missing.index("markerExists") < missing.index("_state = new StateDocument();")



def test_auto_delivery_event_cursor_loss_after_restart_does_not_skip_pending_orders():
    seed = text("src/Bot/ChromeNs/AutoDeliveryOrderEventSeed.cs")
    loader = seed[seed.index("private static CursorState LoadCursors"):seed.index("private static bool SaveCursorsLocked")]
    save = seed[seed.index("private static bool SaveCursorsLocked"):seed.index("private static string CursorMarkerPath")]
    marker = seed[seed.index("private static string CursorMarkerPath"):seed.index("private static string DataRoot")]
    missing = loader[loader.index("if (!exists)"):loader.index("var state = JsonConvert.DeserializeObject")]
    assert 'return CursorPath() + ".initialized";' in marker
    assert "File.GetAttributes(path);" in marker
    assert "catch (FileNotFoundException)" in marker
    assert "TryGetFilePresence(CursorMarkerPath(), out markerExists)" in missing
    assert "if (!TryGetFilePresence(CursorMarkerPath(), out markerExists) || markerExists)" in missing
    assert "loadedSuccessfully = true;" in missing
    assert "EnsureCursorMarkerLocked()) return new CursorState();" in loader
    assert "EnsureCursorMarkerLocked()" in save
