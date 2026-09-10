from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_standby_duplicate_payloads_coalesce_only_expensive_history_probes():
    src = read("src/Bot/ChromeNs/WebSocketPageLifecycleGuard.cs")

    assert "DuplicateProbeFingerprintWindow = TimeSpan.FromMinutes(2)" in src
    assert "MaxDuplicateProbeFingerprints = 2048" in src
    assert "RecentDuplicateProbePayloads" in src
    assert "TryClaimDuplicateProbePayload(seller, e.Type, e.Value)" in src
    assert "BuildDuplicateProbeFingerprint" in src
    assert "SHA256.Create()" in src
    assert "重复千牛standby实时入站已按payload指纹合并，不重复触发CDP历史核对" in src

    gate = src.index("TryClaimDuplicateProbePayload(seller, e.Type, e.Value)")
    probe = src.index("RequestImmediateHistoryProbe(seller, sessionId)", gate)
    assert gate < probe
    assert 'session.Close(' not in src
    assert '"retireDuplicate"' not in src
    assert "physicalClose=false" in src


def test_visual_reply_owns_authoritative_generation_lifecycle_even_after_rebind():
    src = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")

    assert "var lifecycleLease = lease;" in src
    assert "ProcessVisionAsync(qn, lease, lifecycleLease, visionItem)" in src
    assert 'lifecycleLease.MarkReady("vision_answer_materialized")' in src
    assert 'lifecycleLease.MarkSending("vision_send_started")' in src
    assert 'lifecycleLease.MarkCompleted("vision_send_completed")' in src
    assert 'lifecycleLease.MarkFailed("vision_send_failed")' in src
    assert "burst.BuyerNick, answer, 1, lifecycleLease.CancellationToken" in src

    ready = src.index('lifecycleLease.MarkReady("vision_answer_materialized")')
    publish = src.index("ResponseProgressTracker.SetAnswerReady", ready)
    sending = src.index('lifecycleLease.MarkSending("vision_send_started")', publish)
    send = src.index("burst.BuyerNick, answer, 1, lifecycleLease.CancellationToken", sending)
    assert ready < publish < sending < send

    helper = src[src.index("private static bool ShouldBindToRecentImage"):src.index("private static bool HasSubstantiveFollowUpText")]
    assert "return VisionFollowUpContextPipeline.IsVisionReferentialFollowUp(text);" in helper
    for old_fallback in ['compact.Contains("充值")', 'compact.Contains("能充")', 'compact.Contains("可以")', 'elapsed <= TimeSpan.FromSeconds(20)']:
        assert old_fallback not in helper


def test_active_conversation_update_snapshots_chatdesk_to_avoid_startup_toctou_null():
    src = read("src/Bot/ChromeNs/QN.cs")
    method = src[src.index("public void SetActiveConversationByNick"):src.index("private void Cdp_EvShopRobotReceriveNewMessage")]

    assert "var desk = Desk.Inst;" in method
    assert "if (desk != null)" in method
    assert "desk.ChangeSeller" in method
    assert "desk.ChangeBuyer" in method
    assert "Desk.Inst.ChangeSeller" not in method
    assert "Desk.Inst.ChangeBuyer" not in method


def test_auto_delivery_navigation_failures_back_off_same_seller_only_after_candidate_navigation():
    src = read("src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs")

    assert "ConversationNavigationRetryDelay = TimeSpan.FromMinutes(2)" in src
    assert "IsConversationNavigationChurnReason(result.Reason)" in src
    assert "DeferSellerNavigationRecords(record, ConversationNavigationRetryDelay, result.Reason)" in src
    assert "右侧订单面板尚未找到唯一准确订单卡片" in src
    assert "无法确认已切换到订单买家会话" in src
    assert "执行前买家会话发生变化" in src

    helper = src[src.index("private static void DeferSellerNavigationRecords"):src.index("private static void DeferRecord")]
    assert "_state.Pending.Where" in helper
    assert "!x.ConfirmationIntentAt.HasValue" in helper
    assert "string.Equals((x.Snapshot.Seller ?? string.Empty).Trim(), seller" in helper
    assert "live.NextAttemptAt = next" in helper
    assert "避免多个候选订单反复切换前台买家" in helper

    # A silent preflight miss never changed the foreground, so it must not postpone every other
    # due order for the seller. Otherwise one stale order could starve a valid candidate forever.
    process = src[src.index("private static async Task ProcessRecordAsync"):src.index("private static bool IsSilentPreflightReason")]
    assert "DeferRecord(record, SilentPreflightRetryDelay, result.Reason)" in process
    assert "DeferSellerNavigationRecords(record, SilentPreflightRetryDelay" not in process
