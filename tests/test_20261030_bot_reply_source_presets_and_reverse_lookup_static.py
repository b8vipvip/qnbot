"""Regression checks for editable, single-owner Bot reply provenance."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_only_one_store_and_editor_owns_bot_source():
    targets=read("src/Directory.Build.targets")
    ui=read("src/Bot/Knowledge/KnowledgeCenterV2Ui.cs")
    props=read("src/Bot/Directory.Build.props")
    assert "BotReplySourceLedger.cs" not in targets
    assert "KnowledgeV2BotReplySourcePage.cs" not in targets
    assert "KnowledgeV2BotReplyManagementPage.cs" in props
    assert 'Nav("Bot回复管理", () => new KnowledgeV2BotReplyManagementPage' in ui
    assert "OpenBotReplySearch" in ui
    assert "OpenReplySource" in ui

def test_shop_scoped_builtin_presets_and_live_revocation():
    store=read("src/Bot/ChromeNs/BotReplyPresetService.cs")
    context=read("src/Bot/ChromeNs/ConversationContextStore.cs")
    routing=read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "ShopContextLocator.ResolveRuntimeBySellerNick" in store
    assert "new ShopScopedSettingsStore(shop, Paths)" in store
    assert "return new List<BotReplyPreset>();" in store
    assert "GetEnabledPresets(seller)" in context
    assert "GetEnabledPresets(seller).Any" in context
    assert "BotReplyPresetService.ResolveSourceLabel" in routing
    assert "ProductLinkReplies[Math" not in context

def test_provenance_prepared_saved_and_confirmed_echo_never_forged():
    learning=read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    history=read("src/Bot/ChromeNs/BotConversationHistoryStore.cs")
    mirror=read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert "BotConversationHistoryStore.SaveAnswerSourceStamp" in learning
    assert "BotConversationHistoryStore.ResolveAnswerSourceStamp" in learning
    assert "BotConversationHistoryStore.RecordBotEcho(" in mirror
    assert "BuildBotEchoEntityId" in history
    assert "来源未记录（千牛回显已确认，需人工补录）" in mirror
    assert "BotReplyManualSourceEntity" in history
    assert "OriginalSource" in history
    assert "GetManualSource(seller, botEchoRecordId)" in mirror
    assert "KnowledgeLearningService.OverrideAnswerSource(" not in mirror
    assert "KnowledgeLearningService.RegisterAnswerSource(" not in mirror

def test_manager_edits_sources_and_reverse_looks_up_questions_by_reply():
    ui=read("src/Bot/Knowledge/KnowledgeV2BotReplyManagementPage.cs")
    mirror=read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert "BotConversationHistoryStore.UpdateAnswerSource" in ui
    assert "BotReplyPresetService.SavePresets" in ui
    assert "KnowledgeEngineV2Repository.LoadAll" in ui
    assert "answerScore" in ui
    assert ".Where(x => x.Score >= 0.25)" in ui
    assert 'Header = "知识库检索问题"' in mirror
    assert 'Header = "来源"' in mirror
    assert "OpenSourceSearch" in ui
