from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_message_menu_has_resend_and_edit():
    source = text("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    assert 'Header = "重发"' in source
    assert 'Header = "修改"' in source
    assert "EditRequested" in source


def test_local_first_and_source_labels():
    source = text("src/Bot/ChromeNs/MyOpenAI.cs")
    assert "TryFindLocalAnswer" in source
    assert '"本地"' in source
    assert '"AI生成"' in source


def test_manual_reply_comparison_learning_is_shop_scoped_and_blocks_current_bot_send():
    rpa = text("src/Bot/ChromeNs/QNRpa.cs")
    service = text("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    assert "TryBlockForManualReply" in rpa
    assert "当前Bot发送已阻止并进入对比学习" in service
    assert "ParallelReplyRelevanceGate.IsGreetingOnly(sellerEcho)" in service
    assert "return true;" in service
    assert "QueueManualAnswerComparison" in service
    assert "CompareManualAnswerAsync" in service
    assert "AllowNextManualSend" in service
    assert "ScopeKey()" in service
    assert "ShopSettingsScope.Enter(shop)" in service


def test_learning_dedup_sensitive_redaction_and_per_shop_save_lock():
    source = text("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    assert "ContentHash" in source
    assert "已用人工确认答案更新本店知识库" in source
    assert "SaveLocks.GetOrAdd(ScopeKey()" in source
    assert "ShopContextLocator.ResolveRuntimeBySellerNick" in source
    assert "[手机号]" in source
    assert "[API_KEY]" in source
    assert "confidence < 0.95" in source
    assert "KnowledgeEngineV2Repository.TrySaveReviewedDirectAnswer(" in source
    queue = source.split("private static void QueueManualAnswerComparison(", 1)[1].split(
        "private static async Task CompareManualAnswerAsync(", 1
    )[0]
    assert "string shopKey" in queue
    assert "ResolveRuntimeByShopKey(shopKey)" in queue
    assert "shop.ShopKey, shopKey, StringComparison.Ordinal" in queue
    assert "var comparisonKey = shopKey + \"|\"" in queue
    assert "ContainsUnsafeManualLearning" in source


def test_knowledge_manager_refreshes_after_learning():
    source = text("src/Bot/Knowledge/KnowledgeManagerControl.cs")
    assert "KnowledgeBaseChanged" in source
    assert "OnKnowledgeBaseChanged" in source

def test_unavailable_session_history_refresh_is_retried():
    source = text("src/Bot/ChromeNs/ConversationSessionLearningService.cs")
    section = source.split("if (historyCoverage == null || !historyCoverage.IsComplete)", 1)[1].split(
        "if (turns.Count(x => x.Role == \"user\"", 1)[0]
    assert 'historyCoverage == null' in section
    assert '"history_refresh_unavailable"' in section
    assert "historyErrorCode != \"missing_conversation_scope\"" in section
    assert "historyErrorCode != \"invalid_session_window\"" in section
    assert "historyErrorCode != \"missing_conversation_code\"" in section
    assert "session.HistoryRetryCount < 3" in section
    assert 'report.Error = "history_coverage_incomplete:" + historyErrorCode;' in section
