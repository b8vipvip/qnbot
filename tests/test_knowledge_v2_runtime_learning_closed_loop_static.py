from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_app_explicitly_bootstraps_v2_before_streaming_reply_pipeline():
    app = read("src/Bot/App.xaml.cs")
    assert "KnowledgeEngineV2RuntimeBridge.InitializeForApp();" in app
    assert app.index("KnowledgeEngineV2RuntimeBridge.InitializeForApp();") < app.index(
        "BuyerStreamingReplyPipeline.Initialize();"
    )


def test_v2_is_in_the_only_text_reply_path_before_ai_generation():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    method = runtime[
        runtime.index("internal static async Task<bool> TryHandleAsync"):
        runtime.index("private static bool IsApprovedMatchForLogging")
    ]
    entry = pipeline[pipeline.index("private static async Task ProcessTextBurstStreamingAsync"):]
    assert "KnowledgeEngineV2RuntimeBridge.TryHandleWithContextAsync(qn, lease)" in entry
    assert entry.index("KnowledgeEngineV2RuntimeBridge.TryHandleWithContextAsync(qn, lease)") < entry.index(
        'lease.MarkGenerating("streaming_answer_started")'
    ) < entry.index("StreamingBuyerAnswerService.GetAnswerAsync(")
    assert "KnowledgeEngineV2Service.Resolve(" in method
    assert "KnowledgeEngineV2Service.ResolveWithCuratedImageKnowledgeEvidence(" in method
    assert "decision == null || !decision.CanDirectReply" in method
    assert "KnowledgeV2RuntimeResult.Continue(contextPromptAddon)" in method
    assert "KnowledgeV2RuntimeResult.HandledResult()" in method
    assert "return false;" in method
    assert "return true;" in method
    assert "V2HandlerWrapper" not in runtime
    assert "PatchExisting()" not in runtime


def test_manual_bot_answer_edit_persists_reviewed_v2_answer_and_refreshes_runtime_index():
    editor = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    incremental = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Incremental.cs")
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert "TryCorrectBotAnswer(e.Seller" in editor
    assert "TrySaveReviewedDirectAnswer(" in editor
    assert 'record.SourceType = "manual_reviewed_direct";' in repository
    assert "SaveRecordsInTransaction(new List<object> { ToRow(record), revision });" in repository
    assert "MirrorOneToLegacy" not in repository
    assert "BotFeatureStore.SaveKnowledgeBase" not in repository
    assert "KnowledgeEngineV2Service.ApplyRecordUpdate(seller, record)" in repository
    assert "internal static void ApplyRecordUpdate" in incremental
    assert '"manual_reviewed_direct"' in public
    assert "IsStrongManualReviewedSimilarMatch(x, query)" in public


def test_manual_takeover_correction_is_auto_learned_only_after_high_confidence_review():
    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    method = learning[
        learning.index("private static async Task CompareManualAnswerAsync"):
        learning.index("private static bool ContainsUnsafeManualLearning")
    ]
    assert "similarity >= 0.92" in method
    assert "should_learn" in method
    assert "confidence < 0.95" in method
    assert "KnowledgeEngineV2Repository.TrySaveReviewedDirectAnswer(" in method
    assert "seller, question, manualAnswer" in method


def test_unverified_broad_session_suggestions_remain_review_only_but_verified_corrections_auto_apply():
    session = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    assert "private const bool EnableAutomaticWrites = false;" in session
    assert "private const bool EnableVerifiedCorpusCorrectionWrites = true;" in session
    assert "ApplyVerifiedCorpusCorrections(session, corpusAnalysis, expectedCurrentFingerprint, out corpusSkipped)" in session
    assert "EnableVerifiedCorpusNewKnowledgeWrites = true" in session
    assert "ApplyVerifiedCorpusNewKnowledge(" in session
    assert "CanAutoApplyVerifiedCorpusCorrection" in session
    assert "candidate.Confidence < 0.95" in session
    assert "candidate.EvidenceMessageIds.Count == 0" in session
    assert "TryApplyVerifiedSessionCorrection(" in session


def test_manual_correction_pins_actual_buyer_question_as_priority_alias():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    method = repository[
        repository.index("public static bool TryCorrectBotAnswer("):
        repository.index("public static bool TryApplyVerifiedSessionCorrection(")
    ]
    assert "AddPreferredAlias(corrected.Aliases, buyerQuestionAlias, 40)" in method
    assert "if (!aliasQuery.ContextDependent && !string.IsNullOrWhiteSpace(aliasQuery.Subject))" in method
    assert "private static List<string> AddPreferredAlias(" in repository
    assert "AddPreferredAlias(record.Aliases, question, 40)" in repository


def test_manual_takeover_learning_keeps_real_question_when_ai_paraphrases_it():
    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    method = learning[
        learning.index("private static async Task CompareManualAnswerAsync"):
        learning.index("private static bool ContainsUnsafeManualLearning")
    ]
    assert "seller, question, manualAnswer" in method
    assert "confidence < 0.95" in method
    assert "ContainsUnsafeManualLearning(question + \" \" + manualAnswer)" in method
    assert "TextSimilarity(question, learnedQuestion)" not in method


