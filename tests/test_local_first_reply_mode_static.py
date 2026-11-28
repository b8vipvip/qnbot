from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_reply_mode_is_shop_scoped_and_defaults_to_ai_first():
    service = read("src/Bot/ChromeNs/ReplyModeService.cs")
    props = read("src/Bot/Directory.Build.props")

    assert 'SettingsKey = "message.reply_mode"' in service
    assert 'AiFirstValue = "ai_first"' in service
    assert 'LocalFirstValue = "local_first"' in service
    assert "ShopContextLocator.ResolveCurrentOrBySellerNick" in service
    assert "ShopSettingsScope.Current" in service
    assert "ShopScopedSettingsStore" in service
    assert "return BotReplyMode.AiFirst;" in service
    assert 'GetDisplayName(BotReplyMode mode)' in service
    assert 'ChromeNs\\ReplyModeService.cs' in props


def test_message_strategy_ui_exposes_ai_first_and_local_first():
    ui = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")

    assert 'result.Add(MakeSectionTitle("消息策略"));' not in ui
    assert '"回复模式"' in ui
    assert '"Bot消息后缀"' in ui
    assert '_replyMode.Items.Add("AI优先")' in ui
    assert '_replyMode.Items.Add("本地优先")' in ui
    assert 'ReplyModeService.GetMode(Seller)' in ui
    assert 'ReplyModeService.Save(' in ui
    assert 'BotMessageSuffixService.GetSuffix(Seller)' in ui
    assert 'BotMessageSuffixService.Save(' in ui
    assert 'pageTitle = "自动回复规则";' not in ui
    assert 'string.Equals(header, pageTitle, StringComparison.Ordinal)' in ui
    assert 'pageTitle = "自动回复规则"' not in ui
    assert '买家5分钟无新消息后' in ui


def test_local_first_high_confidence_v2_knowledge_returns_before_ai():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    route = runtime[
        runtime.index("internal static async Task<KnowledgeV2RuntimeResult> TryHandleWithContextAsync"):
        runtime.index("internal static string BuildContextPromptAddon")
    ]

    resolve = route.index("KnowledgeEngineV2Service.Resolve(")
    mode = route.index("ReplyModeService.IsLocalFirst(burst.SellerNick)")
    direct = route.index("SendTextWithRetryAsync")
    assert resolve < mode < direct
    assert "KnowledgeEngineV2RuntimeBridge.TryHandleWithContextAsync(qn, lease)" in pipeline
    assert pipeline.index("KnowledgeEngineV2RuntimeBridge.TryHandleWithContextAsync(qn, lease)") < pipeline.index(
        "StreamingBuyerAnswerService.GetAnswerAsync("
    )
    assert "if (decision == null || !decision.CanDirectReply" in route
    assert "KnowledgeV2RuntimeResult.HandledResult()" in route

def test_ai_first_keeps_approved_v2_knowledge_as_context_and_does_not_direct_send():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    route = runtime[
        runtime.index("internal static async Task<KnowledgeV2RuntimeResult> TryHandleWithContextAsync"):
        runtime.index("internal static string BuildContextPromptAddon")
    ]

    guard = route.index("if (!ReplyModeService.IsLocalFirst(burst.SellerNick))")
    direct_send = route.index("SendTextWithRetryAsync")
    assert guard < direct_send
    assert "var aiFirstContext = BuildContextPromptAddon(decision);" in route
    assert "return KnowledgeV2RuntimeResult.Continue(aiFirstContext, decision).WithContextDecision(decision);" in route
    assert "KnowledgeV2RuntimeResult.HandledResult()" in route
    assert "OfflineFallbackDecision" in runtime

def test_handoff_and_fixed_rules_still_run_before_local_knowledge():
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    manual = pipeline.index("BotFeatureStore.EvaluateAutoReplyRule(question)")
    plan = pipeline.index("SmartReplyRouterService.BuildPlanAsync(seller, buyer, question, token)")
    assert manual < plan
    assert "命中人工确认规则，未自动回复" in pipeline


def test_local_first_session_learning_waits_five_minutes_and_uses_full_transcript():
    learning = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")

    assert "public const int InactivityMinutes = 5;" in learning
    assert "TimeSpan.FromMinutes(InactivityMinutes)" in learning
    assert "ReplyModeService.IsLocalFirst(session.Seller)" in learning
    assert '"买家"' in learning
    assert '"Bot"' in learning
    assert '"人工客服"' in learning
    assert "聊天时间线（买家/Bot/人工客服全部消息）" in learning
    assert "BuildTranscript(turns, cards)" in learning
    assert "BuildCards(cards)" in learning
    assert "MyOpenAI.CallStructuredChat" in learning


def test_local_first_misses_continue_to_ai_and_session_learning_remains_v2_only():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    session = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")

    route = runtime[
        runtime.index("internal static async Task<KnowledgeV2RuntimeResult> TryHandleWithContextAsync"):
        runtime.index("internal static string BuildContextPromptAddon")
    ]
    finalize = session[
        session.index("private static async Task FinalizeAsync"):
        session.index("private static async Task<JObject> AnalyzeAsync")
    ]

    assert "ReplyModeService.IsLocalFirst(burst.SellerNick)" in route
    assert "if (decision == null || !decision.CanDirectReply" in route
    assert "KnowledgeV2RuntimeResult.Continue(contextPromptAddon)" in route
    assert "StreamingBuyerAnswerService.GetAnswerAsync(" in pipeline
    assert "AnalyzeKnowledgeCorpusAsync(" in finalize
    assert "ApplyVerifiedCorpusNewKnowledge(" in finalize
    assert "KnowledgeEngineV2Repository.TryCreateVerifiedSessionKnowledge(" in session
    assert "BotFeatureStore.SaveKnowledgeBase" not in session

def test_ai_first_keeps_conservative_human_evidence_learning_policy():
    learning = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")

    assert "当前店铺启用了AI优先模式。保持保守学习" in learning
    assert 'localFirst && suggestion.EvidenceType == "conversation_synthesis"' in learning
    assert "IsHumanEvidence(suggestion.EvidenceType)" in learning
    assert 'suggestion.EvidenceType == "bot_only"' in learning
    assert 'suggestion.EvidenceType == "insufficient"' in learning
