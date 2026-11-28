from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_history_chat_is_a_left_navigation_page():
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    history = read("src/Bot/Knowledge/ChatHistoryScanWindow.cs")
    assert 'Nav("历史聊天整理", () => new KnowledgeV2ChatHistoryPage(_owner, _seller))' in ui
    assert "internal sealed class KnowledgeV2ChatHistoryPage : UserControl, IKnowledgeV2Refreshable" in history
    assert "PromoteHistoryImport(_seller, _shopKey, scan.ImportResult)" in history


def test_smart_import_requests_current_v2_schema_directly():
    service = read("src/Bot/Knowledge/KnowledgeV2SmartImportService.cs")
    required = [
        "title,type,intent,subject,predicate,entities,aliases,answer,short_answer,conditions,exclusions,required_context,product_ids,risk_level,confidence,authority,status",
        '"records"',
        "List<KnowledgeV2Record>",
        "KnowledgeEngineV2Semantics.NormalizeType",
        "KnowledgeEngineV2Semantics.NormalizeIntent",
        "KnowledgeEngineV2Semantics.NormalizePredicate",
        'record.SourceType = "ai_smart_import"',
        "repair once",
    ]
    for marker in required:
        assert marker in service
    assert "KnowledgeAiService.ParseAiKnowledgeResult" not in service
    assert "KnowledgeEngineV2Semantics.FromLegacy(item, null)" not in service
    assert "List<KnowledgeBaseEntry> Items" not in service
    assert "禁止输出旧版 faqs/category/question/keywords" in service


def test_old_faq_shape_is_rejected_not_silently_promoted_by_smart_import():
    service = read("src/Bot/Knowledge/KnowledgeV2SmartImportService.cs")
    assert 'obj["faqs"] != null' in service
    assert 'obj["question"] != null' in service
    assert 'obj["category"] != null' in service
    assert 'obj["keywords"] != null' in service
    assert "检测到旧版 FAQ 字段" in service
    assert "缺少必需字段 title 或 answer" in service


def test_history_result_is_written_directly_to_v2_with_evidence_gated_publication():
    promotion = read("src/Bot/Knowledge/KnowledgeV2HistoryImportService.cs")
    history = read("src/Bot/Knowledge/ChatHistoryScanWindow.cs")
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")

    for marker in [
        '? "business_fact"',
        "&& !hasUnverifiedScope",
        "KnowledgeEngineV2Semantics.NormalizeIntent",
        "KnowledgeEngineV2Semantics.NormalizePredicate",
        "KnowledgeEngineV2Repository.LoadAll",
        "KnowledgeEngineV2Repository.TryAddHistoryImportRecord",
        "ShopContextLocator.ResolveRuntimeByShopKey(shopKey)",
        "using (ShopSettingsScope.Enter(shop))",
        "SourceEvidenceByToken",
        "ImportEvidenceTokens",
        "evidenceValid",
        "ai_auto_learned_human_evidence",
        "chat_history_unverified_candidate",
    ]:
        assert marker in promotion
    assert "PromoteHistoryImport(_seller, _shopKey, scan.ImportResult)" in history
    assert "BotFeatureStore.SaveKnowledgeBase" not in promotion
    assert "MirrorOneToLegacy" not in repository
    assert not (ROOT / "src/Bot/Knowledge/KnowledgeV2LegacyDeltaImportService.cs").exists()
    assert "V2字段：title / type / intent / subject / predicate / entities / aliases / answer" in history

def test_history_wrapper_uses_same_v2_page_for_old_entry_points():
    history = read("src/Bot/Knowledge/ChatHistoryScanWindow.cs")
    assert "public sealed class ChatHistoryScanWindow : Window" in history
    assert "Content = new KnowledgeV2ChatHistoryPage(this, seller);" in history
