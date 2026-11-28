from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_history_scan_captures_attached_shopkey_and_refuses_nickname_only_write():
    page = read("src/Bot/Knowledge/ChatHistoryScanWindow.cs")

    assert "private readonly string _shopKey;" in page
    assert "_shopKey = KnowledgeCenterV2Context.ResolveShopKey(owner, _seller);" in page
    assert "string.IsNullOrWhiteSpace(_seller) || string.IsNullOrWhiteSpace(_shopKey)" in page
    assert "options.LearnKnowledge = true" in page
    assert "ScanBuyerMessagesAsync" in page


def test_history_promotion_uses_exact_shopkey_and_v2_repository_only():
    service = read("src/Bot/Knowledge/KnowledgeV2HistoryImportService.cs")
    props = read("src/Bot/Directory.Build.props")
    method = service[
        service.index("public static KnowledgeV2HistoryPromotionResult PromoteHistoryImport("):
        service.index("private static List<string> Clean(")
    ]

    assert "ShopContextLocator.ResolveRuntimeByShopKey(shopKey)" in method
    assert "shop == null || !shop.HasStableSellerId" in method
    assert "string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)" in method
    assert "using (ShopSettingsScope.Enter(shop))" in method
    assert method.index("KnowledgeEngineV2Repository.LoadAll(seller)") < method.index(
        "KnowledgeEngineV2Repository.TryAddHistoryImportRecord(seller, record)"
    ) < method.index("KnowledgeEngineV2Service.Warm(seller)") < method.index(
        "AppendAudit(seller, importId, result)"
    )
    assert "KnowledgeV2HistoryImportService.cs" in props
    assert "KnowledgeV2LegacyDeltaImportService.cs" not in props
    assert not (ROOT / "src/Bot/Knowledge/KnowledgeV2LegacyDeltaImportService.cs").exists()
    assert "BotFeatureStore.SaveKnowledgeBase" not in service


def test_history_import_uses_exact_buyer_and_human_message_ids_and_excludes_bot_echoes():
    source = read("src/Bot/Knowledge/ChatHistoryScanService.cs")
    importer = read("src/Bot/Knowledge/KnowledgeV2HistoryImportService.cs")
    ai = read("src/Bot/Knowledge/KnowledgeAiService.cs")
    model = read("src/Bot/Knowledge/KnowledgeEngineV2.Models.cs")

    build_pairs = source[
        source.index("private static List<HistoryEvidencePair> BuildPairs("):
        source.index("private static bool IsBotMarkedSellerReply(")
    ]
    assert "public bool IsBotReply;" in source
    assert "IsBotReply = string.Equals(role, \"seller\"" in source
    assert "IsBotMarkedSellerReply(text)" in source
    assert "turn.IsBotReply || IsBotMarkedSellerReply(turn.Text)" in build_pairs
    assert "questionIds.Count == buyers.Count" in build_pairs
    assert "answerIds.Count == sellers.Count" in build_pairs
    assert "MessageKey" in build_pairs
    assert "botAnsweredPendingQuestion && sellers.Count == 0" in build_pairs
    assert "source_evidence_id：" in source
    assert "source_evidence_ids" in ai
    assert "ImportEvidenceTokens = ReadStrings(item, \"source_evidence_ids\")" in ai
    assert "[JsonIgnore]" in model and "ImportEvidenceTokens" in model
    assert "allTokensKnown" in importer
    assert "evidenceValid" in importer
    assert "record.SourceType = autoPublish" in importer
    assert "ai_auto_learned_human_evidence" in importer
    assert "chat_history_unverified_candidate" in importer
    assert "question_message_ids" in importer and "answer_message_ids" in importer



def test_history_promotion_checks_actual_question_and_answer_not_just_valid_tokens():
    source = read("src/Bot/Knowledge/ChatHistoryScanService.cs")
    importer = read("src/Bot/Knowledge/KnowledgeV2HistoryImportService.cs")
    assert "QuestionText = pair.Question" in source
    assert "AnswerText = pair.Answer" in source
    assert "evidence.All(x => MatchesSourceText(title, x.QuestionText)" in importer
    assert "MatchesSourceText(answer, x.AnswerText)" in importer
    assert "var autoPublish = evidenceValid && !risk && !modelCandidate" in importer
    assert "&& !parsed.ContextDependent" in importer
    assert "record.ShortAnswer = autoPublish ? answer" in importer
    assert "record.Aliases = autoPublish ? new List<string> { title }" in importer
    assert "record.Enabled = autoPublish" in importer
    assert 'record.Status = autoPublish ? "active" : "candidate"' in importer
    assert "StringComparison.Ordinal" in importer.split("private static bool MatchesSourceText(", 1)[1].split("private static List<string> Clean(", 1)[0]


