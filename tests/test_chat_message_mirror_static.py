from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_bot_panel_renders_qianniu_style_message_timeline_instead_of_qa_cards():
    source = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml.cs")
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")

    assert "StartMessageMirror();" in source
    assert "RefreshMessageMirror(true);" in source
    assert "stkDialog.Children.Add(ctlConversation)" not in source

    assert 'Text = isSeller ? "客服" : "买家"' in mirror
    assert "HorizontalAlignment.Right" in mirror
    assert "HorizontalAlignment.Left" in mirror
    assert 'Text = "Bot"' in mirror
    assert "BuildMessageMirrorDateSeparator" in mirror
    assert "BuildNativeBotMessageBubble" in mirror
    assert "BuildCopyOnlyContextMenu" in mirror


def test_mirror_uses_real_buyer_and_seller_chat_events_and_remote_history():
    qn = read("src/Bot/ChromeNs/QN.cs")
    context = read("src/Bot/ChromeNs/ConversationContextStore.cs")

    seller_marker = """if (IsSellerMessage(message))
            {"""
    buyer_marker = """if (!_handledBuyerMessageDeduplicator.TryAccept(messageKey))
            {"""

    assert seller_marker in qn
    assert buyer_marker in qn
    assert qn.count("ConversationContextStore.RefreshAndRecord(message, messageText);") >= 2

    assert "GetMirrorTurns(" in context
    assert "Math.Min(100, maxTurns)" in context
    assert "RequestMirrorRemoteRefresh(" in context
    assert "requestedCount = 40" in context
    assert "Math.Min(100, requestedCount)" in context
    assert "RefreshRemoteHistory(state, shop, seller, buyer, ccode, 100);" in context
    assert ".ThenBy(t => t.MessageKey" not in context


def test_a_suffix_is_authoritative_bot_marker_and_uses_native_answer_ui():
    conversation = read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")

    assert 'const string marker = "[A]"' in mirror
    assert "TryExtractBotEchoAnswer" in mirror
    assert "BuildNativeBotMessageBubble" in mirror
    assert "nativeConversation.MirrorAnswerText" in mirror
    assert "nativeConversation.CreateAnswerContextMenu(bubble)" in mirror
    assert "BuildMessageMirrorBubble(turn)" in mirror

    # The native answer menu is the same implementation used by the old answer card.
    assert "CreateAnswerContextMenu" in conversation
    assert 'Header = "查看"' in conversation
    assert 'Header = "复制"' in conversation
    assert 'Header = "重发"' in conversation
    assert 'Header = "修改"' in conversation
    assert "var menu = CreateAnswerContextMenu(txtAnswer);" in conversation

    # Human seller messages must remain plain Qianniu mirrors; matching text alone
    # can no longer turn a human message into a Bot message.
    assert "FindMessageMirrorActionConversation(seller, buyer, turn.Text)" not in mirror


def test_mirror_renderer_is_wired_into_windows_build():
    targets = read("src/Directory.Build.targets")
    xaml = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml")

    assert "CtlRobot.MessageMirror.cs" in targets
    assert 'Background="#F4F6F8"' in xaml
    assert 'Text="暂无聊天消息"' in xaml
    assert 'Text="正在同步当前买家的千牛聊天记录"' in xaml
