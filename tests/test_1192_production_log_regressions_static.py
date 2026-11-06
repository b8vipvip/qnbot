from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
NATIVE = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.NativeSend.cs"
PLATFORM = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.PlatformSendGuard.cs"
RELIABLE = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.ReliableSend.cs"
LOG_WRITER = ROOT / "src" / "BotLib" / "LogWriter.cs"
APP_LIFE = ROOT / "src" / "Bot" / "StartUp" / "AppLife.cs"
RUNTIME_IDENTITY = ROOT / "src" / "Bot" / "Update" / "RuntimeBuildIdentityService.cs"
KNOWLEDGE_V2 = ROOT / "src" / "Bot" / "ChromeNs" / "KnowledgeEngineV2RuntimeBridge.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_hwnd_send_can_bypass_foreign_overlay_only_inside_verified_seller_root():
    text = read(NATIVE)
    assert "ChildWindowFromPointEx" in text
    assert "ResolveTargetInsideVerifiedSellerRoot" in text
    assert "HWND安全发送已绕过外部覆盖窗口并在已验证卖家根内重新解析安全点" in text
    root_owner = text.index("GetWindowThreadProcessId(expectedRoot, out rootPid)")
    global_target = text.index("WindowFromPoint(screenPoint)", root_owner)
    constrained = text.index("ResolveTargetInsideVerifiedSellerRoot(expectedRoot, screenPoint)", global_target)
    final_root_reject = text.index("if (root != expectedRoot)", constrained)
    post = text.index("PostMessage(target, WmLButtonDown", final_root_reject)
    assert root_owner < global_target < constrained < final_root_reject < post
    assert "constrainedRoot == expectedRoot" in text
    assert "安全点不属于当前已验证卖家根窗口" in text


def test_service_attitude_read_probe_is_bounded_cached_and_cannot_block_send_mainline():
    text = read(PLATFORM)
    assert "PlatformReadProbeTimeoutMs = 650" in text
    assert "_serviceAttitudeReadProbeTask" in text
    assert "_serviceAttitudeReadProbeTask == null || _serviceAttitudeReadProbeTask.IsCompleted" in text
    assert "Task.WhenAny(" in text
    assert "Task.Delay(PlatformReadProbeTimeoutMs)" in text
    assert "已放行发送主链且复用同一后台探测避免UIA堆积" in text
    # Field complaint policy: once the platform anti-abuse reminder is actually detected, the Bot
    # fails closed and must not perform any Continue side effect.
    assert "绝不会自动点击“继续发送”" in text
    assert "ReturnModifyButton.AsButton().Invoke()" in text
    assert "ContinueButton.AsButton().Invoke()" not in text
    assert "result.Continued = false;" in text


def test_stale_answer_cancellation_clears_only_exact_bot_owned_draft():
    reliable = read(RELIABLE)
    rpa = read(ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.cs")
    classify = reliable.index("LastSendWasCancelled = IsNonRetryableStaleAnswer")
    block = reliable[classify:reliable.index("private static bool IsNonRetryableStaleAnswer")]
    # Regress the 2026-11-06 34-minute COM stall: error classification
    # must be side-effect free. Finalization performs bounded cleanup.
    assert "ClearCancelledExactBotDraftIfPresent" not in reliable
    assert "TryGetEditorText" not in block
    assert "FocusEditor" not in block

    helper = rpa[rpa.index("private async Task ClearExpectedDraftIfSafeAsync"):
                 rpa.index("private async Task<string> ReadCurrentBuyerForDraftCleanupAsync")]
    exact_before = helper.index("EditorMatchesExpectedText(currentText, expected)")
    owner_before = helper.index("IsOwnedDraftForBuyer(buyer, currentText)", exact_before)
    focus = helper.index("!FocusEditor()", owner_before)
    exact_after = helper.index("EditorMatchesExpectedText(focusedText, expected)", focus)
    owner_after = helper.index("IsOwnedDraftForBuyer(buyer, focusedText)", exact_after)
    clear = helper.index("PressCtrlA()", owner_after)
    assert exact_before < owner_before < focus < exact_after < owner_after < clear
    assert "RunUiMutationAsync(() =>" in helper
    assert "cleanupDeadlineUtc" in helper
    assert "ForgetOwnedDraft();" in helper


def test_normal_restart_keeps_active_log_and_retention_only_deletes_archives():
    text = read(LOG_WRITER)
    ctor = text.index("public LoopSaveFile(string fn")
    maintenance = text.index("MaintainLogFiles(true);", ctor)
    timer = text.index("new NoReEnterTimer", maintenance)
    assert ctor < maintenance < timer
    assert "RotatePreviousRunFileAtStartup" not in text
    assert "CreationTimeUtc <" not in text
    assert "current.Length >= _limitFileSize" in text
    assert "LogRetention = TimeSpan.FromHours(24)" in text
    assert "if (info.LastWriteTimeUtc < cutoffUtc) info.Delete();" in text
    assert "if (string.Equals(path, FileName, StringComparison.OrdinalIgnoreCase)) continue;" in text


def test_undersized_log_rotation_requires_real_version_change_and_updater_handoff():
    app = read(APP_LIFE)
    identity = read(RUNTIME_IDENTITY)
    prepare = app.index("RuntimeBuildIdentityService.PrepareRuntimeLogForStartup")
    open_log = app.index("Log.Initiate(", prepare)
    assert prepare < open_log
    assert 'UpdateHealthFileEnvironmentVariable = "QIANNIU_BOT_UPDATE_HEALTH_FILE"' in identity
    assert 'RuntimeLogVersionMarkerFileName = "runtime-log-release-version.txt"' in identity
    assert "ReadRuntimeLogVersionMarker(markerPath)" in identity
    assert "string.IsNullOrWhiteSpace(previousVersion)" in identity
    assert "TryPersistRuntimeLogVersionMarker(markerPath, currentVersion)" in identity
    assert "string.Equals(previousVersion, currentVersion" in identity
    marker_write = identity.index("if (!TryPersistRuntimeLogVersionMarker(markerPath, currentVersion))")
    updater_gate = identity.index("Environment.GetEnvironmentVariable(UpdateHealthFileEnvironmentVariable)", marker_write)
    rotate = identity.index("RotateRuntimeLogForVerifiedUpdate(activeLogPath)", updater_gate)
    assert marker_write < updater_gate < rotate
    assert "File.Move(activeLogPath, destination);" in identity
    assert "检测到真实客户端版本更新，已允许不足1024KiB的活动日志归档一次" in identity


def test_knowledge_v2_uses_session_agent_terminal_barrier_before_answer_ready():
    text = read(KNOWLEDGE_V2)
    assert "MaxDirectReplyAgeSeconds" not in text
    assert 'lease.MarkReady("knowledge_v2_answer_materialized")' in text
    mark_ready = text.index('lease.MarkReady("knowledge_v2_answer_materialized")')
    ready = text.index("ResponseProgressTracker.SetAnswerReady", mark_ready)
    assert mark_ready < ready
    assert 'lease.MarkSending("knowledge_v2_send_started")' in text
    assert "burst.BuyerNick, answer, 1, lease.CancellationToken" in text
