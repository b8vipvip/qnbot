from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def assert_each_session_isolated(path, method_start, method_end, log_marker):
    source = read(path)
    method = source.split(method_start, 1)[1].split(method_end, 1)[0]
    assert "foreach (var qn" in method
    assert "try" in method
    assert "catch (Exception ex)" in method
    assert log_marker in method


def test_multi_shop_coordinator_continues_after_one_qn_failure():
    source = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")
    method = source.split("private static void Refresh()", 1)[1].split(
        "private static void QueueActiveShopProbe", 1
    )[0]
    assert "foreach (var qn in snapshot)" in method
    assert "单个千牛会话多店铺协调失败，继续处理其他店铺" in method
    assert 'ActiveShopSessionRegistry.ReassertCurrent("runtime-refresh")' in method
    assert "QueueActiveShopProbe(snapshot)" in method


def test_core_qn_event_attach_bridges_isolate_each_session_failure():
    cases = [
        ("src/Bot/ChromeNs/BuyerIdentityAliasRuntimeBridge.cs", "private static void Attach()", "private static void OnReceiveNewMessage", "跳过异常千牛会话的买家昵称别名订阅并继续"),
        ("src/Bot/ChromeNs/ReturningBuyerFirstReplyBridge.Bootstrap.cs", "private static void Tick()", "    }\n}", "跳过异常千牛会话的回访首答订阅并继续"),
        ("src/Bot/ChromeNs/OrderNotificationTraceBridge.cs", "private static void Attach()", "private static async void OnMessageNotify", "跳过异常千牛会话的订单通知订阅并继续"),
        ("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs", "private static void Attach()", "private static void OnReceiveNewMessage", "跳过异常千牛会话的订单模板 V2 订阅并继续"),
        ("src/Bot/ChromeNs/RechargeStatusAutoQueryService.cs", "private static void Attach()", "private static void OnReceiveNewMessage", "跳过异常千牛会话的充值查询订阅并继续"),
        ("src/Bot/Knowledge/KnowledgeCenterWindow.cs", "private static void Attach()", "private static void OnReceive", "跳过异常千牛会话的人工接管AI即时对比订阅并继续"),
    ]
    for path, start, end, marker in cases:
        source = read(path)
        method = source.split(start, 1)[1].split(end, 1)[0]
        assert "foreach (var qn" in method, path
        assert "catch (Exception ex)" in method, path
        assert marker in method, path


def test_safety_and_business_liveness_monitors_keep_processing_other_shops():
    safety = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    refresh = safety.split("private static void Refresh()", 1)[1].split(
        "private static void CleanupRetiredQnState", 1
    )[0]
    assert "foreach (var qn in sessions)" in refresh
    assert "跳过异常千牛会话的安全监控并继续处理其他店铺" in refresh
    assert "CleanupRetiredQnState(sessions)" in refresh
    assert "WriteHeartbeatIfDue()" in refresh

    liveness = read("src/Bot/ChromeNs/BusinessInboundLivenessRecoveryBridge.cs")
    tick = liveness.split("private static void Tick()", 1)[1].split(
        "private static void CleanupRetiredQnState", 1
    )[0]
    assert "foreach (var qn in sessions)" in tick
    assert "跳过异常千牛会话的业务入站恢复调度并继续" in tick
    assert "CleanupRetiredQnState(sessions)" in tick


def test_order_field_priority_adjustment_isolated_per_qn():
    source = read("src/Bot/ChromeNs/OrderTemplateRequiredFieldsPriority.cs")
    reorder = source.split("private static void ReorderAll()", 1)[1].split(
        "public partial class QN", 1
    )[0]
    assert "foreach (var qn in QN.GetRuntimeSafetySnapshot())" in reorder
    assert "跳过异常千牛会话的订单字段优先级调整并继续" in reorder
