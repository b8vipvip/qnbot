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
    assert "grouped.Select(g => new BuyerMessageListItem" in page
    assert 'new FrameworkElementFactory(typeof(Expander))' in page
    assert 'new FrameworkElementFactory(typeof(ListBox))' in page
    assert "Details = g.Messages.OrderByDescending" in page
    assert "OrderByDescending(x => x.ReceivedAt)" in page
    assert "ThenBy(x => x.Message.Id, StringComparer.Ordinal)" in page
    for title in ("接收时间", "买家消息内容", "所属分类", "筛选内同类次数"):
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
    assert "await GroupByAi(seller, progress, ct, days)" in archive
    assert "ScanBuyerMessagesAsync(" in page
    assert "new ChatHistoryScanOptions { ScanAll = true, MaxContacts = 5000 }" in page
    assert "(!start.HasValue || x.ReceivedAtTicks >= start.Value.Ticks)" in archive
    assert "(!end.HasValue || x.ReceivedAtTicks <= end.Value.Ticks)" in archive

def test_delete_row_is_explicit_and_whole_category_has_separate_confirmation():
    page = code("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    single = page.split("private void DeleteSelected()",1)[1].split(
        "private void DeleteSelectedGroup()",1)[0]
    whole = page.split("private void DeleteSelectedGroup()",1)[1].split("internal sealed class BuyerQuestionOptimizationWindow",1)[0]
    assert "_list.SelectedItems.OfType<BuyerMessageListItem>()" in single
    assert ".SelectMany(x => x.Messages.Select(m => m.Id))" in single
    assert "BuyerMessageArchiveService.Delete(_seller, ids)" in single
    assert "BuyerMessageArchiveService.Read(_seller)" in whole
    assert "MessageBoxButton.YesNo" in whole
    assert "包括当前日期筛选以外的记录" in whole
    assert "BuyerMessageArchiveService.RenameGroup(_seller, selected.GroupKey, value)" in page
