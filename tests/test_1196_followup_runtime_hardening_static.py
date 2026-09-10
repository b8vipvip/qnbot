from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_unknown_composer_text_is_never_deleted_and_timed_out_mutation_retains_exclusive_lease():
    q = read("src/Bot/ChromeNs/QNRpa.cs")
    method = q[q.index("ClearStaleComposerBeforeNewDraftAsync"):q.index("TrySetPlainTextByCdpAsync")]
    assert "IsOwnedDraftForBuyer(buyer, observedText)" in method
    assert "输入框存在所有权无法证明的内容，已保留" in method
    assert "RunUiMutationAsync" in method
    helper = q[q.index("private async Task<bool> RunUiMutationAsync"):q.index("private async Task<bool> HasExpectedDraftFastAsync")]
    assert "Task.WhenAny" in helper
    assert "Task.Delay(UiMutationTimeoutMs)" in helper
    assert "_activeUiMutationTask != null && !_activeUiMutationTask.IsCompleted" in helper
    assert "原任务保持独占租约直到安全退出" in helper
    assert "Thread.Abort" not in helper


def test_order_sku_uses_structured_parser_without_holding_the_send_path():
    v2 = read("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")
    legacy = read("src/Bot/Options/LegacyAboutUpdateRedirect.cs")
    assert "internal static string ResolveSkuTextFromPayload(string raw)" in legacy
    assert "SkuText = OrderSkuPayloadRecoveryBridge.ResolveSkuTextFromPayload(raw)" in v2
    assert "var delays = new[] { 0 };" in v2
    assert "field_lookup_policy=post_send_nonblocking" in v2
    scope = v2[v2.index("private static async Task EnrichValidateAndSendAsync"):v2.index("private static async Task<EnrichmentProbe> TryEnrichFromTradeApiAsync")]
    assert scope.index("ProcessOrderTemplateRequiredFieldsPlanAsync") < scope.index("TryEnrichFromTradeApiAsync")


def test_exact_duplicate_cdp_payloads_stay_suppressed_across_recovery_cadences():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    assert "InboundFingerprintWindow = TimeSpan.FromMinutes(2)" in bridge
    assert "InboundFingerprintRetention = TimeSpan.FromMinutes(5)" in bridge
    assert "BuildInboundFingerprint(seller, type, response)" in bridge
    assert "+ (response ?? string.Empty)" in bridge
    assert "MaxPendingInboundEvents = 512" in bridge
    assert "EnqueuePendingBounded(item, \"initial\")" in bridge
    assert "EnqueuePendingBounded(item, \"retry_wait_authoritative_cdp\")" in bridge


def test_owned_draft_forget_helper_clears_state_without_self_recursion():
    q = read("src/Bot/ChromeNs/QNRpa.cs")
    helper = q[q.index("private void ForgetOwnedDraft()"):q.index("private bool IsOwnedDraftForBuyer", q.index("private void ForgetOwnedDraft()"))]
    assert helper.count("ForgetOwnedDraft();") == 0
    assert "LastSetPlainText = string.Empty;" in helper
    assert "LatestSetTextTime = DateTime.MinValue;" in helper


def test_generation_deadline_watch_uses_actual_acceptance_clock_and_direct_registration():
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    bridge = read("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")
    assert "GenerationAcceptedAtUtc" in agent
    assert "acceptedAtUtc = DateTime.UtcNow" in agent
    assert "RegisterAcceptedGeneration(" in agent
    assert "absolute_generation_age_transition_gate" in agent
    assert "absolute_generation_age_current_gate" in agent
    assert "ConcurrentDictionary<string, WatchedGeneration> WatchedGenerations" in bridge
    assert "foreach (var pair in WatchedGenerations.ToArray())" in bridge
    assert "RegisterAcceptedGeneration(" in bridge
    assert "TryGetGenerationAcceptedAtUtc(" in bridge
    assert "BuyerActionAccepted" not in bridge
    assert "state == BuyerSessionAgentState.Generating" not in bridge
    assert "Agent.Cancel(" in bridge
    assert "absolute_generation_age_exceeded" in bridge


def test_cdp_execute_gate_wait_is_bounded_before_per_request_timeout():
    cdp = read("src/Bot/ChromeNs/CDPClient.cs")
    assert "private const int ExecuteGateWaitTimeoutMs = 1500;" in cdp
    assert "_executeGate.WaitAsync(ExecuteGateWaitTimeoutMs)" in cdp
    assert "CDP调用等待串行门超时，已快速失败避免排队放大" in cdp
    assert "if (gateAcquired) _executeGate.Release();" in cdp
    assert "private const int InvokeTimeoutMs = 8000;" in cdp


def test_active_buyer_confirmation_has_end_to_end_wall_clock_budget():
    qn = read("src/Bot/ChromeNs/QN.cs")
    method = qn[qn.index("private async Task<bool> EnsureActiveBuyerForSendAsync"):qn.index("public async void SendImageAsync")]
    assert "ActiveBuyerConfirmDeadlineMs = 9000" in qn
    assert "ActiveBuyerConfirmPollMs = 250" in qn
    assert "deadlineUtc = DateTime.UtcNow.AddMilliseconds(ActiveBuyerConfirmDeadlineMs)" in method
    assert "attempt < 22 && DateTime.UtcNow < deadlineUtc" in method
    assert "Math.Min(ActiveBuyerConfirmPollMs, remainingMs)" in method
    assert "无法在会话确认总预算内确认当前会话为目标买家" in method
    assert "for (var attempt = 0; attempt < 22; attempt++)" not in method


def test_vision_followup_keeps_generation_cancellation_and_tolerates_small_clock_skew():
    vision = read("src/Bot/ChromeNs/VisionFollowUpContextPipeline.cs")
    assert "SourceClockSkewToleranceSeconds = 15" in vision
    assert "elapsed >= TimeSpan.FromSeconds(-SourceClockSkewToleranceSeconds)" in vision
    assert "elapsed = TimeSpan.Zero;" in vision
    assert "ResolveSessionAgent(lease)" in vision
    assert 'GetField("_sessionAgent"' in vision
    assert "SessionGeneration = source.SessionGeneration" in vision
    assert "SemanticContinuationContext = source.SemanticContinuationContext" in vision
