"""Buyer history excludes platform/card/link evidence without deleting buyer questions."""
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def src(path):return (ROOT/path).read_text(encoding="utf-8-sig")
ui=src("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
scan=src("src/Bot/Knowledge/ChatHistoryScanService.cs")
flt=src("src/Bot/Knowledge/BuyerHistoryMessageFilter.cs")
archive=src("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
merge=src("src/Bot/Knowledge/BuyerHistoryScanMessageMergeService.cs")
evidence=src("src/Bot/Knowledge/BuyerHistoryScanEvidenceSession.cs")
targets=src("src/Directory.Build.targets")

def test_copy_menu_on_buyer_exact_and_semantic_and_direct_answer():
    assert 'Header = "复制买家消息"' in ui
    assert 'Header = "复制直答消息"' in ui
    assert 'new System.Windows.Input.MouseButtonEventHandler(ShowBuyerMessageCopyMenu)' in ui
    assert ui.count('new System.Windows.Input.MouseButtonEventHandler(ShowBuyerMessageCopyMenu)') == 2
    assert "Clipboard.SetText(text);" in ui
    assert "IsEnabled = !string.IsNullOrWhiteSpace(item.DirectAnswer)" in ui
    assert 'Header = "修改直答"' in ui
    assert 'Header = "回滚本次直答更新"' in ui
    assert "clicked.DataContext is string" in ui

def test_filter_runs_before_history_evidence_and_archive_write():
    assert "BuyerHistoryMessageFilter.cs" in targets
    scanPart=scan.split("private static bool TryConvertTurn(",1)[1].split(
        "private static string ExtractMessageText(",1)[0]
    assert 'role == "buyer" && BuyerHistoryMessageFilter.ShouldExcludeRaw(' in scanPart
    assert scanPart.index("BuyerHistoryMessageFilter.ShouldExcludeRaw") < scanPart.index("turn = new HistoryTurn")
    assert "evidence.Excluded(reason)" in scan
    assert "FilteredMessages = evidence.Record.ExcludedMessages" in scan
    assert "ExcludedByReason" in evidence
    assert "scan.FilteredMessages" in ui

def test_filter_metadata_and_structured_cards_and_original_buyer_questions():
    assert "NonBuyerConversationGuard.ShouldBlockMessage" in flt
    assert "ConversationContextStore.IsPlatformSystemTip" in flt
    assert "ConversationContextStore.IsWithdrawalNotice" in flt
    assert "message.originalData.header" in flt
    assert "message.originalData.actionUrl" in flt
    assert "message.originalData.itemId" in flt
    assert "StructuredOrderFields.Matches(text).Count >= 2" in flt
    assert "LinkOnly.IsMatch(text)" in flt
    assert "AutomaticNotices.IsMatch(text)" in flt
    # Never use broad keyword-only checks that would delete legitimate buyer
    # utterances like “请问我的订单什么时候发货”.
    assert 'text.Contains("发货")' not in flt
    assert 'text.Contains("订单")' not in flt

def test_legacy_replay_and_groups_do_not_reimport_system_payloads():
    assert "BuyerHistoryMessageFilter.IsExcludedArchivedText(text)" in archive
    assert "BuyerHistoryMessageFilter.IsExcludedArchivedText(x.Text)" in archive
    assert "BuyerHistoryMessageFilter.IsExcludedArchivedText(text)" in merge
    assert "result.ExcludedNonBuyerRows++" in merge
    assert "result.ExcludedNonBuyerRows++" in archive
    assert "result.ExcludedNonBuyerRows" in ui
    assert "DeleteOneRecord" not in flt
    assert "DeleteManyRecords" not in flt
