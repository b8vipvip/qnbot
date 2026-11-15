from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_order_reply_has_action_level_idempotency_independent_of_event_hub():
    src = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "TryBeginExecution(plan, out actionReason)" in src
    assert "order-reply-action-state.json" in src
    assert "action_already_delivered" in src
    assert "action_inflight" in src
    assert "FinishExecution(" in src
    # The business side-effect ledger deliberately uses a plan action kind and canonical order id;
    # lifecycle Created/Paid dedupe remains the responsibility of OrderEventHub.
    start = src.index("private static bool SameAction")
    end = src.index("internal static bool ArePrecisionAliases", start)
    assert "EventType" not in src[start:end]


def test_long_order_id_precision_alias_is_guarded_before_send():
    src = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "IsSuspiciousRoundedOrderId" in src
    assert "ArePrecisionAliases" in src
    assert "precision_risk_order_id" in src
    assert "delta <= 4096UL" in src
    exact = 5127395078262028714
    rounded = 5127395078262028000
    assert str(rounded).endswith("000")
    assert 0 < abs(exact - rounded) <= 4096


def test_timeout_or_early_ai_failure_cannot_be_relabelled_completed():
    src = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "BuyerSessionAgentState.Generating" in src
    assert 'lease.MarkFailed("reply_pipeline_returned_without_ready")' in src
    assert "returnedWithoutReady && burst.HasReplyableItem" in src
    assert "non_replyable_media_skipped" in src


def test_wecom_payload_is_clamped_below_control_plane_limit():
    src = read("src/Bot/ChromeNs/WeComAppBridgeClient.cs")
    assert '["reason"] = SafePayload(rawReason, 480)' in src
    assert '["error"] = SafePayload(error, 480)' in src
    assert "private static string SafePayload" in src


def test_central_log_redaction_covers_colons_and_json_fields():
    src = read("src/BotLib/Log.cs")
    assert "(?:=|:|：)" in src
    assert "RuntimeIdentityJsonFieldRegex" in src
    assert '[@' not in src  # simple guard against an accidental malformed token splice
    # C# verbatim strings must double embedded quote characters; \" inside @"..." is invalid.
    json_regex_start = src.index("RuntimeIdentityJsonFieldRegex")
    json_regex_end = src.index("RegexOptions.IgnoreCase", json_regex_start)
    regex_block = src[json_regex_start:json_regex_end]
    assert '[""\']' in regex_block
    assert '[\\"\']' not in regex_block


def test_duplicate_page_websockets_are_quarantined_not_eagerly_initialized():
    src = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    assert "_duplicateShopSessions" in src
    assert "RecordAuthoritativeCdpSessionCount" in src
    assert "业务CDP=" in src and "页面通道=" in src
    connected = src.index("webSocket.NewSessionConnected")
    received = src.index("webSocket.NewMessageReceived", connected)
    assert "GetOrCreateClient(session);" not in src[connected:received]
    assert 'wMsg.Type == "onConversationChange"' in src
    assert "GetOrCreateClient(session);" in src[received:]


def test_message_center_notify_is_recovered_from_duplicate_pages():
    src = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    start = src.index("private static bool IsRecoverableInboundType")
    end = src.index("private static void ObserveStatusSeller", start)
    assert 'string.Equals(type, "messageCenterNotify", StringComparison.Ordinal)' in src[start:end]
    assert 'string.Equals(type, "onChatDlgActive", StringComparison.Ordinal)' not in src[start:end]
