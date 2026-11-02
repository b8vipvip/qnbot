"""Buyer message list: row numbers, footer totals, direct KB lookup and scan completion UI."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
page = (ROOT / "src/Bot/Knowledge/BuyerMessageManagementPage.cs").read_text(encoding="utf-8-sig")
archive = (ROOT / "src/Bot/Knowledge/BuyerMessageArchiveService.cs").read_text(encoding="utf-8-sig")

def test_scan_refreshes_after_success_cancellation_and_failure():
    method = page.split("private async Task ScanHistoryAsync()",1)[1].split(
        "private void ShowScanRecords()",1)[0]
    assert "RefreshView();" in method.split("finally",1)[1]
    assert "var scanStatus = _status.Text;" in method
    assert "_status.Text = scanStatus;" in method
    assert '"历史消息扫描 · 已取消"' in method
    assert '"历史消息扫描 · 失败"' in method

def test_progress_close_button_has_visible_caption_and_only_closes_after_scan():
    assert 'Text = "关闭", FontSize = 12' in page
    assert "Foreground = Brushes.Black" in page
    assert "Width = 62, Height = 27, IsEnabled = false" in page
    assert "_scanCancellation == null) _root.Children.Remove(_scanCard)" in page
    assert "_scanCardClose.IsEnabled = true;" in page

def test_number_and_bottom_right_counts_follow_filter_and_sort():
    assert "public int Sequence { get; set; }" in page
    assert 'Header = "序号", Binding = new Binding("Sequence")' in page
    assert "RenumberVisibleRows()" in page
    assert "rows[index].Sequence = index + 1;" in page
    assert '"买家消息内容 " + count' in page
    assert '" 条 / 直答 " + answered + " 条"' in page
    assert "DockPanel.SetDock(footer, Dock.Bottom);" in page
    assert "root.Children.Add(footer);" in page
    assert "UpdateFooterStats(rows);" in page

def test_get_direct_is_existing_approved_knowledge_only_without_ai_or_sending():
    method = page.split("private async Task GetDirectAnswersAsync()",1)[1].split(
        "private void ShowMessageDetails(",1)[0]
    assert 'BuyerMessageArchiveService.Groups(_seller,' in method
    assert '(DateTime?)null, (DateTime?)null, ""' in method
    assert "KnowledgeEngineV2Repository.LoadAll(_seller)" in method
    assert "BuyerMessageArchiveService.FindConfirmedDirect(" in method
    assert "BuyerMessageArchiveService.ReadOptimizations(_seller)" in method
    assert 'x.Status == "applied"' in method
    assert "RefreshView();" in method
    assert "未命中" in method
    assert "direct_lookup_complete groups=" in method
    assert "MyOpenAI" not in method
    assert "KnowledgeEngineV2Repository.Save" not in method
    assert "SendMessage" not in method
    assert 'Button("获取直答", 100)' in page
    assert 'match.Count == 1' not in method
    assert 'string.Equals(k.SourceType, "manual_reviewed_direct"' in archive
