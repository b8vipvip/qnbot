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

    # The recovery never accepts an arbitrary overlay target: the constrained target must still
    # resolve back to the exact seller root before any message is posted.
    assert "constrainedRoot == expectedRoot" in text
    assert "安全点不属于当前已验证卖家根窗口" in text


def test_service_attitude_read_probe_is_bounded_cached_and_cannot_block_send_mainline():
    text = read(PLATFORM)

    call = text.index("GetBoundedServiceAttitudeReadProbeAsync(buyer, stage)")
    action_gate = text.index("_serviceAttitudeProbeGate.WaitAsync(0)", call)
    assert call < action_gate

    assert "PlatformReadProbeTimeoutMs = 650" in text
    assert "_serviceAttitudeReadProbeTask" in text
    assert "_serviceAttitudeReadProbeTask == null || _serviceAttitudeReadProbeTask.IsCompleted" in text
    assert "Task.WhenAny(" in text
    assert "Task.Delay(PlatformReadProbeTimeoutMs)" in text
    assert "已放行发送主链且复用同一后台探测避免UIA堆积" in text

    # Read-only workers may be abandoned safely after the bounded wait, but the side-effectful
    # unique Continue Invoke must still never race a timeout (the 1.1.1189 ghost-click regression).
    invoke = text.index("InvokeServiceAttitudeContinue(detected)")
    assert "Task.WhenAny(action" not in text
    assert "自动点击“继续发送”超时" not in text
    assert invoke > action_gate


def test_stale_answer_cancellation_clears_only_exact_bot_owned_draft():
    text = read(RELIABLE)

    classify = text.index("LastSendWasCancelled = IsNonRetryableStaleAnswer")
    cleanup_call = text.index("ClearCancelledExactBotDraftIfPresent()", classify)
    helper = text.index("private void ClearCancelledExactBotDraftIfPresent()")
    exact_before = text.index("EditorMatchesExpectedText(current, expected)", helper)
    focus = text.index("FocusEditor()", exact_before)
    exact_after = text.index("EditorMatchesExpectedText(focusedCurrent, expected)", focus)
    clear = text.index("PressCtrlA()", exact_after)
    assert classify < cleanup_call < helper < exact_before < focus < exact_after < clear
    assert "LastSetPlainText = string.Empty" in text[helper:]
    assert "避免旧答案滞留千牛输入框" in text


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

    # The old duplicate 55-second V2 age authority caused a split lifecycle. BuyerSessionAgent is
    # now the only terminal-age owner and the local bridge must publish Ready explicitly only after
    # the authoritative answer has been fully materialized.
    assert "MaxDirectReplyAgeSeconds" not in text
    assert 'lease.MarkReady("knowledge_v2_answer_materialized")' in text
    mark_ready = text.index('lease.MarkReady("knowledge_v2_answer_materialized")')
    ready = text.index("ResponseProgressTracker.SetAnswerReady", mark_ready)
    assert mark_ready < ready
    assert 'lease.MarkSending("knowledge_v2_send_started")' in text
    assert "burst.BuyerNick, answer, 1, lease.CancellationToken" in text
