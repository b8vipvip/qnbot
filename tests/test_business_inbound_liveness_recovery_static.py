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
    assert "DateTime.Now.AddMinutes(-2).Ticks" in text
    assert "_businessInboundHistoryProbeLedger.TryAccept(messageKey)" in text
    assert "await ProcessIncomingMessageAsync(message)" in text
    assert "business-event-stale" in text
    assert "unseen-buyer-message-detected" in text
    assert "recovered-inbound-replay" in text


def test_history_fallback_does_not_hijack_visible_chat():
    text = source()
    history_method = text.split("internal async Task ProbeActiveConversationForMissedInboundAsync()", 1)[1]
    assert "OpenChat(" not in history_method
    assert "Task.Delay(180)" in history_method
    assert "businessInboundLivenessStableProbe" in history_method
