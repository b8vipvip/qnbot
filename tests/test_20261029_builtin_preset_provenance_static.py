"""Fixed product link replies are not Knowledge V2 records."""
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]

def src(relative):
    return (ROOT/relative).read_text(encoding="utf-8-sig")

def test_builtin_product_preset_is_exact_match_not_knowledge():
    store=src("src/Bot/ChromeNs/ConversationContextStore.cs")
    assert '我看到您发来的商品链接了，想咨询哪方面呢？' in store
    assert 'internal static bool IsProductLinkPresetReply(string answer)' in store
    assert 'normalized.EndsWith("[A]", StringComparison.OrdinalIgnoreCase)' in store
    assert "ProductLinkReplies.Any(reply =>" in store
    assert "string.Equals(reply, normalized, StringComparison.Ordinal)" in store
    pipeline=src("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert 'productLinkReply, "商品链接预设回复"' in pipeline

def test_fallback_card_not_mistaken_for_real_provenance():
    mirror=src("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert 'ConversationContextStore.IsProductLinkPresetReply(botAnswer)' in mirror
    assert '商品链接预设回复（内置固定话术，非知识库条目）' in mirror
    assert "KnowledgeLearningService.ResolveAnswerSource(" in mirror
    assert '千牛已发送回显（来源记录未留存，不能确定知识库命中）' in mirror
    assert '"千牛Bot发送回显（本地答复卡片未找到）"' not in mirror

def test_view_and_provenance_show_details_instead_of_false_knowledge_locator():
    mirror=src("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert 'ResolveMirrorBotSource(action, messageText)' in mirror
    assert '商品链接预设回复（系统内置固定话术，不属于知识库 V2）' in mirror
    assert 'ConversationContextTurn turn,' in mirror
    assert 'turn.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")' in mirror
    assert '发送者：Bot' in mirror
    assert 'if (IsVerifiedKnowledgeMirrorSource(source))' in mirror
    assert '此回复并非已验证的知识库命中，不会错误跳转知识库' in mirror
    assert 'action.MirrorOpenKnowledge(Window.GetWindow(this));' in mirror
    assert '修改本地答案或知识不会修改已送达买家的消息' in mirror
    for a in ("查看","复制","重发","修改","来源"):
        assert f'Header = "{a}"' in mirror
