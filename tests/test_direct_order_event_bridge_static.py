from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_direct_order_bridge_is_initialized_and_compiled():
    bridge = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    app = read("src/Bot/App.xaml.cs")
    targets = read("src/Directory.Build.targets")

    assert "internal static class DirectOrderEventBridge" in bridge
    assert "DirectOrderEventBridge.Initialize();" in app
    assert "ChromeNs\\DirectOrderEventBridge.cs" in targets


def test_system_order_events_are_observed_before_normal_buyer_role_filtering():
    bridge = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")

    assert "qn.EvRecieveNewMessage += OnReceiveNewMessage;" in bridge
    assert "qn.EvMessageNotity += OnMessageCenterNotify;" in bridge
    assert "ProcessDirectOrderMessageAsync" in bridge
    assert "receiveNewMsg系统订单卡片" in bridge
    assert "messageCenterNotify订单通知" in bridge
    assert "OrderPlacedAutoReplyService.TryCreatePlan" in bridge
    assert "ResolveRuntimeBySeller(this).ShopKey" in bridge
    assert "ShopKey = ShopContextLocator.ResolveRuntimeBySeller(this).ShopKey" in bridge
    assert "await ProcessOrderPlacedReplyAsync(plan);" in bridge


def test_direct_order_bridge_resolves_composite_seller_and_buyer_identity():
    bridge = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")

    assert "DirectOrderIdentityResolver" in bridge
    assert "IdentityEquals" in bridge
    assert "var ah = a.Split(':')[0];" in bridge
    assert "var bh = b.Split(':')[0];" in bridge
    assert 'path.Contains("buyer")' in bridge
    assert 'path.Contains("conversation")' in bridge
    assert 'path.Contains("contact")' in bridge


def test_message_center_order_notification_requires_real_order_id_and_is_deduplicated():
    bridge = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")

    assert "OrderIdTextRegex" in bridge
    assert "LooksPotential" in bridge
    assert "RawReservations" in bridge
    assert "DateTime.Now.AddMinutes(2)" in bridge
    assert "买家已付款" in bridge
    assert "买家已下单" in bridge
    assert "订单号：" in bridge


def test_order_bridge_does_not_guess_target_when_buyer_identity_is_missing():
    bridge = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")

    assert "消息中心检测到订单通知但缺少买家昵称" in bridge
    assert "检测到疑似订单系统卡片但无法解析客服/买家身份" in bridge
    assert "if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(buyer))" in bridge
