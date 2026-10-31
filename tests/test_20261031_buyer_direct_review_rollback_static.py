"""Reviewed buyer-message answer workflow: no classification, no automatic KB writes."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
def source(p):
    return (ROOT / p).read_text(encoding="utf-8-sig")

def test_list_has_only_message_frequency_and_reviewed_answer():
    ui = source("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    assert 'Header = "买家消息内容"' in ui
    assert 'Header = "接收次数"' in ui
    assert 'Header = "直答消息（右键修改或回滚）"' in ui
    assert 'Header = "所属分类"' not in ui
    assert 'Button("修改组标题"' not in ui
    assert 'Button("删除所属分类"' not in ui
    assert 'BuyerMessageArchiveService.FindConfirmedDirect(_seller,' in ui
    assert 'AppliedRevision = revision' in ui
    assert 'void EditDirectAnswer(' in ui
    assert 'void RollbackDirectAnswer(' in ui
    assert 'PreviewMouseRightButtonDownEvent' in ui

def test_ai_optimization_no_longer_classifies_and_never_auto_applies():
    archive = source("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
    ui = source("src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    section = archive.split("internal static async Task<int> OptimizeTop(", 1)[1].split(
        "internal static KnowledgeV2Record FindConfirmedDirect(", 1)[0]
    assert "GroupByAi(seller" not in section
    assert 'Status = "pending"' in section
    assert "if (evidence == null || string.IsNullOrWhiteSpace(draftAnswer)" in section
    assert "SaveOptimization(seller, item)" in section
    assert "BuyerMessageArchiveService.Apply(" not in section
    assert "PreviewPending(latest);" in ui
    assert 'Header = "知识库原答案"' in ui
    assert 'Header = "拟更新直答"' in ui
    assert '"确认更新选中项"' in ui
    assert "selected = rows.Where(x => x.Selected)" in ui
    assert "MessageBoxButton.YesNo" in ui

def test_direct_answers_only_from_exact_reviewed_knowledge_not_similarity():
    archive = source("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
    section = archive.split("internal static KnowledgeV2Record FindConfirmedDirect(",1)[1].split(
        "internal static BuyerAiOptimizationRow LatestAppliedForGroup(",1)[0]
    assert 'SourceType, "manual_reviewed_direct"' in section
    assert 'normalized.Contains(KnowledgeEngineV2Semantics.Compact(k.Title))' in section
    assert 'return matches.Count == 1 ? matches[0] : null;' in section
    assert "TextSimilarity" not in section
    assert "k.RequiredContext == null || k.RequiredContext.Count == 0" in section

def test_review_and_rollback_are_bound_to_exact_knowledge_revision():
    repo = source("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    archive = source("src/Bot/Knowledge/BuyerMessageArchiveService.cs")
    assert "TryApplyBuyerDirect(" in repo
    assert "TryRollbackBuyerDirect(" in repo
    apply = repo.split("public static bool TryApplyBuyerDirect(",1)[1].split(
        "public static bool TryRollbackBuyerDirect(",1)[0]
    assert "evidence.Answer != evidenceAnswer" in apply
    assert "exact.Count > 1" in apply
    assert 'Action = "buyer_direct_apply"' in apply
    assert "SaveRecordsInTransaction(new List<object> { ToRow(applied), revision })" in apply
    assert "previous == null ? \"\" : JsonConvert.SerializeObject(previous)" in apply
    assert "MirrorOneToLegacy(shop, applied)" in apply
    rollback = repo.split("public static bool TryRollbackBuyerDirect(",1)[1].split(
        "public static bool TryCorrectBotAnswer(",1)[0]
    assert 'latest.Action != "buyer_direct_apply"' in rollback
    assert "current.UpdatedAt.Ticks != applied.UpdatedAt.Ticks" in rollback
    assert 'Action = "buyer_direct_rollback"' in rollback
    assert "MirrorDeleteToLegacy(shop, knowledgeId)" in rollback
    assert "KnowledgeEngineV2Service.ApplyRecordDelete(seller, knowledgeId)" in rollback
    assert "BuyerMessageArchiveService.Rollback(_seller, item.AppliedRevision)" in source(
        "src/Bot/Knowledge/BuyerMessageManagementPage.cs")
    assert "row.Status = \"rolled_back\"" in archive
