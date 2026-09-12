from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
KNOWLEDGE = ROOT / "src" / "Bot" / "ChromeNs" / "KnowledgeLearningService.cs"
RUNTIME_BRIDGE = ROOT / "src" / "Bot" / "ChromeNs" / "BuyerSessionAgentRuntimeBridge.cs"


def _text(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def test_known_bot_seller_echo_is_rejected_before_manual_learning_side_effects():
    source = _text(KNOWLEDGE)
    method_start = source.index("public static bool TryBlockForManualReply(")
    method_end = source.index("public static bool TryTakeSendBlock(", method_start)
    method = source[method_start:method_end]

    guard = "SendDeliveryWatchdog.IsKnownBotAnswer(seller, buyer, sellerEcho)"
    manual_source = 'RegisterAnswerSource(seller, buyer, question, manualAnswer, "人工回复")'
    comparison = "QueueManualAnswerComparison(question, candidateAnswer, manualAnswer, seller, buyer)"

    assert guard in method
    assert manual_source in method
    assert comparison in method
    assert method.index(guard) < method.index(manual_source)
    assert method.index(guard) < method.index(comparison)
    assert "manualAnswer = sellerEcho;" in method


def test_manual_learning_reuses_runtime_bot_echo_authority():
    learning = _text(KNOWLEDGE)
    runtime = _text(RUNTIME_BRIDGE)

    authority_call = "SendDeliveryWatchdog.IsKnownBotAnswer"
    assert authority_call in learning
    assert authority_call in runtime
    assert "SellerBotEcho" in runtime
    assert "SellerHumanReply" in runtime


def test_real_human_reply_path_remains_available_after_bot_echo_guard():
    source = _text(KNOWLEDGE)
    method_start = source.index("public static bool TryBlockForManualReply(")
    method_end = source.index("public static bool TryTakeSendBlock(", method_start)
    method = source[method_start:method_end]

    guard_end = method.index("manualAnswer = sellerEcho;")
    human_tail = method[guard_end:]
    assert 'RegisterAnswerSource(seller, buyer, question, manualAnswer, "人工回复")' in human_tail
    assert "QueueManualAnswerComparison(question, candidateAnswer, manualAnswer, seller, buyer)" in human_tail
    assert "检测到本店客服人工回复但不取消Bot发送" in human_tail
