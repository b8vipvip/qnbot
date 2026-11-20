from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_configured_order_reply_is_not_suppressed_by_human_reply():
    code = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    process = code[code.index("private async Task ProcessOrderPlacedReplyAsync"):]
    segment = code[code.index("private async Task<bool> SendMandatoryOrderTextAsync"):code.index("private async Task ProcessOrderPlacedReplyAsync")]

    assert "manualReplyDoesNotSuppress=true" in code
    assert "KnowledgeLearningService.AllowNextManualSend" in segment
    assert "SendTextWithRetryAsync(plan.Buyer, text, 0)" in segment
    assert "HasActiveManualIntervention" not in process
    assert "ShouldSuppressOrderPresetBeforeSend" not in process
    assert "OrderGuidanceDeliveryGuard.ShouldSuppressBeforeSend" not in process
    assert "SatisfiedByManual" not in code
    assert "CancelledByManual" not in code
    assert "人工客服已完成固定预设" not in code
    assert "订单自动回复规则强制执行" in process


def test_order_reply_still_requires_enabled_rule_and_global_auto_send():
    code = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "cfg == null || !cfg.EnableOrderPlacedReply" in code
    assert "Params.Robot.GetIsAutoReply()" in code
    assert "Reservations.TryGetValue" in code
    assert "OrderEventHub.Publish(snapshot)" in code
    assert "ShopKey = shopKey" in code
    assert "BuildReservationKey(string shopKey, string seller" in code
    assert "SameShopScope(record.ShopKey, plan.ShopKey)" in code
