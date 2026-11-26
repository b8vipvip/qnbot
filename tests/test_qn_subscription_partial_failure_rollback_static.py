from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_multi_shop_coordinator_rolls_back_all_event_handlers_if_registration_fails():
    source = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")
    subscribe = source.split("private static void Subscribe(QN qn)", 1)[1].split(
        "private static void Unsubscribe(QN qn)", 1
    )[0]
    refresh = source.split("private static void Refresh()", 1)[1].split(
        "private static void QueueActiveShopProbe", 1
    )[0]
    unsubscribe = source.split("private static void Unsubscribe(QN qn)", 1)[1].split(
        "private static void Qn_EvSellerSwitched", 1
    )[0]
    assert "Subscribe(qn);" in refresh
    assert "Unsubscribe(qn);" in refresh
    assert "EvSellerSwitched += Qn_EvSellerSwitched" in subscribe
    assert "EvBuyerSwitched += Qn_EvBuyerSwitched" in subscribe
    assert "EvRecieveNewMessage += Qn_EvRecieveNewMessage" in subscribe
    assert "EvSellerSwitched -= Qn_EvSellerSwitched" in unsubscribe
    assert "EvBuyerSwitched -= Qn_EvBuyerSwitched" in unsubscribe
    assert "EvRecieveNewMessage -= Qn_EvRecieveNewMessage" in unsubscribe


def test_multi_event_bridges_detach_partial_subscriptions_before_retry():
    cases = [
        ("src/Bot/ChromeNs/BuyerIdentityAliasRuntimeBridge.cs", "messageAttempted", "buyerSwitchAttempted", "sellerSwitchAttempted", "EvRecieveNewMessage -= OnReceiveNewMessage", "EvBuyerSwitched -= OnBuyerSwitched", "EvSellerSwitched -= OnSellerSwitched"),
        ("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs", "receiveAttempted", "notifyAttempted", None, "EvRecieveNewMessage -= OnReceiveNewMessage", "EvMessageNotity -= OnMessageNotify", None),
    ]
    for path, first, second, third, first_remove, second_remove, third_remove in cases:
        source = read(path)
        assert first in source
        assert second in source
        assert first_remove in source
        assert second_remove in source
        if third:
            assert third in source
            assert third_remove in source


def test_single_event_bridges_remove_handler_when_marker_registration_fails():
    cases = [
        ("src/Bot/ChromeNs/ReturningBuyerFirstReplyBridge.Bootstrap.cs", "EvRecieveNewMessage += OnMessage", "EvRecieveNewMessage -= OnMessage"),
        ("src/Bot/ChromeNs/OrderNotificationTraceBridge.cs", "EvMessageNotity += OnMessageNotify", "EvMessageNotity -= OnMessageNotify"),
        ("src/Bot/ChromeNs/RechargeStatusAutoQueryService.cs", "EvRecieveNewMessage += OnReceiveNewMessage", "EvRecieveNewMessage -= OnReceiveNewMessage"),
        ("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs", "EvRecieveNewMessage += Qn_EvRecieveNewMessage", "EvRecieveNewMessage -= Qn_EvRecieveNewMessage"),
        ("src/Bot/Knowledge/KnowledgeCenterWindow.cs", "EvRecieveNewMessage += OnReceive", "EvRecieveNewMessage -= OnReceive"),
        ("src/Bot/ShopScope/ShopScopedRuntimeBridge.cs", "EvBuyerSwitched += Qn_EvBuyerSwitched", "EvBuyerSwitched -= Qn_EvBuyerSwitched"),
    ]
    for path, add, remove in cases:
        source = read(path)
        assert add in source
        assert remove in source
        assert "subscriptionAttempted = true;" in source
