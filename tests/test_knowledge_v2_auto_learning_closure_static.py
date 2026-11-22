from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_manual_takeover_learns_the_actual_human_reply_directly_into_v2():
    source = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    method = source.split("private static async Task CompareManualAnswerAsync(", 1)[1].split(
        "private static bool ContainsUnsafeManualLearning(", 1
    )[0]
    assert "confidence < 0.95" in method
    assert "KnowledgeEngineV2Repository.TrySaveReviewedDirectAnswer(" in method
    assert "seller, question, manualAnswer" in method
    assert "ResolveRuntimeByShopKey(shopKey)" in source
    assert "SaveLearned(" not in method


def test_session_corpus_correction_is_exact_id_revision_guarded_and_rollbackable():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    method = repository.split("public static bool TryApplyVerifiedSessionCorrection(", 1)[1].split(
        "public static bool TryRollbackLastBotCorrection(", 1
    )[0]
    assert "confidence < 0.95" in method
    assert "evidenceIds.Count == 0" in method
    assert "expectedQuestion.Length < 5" in method
    assert "before.Title ?? string.Empty).Trim(), expectedQuestion" in method
    assert "ReadOneRecord<KnowledgeV2RecordRow>(x => x.Id == knowledgeId)" in method
    assert "observedAnswer, StringComparison.Ordinal" in method
    assert 'corrected.SourceType = "human_evidence_auto_reviewed";' in method
    assert "SaveRecordsInTransaction(new List<object> { ToRow(corrected), revision });" in method
    assert "MirrorOneToLegacy(shop, corrected)" in method
    assert "KnowledgeEngineV2Service.ApplyRecordUpdate(seller, corrected)" in method


def test_verified_auto_learning_source_is_production_approved():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    assert 'source.IndexOf("human_evidence_auto_reviewed", StringComparison.OrdinalIgnoreCase) >= 0' in bridge
    assert 'source.IndexOf("human_evidence_auto_learned", StringComparison.OrdinalIgnoreCase) >= 0' in bridge
    assert "if (IsExplicitHumanConfirmationSource(record.SourceType)) return false;" in bridge


def test_manual_edit_carries_buyer_question_alias_and_retrieval_uses_strict_similarity_gates():
    editor = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    service = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert "e.MessageRecordId, e.Question, out message" in editor
    assert "buyerQuestionAlias" in repository
    assert "corrected.Aliases" in repository
    assert "IsStrongManualReviewedSimilarMatch(x, query)" in service
    assert "match.PredicateScore < 0.99" in service
    assert "match.IntentScore < 0.99" in service
    assert "match.EntityScore >= 0.66 || subjectSimilarity >= 0.82" in service


def test_human_direct_answer_updates_existing_exact_alias_without_creating_duplicate():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    assert "var compactQuestion = KnowledgeEngineV2Semantics.Compact(question);" in repository
    assert "x.Aliases ?? new List<string>()" in repository
    assert "var matchedByAlias" in repository
    assert "aliasQuery.ContextDependent" in repository
    assert "if (!matchedByAlias)" in repository
    assert "record.CorrectionCount++" in repository
    assert "拒绝覆盖原知识" in repository

