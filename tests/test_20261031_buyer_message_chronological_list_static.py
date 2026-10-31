"""Buyer message management: raw chronological row list without changing archive semantics."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def code(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_each_exact_or_semantic_group_is_one_collapsible_row():
    page = code("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    assert "private readonly DataGrid _list;" in page
    assert "private readonly TreeView _tree;" not in page
    assert "new DataGridTemplateColumn" in page
    assert "grouped.Select(g =>" in page
    assert 'new FrameworkElementFactory(typeof(Expander))' in page
    assert 'new FrameworkElementFactory(typeof(ListBox))' in page
    assert "Details = wordings.Select(" in page
    assert "BuyerMessageContentSelector" in page
    assert "IsSemanticMerged = wordings.Count > 1" in page
    assert "OrderByDescending(x => x.ReceivedAt)" in page
    assert "ThenBy(x => x.Message.Id, StringComparer.Ordinal)" in page
    for title in ("买家消息内容", "接收次数", "直答消息"):
        assert title in page
    assert "EnableRowVirtualization = true" in page
    assert "SelectionMode = DataGridSelectionMode.Extended" in page

def test_time_search_scan_grouping_and_full_message_details_still_work():
    page = code("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    archive = code("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
    assert "DatePicker" in page
    assert "BuyerMessageArchiveService.Groups(_seller, start, end, _search.Text)" in page
    assert "ShowMessageDetails(row.Item as BuyerMessageListItem)" in page
    assert "new BuyerQuestionOptimizationWindow(owner, _seller, true).ShowDialog()" in page
    assert "await GroupByAi(seller, progress, ct, days)" not in archive
    assert "ScanBuyerMessagesAsync(" in page
    assert "new ChatHistoryScanOptions { ScanAll = true, MaxContacts = 5000 }" in page
    assert "(!start.HasValue || x.ReceivedAtTicks >= start.Value.Ticks)" in archive
    assert "(!end.HasValue || x.ReceivedAtTicks <= end.Value.Ticks)" in archive

def test_delete_only_explicit_selected_messages_without_category_controls():
    page = code("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    single = page.split("private void DeleteSelected()",1)[1].split(
        "private void EditDirectAnswer(",1)[0]
    assert "_list.SelectedItems.OfType<BuyerMessageListItem>()" in single
    assert ".SelectMany(x => x.Messages.Select(m => m.Id))" in single
    assert "BuyerMessageArchiveService.Delete(_seller, ids)" in single
    assert 'Header = "所属分类"' not in page
    assert "删除所属分类" not in page
