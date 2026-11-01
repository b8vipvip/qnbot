"""The buyer history scan card must not consume screen height before a scan."""
from pathlib import Path

ui = (Path(__file__).resolve().parents[1] /
      "src/Bot/Knowledge/BuyerMessageManagementPage.cs").read_text(encoding="utf-8-sig")

def test_card_is_constructed_unmounted_and_only_inserted_after_user_confirmation():
    ctor = ui.split("internal BuyerMessageManagementPage(", 1)[1].split(
        "private static Button Button(", 1)[0]
    scan = ui.split("private async Task ScanHistoryAsync()", 1)[1].split(
        "private void ShowScanRecords()", 1)[0]
    assert "_scanCard = new Border" in ctor
    assert "root.Children.Add(scanDetail)" not in ctor
    assert "root.Children.Add(_scanCard)" not in ctor
    assert "Intentionally not added to root here" in ctor
    assert "MessageBoxResult.Yes) return;" in scan
    assert "_root.Children.Insert(_root.Children.IndexOf(_list), _scanCard);" in scan
    assert scan.index("MessageBoxResult.Yes) return;") < scan.index("_root.Children.Insert(")
    assert "if (!_root.Children.Contains(_scanCard))" in scan

def test_card_close_does_not_cancel_scan_or_remove_saved_evidence():
    ctor = ui.split("internal BuyerMessageManagementPage(", 1)[1].split(
        "private static Button Button(", 1)[0]
    scan = ui.split("private async Task ScanHistoryAsync()", 1)[1].split(
        "private void ShowScanRecords()", 1)[0]
    assert "if (_scanCancellation == null) _root.Children.Remove(_scanCard);" in ctor
    assert "_scanCardClose.IsEnabled = false;" in scan
    assert "_scanCardClose.IsEnabled = true;" in scan
    assert "_scanCardTitle.Text = \"历史消息扫描 · 已取消\";" in scan
    assert "_scanCardTitle.Text = \"历史消息扫描 · 失败\";" in scan
    assert "历史消息扫描 · 部分完成" in scan
    assert "scan.FailedContacts == 0" in scan
    assert "BuyerHistoryScanEvidenceSession.List(_seller)" in ui
    assert "File.Copy(selected.PackagePath, dialog.FileName, true)" in ui

def test_queued_old_progress_cannot_overwrite_new_scan_card():
    scan = ui.split("private async Task ScanHistoryAsync()", 1)[1].split(
        "private void ShowScanRecords()", 1)[0]
    assert "var generation = ++_scanGeneration;" in scan
    assert "if (generation == _scanGeneration) UpdateScanProgress(p);" in scan
    assert "_scanCancellation = null;" in scan
