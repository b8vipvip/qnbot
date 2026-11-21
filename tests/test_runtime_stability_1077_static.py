from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(rel):
    return (ROOT / rel).read_text(encoding="utf-8-sig")


def test_deterministic_rule_gates_follow_generation_cancellation_with_correct_failure_policy():
    s = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    assert "await gate.WaitAsync();" not in s

    # Ordinary work-hours fixed rules must serialize for the lifetime of the current generation.
    # A timeout must not fail-open into AI while an earlier deterministic reply may still be sending.
    ordinary_gate = s.index("var generationToken = item.SessionGeneration > 0")
    offhours = s.index("private static async Task<bool> HandleOffHoursExclusiveAsync")
    ordinary_block = s[ordinary_gate:offhours]
    assert "GetCancellationToken(" in ordinary_block
    assert "AcquireKeyedGateAsync(BuyerGates, key, generationToken)" in ordinary_block
    assert "gateLease.Dispose()" in ordinary_block
    assert "WaitAsync(1800)" not in ordinary_block
    assert "已放行消息合并/AI链路" not in ordinary_block
    assert "固定规则串行等待期间generation已失效" in ordinary_block
    assert "return false;" in ordinary_block

    # Off-hours remains separately bounded + fail-closed because no Knowledge/AI answer is allowed
    # during the configured off-hours window. Its lease also releases the per-buyer entry.
    scoped = s.index("private static async Task<bool> HandleScopedBeforeMergeAsync", offhours)
    offhours_block = s[offhours:scoped]
    assert "TryAcquireKeyedGateAsync(OffHoursGates, buyerKey, 1800)" in offhours_block
    assert "gateLease == null" in offhours_block
    assert "gateLease.Dispose()" in offhours_block
    assert "fail-closed阻止Knowledge/AI链路" in offhours_block
    assert "return false;" in offhours_block


def test_forwarded_duplicate_session_cannot_be_promoted_for_commands():
    s = read("src/Bot/ChromeNs/CDPClient.cs")
    assert 'PreferRuntimeSession(sellerNick, physicalSourceSession, buyerNick, "onConversationChange")' not in s
    assert 'PreferRuntimeSession(shopKey, sellerNick, SessionId, buyerNick, "onConversationChange")' in s


def test_order_retry_requires_remote_delivery_verification():
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    verifier = read("src/Bot/ChromeNs/QN.DeliveryVerification.cs")
    assert "VerifySellerEchoInRemoteHistoryAsync" in order
    assert "RemoteSellerEchoVerification.Unavailable" in order
    assert "MarkDeliveryUncertain" in order
    assert "action_delivery_uncertain" in order
    assert '"im.singlemsg.GetRemoteHisMsg"' in verifier
    assert "RemoteSellerEchoVerification.Delivered" in verifier


def test_recovered_buyer_media_uses_verified_conversation_plus_buyer_alias():
    s = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    assert "IsRecoveredBuyerMessageForTarget" in s
    assert "AreBuyerAliasesEquivalentInShop(seller, message.fromid.nick, buyer)" in s


def test_raw_receive_payload_is_not_logged():
    s = read("src/Bot/ChromeNs/QN.cs")
    assert 'Log.Info("收到千牛新消息事件: " + e.Message)' not in s
    assert 'Log.Error("收到新消息但无法解析: " + e.Message)' not in s
    assert "收到千牛新消息事件: payloadLength=" in s


def test_manual_comparison_cancellation_is_not_generic_failure():
    files = list((ROOT / "src").rglob("*.cs"))
    sources = [p.read_text(encoding="utf-8-sig") for p in files]
    assert any("人工答案对比学习任务已取消" in s and "catch (OperationCanceledException)" in s for s in sources)


def test_order_delivery_uncertain_does_not_become_long_delivered_reservation():
    s = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert 'string.Equals(actionReason, "action_already_delivered", StringComparison.Ordinal)' in s
    assert 'OrderPlacedAutoReplyService.Complete(plan, true);' in s
    assert 'else if (!string.Equals(actionReason, "action_inflight", StringComparison.Ordinal))' in s
    assert '!string.Equals(actionReason, "precision_risk_order_id", StringComparison.Ordinal)' not in s


def test_order_action_identity_canonicalizes_buyer_aliases():
    s = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "private static string NormalizeBuyer(string seller, string buyer)" in s
    assert "BuyerIdentityAliasService.ResolveInternalNick" in s
    assert 'NormalizeBuyer(record.Seller, record.Buyer) == NormalizeBuyer(plan.Seller, plan.Buyer)' in s
    assert 'Normalize(seller) + "#" + NormalizeBuyer(seller, buyer)' in s


