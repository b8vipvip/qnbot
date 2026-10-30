"""Regression guards for confirmed Bot echoes and reception turn authority."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def load(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8-sig")


def test_confirmed_bot_echo_survives_missing_local_answer_card():
    mirror = load("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MessageMirror.cs")
    assert "TryExtractBotEchoAnswer(turn, out botAnswer)" in mirror
    assert "FindMessageMirrorActionConversation(" in mirror
    assert "if (nativeBotConversation == null)" in mirror
    assert "nativeBotConversation = CtlConversation.Create(" in mirror
    assert '"千牛Bot发送回显（本地答复卡片未找到）"' not in mirror
    assert 'BotReplyPresetService.TryFindPreset(seller, botAnswer, out matchedPreset)' in mirror
    assert 'BotConversationHistoryStore.RecordBotEcho(' in mirror
    assert 'nativeBotConversation.SetSendResult(true, "千牛回显确认已发送");' in mirror
    assert "nativeBotConversation.ResendRequested += CtlConversation_ResendRequested;" in mirror
    assert "nativeBotConversation.EditRequested += CtlConversation_EditRequested;" in mirror
    assert "turn, seller, buyer, botAnswer, nativeBotConversation" in mirror
    assert "messageElement = nativeBotConversation;" not in mirror
    assert 'const string marker = "[A]";' in mirror
    # A real sent message must not be removed when local matching fails.
    fallback = mirror.split("if (nativeBotConversation == null)", 1)[1].split(
        "usedNativeBotConversations.Add(nativeBotConversation);", 1
    )[0]
    assert "continue;" not in fallback


def test_reply_context_menu_reports_provenance_and_edit_scope():
    conversation = load("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    assert 'Header = "复制"' in conversation
    assert 'Header = "修改"' in conversation
    assert 'Header = "来源"' in conversation
    assert "_answerSource = source;" in conversation
    assert "不会修改已送达买家的千牛消息" in conversation


def test_reception_can_wait_without_upstream_call_or_error():
    source = load("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    assert "ReceptionFlowPolicy.TryChooseSilence(seller, buyer, question, out silentReason)" in source
    assert "return ReceptionFlowPolicy.SilentMarker + silentReason;" in source
    assert "answer.StartsWith(ReceptionFlowPolicy.SilentMarker" in source
    assert 'lease.MarkCompleted("reception_wait_for_evidence");' in source
    assert "conversationCtl.SetSkipped(why);" in source
    assert "ResponseProgressTracker.Complete(burst.SellerNick, burst.BuyerNick);" in source
    assert "MobileOnly.IsMatch(input)" in source
    assert "PhotoRequest.IsMatch(instruction)" in source
    assert 'instruction.IndexOf("手机号", StringComparison.Ordinal) >= 0' in source


def test_human_reply_supersedes_old_normal_ai_output_but_not_order_ledger():
    source = load("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    assert "var snapshot = new BuyerSessionAgent().GetSnapshot(seller, buyer);" in source
    assert "x.Kind == BuyerSessionEventKind.SellerHumanReply" in source
    assert "x.SourceTimestamp > dispatchedAt.AddMilliseconds(120)" in source
    assert "人工客服已在本轮回复，抑制未发送的普通AI答案" in source
    assert "if (!ParallelReplyRelevanceGate.ShouldSend(" in source
    assert 'lease.MarkCompleted("streaming_relevance_suppressed");' in source


def test_workflow_prompt_requires_visual_evidence_and_store_link_grounding():
    source = load("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    assert "ReceptionFlowPolicy.BuildPromptAddon(seller, buyer, question)" in source
    assert "不能因为商品名称含会员就回答" in source
    assert "已确认设备型号一致的教程链接" in source
    assert "不能编造教程链接" in source
