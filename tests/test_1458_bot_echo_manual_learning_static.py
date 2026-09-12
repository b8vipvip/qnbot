from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
KNOWLEDGE = ROOT / "src" / "Bot" / "ChromeNs" / "KnowledgeLearningService.cs"
RUNTIME_BRIDGE = ROOT / "src" / "Bot" / "ChromeNs" / "BuyerSessionAgentRuntimeBridge.cs"
DELIVERY_WATCHDOG = ROOT / "src" / "Bot" / "ChromeNs" / "SendDeliveryWatchdog.cs"


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


def test_manual_learning_reuses_delivery_watchdog_bot_echo_authority():
    learning = _text(KNOWLEDGE)
    watchdog = _text(DELIVERY_WATCHDOG)
    runtime = _text(RUNTIME_BRIDGE)

    authority_call = "SendDeliveryWatchdog.IsKnownBotAnswer"
    assert authority_call in learning
    assert "public static bool IsKnownBotAnswer(" in watchdog
    assert "KnownBotAnswers[AnswerKey(seller, buyer, answer)]" in watchdog
    # The runtime keeps Bot and human seller events distinct; the learning guard consumes
    # the same delivery ledger that establishes Bot ownership instead of duplicating heuristics.
    assert "SellerBotEcho" in runtime
    assert "SellerHumanReply" in runtime


def test_bot_echo_identity_canonicalizes_qianniu_transport_suffix_once():
    watchdog = _text(DELIVERY_WATCHDOG)
    method_start = watchdog.index("private static string Normalize(string value)")
    method = watchdog[method_start:]

    # Qianniu can expose a confirmed Bot seller echo as "... [A]" while the delivery
    # ledger records the buyer-visible body. Keep this transport normalization inside
    # the watchdog's AnswerKey authority so learning does not add another decision layer.
    strip_transport = 'Regex.Replace(value.Trim(), @"\\s*\\[A\\]\\s*$", string.Empty, RegexOptions.IgnoreCase)'
    build_key = "return ConversationKey(seller, buyer) + \"#\" + Normalize(answer);"
    assert build_key in watchdog
    assert strip_transport in method
    assert method.index(strip_transport) < method.index('return Regex.Replace(value.Trim(), @"\\s+", string.Empty);')


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
