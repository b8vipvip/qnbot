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
    assert "KnowledgeEngineV2RuntimeBridge.TryHandleAsync(qn, lease)" in entry
    assert entry.index("KnowledgeEngineV2RuntimeBridge.TryHandleAsync(qn, lease)") < entry.index(
        'lease.MarkGenerating("streaming_answer_started")'
    ) < entry.index("StreamingBuyerAnswerService.GetAnswerAsync(")
    assert "KnowledgeEngineV2Service.Resolve(burst.SellerNick, burst.BuyerNick, burst.CombinedQuestion)" in method
    assert "decision == null || !decision.CanDirectReply" in method
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
    assert "Save(seller, record); // Durable write + immediate immutable snapshot update." in repository
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
    assert "ApplyVerifiedCorpusCorrections(session, corpusAnalysis, out corpusSkipped)" in session
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
