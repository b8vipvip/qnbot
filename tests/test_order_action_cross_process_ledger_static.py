from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


# The durable action ledger is the source of truth across Bot processes; any uncertain read is fail-closed.
def test_action_ledger_persists_cross_process_inflight_before_send():
    code = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    begin = code[code.index("internal static bool TryBeginExecution"):code.index("internal static void MarkDeliveryUncertain")]
    assert "public bool InFlight" in code
    assert 'CrossProcessAtomicStateFile.Acquire(path, "OrderReplyActionState", 3000)' in begin
    assert "ReloadAndMergeActionStateLocked(path, out reloadError)" in begin
    assert "action_inflight_cross_process" in begin
    assert "durable.InFlight = true" in begin
    assert "SaveActionStateLocked(path)" in begin
    assert "action_state_persist_failed" in begin
    assert "action_state_unavailable" in begin


def test_action_ledger_write_is_atomic_and_old_valid_file_is_never_deleted_first():
    helper = read("src/Bot/ChromeNs/CrossProcessAtomicStateFile.cs")
    assert "Guid.NewGuid().ToString(\"N\")" in helper
    assert "FileMode.CreateNew" in helper
    assert "FileOptions.WriteThrough" in helper
    assert "stream.Flush(true)" in helper
    assert "File.Replace(temp, path, backupPath, true)" in helper
    assert "string backupPath = null" in helper
    assert "File.Delete(path)" not in helper
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    state = order[order.index("private static void EnsureActionStateLoadedLocked"):order.index("private static string GetActionStatePath")]
    assert "ReadAllTextShared" in state
    assert "TryReadActionStateFromDiskLocked" in state
    assert "read_failed:" in state
    assert "parse_failed:" in state
    assert "WriteAllTextAtomic" in state
    assert "File.WriteAllText(" not in state
    assert "File.Delete(path)" not in state


def test_finish_and_uncertain_paths_clear_durable_inflight_under_same_mutex():
    code = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    uncertain = code[code.index("internal static void MarkDeliveryUncertain"):code.index("internal static bool FinishExecution")]
    finish = code[code.index("internal static bool FinishExecution"):code.index("private static void ObserveCanonicalOrderId")]
    assert "existing.InFlight = false" in uncertain
    assert "existing.DeliveryUncertain = true" in uncertain
    assert "existing.InFlight = false" in finish
    assert 'CrossProcessAtomicStateFile.Acquire(path, "OrderReplyActionState", 3000)' in finish

def test_uncertain_reply_keeps_long_pre_send_barrier_when_post_send_persist_fails():
    code = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    begin = code[
        code.index("internal static bool TryBeginExecution"):
        code.index("internal static void MarkDeliveryUncertain")
    ]
    uncertain = code[
        code.index("internal static void MarkDeliveryUncertain"):
        code.index("internal static bool FinishExecution")
    ]

    # The durable lease is written before the external side effect. If the later
    # uncertain-state write fails, the existing disk record must still block retries.
    assert "durable.Until = now.AddHours(720);" in begin
    assert "if (!SaveActionStateLocked(path))" in uncertain
    assert "发送前已持久化的30天in-flight屏障" in uncertain
    assert "durable.Until = now.AddMinutes(10);" not in begin



def test_orphaned_order_reply_inflight_is_recovered_to_manual_review_not_resend():
    code = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    begin = code[
        code.index("internal static bool TryBeginExecution"):
        code.index("internal static void MarkDeliveryUncertain")
    ]
    helpers = code[
        code.index("private static bool TryGetCurrentProcessIdentity"):
        code.index("private static bool SaveActionStateLocked")
    ]

    assert "public int ProcessId { get; set; }" in code
    assert "public long ProcessStartTimeUtcTicks { get; set; }" in code
    assert "durable.ProcessId = ownerProcessId" in begin
    assert "durable.ProcessStartTimeUtcTicks = ownerProcessStartTimeUtcTicks" in begin
    assert "if (IsActionOwnerProcessAlive(priorAction))" in begin
    assert "priorAction.DeliveryUncertain = true" in begin
    assert 'reason = "action_delivery_uncertain"' in begin
    assert "var reviewUntil = now.AddHours(720);" in begin
    assert "if (priorAction.Until < reviewUntil) priorAction.Until = reviewUntil;" in begin
    assert "Process.GetProcessById(record.ProcessId)" in helpers
    assert "process.StartTime.ToUniversalTime().Ticks" in helpers



