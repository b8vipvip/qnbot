from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_service_attitude_prompt_is_a_hard_fail_closed_platform_signal():
    guard = read("src/Bot/ChromeNs/QNRpa.PlatformSendGuard.cs")
    assert "服务态度提醒" in guard
    assert "继续发送" in guard
    assert 'SetSendCancellation("平台发送拦截"' in guard
    assert "不会自动点击“继续发送”" in guard
    assert "安全策略禁止Bot自动点击“继续发送”" in guard
    assert "result.Continued = false;" in guard
    assert "result.ContinueButton.AsButton().Invoke()" not in guard
    assert ".AsButton().Invoke()" not in guard
    assert "千牛服务态度提醒已自动点击“继续发送”" not in guard


def test_native_send_uses_submission_guard_between_authoritative_actions():
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    guard = read("src/Bot/ChromeNs/QNRpa.PlatformSendGuard.cs")
    first = native.index('StopIfPlatformSendBlockedAsync(buyer, "发送前")')
    cdp = native.index("TryTriggerSendViaCdpDomAsync", first)
    cdp_confirm = native.index('buyer, text, sendStart, "CDP页面发送按钮", 1700', cdp)
    hwnd = native.index("TryPostSafeMainSendMouseMessage", cdp_confirm)
    hwnd_confirm = native.index('buyer, text, sendStart, "发送按钮HWND安全消息", 1800', hwnd)
    safe_uia = native.index("TryInvokeCachedSendButtonNow", hwnd_confirm)
    uia_confirm = native.index('buyer, text, sendStart, "发送按钮左侧UIA安全调用（原生前置）", 1800', safe_uia)
    legacy_uia = native.index("TrySendTextViaUiaAsync", uia_confirm)
    after_uia = native.index('StopIfPlatformSendBlockedAsync(buyer, "UIA发送后")', legacy_uia)
    assert first < cdp < cdp_confirm < hwnd < hwnd_confirm < safe_uia < uia_confirm < legacy_uia < after_uia
    stable_probe = guard.index('buyer, method + "稳定清空后平台确认"')
    buyer_check = guard.index('buyer, method + "提交后会话确认"', stable_probe)
    watchdog = guard.index("SendDeliveryWatchdog.MarkSubmissionAccepted", buyer_check)
    success = guard.index("发送提交确认成功", watchdog)
    assert stable_probe < buyer_check < watchdog < success
    assert "Task.Delay(650)" in guard
    assert "迟到服务态度提醒单次监控" in guard
    assert "for (var attempt = 0; attempt < 8; attempt++)" not in guard


def test_hwnd_sender_never_clicks_a_modal_or_other_sibling_root_window():
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    block = native[native.index("if (root != expectedRoot)"):][:900]
    assert "安全点不属于当前已验证卖家根窗口" in block
    assert "HWND安全发送已阻止跨根窗口点击" in block
    assert "return false;" in block
    helper = native.index("if (targetPid != expectedPid)", native.index("if (root != expectedRoot)"))
    post = native.index("PostMessage(target, WmLButtonDown", helper)
    assert native.index("if (root != expectedRoot)") < helper < post
    assert "允许同一千牛进程的独立根窗口" not in block
    qn = read("src/Bot/ChromeNs/QN.cs")
    assert "if (!ok && rpa.LastSendWasCancelled)" in qn
    assert "禁止重试" in qn
