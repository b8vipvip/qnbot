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
    assert "ShopContextLocator.ResolveBySellerNick" in service
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
    assert 'pageTitle = "自动回复规则"' in ui
    assert '买家5分钟无新消息后' in ui


def test_local_first_high_confidence_knowledge_returns_before_any_ai_call():
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    plan_at = pipeline.index("var plan = await SmartReplyRouterService.BuildPlanAsync(seller, buyer, question, token)")
    local_at = pipeline.index("replyMode == BotReplyMode.LocalFirst", plan_at)
    direct_at = pipeline.index("plan.Route == SmartReplyRouteKind.DirectKnowledge", local_at)
    return_at = pipeline.index("return directAnswer;", direct_at)
    endpoints_at = pipeline.index("var endpoints = AiEndpointStore.GetEnabledEndpoints()", return_at)

    assert plan_at < local_at < direct_at < return_at < endpoints_at
    assert 'RegisterAnswerSource(seller, buyer, question, directAnswer, "智能路由-本地直答")' in pipeline
    assert "本地优先高置信知识直答" in pipeline


def test_ai_first_does_not_short_circuit_on_direct_knowledge_match():
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    direct_block = pipeline[
        pipeline.index("var replyMode = ReplyModeService.GetMode(seller)"):
        pipeline.index("var endpoints = AiEndpointStore.GetEnabledEndpoints()")
    ]

    assert "replyMode == BotReplyMode.LocalFirst" in direct_block
    assert "BotReplyMode.AiFirst" not in direct_block
    assert "return directAnswer;" in direct_block
    assert "StreamMessagesAsync(messages, token, partial)" in pipeline


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


def test_local_first_ai_review_can_synthesize_safe_reusable_qa_and_deduplicates_it():
    learning = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    reviewed = read("src/Bot/ChromeNs/ReviewedKnowledgeLearningService.cs")

    assert "conversation_synthesis" in learning
    assert "suggestion.Confidence < 0.92" in learning
    assert "DeduplicateSuggestions" in learning
    assert ".GroupBy(" in learning
    assert 'localFirst ? "本地优先-会话AI复盘" : "人工接待复盘"' in learning
    assert "ContainsHighRisk" in learning

    # Knowledge-store dedupe remains the second line of defence across different sessions.
    assert "FindExisting(list, question)" in reviewed
    assert "bestScore >= 0.92" in reviewed
    assert "KnowledgeAiService.ContentHash" in reviewed
    assert 'existingSource.Contains("会话AI复盘")' in reviewed
    assert "知识库已存在相同问答内容，已去重，不重复新增" in reviewed


def test_ai_first_keeps_conservative_human_evidence_learning_policy():
    learning = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")

    assert "当前店铺启用了AI优先模式。保持保守学习" in learning
    assert 'localFirst && suggestion.EvidenceType == "conversation_synthesis"' in learning
    assert "IsHumanEvidence(suggestion.EvidenceType)" in learning
    assert 'suggestion.EvidenceType == "bot_only"' in learning
    assert 'suggestion.EvidenceType == "insufficient"' in learning
