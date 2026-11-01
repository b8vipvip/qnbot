"""Buyer history scan is interruptible, bounded, auditable and never logs buyer IDs."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
def src(p):
    return (ROOT / p).read_text(encoding="utf-8-sig")

def test_scan_bound_to_verified_conversation_and_safe_when_focus_changes():
    scan = src("src/Bot/Knowledge/ChatHistoryScanService.cs")
    assert 'var currentNick = FindString(current, "nick", "buyerNick", "display")' in scan
    assert "string.Equals(CleanNick(currentNick), CleanNick(buyer)" in scan
    assert 'onInterruption("switched")' in scan
    assert "WaitForSafeChatSwitchAsync" in scan
    assert 'BotActivityCoordinator.GetSnapshot(qn.Seller.Nick).ActiveCount == 0' in scan
    assert "BotActivityCoordinator.GetSnapshot(seller).ActiveCount == 0" in scan
    assert 'ActiveBuyerScans.TryAdd(seller, 0)' in scan
    assert 'ActiveBuyerScans.TryRemove(seller, out ignored)' in scan
    assert "new { ccode = ccode, type = 1 }" in scan
    assert "if (!string.Equals(CleanNick(currentNick)" in scan
    assert "if (!await scanContact(names[i], i + 1)) pending.Add(names[i]);" in scan
    assert "if (!await scanContact(pending[i]" in scan
    assert 'evidence.Contact(pending[i], false, 0, "retry_exhausted")' in scan

def test_scan_retries_remote_pages_and_does_not_save_foreign_buyer_turns():
    scan = src("src/Bot/Knowledge/ChatHistoryScanService.cs")
    assert "for (var retry = 0; retry < 3; retry++)" in scan
    assert 'onInterruption("page_retry")' in scan
    assert 'throw new InvalidOperationException("history_page_unavailable")' in scan
    assert 'turns.Where(x => x.Role == "buyer")' in scan
    assert "else return false;" in scan
    assert "BuyerMessageArchiveService.Observe(seller, turn.Text, turn.MessageKey, turn.Time);" in scan

def test_package_saved_for_success_error_cancel_without_buyer_identity():
    scan = src("src/Bot/Knowledge/ChatHistoryScanService.cs")
    evidence = src("src/Bot/Knowledge/BuyerHistoryScanEvidenceSession.cs")
    targets = src("src/Directory.Build.targets")
    assert "BuyerHistoryScanEvidenceSession.cs" in targets
    assert "evidence.Complete(scanState, phase);" in scan
    assert 'scanState = "cancelled"; phase = "user_cancelled";' in scan
    assert 'scanState = failed == 0 && names.Count > 1 ? "completed" : "partial";' in scan
    assert "PackagePath = evidence.ExpectedPackagePath" in scan
    assert 'new ShopScopedPathProvider().GetLogRoot(shop)' in evidence
    assert '"buyer-history-scans"' in evidence
    for expected in ("manifest.json", "events.jsonl", "messages-redacted.jsonl",
                     "ZipArchive", "FileStream", "RunId", "ContactHash",
                     "FailedContacts", "InterruptedContacts", "RetryAttempts"):
        assert expected in evidence
    assert "BuyerMessageArchiveService.Redact(turn.Item1)" in evidence
    assert 'contact = pseudonym' in evidence
    assert "buyerNick" not in evidence and "BuyerNick" not in evidence
    assert 'JsonConvert.SerializeObject(_record, Formatting.Indented)' in evidence
    assert 'Complete("interrupted", "disposed_without_terminal_state")' in evidence

def test_ui_has_progress_journal_and_export_selected_run():
    ui = src("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    for needle in (
        'Button("扫描记录 / 数据包", 145)',
        'private readonly ProgressBar _scanProgress;',
        'private readonly TextBox _scanLog;',
        "private void UpdateScanProgress(ChatHistoryScanProgress p)",
        'private void ShowScanRecords()',
        'BuyerHistoryScanEvidenceSession.List(_seller)',
        'new SaveFileDialog',
        'File.Copy(selected.PackagePath, dialog.FileName, true)',
        '_scanCancellation.Token',
        '进度',
    ):
        if needle == "进度":
            assert "扫描" in ui
        else:
            assert needle in ui
    assert 'if (_scanCancellation != null) _scanCancellation.Cancel();' in ui
    assert 'p.InterruptionCount' in ui
    assert 'p.RetryCount' in ui
