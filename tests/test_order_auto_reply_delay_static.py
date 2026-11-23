from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_order_reply_delay_is_configurable_and_shop_scoped():
    settings = read("src/Bot/Options/OrderPlacedReplyDelaySettings.cs")
    options = read("src/Bot/Options/CtlRobotOptions.xaml.cs")
    assert "public const int DefaultDelaySeconds = 0" in settings
    assert "public const int MaxDelaySeconds = 300" in settings
    assert "BotFeatureStore.GetAutoReplyRules()" in settings
    assert "return Clamp(config.OrderPlacedReplyDelaySeconds);" in settings
    assert "config.OrderPlacedReplyDelaySeconds = Clamp(seconds);" in settings
    assert "BotFeatureStore.SaveAutoReplyRules(config);" in settings
    assert "_knowledgeDirectReplyDelaySeconds" in options
    assert "知识库直答延迟(秒)" in options
    assert "KnowledgeDirectReplyDelaySeconds = Math.Max(0, Math.Min(300, knowledgeDirectDelay))" in options
    assert "下单回复延迟(秒)" in options
    assert "OrderPlacedReplyDelaySeconds = Math.Max(0, Math.Min(300, orderReplyDelay))" in options
    assert "OrderPlacedReplyDelaySettings.Initialize();" in read("src/Bot/App.xaml.cs")


def test_order_reply_delay_rechecks_bot_and_exact_shop_before_sending():
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    delay_lookup = "var delaySeconds = OrderPlacedReplyDelaySettings.GetSeconds();"
    delay_guard = "if (delaySeconds > 0)"
    delay = "await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);"
    exact_shop = "!string.Equals(currentClient.ShopKey, plan.ShopKey, StringComparison.Ordinal)"
    exact_session = "!string.Equals(currentClient.SessionId, plan.SourceSessionId, StringComparison.Ordinal)"
    authority = "currentServer.IsAuthoritativeShopSession(plan.ShopKey, currentClient.SessionId)"
    source_session = "plan.SourceSessionId = sourceClient.SessionId;"
    preset_send = "presetSendResult = await SendOrderPresetAnswerAsync(plan, answer);"
    mandatory = "sendOk = await SendMandatoryOrderTextAsync(plan, answer);"
    assert delay_lookup in order and delay_guard in order and delay in order
    assert source_session in order
    assert exact_shop in order and exact_session in order and authority in order
    assert order.index(delay_lookup) < order.index(delay_guard) < order.index(delay) < order.index(exact_shop)
    assert order.index(exact_shop) < order.index(exact_session) < order.index(authority) < order.index(preset_send) < order.index(mandatory)
    process = order[order.index("private async Task ProcessOrderPlacedReplyAsync"): ]
    assert "ResponseProgressTracker.HasActiveManualIntervention" not in process
    assert "KnowledgeLearningService.AllowNextManualSend(plan.Seller, plan.Buyer, text);" in order


def test_three_delays_round_trip_through_shop_scoped_web_sync():
    sync = read("src/Bot/ChromeNs/BotWebAutoReplyRulesSyncService.cs")
    assert "BuildCurrentSettings(state.Shop)" in sync
    assert "ApplyDesiredSettings(desired, state.Shop)" in sync
    assert '"knowledge_direct_reply_delay_seconds"' in sync
    assert '"first_inquiry_fixed_reply_delay_seconds"' in sync
    assert '"order_placed_reply_delay_seconds"' in sync
    assert "cfg.KnowledgeDirectReplyDelaySeconds = v" in sync
    assert "cfg.OrderPlacedReplyDelaySeconds = v" in sync
    assert "FirstInquiryFixedReplyService.Save(" in sync
    assert "shop.SellerNick, shop.ShopKey" in sync
    assert "qianniu-bot-web-auto-reply-rules/3.0" in sync


def test_direct_order_event_is_dispatched_before_normal_message_loop_and_uses_shared_send_gate():
    qn = read("src/Bot/ChromeNs/QN.cs")
    bridge = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    event_raise = 'EvRecieveNewMessage(this, e);'
    normal_loop = 'await ProcessIncomingMessageAsync(message);'
    assert qn.index(event_raise) < qn.index(normal_loop)
    assert 'qn.EvRecieveNewMessage += OnReceiveNewMessage;' in bridge
    assert 'await qn.ProcessDirectOrderMessageAsync' in bridge
    assert 'private readonly SemaphoreSlim _sendGate' in qn
    assert 'await _sendGate.WaitAsync(cancellationToken);' in qn
