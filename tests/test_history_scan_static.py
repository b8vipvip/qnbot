from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_history_scan_button_and_options():
    shell = text("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    window = text("src/Bot/Knowledge/ChatHistoryScanWindow.cs")
    assert 'Nav("历史聊天整理", () => new KnowledgeV2ChatHistoryPage(_owner, _seller))' in shell
    assert 'MakeButton("开始整理", 100, Start_Click)' in window
    assert "PromoteHistoryImport(_seller, _shopKey, scan.ImportResult)" in window
    assert "全部扫描" in window
    assert "按时间段扫描" in window
    assert "DatePicker" in window


def test_history_scan_prefers_all_buyers_and_keeps_manager_fallback():
    source = text("src/Bot/Knowledge/ChatHistoryScanService.cs")
    window = text("src/Bot/Knowledge/ChatHistoryScanWindow.cs")
    assert "ReadVisibleChatBuyerListContactsAsync" in source
    assert "ChatBuyerListContactCount" in source
    assert "正在读取千牛左侧“全部买家”列表" in source
    assert "TryOpenMessageManagerAsync" in source
    assert '"消息管理器"' in source
    assert "ReadVisibleMessageManagerContactsAsync" in source
    assert "mtop.taobao.wireless.amp2.im.relation.rebase" in source
    assert "联系人来源：全部买家列表" in window
    assert "有效聊天消息" in window


def test_history_scan_pages_remote_history():
    source = text("src/Bot/Knowledge/ChatHistoryScanService.cs")
    assert "im.singlemsg.GetRemoteHisMsg" in source
    assert "msgid = cursorId" in source
    assert "msgtime = cursorTime" in source
    assert "MaxHistoryPages" not in source
    assert "while (true)" in source
    assert "visitedCursors.Add(cursor)" in source
    assert "history_pagination_incomplete:cursor_stalled" in source


def test_history_scan_reuses_smart_import_batches():
    source = text("src/Bot/Knowledge/ChatHistoryScanService.cs")
    assert "KnowledgeAiService" in source
    assert "ImportAsync" in source
    assert "GetSmartImportTimeoutSeconds" in source
    assert "历史聊天扫描完成" in source
    assert "importResult.SourceEvidenceByToken = sourceEvidenceByToken" in source


def test_history_scan_filters_and_redacts():
    source = text("src/Bot/Knowledge/ChatHistoryScanService.cs")
    assert "IsPlatformSystemTip" in source
    assert "IsWithdrawalNotice" in source
    assert "[手机号]" in source
    assert "[敏感编号]" in source
    assert "[API_KEY]" in source
    assert "IsUsefulPair" in source
