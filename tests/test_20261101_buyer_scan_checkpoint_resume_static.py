"""Regression: restartable per-shop history scans with encrypted checkpoints."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
def read(p):
    return (ROOT / p).read_text(encoding="utf-8-sig")
scan = read("src/Bot/Knowledge/ChatHistoryScanService.cs")
checkpoint = read("src/Bot/Knowledge/BuyerHistoryScanCheckpointStore.cs")
page = read("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
evidence = read("src/Bot/Knowledge/BuyerHistoryScanEvidenceSession.cs")
archive = read("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
targets = read("src/Directory.Build.targets")

def test_checkpoint_is_private_encrypted_shop_scoped_atomic_and_versioned():
    for x in ('"buyer-history-resume-private"', 'ShopContextLocator.ResolveBySellerNick',
              'shop.ShopKey', "ProtectedData.Protect(", "ProtectedData.Unprotect(",
              "DataProtectionScope.CurrentUser", "File.Replace(temp, path, null)",
              "File.Move(temp, path)", "SchemaVersion", "CreatedAt",
              "FileName(string seller, string id)"):
        assert x in checkpoint
    assert "BuyerHistoryScanCheckpointStore.cs" in targets
    assert "FileName(seller, id)" in checkpoint
    assert "value.ScanAll != options.ScanAll" in checkpoint
    assert "value.MaxContacts != options.MaxContacts" in checkpoint
    assert "value.StartTime != options.StartTime" in checkpoint
    assert "value.EndTime != options.EndTime" in checkpoint
    assert "value.Completed = value.Completed.Where(x => value.Contacts.Contains(x))" in checkpoint
    assert "Regex" in checkpoint

def test_completed_buyers_survive_crash_and_only_unfinished_are_scanned():
    assert "BuyerHistoryScanCheckpointStore.Load(" in scan
    assert "BuyerHistoryScanCheckpointStore.Create(" in scan
    assert "foreach (var previous in checkpoint.Contacts)" in scan
    assert "checkpoint.Completed = checkpoint.Completed" in scan
    assert "var successful = new HashSet<string>(checkpoint.Completed" in scan
    assert "if (successful.Contains(names[i])) continue;" in scan
    assert "checkpoint.Completed.Add(buyer);" in scan
    assert "checkpoint.ArchivedMessages += count;" in scan
    assert "BuyerHistoryScanCheckpointStore.Save(seller, checkpoint);" in scan
    assert 'checkpoint.State = scanState;' in scan
    assert '"[BuyerQuestions] history_scan_resumed run="' in scan
    assert "ResumeCheckpointId = checkpoint.Id" in scan
    assert "PreviouslyCompletedContacts = previouslyCompleted" in scan

def test_archive_is_idempotent_and_db_errors_cannot_be_checkpointed_as_success():
    assert "ObserveForHistoryScan(" in scan
    assert '"history:" + buyer + "|" + turn.Time.Ticks + "|" + turn.Text' in scan
    assert "internal static void ObserveForHistoryScan(" in archive
    core = archive.split("internal static void ObserveForHistoryScan(", 1)[1].split(
        "internal static List<BuyerQuestionRow> Read(", 1)[0]
    assert "state.Db.SaveOneRecord(" in core
    assert "catch (Exception" not in core
    assert "catch (Exception ex)" in archive.split(
        "internal static void Observe(", 1)[1].split(
        "internal static void ObserveForHistoryScan(", 1)[0]
    scanCore = scan.split("var buyerTurns = turns.Where(", 1)[1].split(
        'evidence.Contact(buyer, true', 1)[0]
    assert scanCore.index("ObserveForHistoryScan(") < scanCore.index("checkpoint.Completed.Add(buyer);")

def test_user_can_choose_resume_fresh_or_cancel_and_summary_is_cumulative():
    assert "BuyerHistoryScanCheckpointStore.Latest(_seller)" in page
    assert "MessageBoxButton.YesNoCancel" in page
    assert "if (choice == MessageBoxResult.Cancel) return;" in page
    assert "if (choice == MessageBoxResult.Yes) resumeId = previous.Id;" in page
    assert "ResumeCheckpointId = resumeId" in page
    assert '"历史消息扫描 · 断点续扫中"' in page
    assert "scan.PreviouslyCompletedContacts" in page
    assert "scan.ResumeCheckpointId" in page
    assert "evidence.Resume(checkpoint.Id, checkpoint.Completed.Count);" in scan
    assert "CumulativeCompleted(successful.Count)" in scan
    assert "SkippedPreviouslyCompleted" in evidence
    assert "CumulativeCompletedContacts" in evidence
    assert "checkpoint" not in evidence.split("foreach (var source in new[]",1)[1].split(")",1)[0]
