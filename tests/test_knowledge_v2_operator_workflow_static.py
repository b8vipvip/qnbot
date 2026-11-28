from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8-sig")


def test_manual_new_knowledge_is_one_sentence_ai_generated():
    svc = read("src/Bot/Knowledge/KnowledgeV2NaturalLanguageService.cs")
    ui = read("src/Bot/Knowledge/KnowledgeV2OperatorUiBridge.cs")
    assert "AI一句话新增" in ui
    assert "legacyAdd.Visibility = Visibility.Collapsed" in ui
    assert "MyOpenAI.CallStructuredChat" in svc
    for field in ["title", "type", "intent", "subject", "predicate", "entities", "aliases", "answer", "conditions", "exclusions", "required_context", "product_ids", "risk_level", "confidence", "authority", "status"]:
        assert '"' + field + '"' in svc
    assert 'SourceType = "manual_ai_generated"' in svc
    assert "KnowledgeEngineV2Repository.Save(seller, record)" in ui


def test_history_chat_organizer_is_native_v2_and_uses_evidence_gated_promotion():
    shell = read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    operator = read("src/Bot/Knowledge/KnowledgeV2OperatorUiBridge.cs")
    page = read("src/Bot/Knowledge/ChatHistoryScanWindow.cs")
    importer = read("src/Bot/Knowledge/KnowledgeV2HistoryImportService.cs")

    assert 'Nav("历史聊天整理", () => new KnowledgeV2ChatHistoryPage(_owner, _seller))' in shell
    assert "new ChatHistoryScanWindow" not in operator
    assert "InjectHistoryButton" not in operator
    assert "KnowledgeV2ChatHistoryPage" in page
    assert "PromoteHistoryImport(_seller, _shopKey, scan.ImportResult)" in page
    assert "ai_auto_learned_human_evidence" in importer
    assert "chat_history_unverified_candidate" in importer
    assert "SourceEvidenceByToken" in importer or "SourceEvidenceByToken" in read("src/Bot/Knowledge/KnowledgeAiService.cs")
    assert "KnowledgeEngineV2Repository.LoadAll" in importer
    assert "KnowledgeEngineV2Repository.TryAddHistoryImportRecord" in importer
    assert "using (ShopSettingsScope.Enter(shop))" in importer
    assert "BotFeatureStore.SaveKnowledgeBase" not in importer
    assert "KnowledgeV2LegacyDeltaImportService" not in shell
    assert not (ROOT / "src/Bot/Knowledge/KnowledgeV2LegacyDeltaImportService.cs").exists()

def test_operator_bridge_is_bootstrapped():
    bootstrap = read("src/Bot/Knowledge/KnowledgeEngineV2GovernanceBootstrap.cs")
    assert "KnowledgeV2OperatorUiBridge.Initialize" in bootstrap
