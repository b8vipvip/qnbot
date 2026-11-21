from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def method_body(code: str, method_name: str, next_method_name: str) -> str:
    start = code.index(method_name)
    end = code.index(next_method_name, start)
    return code[start:end]


def test_bot_overlay_is_filtered_to_verified_reception_chat_windows():
    scanner = read("src/Bot/ControllerNs/DeskScanner.cs")
    verify = method_body(
        scanner,
        "private static bool IsVerifiedReceptionChatWindow",
        "private static void DetectQianniu",
    )

    assert ".Where(IsVerifiedReceptionChatWindow)" in scanner
    assert 'title.Equals("千牛接待台"' in verify
    assert 'title.IndexOf("接待"' in verify
    assert 'title.IndexOf("客服"' in verify
    assert 'title.IndexOf("千牛"' not in verify
    assert "MatchUniqueSellerFromTitle" not in verify
    assert "rect.Width" not in verify
    assert "seller identity and window size" in verify.lower()
    assert "desk.Dispose();" in scanner
    assert "已离开接待聊天窗口" in scanner


def test_missing_active_conversation_is_not_reported_as_cdp_failure():
    monitor = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    probe = method_body(
        monitor,
        "private static async Task ProbeCurrentConversationAsync",
        "private static bool HasVerifiedReceptionDesk",
    )
    neutral = method_body(
        monitor,
        "private static void RecordNoActiveChat",
        "private static string ReadConversationNick",
    )

    assert "HasVerifiedReceptionDesk" in monitor
    assert "DeskSellerBindingRegistry.FindSellerDesk(seller, shopKey)" in monitor
    assert 'RecordNoActiveChat(qn, seller, "接待窗口在线，但当前没有选中的买家会话")' in probe
    assert 'RecordProbeFailure(qn, "im.uiutil.GetCurrentConversationID 返回空值")' not in monitor
    assert "ConsecutiveProbeFailures.TryRemove(qn, out failures);" in neutral
    assert "BotConnectionDiagnostics.RecordCdpStatus(true" in neutral
    assert "当前没有需要探测的活动聊天会话" in neutral


def test_probe_is_paused_when_no_verified_reception_desk_exists():
    monitor = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    schedule = method_body(
        monitor,
        "private static void ScheduleConversationProbe",
        "private static async Task ProbeCurrentConversationAsync",
    )

    assert "!HasVerifiedReceptionDesk(seller, shop.ShopKey)" in schedule
    assert "未检测到当前ShopKey对应的千牛接待聊天窗口" in schedule
    assert "RecordNoActiveChat" in schedule
