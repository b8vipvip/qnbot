"""Source checks for editable, shop-scoped product link presets."""
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]

def src(relative):
    return (ROOT/relative).read_text(encoding="utf-8-sig")

def test_product_link_preset_provenance_is_exact_and_editable():
    store=src("src/Bot/ChromeNs/ConversationContextStore.cs")
    catalog=src("src/Bot/ChromeNs/BotReplyPresetService.cs")
    pipeline=src("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert '我看到您发来的商品链接了，想咨询哪方面呢？' in catalog
    assert 'internal static bool IsProductLinkPresetReply(string seller, string answer)' in store
    assert "BotReplyPresetService.IsPresetReply(seller, answer)" in store
    assert "TryFindPreset(string seller, string answer" in catalog
    assert 'normalized.EndsWith("[A]", StringComparison.OrdinalIgnoreCase)' not in store
    assert "BotReplyPresetService.ResolveSourceLabel(item.SellerNick, productLinkReply)" in pipeline
    assert "ProductLinkReplies[Math" not in store

def test_confirmed_bot_echo_stamps_own_source_or_marks_unknown():
    mirror=src("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert 'BotReplyPresetService.TryFindPreset(seller, botAnswer, out matchedPreset)' in mirror
    assert "BotConversationHistoryStore.ResolveAnswerSourceStamp(" in mirror
    assert "BotConversationHistoryStore.RecordBotEcho(" in mirror
    assert '来源未记录（千牛回显已确认，需人工补录）' in mirror
    assert '"千牛Bot发送回显（本地答复卡片未找到）"' not in mirror

def test_view_source_edit_and_reverse_lookup_available_on_bot_bubble():
    mirror=src("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert "ResolveMirrorBotSource(seller, buyer, question, action, messageText)" in mirror
    assert 'turn.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")' in mirror
    assert '发送者：Bot' in mirror
    assert 'Header = "知识库检索问题"' in mirror
    assert "KnowledgeCenterV2UiBridge.ShowBotReplySearch" in mirror
    assert "BotReplySourceEditorWindow.Edit" in mirror
    assert "BotConversationHistoryStore.UpdateAnswerSource" in mirror
    for label in ("查看","复制","重发","修改","来源"):
        assert f'Header = "{label}"' in mirror
