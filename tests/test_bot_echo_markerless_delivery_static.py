from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def text(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_delivery_matching_uses_one_canonical_buyer_visible_identity():
    watchdog = text("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")

    normalize = watchdog.index("private static string Normalize(string value)")
    strip_marker = watchdog.index("BotOutboundMessageFormatter.StripAiMarker", normalize)
    strip_transport = watchdog.index('Regex.Replace(value.Trim(), @"\\s*\\[A\\]\\s*$"', strip_marker)
    whitespace = watchdog.index('return Regex.Replace(value.Trim(), @"\\s+", string.Empty);', strip_transport)
    answer_key = watchdog.index('return ConversationKey(seller, buyer, shopKey) + "#" + Normalize(answer);')

    # Bot ownership is decided by the watchdog ledger. Both the internal AI marker and
    # Qianniu's transport-only [A] echo suffix must disappear before the AnswerKey is built,
    # rather than being compensated for later in manual-learning code.
    assert normalize < strip_marker < strip_transport < whitespace
    assert answer_key >= 0


def test_rpa_registers_pending_delivery_before_real_send_action():
    rpa = text("src/Bot/ChromeNs/QNRpa.cs")

    pending = rpa.index("SendDeliveryWatchdog.EnsurePending(")
    assert "shopKey" in rpa[pending:pending + 180]
    real_send = rpa.index("TrySendTextNativeFirstAsync(", pending)

    assert pending < real_send


def test_human_observation_still_requires_bot_delivery_checks_to_fail_first():
    monitor = text("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")

    confirm = monitor.index("TryConfirmBotDelivery(seller, buyer, texts, shopKey)")
    marker = monitor.index("texts.FirstOrDefault(IsExplicitBotAuthoredReply)", confirm)
    manual = monitor.index("ResponseProgressTracker.MarkManualIntervention", marker)

    assert confirm < marker < manual
    assert "CancelActiveBuyerGeneration" not in monitor
    assert "Bot任务继续" in monitor


def test_first_inquiry_and_segmented_order_replies_share_the_reliable_send_path():
    deterministic = text("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    qn = text("src/Bot/ChromeNs/QN.cs")

    assert "FirstInquiryFixedReplyService.TryResolve" in deterministic
    assert "item.BuyerNick, answer, 3, generationToken, preSendGuard" in deterministic
    assert 'const string segmentToken = "{分段符}"' in qn
    assert "await SendTextWithRetryAsync(buyer, segment, retryCount, cancellationToken, preSendGuard)" in qn

def test_legacy_fixed_reply_rechecks_manual_takeover_before_send_and_retry():
    deterministic = text("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    method = deterministic[
        deterministic.index("private static async Task<bool> SendFixedAsync"):
        deterministic.index("public static async Task<bool> TryHandleAsync")
    ]

    assert "Func<bool> preSendGuard = () =>" in method
    assert "Params.Robot.CanUseRobotReal" in method
    assert "Params.Robot.GetIsAutoReply()" in method
    assert method.index("Params.Robot.GetIsAutoReply()") < method.index(
        "ParallelReplyRelevanceGate.ShouldSend("
    )
    assert "ParallelReplyRelevanceGate.ShouldSend(" in method
    assert "item.ShopKey" in method
    assert "item.BuyerNick, answer, 3, generationToken, preSendGuard" in method

