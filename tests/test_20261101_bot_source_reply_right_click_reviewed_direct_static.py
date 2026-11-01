"""A sent Bot reply may be edited and immediately registered as a reviewed direct answer,
but editing must never rewrite the historical sent message or install global [图片] rules.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
def read(name):
    return (ROOT/name).read_text(encoding="utf-8-sig")
ui = read("src/Bot/Knowledge/KnowledgeV2BotReplyManagementPage.cs")
repo = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")

def test_right_click_targets_the_clicked_record_not_previous_selection():
    assert "_sourceGrid.PreviewMouseRightButtonDown +=" in ui
    assert "FindSourceRow(target)" in ui
    assert "row.IsSelected = true;" in ui
    assert "_sourceGrid.SelectedItem = null;" in ui
    assert 'Header = "修改 Bot 回复并设为直答"' in ui
    assert "_sourceGrid.ContextMenu = sourceMenu;" in ui
    assert "correctReply.IsEnabled = IsSentBotReply(chosen.Entity);" in ui
    assert 'string.Equals(entity.StatusKind, "sent"' in ui
    assert 'entity.EntityId.StartsWith("bot-echo-"' in ui

def test_editor_requires_full_question_and_reviewed_confirmation():
    assert "internal static class BotReplyDirectReviewWindow" in ui
    assert 'Text = source.Question ?? string.Empty' in ui
    assert 'Text = source.Answer ?? string.Empty' in ui
    assert "KnowledgeEngineV2Semantics.IsContextDependent(question)" in ui
    assert "question == \"[图片]\"" in ui
    assert "question == \"[链接]\"" in ui
    assert 'Content = "确认并立即设为直答"' in ui
    assert "MessageBoxButton.YesNo, MessageBoxImage.Warning" in ui
    assert "if (confirmed)" in ui
    assert "question = approvedQuestion;" in ui
    assert "answer = approvedAnswer;" in ui

def test_direct_write_uses_existing_shop_scoped_kb_and_source_message_id():
    method = ui.split("private void CorrectSelectedReplyAsDirect()",1)[1].split(
        "private void SearchKnowledgeFromSelected()",1)[0]
    assert "BotReplyDirectReviewWindow.Edit(_owner, entity" in method
    assert "BotReplyDirectReviewWindow.IsStandaloneQuestion(completeQuestion)" in method
    assert "KnowledgeEngineV2Repository.TrySaveReviewedDirectAnswer(" in method
    assert "_seller, completeQuestion, reviewedAnswer, entity.EntityId" in method
    assert "UpdateAnswerSource" not in method
    assert "_selectedSource.Answer =" not in method
    assert "历史已发送消息保持不变" in method
    assert "return;" in method

def test_manual_direct_does_not_overwrite_product_scoped_knowledge():
    method = repo.split("public static bool TrySaveReviewedDirectAnswer(",1)[1].split(
        "public static bool TryApplyBuyerDirect(",1)[0]
    assert "record.Conditions != null && record.Conditions.Count > 0" in method
    assert "record.ProductIds != null && record.ProductIds.Count > 0" in method
    assert "record.RequiredContext != null && record.RequiredContext.Count > 0" in method
    assert 'record.SourceType = "manual_reviewed_direct";' in method
    assert 'KnowledgeEngineV2GovernanceAuditService.TryAppendAction(' in method
    assert "Save(seller, record)" in method
