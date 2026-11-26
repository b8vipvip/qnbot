from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_strict_classifier_does_not_treat_generic_tid_or_status_as_order():
    code = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs")
    order_keys = code.split("StrongOrderIdKeyRegex", 1)[1].split("RegexOptions", 1)[0].lower()
    assert "orderid" in order_keys; assert "bizorderid" in order_keys; assert "tradeid" in order_keys; assert "|tid" not in order_keys
    assert "普通消息 tid 不作为订单号" in code; assert "structureScore >= 2" in code; assert "StrongSystemCueRegex" in code

def test_order_plan_requires_strict_evidence_before_parser():
    code = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    strict_at = code.index("OrderMessageClassifier.IsConfirmedOrderEvent"); followup_at = code.index("TryCreateBuyerFollowUpPlan", strict_at); parse_at = code.index("OrderCardParser.TryParse")
    assert strict_at < followup_at < parse_at
    helper = code[code.index("private static bool TryCreateBuyerFollowUpPlan"):]
    assert "OrderGuidanceDeliveryGuard.CanCreateFollowUp" in helper; assert "return false;" in helper

def test_initial_guidance_is_persisted_once_across_created_paid_and_restart():
    guard = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs"); service = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "order-guidance-delivery-state.json" in guard; assert "InitialDeliveredAt" in guard; assert "该订单的充值流程已经发送过一次" in guard
    assert "OrderGuidanceDeliveryGuard.ObserveOrder(snapshot)" in service; assert "OrderGuidanceDeliveryGuard.MarkDelivered" in service
    assert "Bot强制订单规则发送" in service

def test_manual_equivalent_reply_does_not_suppress_mandatory_order_rule():
    guard = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs"); service = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "ConversationContextStore.GetRecentTurns" in guard; assert "EquivalentGuidance" in guard
    process = service[service.index("private async Task ProcessOrderPlacedReplyAsync"):]
    assert "OrderGuidanceDeliveryGuard.ShouldSuppressBeforeSend" not in process
    assert "ResponseProgressTracker.HasActiveManualIntervention" not in process
    assert "SendMandatoryOrderTextAsync" in process

def test_only_explicit_followups_allow_one_extra_guidance_message():
    guard = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs"); service = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    for phrase in ["拍了", "下单了", "怎么充", "怎么充值"]: assert phrase in guard
    for negative in ["没下单", "还没付款", "怎么下单"]: assert negative in guard
    assert "FollowUpDeliveredAt.HasValue" in guard; assert "该订单已经补发过一次充值流程" in guard
    assert "#guidance-followup" in service; assert "订单规则强制补发一次" in service

def test_build_includes_guard():
    targets = read("src/Directory.Build.targets")
    assert "ChromeNs\\OrderGuidanceDeliveryGuard.cs" in targets


def test_order_guidance_ledger_corruption_fails_closed_and_never_overwrites_the_old_file():
    guard = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs")
    loader = guard[
        guard.index("private static bool EnsureLoaded()"):
        guard.index("private static void CleanupInternal()")
    ]
    saver = guard[
        guard.index("private static bool SaveInternal()"):
        guard.index("private static string GetPath()")
    ]
    recent = guard[
        guard.index("public static List<OrderSnapshot> GetRecentOrderSnapshots"):
        guard.index("public static bool TryGetLatestOrderSnapshot")
    ]

    assert "File.GetAttributes(path)" in loader
    assert "CrossProcessAtomicStateFile.ReadAllTextShared" in loader
    assert "loaded.Records == null" in loader
    assert "throw new InvalidDataException" in loader
    assert "_state = null;" in loader
    assert "return false;" in loader
    assert "CrossProcessAtomicStateFile.WriteAllTextAtomic" in saver
    assert "File.Delete(path)" not in saver
    assert "File.Move(temp, path)" not in saver
    assert "if (!EnsureLoaded())" in recent
    assert "throw new IOException" in recent


def test_commerce_context_does_not_masquerade_as_empty_order_history_when_ledger_is_unavailable():
    commerce = read("src/Bot/ChromeNs/CommerceContextService.cs")
    select = commerce[
        commerce.index("internal static OrderSnapshot SelectRelevantOrderSnapshot"):
        commerce.index("private static bool ContainsSignal")
    ]

    assert "try" in select and "catch (Exception ex)" in select
    assert "order_ledger_unavailable" in select
    assert "GetRecentOrderSnapshots(seller, buyer, 12, shopKey)" in select
    assert "return null;" in select



def test_order_guidance_ledger_cross_process_transactions_reload_from_disk():
    guard = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs")
    # Every state access uses the same named mutex, and invalidates the process-local
    # cache while still holding that mutex. Atomic replacement alone is not an RMW lock.
    mutex = 'using (var ledgerLease = CrossProcessAtomicStateFile.Acquire(GetPath(), "order-guidance-ledger", 5000))'
    assert guard.count(mutex) == 6
    assert guard.count("if (!ledgerLease.Acquired)") == 6
    assert guard.count("_state = null;") >= 8

    observe = guard[guard.index("public static void ObserveOrder"):guard.index("public static bool IsExplicitBuyerFollowUp")]
    recent = guard[guard.index("public static List<OrderSnapshot> GetRecentOrderSnapshots"):guard.index("public static bool TryGetLatestOrderSnapshot")]
    followup = guard[guard.index("public static bool CanCreateFollowUp"):guard.index("public static bool ShouldSuppressBeforeSend")]
    presend = guard[guard.index("public static bool ShouldSuppressBeforeSend"):guard.index("public static void MarkDelivered")]
    delivered = guard[guard.index("public static void MarkDelivered"):guard.index("private static OrderGuidanceRecord NewRecord")]

    assert observe.index("if (!ledgerLease.Acquired)") < observe.index("_state = null;") < observe.index("EnsureLoaded()") < observe.index("SaveInternal()")
    assert "record.Snapshot = CloneSnapshot(snapshot)" in observe
    assert 'throw new IOException("订单账本跨进程锁不可用' in recent
    assert "订单账本跨进程锁不可用，已阻止自动补发" in followup
    assert "订单账本跨进程锁不可用，已阻止发送" in presend
    assert delivered.index("if (!ledgerLease.Acquired)") < delivered.index("MarkDeliveredInternal") < delivered.index("SaveInternal()")



def test_equivalent_seller_reply_dedup_never_reads_another_shop_timeline():
    source = read("src/Bot/ChromeNs/OrderGuidanceDeliveryGuard.cs")
    presend = source[source.index("public static bool ShouldSuppressBeforeSend"):source.index("public static void MarkDelivered")]
    compare = source[source.index("private static bool FindEquivalentSellerReply"):source.index("internal static bool EquivalentGuidance")]
    assert "FindEquivalentSellerReply(plan.ShopKey, plan.Seller, plan.Buyer, answer, since, out matched)" in presend
    assert "string shopKey," in compare
    assert "ConversationContextStore.GetRecentTurns(seller, buyer, string.Empty, 24, shopKey)" in compare
    assert "GetRecentTurns(seller, buyer, shopKey, 24)" not in compare
