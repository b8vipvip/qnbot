from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RECOVERY = ROOT / "src" / "Bot" / "Update" / "BotUpdateQianniuRecovery.Fast.cs"
STARTUP = ROOT / "src" / "Bot" / "Update" / "BotUpdateStartupConnection.Fast.cs"
UPDATER = ROOT / "src" / "Bot" / "Update" / "BotAutoUpdater.ps1"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_restart_authority_requires_fresh_success_for_exact_current_version():
    text = read(RECOVERY)
    assert 'last-update-result.json' in text
    assert 'string.Equals(status, "success"' in text
    assert 'string.Equals(targetVersion, currentVersion' in text
    assert 'EvidenceMaxAge = TimeSpan.FromMinutes(20)' in text
    assert 'post-update-qn-recovery.consumed.json' in text
    assert 'TryConsumeEvidence' in text
    assert 'AtomicWrite(path' in text


def test_normal_startup_does_not_restart_qianniu_and_delegates_only_after_bounded_retries():
    text = read(STARTUP)
    assert 'RetryDelaySeconds = { 3, 5, 8, 12 }' in text
    assert text.index('for (var attempt = 0; attempt < RetryDelaySeconds.Length; attempt++)') < text.index('PostUpdateQianniuRecovery.TryRecoverAsync')
    assert 'Process.Kill' not in text
    assert 'Process.Start' not in text
    assert '不会自动重启千牛以保护登录态' in text


def test_post_update_recovery_restarts_qianniu_once_and_restores_remembered_login():
    text = read(RECOVERY)
    assert 'Process.GetProcessesByName("AliWorkbench")' in text
    assert 'process.CloseMainWindow()' in text
    assert 'process.Kill()' in text
    assert 'Process.Start(new ProcessStartInfo' in text
    assert 'TryClickRememberedAccountLogin' in text
    assert 'TryInvokeExactQianniuElement("登录", false)' in text
    assert 'TryOpenReceptionWindow' in text
    assert '"接待中心"' in text
    assert '"接待工作台"' in text


def test_previous_message_dialog_is_confirmed_only_when_prompt_is_present():
    text = read(RECOVERY)
    assert 'TryConfirmRestorePreviousMessages' in text
    assert 'TryInvokeExactQianniuElement("确认", true)' in text
    assert 'IndexOf("之前的消息"' in text
    assert 'if (!hasPrompt) continue;' in text


def test_language_status_reconciles_from_qninject_active_resource_authority():
    text = read(RECOVERY)
    startup = read(STARTUP)
    assert 'typeof(QNInject).GetMethod("GetActiveResourceZip"' in text
    assert '20260713-hans-all-pages-v3' in text
    assert 'BotConnectionDiagnostics.RecordLanguageStatus(true' in text
    assert '清除早期“待安全修复”临时状态' in text
    assert 'QnLanguageStatusReconciler.TryReconcile();' in startup


def test_existing_updater_script_is_not_given_qianniu_restart_responsibility():
    text = read(UPDATER)
    assert 'post-update-qn-recovery.consumed.json' not in text
    assert 'TryClickRememberedAccountLogin' not in text
    assert 'TryConfirmRestorePreviousMessages' not in text
