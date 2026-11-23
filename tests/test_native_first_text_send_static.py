from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_text_send_routes_through_native_first_pipeline_before_legacy_uia():
    qnrpa = read("src/Bot/ChromeNs/QNRpa.cs")
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")

    assert "sendResult = await TrySendTextNativeFirstAsync(" in qnrpa
    assert "buyer, text, sendStart, shopKey, manualTakeoverCheck, preSendGuard" in qnrpa
    assert "sendResult = await TrySendTextViaUiaAsync(buyer, text, sendStart)" not in qnrpa
    assert "TryTriggerSendViaCdpDomAsync" in native
    assert "TryPostSafeMainSendMouseMessage" in native
    assert "TrySendTextViaUiaAsync(" in native
    assert "buyer, text, sendStart, manualTakeoverCheck" in native

    dom = native.index("TryTriggerSendViaCdpDomAsync")
    hwnd = native.index("TryPostSafeMainSendMouseMessage", dom)
    uia = native.index("TrySendTextViaUiaAsync(", hwnd)
    assert dom < hwnd < uia


def test_native_send_never_guesses_undocumented_imsdk_send_api_or_uses_enter():
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")

    assert "EvaluateExpressionAsync" in native
    assert "__QNBOT_DOM_SEND__:clicked:" in native
    assert "intelligentservice.SendSmartTipMsg" not in native
    assert "imsdk.invoke" not in native
    assert "keybd_event" not in native
    assert "PressEnter" not in native
    assert "Ctrl+Enter" not in native


def test_dom_discovery_requires_exact_send_label_and_rejects_dropdown_identity():
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")

    assert "s==='发送'||s==='發送'||s.toLowerCase()==='send'" in native
    assert "arrow|dropdown|drop-down|menu|more|chevron|downbutton|下拉|展开" in native
    assert "button,[role=button],[aria-label],[title]" in native


def test_hwnd_fallback_targets_same_verified_seller_root_and_left_safe_region():
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")

    # WindowFromPoint must inspect the seller window, not a Bot/settings window that happens
    # to cover the same screen coordinate when a diagnostic send is launched.
    assert "desk.BringTop();" in native
    assert "WindowFromPoint" in native
    assert native.index("desk.BringTop();") < native.index("WindowFromPoint(screenPoint)")
    assert "GetAncestor(target, GaRoot)" in native
    assert "expectedRoot = new IntPtr(desk.Hwnd.Handle)" in native
    assert "if (root != expectedRoot)" in native
    assert "arrowGuard = Math.Max(18, Math.Min(30, rect.Width / 3))" in native
    assert "PostMessage(target, WmLButtonDown" in native
    assert "PostMessage(target, WmLButtonUp" in native


def test_each_fallback_revalidates_exact_owned_draft_and_reports_integrity_mismatch():
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    guard = read("src/Bot/ChromeNs/QNRpa.PlatformSendGuard.cs")

    assert native.count("HasExpectedDraftFastAsync(text") >= 4
    # Real production logs proved echo-only confirmation can falsely return failure after Qianniu
    # already consumed the draft. Native actions must now use submission-aware confirmation.
    assert "WaitForTextSubmissionAcceptedAsync" in native
    assert "WaitForTextSendConfirmedAsync" not in native
    assert "稳定清空确认" in guard
    assert "提交后会话确认" in guard
    assert "targetHigherIntegrity=" in native
    assert "TokenIntegrityLevel" in native
    assert 'return "High"' in native
    assert "Windows输入权限诊断" in native


def test_native_send_helper_is_compiled_for_bot_and_wpf_temp_projects():
    props = read("src/Bot/Directory.Build.props")
    assert "ChromeNs\\QNRpa.NativeSend.cs" in props


def test_dom_send_is_pinned_to_the_verified_authoritative_shop_session():
    native = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    send = native[
        native.index("private async Task<bool> TrySendTextNativeFirstAsync"):
        native.index("private bool IsExpectedShopSessionCurrent")
    ]
    assert send.index("VerifyShopIdentityBeforeSendAsync(shopKey)") < send.index("TryTriggerSendViaCdpDomAsync")
    assert "TryTriggerSendViaCdpDomAsync(buyer, shopKey, sourceClient)" in send
    assert "禁止其他发送回退以避免重复发送" in send

    authority = native[
        native.index("private bool IsExpectedShopSessionCurrent"):
        native.index("private async Task<bool> TryTriggerSendViaCdpDomAsync")
    ]
    assert "ReferenceEquals(_qn.CDP, sourceClient)" in authority
    assert "sourceClient.IsInvalidated" in authority
    assert "sourceClient.IsShopIdentityRetired" in authority
    assert "server.IsAuthoritativeShopSession(expectedShopKey, sourceClient.SessionId)" in authority

    dom = native[
        native.index("private async Task<bool> TryTriggerSendViaCdpDomAsync"):
        native.index("private bool TryPostSafeMainSendMouseMessage")
    ]
    assert "sourceClient.EvaluateExpressionAsync" in dom
    assert "_qn.CDP.EvaluateExpressionAsync" not in dom
