from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_background_recovery_waits_are_bounded_and_retryable():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")

    assert "BackgroundRecoverySendGateWaitMs" in source
    assert "BackgroundRecoveryGateWaitMs" in source
    assert "BackgroundRecoveryMaxAttempts" in source
    assert "_sendGate.WaitAsync(BackgroundRecoverySendGateWaitMs)" in source
    assert "_backgroundRecoveryGate.WaitAsync(BackgroundRecoveryGateWaitMs)" in source
    assert "后台消息补偿本轮尚未恢复到可处理消息，将重试" in source
    assert "await _backgroundRecoveryGate.WaitAsync();" not in source
    assert "await _sendGate.WaitAsync();" not in source


def test_missing_detailed_event_auto_switches_and_refetches_history():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")

    assert "BackgroundRecoveryInitialDelayMs = 1000" in source
    assert "BackgroundRecoveryPostSwitchHydrationDelayMs" in source
    assert "Bot准备自动切换目标买家并补抓历史" in source
    assert "OpenChat(buyer);" in source
    assert '"backgroundRecoveryAutoSwitch"' in source
    assert 'cdp.Invoke<JObject>("im.singlemsg.GetRemoteHisMsg"' in source
    assert "后台消息补偿已自动切换到目标买家" in source
    assert "等待会话消息加载后补抓历史" in source


def test_empty_history_after_successful_switch_does_not_complete_recovery():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")

    start = source.index("if (recovered == null || recovered.Count < 1)")
    block = source[start: start + 700]
    assert "没有发现最近买家消息或订单卡片，将继续重试" in block
    assert "return false;" in block
    assert "return true;" not in block


def test_live_detailed_event_still_cancels_pending_recovery_without_replay():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")

    assert "MarkBuyerMessageObserved" in source
    assert "_backgroundRecoveryVersions.TryRemove" in source
    assert "后台消息补偿处理前检测到详细买家事件已到，取消历史重放" in source


def test_bypass_recovery_cannot_requeue_a_message_already_handled_by_authority():
    qn = read("src/Bot/ChromeNs/QN.cs")
    recovery = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")

    assert "_handledBuyerMessageDeduplicator" in qn
    assert "_handledBuyerMessageDeduplicator.TryAccept(messageKey)" in qn
    assert "_handledBuyerMessageDeduplicator.TryAccept(messageKey)" in recovery
    assert "后台补偿跳过已由权威业务链处理的买家消息" in recovery
    handled = qn.index("_handledBuyerMessageDeduplicator.TryAccept(messageKey)")
    order_route = qn.index("OrderPlacedAutoReplyService.TryCreatePlan(", handled)
    assert handled < order_route


def test_recovery_does_not_hold_navigation_locks_while_processing_answers():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")

    release_pos = source.index("if (recoveryGateAcquired) _backgroundRecoveryGate.Release();")
    process_pos = source.index("ProcessRecoveredMessageWithKnownBuyerAsync")
    assert release_pos < process_pos

def test_background_message_recovery_aliases_are_scoped_to_the_exact_qn_shop():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    key = source[
        source.index("private string RecoveryKey"):
        source.index("private void MarkBuyerMessageObserved")
    ]
    schedule = source[
        source.index("private void ScheduleBackgroundMessageRecovery"):
        source.index("private async Task<bool> RecoverMissedBuyerMessagesAsync")
    ]
    recovery = source[
        source.index("private async Task<bool> RecoverMissedBuyerMessagesAsync"):
        source.index("private async Task ProcessRecoveredMessageWithKnownBuyerAsync")
    ]

    assert "ShopContextLocator.ResolveRuntimeBySeller(this)" in key
    assert "shopKey + \"#\" + seller + \"#\"" in key
    assert "ShopSettingsScope.Enter(shop, false)" in key
    assert "ResolveRecoveryShop(seller)" in schedule
    assert "BuyerIdentityAliasService.Observe" in schedule
    assert "ShopSettingsScope.Enter(shop, false)" in schedule
    assert "ResolveBuyerInternalNickInShop(seller, buyer)" in recovery
    assert "AreBuyerAliasesEquivalentInShop(seller, current.Nick, buyer)" in recovery
    assert "ObserveBuyerAliasInShop(seller, current.Nick, current.Display, current.TargetId)" in recovery
    assert "ResolveBuyerConversationKeyInShop(seller, current.Nick)" in recovery
    assert "BuyerIdentityAliasService.AreEquivalent(seller, message.fromid.nick, buyer)" not in source
