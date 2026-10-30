"""Verified Knowledge V2 Bot-message correction contract.

A source-stamped V2 answer edits its *original* row rather than feeding a fuzzy
AI organizer which might create another conflicting knowledge record.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def src(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_message_menu_carries_original_source_and_override_guard():
    menu = src("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    ctl = src("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    assert "action.MirrorRequestEdit(currentSource(), sourceRecordId, manuallyCorrected)" in menu
    assert "SourceWasManuallyCorrected" in ctl
    assert "SourceLabel" in ctl
    assert "MessageRecordId" in ctl
    assert "MirrorRequestEdit(string originalSource, string messageRecordId," in ctl


def test_verified_v2_edit_does_not_go_through_fuzzy_learning():
    editor = src("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    block = editor.split("private async void CtlConversation_EditRequested(", 1)[1].split(
        "private void ShowGridTip(", 1
    )[0]
    assert "BotReplySourceRouting.Resolve(e.SourceLabel, e.SourceWasManuallyCorrected)" in block
    assert "!e.SourceWasManuallyCorrected" in block
    assert "route.Kind == BotReplySourceRouting.RouteKind.KnowledgeRecord" in block
    assert "KnowledgeEngineV2Repository.TryCorrectBotAnswer(e.Seller," in block
    assert "route.TargetId, e.Answer, corrected, e.MessageRecordId" in block
    assert "KnowledgeEngineV2Repository.TrySaveReviewedDirectAnswer(" in block
    assert "KnowledgeLearningService.LearnAsync(" not in block
    assert block.index("KnowledgeEngineV2Repository.TryCorrectBotAnswer") < block.index(
        "KnowledgeEngineV2Repository.TrySaveReviewedDirectAnswer("
    )
    assert 'ctl.SetAnswer(corrected, e.SourceLabel);' in block


def test_exact_id_edit_is_transactional_and_snapshot_is_immediate():
    repo = src("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    method = repo.split("public static bool TryCorrectBotAnswer(", 1)[1].split(
        "public static bool TryRollbackLastBotCorrection(", 1
    )[0]
    assert "ReadOneRecord<KnowledgeV2RecordRow>(x => x.Id == id)" in method
    assert "if (!string.Equals((before.Answer ?? string.Empty).Trim(), observedAnswer" in method
    assert "record.Id" not in method.split("var row =", 1)[0]
    assert "KnowledgeEngineV2Semantics.IsHighRisk(correctedAnswer)" in method
    assert 'corrected.SourceType = "manual_reviewed_direct";' in method
    assert "corrected.CorrectionCount++" in method
    assert "new KnowledgeV2ManualEditRevisionRow" in method
    assert 'Action = "apply"' in method
    assert "state.Db.SaveRecordsInTransaction(new List<object> { ToRow(corrected), revision });" in method
    assert "MirrorOneToLegacy(shop, corrected)" in method
    assert "KnowledgeEngineV2Service.ApplyRecordUpdate(seller, corrected)" in method
    assert "GetRecords(seller).FirstOrDefault" not in method
    assert "KnowledgeLearningService.LearnAsync" not in method


def test_same_shop_rollback_rejects_stale_revision_and_refreshes_snapshot():
    repo = src("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    models = src("src/Bot/Knowledge/KnowledgeEngineV2.Models.cs")
    page = src("src/Bot/Knowledge/KnowledgeCenterV2RecordsPage.cs")
    assert "KnowledgeV2ManualEditRevisionRow" in models
    assert "typeof(KnowledgeV2ManualEditRevisionRow)" in repo
    rollback = repo.split("public static bool TryRollbackLastBotCorrection(", 1)[1].split(
        "public static bool Delete(", 1
    )[0]
    assert 'latest.Action != "apply"' in rollback
    assert "before.UpdatedAt.Ticks != applied.UpdatedAt.Ticks" in rollback
    assert 'Action = "rollback"' in rollback
    assert "SaveRecordsInTransaction(new List<object> { ToRow(restored), rollback });" in rollback
    assert "KnowledgeEngineV2Service.ApplyRecordUpdate(seller, restored)" in rollback
    assert 'Btn("回滚Bot人工纠错", 138)' in page
    assert "TryRollbackLastBotCorrection(_seller, id, out reason)" in page
