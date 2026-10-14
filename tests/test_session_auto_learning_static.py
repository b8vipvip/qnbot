from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_session_learning_waits_five_minutes_and_keeps_audit_records():
    source = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")

    assert "public const int InactivityMinutes = 5" in source
    assert "public const int SellerQuietSeconds = 30" in source
    assert "ConversationSessionLearningReportEntity" in source
    assert "StoreReplyStyleProfileEntity" in source
    assert 'report.Status = "学习完成"' in source
    assert "ResumePending" in source


def test_session_learning_rejects_bot_only_and_high_risk_self_learning():
    source = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")

    assert 'suggestion.EvidenceType == "bot_only"' in source
    assert 'suggestion.EvidenceType == "insufficient"' in source
    assert 'suggestion.Confidence < 0.86' in source
    assert "ContainsHighRisk" in source
    assert "禁止Bot自我学习" in source
    assert "withdrawn_bot_then_manual" in source
    assert "manual_correction" in source


def test_reviewed_knowledge_prefers_strong_human_corrections():
    source = read("src/Bot/ChromeNs/ReviewedKnowledgeLearningService.cs")

    assert 'evidenceType == "manual_correction"' in source
    assert 'evidenceType == "withdrawn_bot_then_manual"' in source
    assert "existingLooksCurated" in source
    assert "confidence < 0.95" in source
    assert "existing.AiGenerated = false" in source
    assert 'SourceType = sourceType' in source


def test_runtime_bridge_uses_exact_seller_and_injects_store_style_as_system_context():
    source = read("src/Bot/ChromeNs/ConversationSessionLearningRuntimeBridge.cs")

    assert "QN.FindExistingBySellerNick(seller)" in source
    assert "?? QN.CurQN" not in source
    assert "InjectLearnedStyle" in source
    assert 'Role = "system"' in source
    assert "BuildReplyStylePromptAddon(seller)" in source
    assert '.Where(x => x.Role == "user" || x.Role == "assistant")' in source


def test_bot_history_can_be_loaded_by_session_time_range():
    source = read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")

    assert "LoadRange(" in source
    assert "CreatedAtTicks >= ? and CreatedAtTicks <= ?" in source


def test_session_learning_is_initialized_and_visible_in_logs_ui():
    app = read("src/Bot/App.xaml.cs")
    ui = read("src/Bot/Options/ConversationSessionLearningUi.cs")
    targets = read("src/Directory.Build.targets")

    assert "ConversationSessionLearningService.Initialize();" in app
    assert "ConversationSessionLearningUi.Initialize();" in app
    assert 'TabHeader = "自动学习记录"' in ui
    assert "ConversationSessionLearningService.ReportsChanged" in ui
    assert "ConversationSessionLearningService.cs" in targets
    assert "ConversationSessionLearningRuntimeBridge.cs" in targets
    assert "ReviewedKnowledgeLearningService.cs" in targets
    assert "ConversationSessionLearningUi.cs" in targets


def test_five_minute_session_and_shop_safe_learning():
    s = read("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    assert "Schedule(existing, TimeSpan.FromMinutes(InactivityMinutes));" in s
    assert "TimeSpan.FromMinutes(InactivityMinutes) - (DateTime.Now - session.LastActivityAt)" in s
    assert "TimeSpan.FromSeconds(SellerQuietSeconds) - (DateTime.Now - session.LastActivityAt)" not in s
    assert "ShopContextLocator.ResolveBySellerNick(session.Seller)" in s
    assert "using (ShopSettingsScope.Enter(shop))" in s
    assert "CountHumanSellerTurns(turns, cards) == 0" in s


def test_ai_history_exposes_existing_session_learning_reports():
    ui = read("src/Bot/Knowledge/KnowledgeCenterV2AiToolsUi.cs")
    assert "ConversationSessionLearningService.GetReports(1000)" in ui
    assert "string.Equals(x.Seller, _seller, StringComparison.Ordinal)" in ui
    assert '"会话自动学习"' in ui
    assert "x.AppliedCount" in ui and "x.SkippedCount" in ui