def test_buyer_business_dedupe_is_claimed_only_after_role_classification():
    s = read("src/Bot/ChromeNs/QN.cs")
    method = s[s.index("private Task ProcessIncomingMessageAsync"):s.index("private async Task ProcessBuyerBurstAsync")]
    seller_pos = method.index("if (IsSellerMessage(message))")
    buyer_pos = method.index("if (!IsBuyerMessage(message))")
    handled_pos = method.index("_handledBuyerMessageDeduplicator.TryAccept(messageKey)")
    transport_pos = method.index("_incomingMessageDeduplicator.TryAccept(messageKey)")
    assert seller_pos < transport_pos < buyer_pos < handled_pos
    assert method.count("_incomingMessageDeduplicator.TryAccept(messageKey)") == 1
    assert "This is the authoritative business claim" in method


def test_legacy_text_path_cannot_complete_an_ai_error():
    s = read("src/Bot/ChromeNs/QN.cs")
    method = s[s.index("private async Task ProcessTextBurstAsync"):s.index("private async Task ProcessBuyerBurstAsync") if False else s.index("private async Task ProcessVisionBurstAsync")]
    failure_check = 'if (string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误：", StringComparison.Ordinal))'
    assert failure_check in method
    assert method.index(failure_check) < method.index("ResponseProgressTracker.SetAnswerReady(")
    failure_block = method[method.index(failure_check):method.index("var answerReadyAt = DateTime.Now;")]
    assert "ResponseProgressTracker.Fail" in failure_block
    assert "ResponseProgressTracker.Complete" not in failure_block


def test_streaming_timeout_is_terminal_failure_not_completed():
    s = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    catch_start = s.index("catch (OperationCanceledException)")
    catch_end = s.index("catch (Exception ex)", catch_start)
    block = s[catch_start:catch_end]
    assert "ResponseProgressTracker.Fail" in block
    assert "return;" in block
    assert "ResponseProgressTracker.Complete" not in block


def test_wecom_reason_is_bounded_below_control_plane_limit():
    s = read("src/Bot/ChromeNs/WeComAppBridgeClient.cs")
    assert '["reason"] = SafePayload(rawReason, 480)' in s
    assert "schema limits reason to 500" in s


def test_central_log_redaction_covers_colons_and_json_identity_fields():
    s = read("src/BotLib/Log.cs")
    assert "(?:=|:|：)" in s
    assert "RuntimeIdentityJsonFieldRegex" in s
    for key in ("seller", "buyer", "session", "客服", "买家"):
        assert key in s


def test_websocket_diagnostics_distinguish_page_channels_from_business_cdp():
    s = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    assert '"已连接｜业务CDP=" + authoritativeCdpSessionCount + "｜页面通道=" + wsSessionCount' in s
    assert "RecordAuthoritativeCdpSessionCount" in s


def test_raw_order_id_literal_wins_before_json_numeric_rounding():
    direct = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    order = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "RawOrderIdKeyRegex" in direct
    assert "internal static string ExtractExactOrderIdFromRaw(string raw)" in direct
    envelope = direct[direct.index("private static NotificationEnvelope BuildEnvelope"):direct.index("private static List<FlatValue> Flatten")]
    assert envelope.index("ExtractExactOrderIdFromRaw(raw)") < envelope.index("FindValue(flat, OrderIdKeys)")
    assert "string exactOrderIdHint = null" in direct
    assert "string exactOrderIdHint = null" in order
    plan = order[order.index("public static bool TryCreatePlan"):order.index("private static bool TryCreateBuyerFollowUpPlan")]
    assert plan.index("snapshot.OrderId = exactOrderId;") < plan.index("OrderEventHub.Publish(snapshot)")


def test_regression_order_id_above_js_safe_integer_is_kept_as_string_literal():
    direct = read("src/Bot/ChromeNs/DirectOrderEventBridge.cs")
    sample = "5127395078262028714"
    assert "2^53" in direct
    assert "\\d{8,40}" in direct
    # Guard the exact production incident shape: no code should hard-code a rounded replacement.
    assert sample.replace("8714", "8000") not in direct


def test_delivery_verification_partial_is_included_in_legacy_msbuild_and_wpf_tmp_projects():
    targets = read("src/Directory.Build.targets")
    assert "QN.DeliveryVerification.cs" in targets
    assert '<Compile Include="$(MSBuildProjectDirectory)\\ChromeNs\\QN.DeliveryVerification.cs" />' in targets