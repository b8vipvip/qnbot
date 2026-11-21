from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_order_fixed_preset_continues_after_manual_takeover_but_skips_only_exactly_satisfied_segment():
    source = text("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "SendOrderPresetAnswerAsync" in source
    assert "SendMandatoryOrderTextAsync" in source
    assert "KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, text)" in source
    assert "IsOrderPresetSegmentAlreadySatisfiedAsync" in source
    assert "VerifySellerEchoInRemoteHistoryAsync" in source
    assert "BotOutboundMessageFormatter.StripAiMarker(text)" in source
    assert "result.SatisfiedSegments++" in source
    satisfied_at = source.index("result.SatisfiedSegments++")
    send_log_at = source.index('Log.Info("下单固定预设分段强制自动发送', satisfied_at)
    assert "continue;" in source[satisfied_at:send_log_at]
    assert "OrderPresetSegmentOutcome.CancelledByManual" not in source
    assert "停止本段及全部剩余分段" not in source
    assert "manualReplyDoesNotSuppress=true" in source

def test_active_manual_takeover_does_not_cancel_configured_order_rule():
    source = text("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    process = source[source.index("private async Task ProcessOrderPlacedReplyAsync"):]
    assert "ResponseProgressTracker.HasActiveManualIntervention" not in process
    assert "ShouldSuppressOrderPresetBeforeSend" not in process
    assert "订单自动回复规则强制执行" in process

def test_manual_intervention_still_blocks_ordinary_ai_replies():
    rpa = text("src/Bot/ChromeNs/QNRpa.cs")
    reliable = text("src/Bot/ChromeNs/QNRpa.ReliableSend.cs")
    qn = text("src/Bot/ChromeNs/QN.cs")
    assert "TryCancelForManualTakeover(buyer, text, shopKey, out manualBypassConsumed)" in rpa
    block = rpa[rpa.index("private bool TryCancelForManualTakeover"):][:900]
    assert "KnowledgeLearningService.TryBlockForManualReply(" in block
    assert 'SetSendCancellation("人工接管"' in block
    assert 'SetSendCancellation("人工接管"' in block
    assert "LastSendWasCancelled" in reliable
    assert "自动发送因人工接管取消，禁止重试" in qn