def test_verified_history_source_identity_is_stable_across_device_scans():
    importer = read("src/Bot/Knowledge/KnowledgeV2HistoryImportService.cs")
    cloud = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    assert "? BuildVerifiedHistorySourceId(questionIds, answerIds, contextIds)" in importer
    helper = importer.split("private static string BuildVerifiedHistorySourceId(", 1)[1].split("// Deliberately ignore whitespace only;", 1)[0]
    for name in ("question", "answer", "context"):
        assert name + "_message_ids = " + name + "Ids.OrderBy(x => x, StringComparer.Ordinal).ToArray()" in helper
    assert "importId" not in helper and "tokens" not in helper and "Guid" not in helper
    assert "AutoPublishedEvidenceKey" in cloud
    assert "return (record.SourceId ?? string.Empty).Trim();" in cloud


def test_history_import_atomic_insert_respects_existing_rows_and_deleted_knowledge():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    importer = read("src/Bot/Knowledge/KnowledgeV2HistoryImportService.cs")
    method = repository.split("internal static bool TryAddHistoryImportRecord(", 1)[1].split("// A knowledge editor", 1)[0]
    assert method.index("lock (state.Sync)") < method.index("HasExplicitlyRemovedKnowledge") < method.index("state.Db.SaveOneRecord")
    assert "existing.SourceId, record.SourceId" in method
    assert 'existing.Status, "deleted"' in method
    assert "existing.Aliases" in method
    assert "KnowledgeAiService.ContentHash(existing.Title, existing.Answer)" in method
    assert "KnowledgeEngineV2Service.ApplyRecordUpdate(seller, record)" in method
    assert "if (!KnowledgeEngineV2Repository.TryAddHistoryImportRecord" in importer
    assert "KnowledgeEngineV2Repository.IsSafeHistoryImportForAutoPublish(title, answer)" in importer
    assert "return IsSafeAutomaticNewKnowledge(question, answer);" in repository


def test_history_scan_rejects_unbound_or_changed_desk_before_promoting_evidence():
    source = read("src/Bot/Knowledge/ChatHistoryScanService.cs")
    scan = source[source.index("public async Task<ChatHistoryScanResult> ScanAndImportAsync("):]
    assert "ReferenceEquals(QN.CurQN, qn)" in scan
    assert "ReferenceEquals(qn.CDP, scanCdp)" in scan
    assert "!string.IsNullOrWhiteSpace(scanSessionId)" in scan
    assert "string.Equals(scanCdp.SessionId, scanSessionId, StringComparison.Ordinal)" in scan
    assert "string.Equals(scanCdp.ShopKey, shopKey, StringComparison.Ordinal)" in scan
    for operation in ("ReadVisibleChatBuyerListContactsAsync", "TryOpenMessageManagerAsync",
                      "ReadVisibleMessageManagerContactsAsync", "ReadContactsFromApisAsync",
                      "ResolveConversationCodeAsync", "ReadHistoryAsync"):
        call = scan.index("await " + operation)
        assert "requireOriginalSession();" in scan[call:scan.index(";", call)+100]
    assert "if (isOriginalSession() && !string.IsNullOrWhiteSpace(originalBuyer))" in scan
    assert scan.index("requireOriginalSession();", scan.index("finally")) < scan.index("BuildTranscript(histories")


def test_verified_history_does_not_promote_unproven_model_scope_or_retrieval_metadata():
    importer = read("src/Bot/Knowledge/KnowledgeV2HistoryImportService.cs")
    for field in ("Conditions", "Exclusions", "RequiredContext", "ProductIds"):
        assert "Clean(record." + field + ").Count > 0" in importer
    assert "&& !hasUnverifiedScope" in importer
    assert '? "business_fact"' in importer
    assert "autoPublish || string.IsNullOrWhiteSpace(record.Intent)" in importer
    assert "autoPublish || string.IsNullOrWhiteSpace(record.Subject)" in importer
    assert "autoPublish || string.IsNullOrWhiteSpace(record.Predicate)" in importer
    assert "autoPublish || record.Entities == null" in importer
