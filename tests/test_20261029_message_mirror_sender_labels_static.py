"""Bot messages and human messages share one correctly aligned identity layout."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_exact_time_sender_header_and_alignment():
    src=read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    # All sender labels come from the existing, scoped QN conversation identities.
    assert 'var sender = isBot ? "Bot" : (isSeller ? seller : buyer);' in src
    assert 'turn.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")' in src
    assert 'timestamp + " " + sender' in src
    assert 'Text = caption' in src
    assert 'Text = isSeller ? "客服" : "买家"' not in src
    assert 'HorizontalAlignment = isSeller ? HorizontalAlignment.Right : HorizontalAlignment.Left' in src
    assert 'TextAlignment = isSeller ? TextAlignment.Right : TextAlignment.Left' in src
    assert "BuildMessageMirrorDateSeparator" in src

def test_bot_echo_is_a_right_aligned_bubble_not_left_qa_card():
    src=read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert "TryExtractBotEchoAnswer(turn, out botAnswer)" in src
    assert "turn, seller, buyer, botAnswer, nativeBotConversation" in src
    assert 'var isBot = isSeller && botAction != null;' in src
    assert 'var messageText = turn.Withdrawn ? "已撤回一条消息"' in src
    assert ': isBot ? botAnswer : (turn.Text ?? string.Empty);' in src
    assert "messageElement = nativeBotConversation;" not in src
    assert "nativeBotConversation.UseAnswerOnlyMirrorPresentation();" not in src
    assert 'const string marker = "[A]";' in src
    assert "usedNativeBotConversations.Add(nativeBotConversation);" in src

def test_bot_actions_are_preserved_after_converting_to_bubble():
    src=read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    actions=read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    assert "BuildMessageMirrorBotContextMenu(botAction, turn, seller, buyer," in src
    assert "BuildCopyOnlyContextMenu(messageText, placementTarget)" in src
    assert "action.MirrorRequestResend()" in src
    assert "action.MirrorRequestEdit()" in src
    assert "action.MirrorOpenKnowledge(Window.GetWindow(this))" in src
    assert "action.MirrorAnswerSource" in src
    assert "internal string MirrorAnswerSource" in actions
    assert "nativeBotConversation.ResendRequested += CtlConversation_ResendRequested;" in src
    assert "nativeBotConversation.EditRequested += CtlConversation_EditRequested;" in src
