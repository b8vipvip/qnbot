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
    assert "BuildMessageMirrorContextMenu" in mirror


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
    assert 'count = 40' in context
    assert "RefreshRemoteHistory(state, shop, seller, buyer, ccode);" in context


def test_message_mirror_keeps_bot_reply_actions_without_showing_legacy_cards():
    conversation = read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    mirror = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")

    assert "MirrorAnswerText" in conversation
    assert "MirrorCanResend" in conversation
    assert "MirrorRequestResend" in conversation
    assert "MirrorRequestEdit" in conversation
    assert "MirrorOpenKnowledge" in conversation

    assert 'Header = "查看知识记录"' in mirror
    assert 'Header = "重发Bot回复"' in mirror
    assert 'Header = "修改Bot回复并学习"' in mirror


def test_mirror_renderer_is_wired_into_windows_build():
    targets = read("src/Directory.Build.targets")
    xaml = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.xaml")

    assert "CtlRobot.MessageMirror.cs" in targets
    assert 'Background="#F4F6F8"' in xaml
    assert 'Text="暂无聊天消息"' in xaml
    assert 'Text="正在同步当前买家的千牛聊天记录"' in xaml
