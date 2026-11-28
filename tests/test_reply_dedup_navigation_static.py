from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8-sig")


def test_conversation_context_menu_can_open_exact_knowledge_entry():
    text = read("src/Bot/AssistWindow/Widget/Robot/CtlConversation.xaml.cs")
    assert 'Header = "查看"' in text
    assert "KnowledgeCenterWindow.ShowManagerAndLocate" in text
    assert "_seller" in text and "_buyer" in text
    assert "_question" in text and "_answer" in text


def test_knowledge_manager_locates_and_scrolls_to_entry():
    manager = read("src/Bot/Knowledge/KnowledgeCenterV2RecordsPage.cs")
    assert "OpenRecordById(string recordId)" in manager
    assert "string.Equals(x.Id, targetId, StringComparison.Ordinal)" in manager
    assert "ScrollIntoView(exact)" in manager
    assert "SelectedItem = exact" in manager
    assert "不会自动选择相似条目" in manager


def test_incoming_timeline_is_recorded_before_burst_generation():
    text = read("src/Bot/ChromeNs/QN.cs")
    refresh = text.index("ConversationContextStore.RefreshAndRecord(message, messageText);")
    enqueue = text.index("_buyerMessageBurstCoordinator.Enqueue", refresh)
    reply_decision = text.index("FirstInquiryFixedReplyService.TryResolve(", enqueue)
    ai = text.index("MyOpenAI.GetAnswer(", reply_decision)
    assert refresh < enqueue < reply_decision < ai


def test_every_text_burst_is_checked_before_display_and_send():
    text = read("src/Bot/ChromeNs/QN.cs")
    fixed = text.index("FirstInquiryFixedReplyService.TryResolve(")
    ai = text.index("MyOpenAI.GetAnswer(", fixed)
    stale_check = text.index("if (!lease.IsCurrent)", ai)
    conditional_check = text.index("if (!usedFirstInquiryFixedReply)", stale_check)
    checked = text.index("ReplyDeduplicationService.EnsureDistinct", conditional_check)
    stable = text.index("ConfirmStableAsync(220)", checked)
    displayed = text.index("ResponseProgressTracker.SetAnswerReady", stable)
    sent = text.index("SendTextWithRetryAsync(", displayed)
    send_call = text[sent:text.index(");", sent) + 2]
    remembered = text.index("ReplyDeduplicationService.RememberDelivered", sent)
    assert fixed < ai < stale_check < conditional_check < checked < stable < displayed < sent < remembered
    assert "burst.BuyerNick, answer, 1, lease.CancellationToken, preSendGuard" in send_call


def test_duplicate_service_regenerates_and_rejects_same_result():
    text = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    assert "SameAnswer(previousAnswer, result.Answer)" in text
    assert "MyOpenAI.CallStructuredChat" in text
    assert "SameAnswer(previousAnswer, regenerated)" in text
    assert "BuildSafeFallback" in text
    assert "AI重答（Knowledge V2上下文约束）" in text
    assert "PreSendAnswerValidator.Validate" in text


def test_contextual_matching_can_use_last_delivered_answer_without_remote_echo():
    assert not (ROOT / "src/Bot/ChromeNs/KnowledgeContextualReplyService.cs").exists()
    dedup = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    assert "ReplyDeduplicationService.RememberDelivered(" in runtime
    assert "ReplyDeduplicationService.RememberDelivered(" in pipeline
    assert "TryGetLastDelivered(seller, buyer, out previousAnswer, out previousAt)" in dedup
    assert "SameAnswer(previousAnswer, result.Answer)" in dedup


def test_new_services_are_included_in_windows_and_wpf_temp_builds():
    text = read("src/Directory.Build.targets")
    assert "ReplyDeduplicationService.cs" in text
    assert "BuyerMessageBurstCoordinator.cs" in text
    assert '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\ReplyDeduplicationService.cs" />' in text
    assert '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\BuyerMessageBurstCoordinator.cs" />' in text
