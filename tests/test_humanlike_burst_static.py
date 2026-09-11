from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_buyer_message_burst_coordinator_is_in_build():
    targets = text("src/Directory.Build.targets")
    coordinator = text("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "BuyerMessageBurstCoordinator.cs" in targets
    assert "QuietDelayMilliseconds" in coordinator
    assert "ConfirmStableAsync" in coordinator
    assert "买家本轮连续消息" in coordinator


def test_burst_waits_for_fragments_and_collapses_input_artifacts():
    coordinator = text("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    timing = text("src/Bot/ChromeNs/AdaptiveReplyTimingService.cs")
    assert 'if (normalized == previousNormalized) continue;' in coordinator
    assert "normalized.StartsWith(previousNormalized" in coordinator
    assert "previousNormalized.EndsWith(normalized" in coordinator
    assert "baseline = 950;" in coordinator
    assert "baseline = 1200;" in coordinator
    assert "baseline = 700;" in coordinator
    assert "baseline = 420;" in coordinator
    assert "baseline = 350;" in coordinator
    assert "AdaptiveReplyTimingService.AdjustDelay" in coordinator
    assert "Clamp(adjusted, 700, 1450)" in timing
    assert "Clamp(adjusted, 650, 1550)" in timing
    assert "Clamp(adjusted, 600, 1400)" in timing
    assert "Clamp(adjusted, 300, 720)" in timing
    assert "TimeSpan.FromSeconds(4)" in coordinator


def test_single_owner_lane_serializes_premerge_and_hard_cancel_invalidates_dispatch():
    coordinator = text("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "state.PendingRules.Enqueue(item)" in coordinator
    assert "state.Version++" in coordinator
    assert "state.HardCancelVersion++" in coordinator
    assert "return state.HardCancelVersion == capturedHardCancelVersion;" in coordinator
    assert "if (state.Version != capturedVersion || state.Items.Count < 1) continue;" in coordinator
    assert "state.DelayCancellation.Cancel()" in coordinator
    assert "CanonicalPreMergeDecisionService.HandleAsync(" in coordinator


def test_qn_ingests_all_messages_and_invalidates_stale_drafts():
    qn = text("src/Bot/ChromeNs/QN.cs")
    assert "只处理该批次最新一条" not in qn
    assert "_buyerMessageBurstCoordinator.Enqueue" in qn
    assert "旧文本草稿已作废" in qn
    assert "旧视觉草稿已作废" in qn
    assert "ConfirmStableAsync(220)" in qn
    assert 'burst.CombinedQuestion.Replace("\\n", " | ")' in qn
    assert 'burst.CombinedQuestion.Replace("\n", " | ")' not in qn
    assert "deferLearningUntilDelivered" in text("src/Bot/ChromeNs/MyOpenAI.cs")


def test_media_placeholders_and_human_conversation_rules():
    safety = text("src/Bot/ChromeNs/IncomingMessageSafety.cs")
    prompt = text("src/Bot/ChromeNs/MyOpenAI.cs")
    assert "[图片]" in safety and "[视频]" in safety
    assert "[语音]" in safety and "[表情]" in safety
    assert "不要按每一行逐条作答" in prompt
    assert "后一条明确纠正前文" in prompt
    assert "同义重复和连续问号只回应一次" in prompt
    assert "只追问一个最关键的信息" in prompt
    assert "旧答案应作废" in prompt


def test_wecom_notification_contains_buyer_message_text_only():
    bridge = text("services/api-control-plane/wecom_bridge.py")
    handoff = text("src/Bot/ChromeNs/HandoffNotificationService.cs")
    assert "买家消息：\\n" in bridge
    assert "safe_buyer_message" in bridge
    assert "买家消息：\\n" in handoff
    assert "SafeBuyerMessage" in handoff
    assert "[手机号]" in bridge
    assert "[API_KEY]" in bridge