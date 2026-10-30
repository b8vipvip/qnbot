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

    assert 'var sender = isBot ? "Bot" : (isSeller ? seller : buyer);' in mirror
    assert 'turn.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")' in mirror
    assert "HorizontalAlignment.Right" in mirror
    assert "HorizontalAlignment.Left" in mirror
    assert "BuildMessageMirrorDateSeparator" in mirror
    assert "BuildMessageMirrorBotContextMenu" in mirror
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


def test_a_suffix_is_transport_only_and_bot_is_rendered_on_right_with_native_actions():
    conversation = read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")

    assert 'const string marker = "[A]"' in mirror
    assert "TryExtractBotEchoAnswer" in mirror
    assert "var usedNativeBotConversations = new HashSet<CtlConversation>();" in mirror
    assert "if (nativeBotConversation == null)" in mirror
    assert "usedNativeBotConversations.Add(nativeBotConversation);" in mirror
    assert "BuildMessageMirrorBubble(" in mirror
    assert "turn, seller, buyer, botAnswer, nativeBotConversation" in mirror
    assert "messageElement = nativeBotConversation;" not in mirror
    assert "BuildMessageMirrorBotContextMenu(botAction, turn, seller, buyer," in mirror

    # Bot bubbles retain their original actions even though the old Q/A card
    # is no longer the rendered element.
    assert "CreateAnswerContextMenu" in conversation
    assert "MirrorRequestResend" in mirror
    assert "MirrorRequestEdit" in mirror
    assert "MirrorOpenKnowledge" in mirror
    assert "MirrorAnswerSource" in mirror
    for label in ("查看", "复制", "重发", "修改", "来源"):
        assert f'Header = "{label}"' in mirror

    # Human seller and buyer messages follow the same bubble layout.
    assert "messageElement = BuildMessageMirrorBubble(turn, seller, buyer);" in mirror


def test_mirror_renderer_is_wired_into_windows_build():
    targets = read("src/Directory.Build.targets")
    xaml = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml")

    assert "CtlRobot.MessageMirror.cs" in targets
    assert 'Background="#F4F6F8"' in xaml
    assert 'Text="暂无聊天消息"' in xaml
    assert 'Text="正在同步当前买家的千牛聊天记录"' in xaml
