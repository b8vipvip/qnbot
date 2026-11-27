from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
BRIDGE = ROOT / "src" / "Bot" / "ChromeNs" / "BusinessInboundLivenessRecoveryBridge.cs"
PROPS = ROOT / "src" / "Directory.Build.props"


def source() -> str:
    return BRIDGE.read_text(encoding="utf-8-sig")


def test_bridge_is_compiled_and_bootstrapped():
    text = source()
    props = PROPS.read_text(encoding="utf-8-sig")
    assert "BusinessInboundLivenessRecoveryBridge.InitializeForApp()" in text
    assert "BusinessInboundLivenessRecoveryBridge.cs" in props
    assert "业务入站存活恢复已启动" in text


def test_runtime_hook_repairs_replaced_imsdk_object_without_stacking_same_target():
    text = source()
    assert "__qnbotBusinessLivenessImsdkTarget === window.imsdk" in text
    assert "__qnbotBusinessLivenessOnRef === window.imsdk.on" in text
    assert "target.on(['im.singlemsg.onReceiveNewMsg'], handler)" in text
    assert "target.invoke('im.singlemsg.GetNewMsg'" in text
    assert "emit('receiveNewMsg', res)" in text
    assert "business-inbound-hook-repaired" in text


def test_remote_history_fallback_reenters_single_normal_business_pipeline():
    text = source()
    assert 'Invoke<JObject>("im.singlemsg.GetRemoteHisMsg"' in text
    assert "IsRecoveredBuyerMessageForTarget" in text
    assert "_messageSafetyStartedAt.AddSeconds(-8).Ticks" in text
    assert "catchupFloor.Ticks" in text
    assert "_businessInboundHistoryProbeLedger.TryAccept(messageKey)" in text
    assert "await ProcessIncomingMessageAsync(message)" in text
    assert "business-event-gap-confirmed" in text
    assert "unseen-buyer-message-detected" in text
    assert "recovered-inbound-replay" in text


def test_recovery_uses_live_source_checkpoint_not_wall_clock_staleness_timeout():
    text = source()
    method = text.split("internal async Task ProbeActiveConversationForMissedInboundAsync()", 1)[1]
    assert "_latestBuyerMessageObserved.TryGetValue(recoveryKey, out observedAt)" in method
    assert "IncomingMessageSafety.GetSortValue(message) > observedAt.Ticks" in method
    assert "var missedCandidates = candidates" in method
    assert "foreach (var message in missedToReplay)" in method
    assert "DateTime.Now.AddSeconds(-2)" not in method
    assert "liveBusinessEventAgeSeconds" not in method
    assert "business-event-stale" not in method


def test_remote_history_fallback_catches_up_after_long_probe_gap():
    text = source()
    method = text.split("internal async Task ProbeActiveConversationForMissedInboundAsync()", 1)[1]
    assert "BusinessInboundNormalLookback = TimeSpan.FromMinutes(2)" in text
    assert "BusinessInboundMaxCatchupLookback = TimeSpan.FromMinutes(45)" in text
    assert "BusinessInboundCatchupOverlap = TimeSpan.FromSeconds(8)" in text
    assert "_lastBusinessInboundHistoryProbeCompletedAt" in text
    assert "lastCompletedAt.Subtract(BusinessInboundCatchupOverlap)" in method
    assert "probeStartedAt.Subtract(BusinessInboundMaxCatchupLookback)" in method
    assert "gapRecovery ? 100 : 20" in method
    assert "count = historyCount" in method
    assert "business-inbound-probe-gap-detected" in method
    assert "DateTime.Now.AddMinutes(-2).Ticks" not in method
    assert "PersistBusinessInboundCheckpoint(shop, DateTime.Now)" in method
    assert "business-inbound-checkpoint-held-for-unanswered-turns" in method


def test_history_fallback_does_not_hijack_visible_chat():
    text = source()
    history_method = text.split("internal async Task ProbeActiveConversationForMissedInboundAsync()", 1)[1]
    assert "OpenChat(" not in history_method
    assert "Task.Delay(180)" in history_method
    assert "businessInboundLivenessStableProbe" in history_method

def test_business_inbound_checkpoint_is_persisted_encrypted_per_exact_shopkey_and_restored_after_restart():
    text = source()
    assert 'BusinessInboundCheckpointSettingKey = "BusinessInboundLastHistoryProbeUtcTicks"' in text
    assert "new ShopScopedSettingsStore(shop, new ShopScopedPathProvider())" in text
    assert "store.TryGetString(BusinessInboundCheckpointSettingKey, out raw)" in text
    assert "store.SetString(" in text
    assert "utcTicks.ToString(CultureInfo.InvariantCulture)" in text
    assert "EnsureBusinessInboundCheckpointLoaded(shop)" in text
    assert "_businessInboundCheckpointRestored" in text
    assert "var threshold = _businessInboundCheckpointRestored" in text
    assert "PersistBusinessInboundCheckpoint(shop, DateTime.Now)" in text


def test_restart_recovery_skips_buyer_messages_with_a_later_seller_reply_in_remote_history():
    text = source()
    assert "HasSellerReplyAfterRecoveredBuyer(" in text
    helper = text.split("private bool HasSellerReplyAfterRecoveredBuyer", 1)[1].split(
        "internal async Task RepairBusinessInboundHookAsync", 1
    )[0]
    assert "IsSellerMessage(message)" in helper
    assert "AreBuyerAliasesEquivalentInShop(seller, message.toid.nick, buyer)" in helper
    assert "sellerSort > buyerSort" in helper
    assert "sellerSort < nextBuyerSort" in helper
    method = text.split("internal async Task ProbeActiveConversationForMissedInboundAsync()", 1)[1]
    assert "business-inbound-recovery-skipped-already-answered" in method
    assert "var unanswered = candidates" in method
    assert "var missedToReplay = missedCandidates" in method


def test_durable_checkpoint_is_not_advanced_past_an_unanswered_or_inflight_buyer_turn():
    text = source()
    method = text.split("internal async Task ProbeActiveConversationForMissedInboundAsync()", 1)[1]
    assert "if (unanswered.Count == 0)" in method
    assert "Do not advance past an unanswered buyer turn" in method
    assert "if (!_businessInboundCheckpointRestored)" in method
    assert "PersistBusinessInboundCheckpoint(shop, safeBaseline)" in method
    assert "BusinessInboundMaxCatchupLookback = TimeSpan.FromMinutes(45)" in text
