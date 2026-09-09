from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
NATIVE = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.NativeSend.cs"
PLATFORM = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.PlatformSendGuard.cs"
RELIABLE = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.ReliableSend.cs"
RPA = ROOT / "src" / "Bot" / "ChromeNs" / "QNRpa.cs"
QN = ROOT / "src" / "Bot" / "ChromeNs" / "QN.cs"
WATCHDOG = ROOT / "src" / "Bot" / "ChromeNs" / "SendDeliveryWatchdog.cs"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_native_send_accepts_verified_submission_instead_of_echo_only_retry():
    native = read(NATIVE)
    platform = read(PLATFORM)
    assert "WaitForTextSubmissionAcceptedAsync" in native
    assert '"CDP页面发送按钮", 1700' in native
    assert '"发送按钮HWND安全消息", 1800' in native
    assert '"发送按钮左侧UIA安全调用（原生前置）", 1800' in native
    assert "WaitForTextSendConfirmedAsync" not in native
    assert "禁止重复写入" in native
    assert "禁止因实时回显缺失重新写入同一文本" in platform
    first_empty = platform.index("emptyObserved = true")
    stable_empty = platform.index("稳定清空确认", first_empty)
    buyer_check = platform.index("提交后会话确认", stable_empty)
    watchdog_mark = platform.index("SendDeliveryWatchdog.MarkSubmissionAccepted", buyer_check)
    success = platform.index("发送提交确认成功", watchdog_mark)
    assert first_empty < stable_empty < buyer_check < watchdog_mark < success
    assert "VerifyCurrentBuyerWithoutNavigationAsync" in platform


def test_service_attitude_reminder_is_read_only_and_fail_closed():
    text = read(PLATFORM)
    assert "服务态度提醒" in text
    assert "继续发送" in text
    assert "GetBoundedServiceAttitudeReadProbeAsync" in text
    assert "_serviceAttitudeReadProbeTask" in text
    assert "Task.WhenAny(" in text
    assert "不会自动点击“继续发送”" in text
    assert "result.Continued = false;" in text
    assert ".AsButton().Invoke()" not in text
    assert "千牛服务态度提醒已自动点击“继续发送”" not in text
    assert "ArmLateServiceAttitudeContinuationWatch" in text
    assert "Task.Delay(650)" in text
    assert "迟到服务态度提醒单次监控" in text
    assert "for (var attempt = 0; attempt < 8; attempt++)" not in text


def test_stale_answer_is_non_retryable_before_reliable_retry_loop():
    reliable = read(RELIABLE)
    rpa = read(RPA)
    qn = read(QN)
    assert "买家已发送更新消息，旧答案不会发送" in rpa
    assert "旧答案发送/重试已取消" in rpa
    assert "IsNonRetryableStaleAnswer" in reliable
    assert 'IndexOf("买家已发送更新消息"' in reliable
    assert 'IndexOf("旧答案不会发送"' in reliable
    assert "LastSendWasCancelled = IsNonRetryableStaleAnswer" in reliable
    assert "可靠发送层必须立即停止重试" in reliable
    send = qn.index("var ok = await SendTextAsync(buyer, text)")
    cancel = qn.index("if (!ok && rpa.LastSendWasCancelled)", send)
    retry = qn.index("自动发送失败，准备重试第", cancel)
    assert send < cancel < retry
    retry_action = qn.index("ok = await SendTextAsync(buyer, text)", retry)
    retry_cancel = qn.index("if (!ok && rpa.LastSendWasCancelled)", retry_action)
    assert retry < retry_action < retry_cancel


def test_delivery_watchdog_keeps_echo_as_best_proof_but_never_false_fails_verified_submission():
    watchdog = read(WATCHDOG)
    assert "MarkSubmissionAccepted" in watchdog
    assert "SubmissionAcceptedTicks" in watchdog
    assert "SubmissionEvidence" in watchdog
    assert "Interlocked.Exchange(ref pending.SubmissionAcceptedTicks" in watchdog
    assert "Interlocked.Read(ref pending.SubmissionAcceptedTicks)" in watchdog
    assert "HasRecentSellerEcho" in watchdog
    assert "已通过卖家消息回显确认真实发送" in watchdog
    accepted = watchdog.index("if (!delivered && submissionTicks > 0)")
    success = watchdog.index("[本店发送回显缺失但提交已确认]", accepted)
    failure = watchdog.index("if (!delivered)", accepted + 1)
    anomaly = watchdog.index("SendFailureAnomalyService.Queue", failure)
    assert accepted < success < failure < anomaly
    assert "不生成发送失败异常，不触发同文本重发" in watchdog
    assert "KnownBotAnswers[AnswerKey(seller, buyer, answer)]" in watchdog
