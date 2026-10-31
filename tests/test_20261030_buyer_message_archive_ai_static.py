"""Buyer message archive: privacy, capture, grouping and explicit human approval."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]

def src(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_captures_incoming_buyer_messages_before_reply_independent_of_learning():
    source = src("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")
    section = source.split("private static void ObserveMessage(", 1)[1].split(
        "private static void InjectLearnedStyle(", 1)[0]
    assert "if (isBuyer && !ConversationContextStore.IsPlatformSystemTip(message, text))" in section
    assert "BuyerMessageArchiveService.Observe(seller, text, messageKey, messageTime);" in section
    assert section.index("BuyerMessageArchiveService.Observe(") < section.index("ConversationContextStore.RefreshAndRecord(")

def test_archive_has_no_buyer_nick_and_pre_save_redaction():
    source = src("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
    record = source.split("internal sealed class BuyerQuestionRow", 1)[1].split(
        "internal sealed class BuyerAiOptimizationRow", 1)[0]
    for forbidden in ("BuyerNick", "BuyerId", "ConversationId", "OrderId", "UserId"):
        assert forbidden not in record
    assert "text = Redact(text);" in source
    for token in ("[敏感内容]", "[手机号]", "[邮箱]", "[链接]", "[长数字]"):
        assert token in source
    assert "ReceiptHash = " in source
    assert "ReadOneRecord<BuyerQuestionRow>" in source
    assert "new SQLiteHelper(Path.Combine(root, \"buyer-questions-v2.db\")" in source

def test_time_range_frequency_is_actual_message_occurrences():
    source = src("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
    assert "ReceivedAtTicks >= cutoff" in source
    assert "Count = g.Count()" in source
    assert "GroupBy(x => x.GroupKey ?? x.Id)" in source
    assert "for (var offset = 0; offset < unique.Count; offset += 60)" in source
    assert "if (assigned.Count != batch.Count)" in source

def test_ai_top_optimization_reviews_before_production_write():
    source = src("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
    ui = src("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    assert "await GroupByAi(seller, progress, ct, days)" in source
    assert "Take(count)" in source
    assert 'Status = "pending"' in source
    assert "TrySaveReviewedDirectAnswer(" in source
    assert "MessageBoxButton.YesNo" in ui
    assert "人工确认并应用直答" in ui
    assert "SaveDraft()" in ui
    assert "KnowledgeEngineV2GovernanceAuditService.TryAppendAction" in source

def test_message_management_is_in_v2_and_compiled():
    nav = src("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    targets = src("src/Directory.Build.targets")
    ui = src("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    assert 'Nav("买家消息管理", () => new BuyerMessageManagementPage(_owner, _seller))' in nav
    assert "BuyerMessageArchiveService.cs" in targets
    assert "BuyerMessageManagementPage.cs" in targets
    assert "new DataGrid" in ui
    assert "BuyerMessageListItem" in ui
    assert "OrderByDescending(x => x.ReceivedAt)" in ui
    assert "TreeViewItem" not in ui
    assert "AI分析优化" in ui
    assert "AI分析并归类" not in ui
    assert "AI统计优化" not in ui
    assert "修改组标题" in ui
    assert "删除选中消息" in ui
    assert "删除所属分类" in ui
    assert "WindowStartupLocation.CenterOwner" in ui