def test_orphaned_order_reply_actions_are_recovered_on_startup_and_periodically():
    code = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    bootstrap = read("src/Bot/AutoDeliveryAppBootstrap.cs")
    initialize = code[
        code.index("internal static void Initialize()"):
        code.index("private static void RecoverOrphanedInFlightActions")
    ]
    recovery = code[
        code.index("private static void RecoverOrphanedInFlightActions"):
        code.index("public static bool TryCreatePlan")
    ]

    assert "OrderPlacedAutoReplyService.Initialize();" in bootstrap
    assert "RecoverOrphanedInFlightActions();" in initialize
    assert "new Timer(" in initialize
    assert "TimeSpan.FromMinutes(1)" in initialize
    assert 'CrossProcessAtomicStateFile.Acquire(path, "OrderReplyActionState", 3000)' in recovery
    assert "ReloadAndMergeActionStateLocked(path, out reloadError)" in recovery
    assert "IsActionOwnerProcessAlive(record)" in recovery
    assert "record.DeliveryUncertain = true;" in recovery
    assert "record.ProcessId = 0;" in recovery
    assert "SaveActionStateLocked(path)" in recovery


def test_live_inflight_order_reply_barrier_never_expires_while_owner_process_is_alive():
    code = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    begin = code[
        code.index("internal static bool TryBeginExecution"):
        code.index("internal static void MarkDeliveryUncertain")
    ]

    assert "(!x.InFlight && x.Until <= now)" in begin
    assert "if (priorAction != null && priorAction.InFlight)" in begin
    assert "if (IsActionOwnerProcessAlive(priorAction))" in begin



def test_order_reply_recovery_timer_keeps_unknown_delivery_fail_closed_after_restart():
    code = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    initialize = code[
        code.index("internal static void Initialize()"):
        code.index("private static void RecoverOrphanedInFlightActions")
    ]
    recovery = code[
        code.index("private static void RecoverOrphanedInFlightActions"):
        code.index("public static bool TryCreatePlan")
    ]
    begin = code[
        code.index("internal static bool TryBeginExecution"):
        code.index("internal static void MarkDeliveryUncertain")
    ]

    assert "RecoverOrphanedInFlightActions();" in initialize
    assert "TimeSpan.FromMinutes(1)" in initialize
    assert "ReloadAndMergeActionStateLocked(path, out reloadError)" in recovery
    assert "if (IsActionOwnerProcessAlive(record)) continue;" in recovery
    assert "record.DeliveryUncertain = true;" in recovery
    assert "record.InFlight = false;" in recovery
    assert "if (IsActionOwnerProcessAlive(priorAction))" in begin
    assert 'reason = "action_delivery_uncertain";' in begin
    assert "return false;" in begin[begin.index('reason = "action_delivery_uncertain";'):]


def test_order_reply_process_identity_uses_pid_and_start_time_to_avoid_pid_reuse_false_owner():
    code = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    helper = code[
        code.index("private static bool TryGetCurrentProcessIdentity"):
        code.index("private static bool SaveActionStateLocked")
    ]

    assert "Process.GetCurrentProcess()" in helper
    assert "process.StartTime.ToUniversalTime().Ticks" in helper
    assert "Process.GetProcessById(record.ProcessId)" in helper
    assert "process.HasExited" in helper
    assert "record.ProcessStartTimeUtcTicks" in helper
    assert "TimeSpan.FromSeconds(2).Ticks" in helper
