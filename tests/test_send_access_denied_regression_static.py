from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
NATIVE = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.NativeSend.cs"
QNRPA = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.cs"
PLATFORM = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.PlatformSendGuard.cs"


def _native() -> str:
    return NATIVE.read_text(encoding="utf-8-sig")


def _qnrpa() -> str:
    return QNRPA.read_text(encoding="utf-8-sig")


def _platform() -> str:
    return PLATFORM.read_text(encoding="utf-8-sig")


def test_qianniu_helper_pid_is_only_trusted_under_exact_verified_seller_root():
    text = _native()

    # The seller desk root remains the trust anchor. A helper PID can only be used after the
    # verified root HWND still belongs to the bound seller process and the point target resolves
    # back to that exact root. An external overlay may be bypassed only by resolving the same
    # verified safe point from inside expectedRoot and re-proving that exact root.
    assert "GetWindowThreadProcessId(expectedRoot, out rootPid)" in text
    assert "rootPid == 0 || rootPid != expectedPid" in text
    assert "GetAncestor(target, GaRoot)" in text
    assert "ResolveTargetInsideVerifiedSellerRoot" in text
    assert "constrainedRoot == expectedRoot" in text
    assert "if (root != expectedRoot)" in text
    assert "if (targetPid != expectedPid)" in text
    assert "HWND安全发送已验证千牛辅助进程子窗口" in text

    root_owner = text.index("GetWindowThreadProcessId(expectedRoot, out rootPid)")
    target_root = text.index("GetAncestor(target, GaRoot)", root_owner)
    constrained = text.index("ResolveTargetInsideVerifiedSellerRoot(expectedRoot, screenPoint)", target_root)
    root_match = text.index("if (root != expectedRoot)", constrained)
    helper_accept = text.index("if (targetPid != expectedPid)", root_match)
    post_message = text.index("PostMessage(target, WmLButtonDown", helper_accept)
    assert root_owner < target_root < constrained < root_match < helper_accept < post_message


def test_safe_uia_main_action_runs_before_legacy_physical_coordinate_fallback():
    native = _native()
    qnrpa = _qnrpa()

    # AccessDenied from FlaUI Mouse.Click must not be required before trying the proven UIA
    # left/main action. The native pipeline invokes the safe child before entering the legacy
    # physical-coordinate path.
    hwnd = native.index("TryPostSafeMainSendMouseMessage")
    safe_uia = native.index("TryInvokeCachedSendButtonNow", hwnd)
    legacy_uia = native.index("TrySendTextViaUiaAsync(\n                buyer, text, sendStart, manualTakeoverCheck)", safe_uia)
    assert hwnd < safe_uia < legacy_uia
    assert "发送按钮左侧UIA安全调用（原生前置）" in native

    # Preserve the split-button safety invariant: never restore direct Invoke on the cached whole
    # send button. Only the previously verified left/main child is allowed to invoke.
    assert "_sendMessageButton.AsButton().Invoke()" not in qnrpa
    assert "TryInvokeSafeMainSendCandidate" in qnrpa
    assert "almostWholeSplit" in qnrpa
    assert "protectedArrowStart" in qnrpa


def test_every_new_prephysical_send_transition_revalidates_owned_draft_and_submission():
    native = _native()
    platform = _platform()

    assert "安全UIA回退前确认" in native
    assert "物理/UIA兼容回退前确认" in native
    assert native.count("HasExpectedDraftFastAsync(text") >= 6

    # Do not restore the echo-only confirmation that caused production duplicate sends after an
    # already-consumed draft. Every authoritative action now shares submission-aware confirmation.
    assert "WaitForTextSendConfirmedAsync" not in native
    assert native.count("WaitForTextSubmissionAcceptedAsync") >= 4
    assert "稳定清空确认" in platform
    assert "提交后会话确认" in platform
    assert "禁止因实时回显缺失重新写入同一文本" in platform