def test_v2_runtime_initialization_and_working_memory_are_shop_scoped():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    assert "shopScope.ShopKey, burst.ShopKey" in runtime
    assert "PreparedShopKeys.TryAdd(shopKey, 1)" in runtime
    assert "WarmingShopKeys.TryAdd(shopKey, 1)" in runtime
    assert "ShopSettingsScope.Enter(shop, false)" in runtime
    assert "GetWorkingMemory(shop.ShopKey, seller, buyer)" in public
    assert "UpdateWorkingMemory(shop.ShopKey, seller, buyer, query)" in public
    assert "WorkingKey(string shopKey, string seller, string buyer)" in index


def test_duplicate_manual_exact_aliases_fail_closed():
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    assert "var manualExactMatches = rankedMatches.Where(IsApprovedProductionMatch)" in public
    assert "var manualExactConflict = manualExactMatches.Count > 1;" in public
    assert "manualExactConflict || (manualExact == null && HasConflict(best, second))" in public


def test_durable_manual_save_warms_a_cold_snapshot_before_next_buyer_question():
    incremental = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Incremental.cs")
    method = incremental[
        incremental.index("internal static void ApplyRecordUpdate("):
        incremental.index("internal static void ApplyRecordDelete(")
    ]
    assert "if (!Snapshots.TryGetValue(shop.ShopKey, out current) || current == null)" in method
    assert "Warm(seller);" in method
    assert "just-committed SQLite row" in method


def test_manual_takeover_auto_learning_requires_native_buyer_and_human_message_ids():
    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    queue = learning.split("private static void QueueManualAnswerComparison(", 1)[1].split(
        "private static async Task CompareManualAnswerAsync(", 1
    )[0]
    resolver = learning.split("private static bool TryResolveManualEvidenceMessageIds(", 1)[1].split(
        "private static void QueueManualAnswerComparison(", 1
    )[0]
    compare = learning.split("private static async Task CompareManualAnswerAsync(", 1)[1].split(
        "private static bool ContainsUnsafeManualLearning(", 1
    )[0]

    assert "TryResolveManualEvidenceMessageIds(" in learning
    assert "questionTurn.NativeAliases" in resolver
    assert "answerTurn.NativeAliases" in resolver
    assert "questionMatches.Count != 1 || answerMatches.Count != 1" in resolver
    classifier = learning[
        learning.index("private static bool IsBotAnswer("):
        learning.index("private static string NormalizeBotAnswer(")
    ]
    assert "!IsBotAnswer(seller, buyer, x.Text, x.Timestamp, shopKey, botCards)" in resolver
    assert "SendDeliveryWatchdog.IsKnownBotAnswer(seller, buyer, answer, shopKey)" in classifier
    assert "catch { /* The reliable session-scoped Bot-card snapshot remains authoritative. */ }" in classifier
    assert "var humanReplyTurns = turns.Skip(questionIndex + 1)" in resolver
    assert "humanReplyTurns.Count != 1 || !ReferenceEquals(humanReplyTurns[0], answerTurn)" in resolver
    assert "buyerMessageId.Length == 0 || humanMessageId.Length == 0" in resolver
    assert "buyerMessageId + \"|\" + humanMessageId" in compare
    assert "string.Empty, out saveMessage" not in compare
    assert "buyerMessageId.Length == 0 || humanMessageId.Length == 0" in queue


def test_manual_takeover_direct_answer_is_atomic_reversible_and_respects_operator_disable():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    method = repository.split("public static bool TrySaveReviewedDirectAnswer(string seller, string question,", 1)[1].split(
        "public static bool TryCreateVerifiedSessionKnowledge(", 1
    )[0]

    assert "var suppressed = all.Where(matchingQuestion)" in method
    assert "!x.Enabled" in method
    assert 'x.Status, "deleted"' in method
    assert 'x.Status, "disabled"' in method
    assert "PreviousJson = previousJson" in method
    assert "CurrentJson = JsonConvert.SerializeObject(record)" in method
    assert "MessageRecordId = (sourceMessageId ?? string.Empty).Trim()" in method
    assert "SaveRecordsInTransaction(new List<object> { ToRow(record), revision });" in method
    assert "KnowledgeEngineV2Service.ApplyRecordUpdate(seller, record);" in method


def test_manual_takeover_learning_retries_transient_ai_and_repository_failures():
    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    queue = learning.split("private static void QueueManualAnswerComparison(", 1)[1].split(
        "private static async Task CompareManualAnswerAsync(", 1
    )[0]
    compare = learning.split("private static async Task CompareManualAnswerAsync(", 1)[1].split(
        "private static bool ContainsUnsafeManualLearning(", 1
    )[0]

    assert "for (var attempt = 0; attempt < 3; attempt++)" in queue
    assert "Task.Delay(TimeSpan.FromSeconds(attempt + 1))" in queue
    assert "ManualComparisons.TryRemove(comparisonKey, out ignoredComparison);" in queue
    assert "3次尝试后" in queue
    assert "AI对比上游未返回可靠结构化结果，等待自动重试。" in compare
    assert "AI对比结果不是可恢复的结构化JSON，等待自动重试。" in compare
    assert "saveMessage ?? string.Empty).StartsWith(\"人工直答保存失败：\"" in compare
