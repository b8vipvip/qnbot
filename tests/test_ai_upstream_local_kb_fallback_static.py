from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_ai_failure_fallback_uses_only_a_preapproved_v2_decision_and_authenticated_shop_scope():
    client = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    server = read("services/api-control-plane/app.py")

    assert "KnowledgeV2Decision OfflineFallbackDecision" in runtime
    assert "return KnowledgeV2RuntimeResult.Continue(aiFirstContext, decision).WithContextDecision(decision);" in runtime
    assert "TryResolveV2OfflineFallback(" in client
    assert "approvedDecision.CanDirectReply" in client
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved" in client
    assert "MinimumFallbackScore" not in client
    assert "BotFeatureStore.GetKnowledgeBase()" not in client
    route_pos = server.index('@app.post("/v1/chat/completions")')
    auth_pos = server.index("Depends(require_client)", route_pos)
    dispatch_pos = server.index("dispatch_chat", auth_pos)
    exhausted_pos = server.index('"type": "upstream_exhausted"', dispatch_pos)
    assert route_pos < auth_pos < dispatch_pos < exhausted_pos
    assert "status_code=502" in server[dispatch_pos:exhausted_pos + 400]

def test_ai_failure_does_not_lower_the_normal_v2_direct_answer_threshold():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    service = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    client = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    assert "DirectThreshold" in service
    assert "MinConfidence" in service
    assert "var latest = KnowledgeEngineV2Service.Resolve(seller, buyer, v2Question);" in client
    assert "latest.CanDirectReply" in client
    assert "latest.Answer, approvedDecision.Answer, StringComparison.Ordinal" in client
    assert "MinimumFallbackScore" not in client
    assert "return KnowledgeV2RuntimeResult.Continue(aiFirstContext, decision).WithContextDecision(decision);" in runtime

def test_ai_failure_answer_is_replaced_by_v2_before_the_pipeline_error_gate():
    reply = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")

    assert "TryResolveV2OfflineFallback(" in reply
    assert '"Knowledge V2 AI故障兜底"' in reply
    assert "if (!string.IsNullOrWhiteSpace(offline))" in reply
    assert "onOfflineFallbackUsed()" in reply
    assert "PreSendAnswerValidator.Validate(" in read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    assert "knowledgeV2Result == null ? null : knowledgeV2Result.OfflineFallbackDecision" in pipeline
    assert "KnowledgeV2Decision OfflineFallbackDecision" in runtime

def test_ai_failure_fallback_is_exact_shop_scoped_and_rejects_stale_or_withdrawn_v2_answers():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    method = source[
        source.index("private static string TryResolveV2OfflineFallback"):
        source.index("private static bool IsRetryableStreamError")
    ]

    assert "ShopContextLocator.ResolveRuntimeByShopKey(shopKey)" in method
    assert "shop.ShopKey, shopKey.Trim(), StringComparison.Ordinal" in method
    assert "shop.SellerNick, (seller ?? string.Empty).Trim(), StringComparison.Ordinal" in method
    assert "KnowledgeEngineV2Service.IsEnabled(seller)" in method
    assert "KnowledgeEngineV2Service.IsSnapshotReady(seller)" in method
    assert "KnowledgeEngineV2Service.Resolve(seller, buyer, v2Question)" in method
    assert "latestMatch.Record.Id, priorMatch.Record.Id, StringComparison.Ordinal" in method
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved(persisted)" in method
    assert "ConversationContextStore.IsWithdrawnAnswer(seller, buyer, answer)" in method
    assert "KnowledgeEngineV2Repository.LoadCommittedRecord(seller, latestMatch.Record.Id)" in method
    assert "BotFeatureStore.GetKnowledgeBase()" not in method

def test_streaming_reply_retries_only_transient_provider_failures_within_generation_budget():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    stream = source[
        source.index("private static async Task<string> StreamMessagesAsync"):
        source.index("private static async Task<StreamResult> StreamOneAsync")
    ]
    assert "for (var attempt = 0; attempt < 2; attempt++)" in stream
    assert "IsRetryableStreamError(error)" in stream
    assert "IsRetryableStreamError(fallbackError)" in stream
    assert "Task.Delay(TimeSpan.FromMilliseconds(450), token)" in stream
    assert "token.ThrowIfCancellationRequested();" in stream


def test_smart_reply_circuit_breaker_is_shop_scoped_and_has_a_single_half_open_probe():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    breaker = source[
        source.index("internal static class ShopScopedReplyCircuitBreaker"):
        source.index("internal static class ReplyTranscriptSanitizer")
    ]
    service = source[
        source.index("internal static class StreamingBuyerAnswerService"):
    ]
    assert "ConcurrentDictionary<string, State>" in breaker
    assert "FailureThreshold = 3" in breaker
    assert "OpenSeconds = 30" in breaker
    assert "ProbeInFlight" in breaker
    assert "HalfOpenProbe = true" in breaker
    assert "ShopScopedReplyCircuitBreaker.TryEnter(shopKey)" in service
    assert "ShopScopedReplyCircuitBreaker.RecordFailure(permit)" in service
    assert "ShopScopedReplyCircuitBreaker.RecordSuccess(permit)" in service
    assert "ShopScopedReplyCircuitBreaker.ReleaseProbe(permit)" in service
    assert "burst.ShopKey" in source


def test_streaming_reply_circuit_open_and_ai_failure_only_use_revalidated_v2_approved_knowledge():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    service = source[
        source.index("internal static class StreamingBuyerAnswerService"):
    ]
    fallback = service[
        service.index("private static string TryResolveV2OfflineFallback"):
        service.index("private static bool IsRetryableStreamError")
    ]

    assert '"shop_ai_circuit_open"' in service
    assert '"ai_upstream_exhausted"' in service
    assert '"no_enabled_ai_endpoint"' in service
    assert "KnowledgeV2AuthorityPolicy.IsProductionApproved" in fallback
    assert "latest.CanDirectReply" in fallback
    assert "ConversationContextStore.IsWithdrawnAnswer(seller, buyer, answer)" in fallback
    assert "string.IsNullOrWhiteSpace(approvedDecision.ConditionalAdviceKey)" in fallback
    assert "BotFeatureStore.GetKnowledgeBase()" not in fallback

def test_pre_send_dedup_requires_verified_shop_scope_and_uses_v2_evidence_only():
    source = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    start = source.index(
        "public static ReplyDeduplicationResult EnsureDistinct(\n"
        "            string seller,\n"
        "            string buyer,\n"
        "            string question,\n"
        "            string candidateAnswer,\n"
        "            CancellationToken"
    )
    preserve = source.index("if (preserveTrustedAnswer)", start)
    scope_guard = source[start:preserve]

    assert "ShopContextLocator.ResolveCurrentOrBySellerNick(seller)" in scope_guard
    assert "using (ShopSettingsScope.Enter(resolvedShop))" in scope_guard
    assert "return EnsureDistinct(seller, buyer, question, candidateAnswer," in scope_guard
    assert "return BuildBlockedResult(" in scope_guard
    assert "approvedV2ContextEvidence" in source
    assert "AiFailureKnowledgeFallbackService.TryResolve" not in source
    assert "BotFeatureStore.GetKnowledgeBase()" not in source



def test_v2_offline_fallback_revalidates_after_delay_and_inside_send_guard():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    assert "() => offlineFallbackUsed = true" in source
    assert "if (deduplication.Regenerated)" in source
    assert "offlineFallbackUsed = false;" in source[source.index("if (deduplication.Regenerated)"):source.index("catch (OperationCanceledException)", source.index("if (deduplication.Regenerated)"))]
    assert "requiresFinalFactValidation = true" in source[source.index("if (deduplication.Regenerated)"):source.index("catch (OperationCanceledException)", source.index("if (deduplication.Regenerated)"))]
    assert 'offlineFallbackUsed ? "Knowledge V2 AI故障兜底"' in source
    delay = source.index("await Task.Delay(TimeSpan.FromSeconds(directDelaySeconds)")
    check = source.index("if (offlineFallbackUsed && !StreamingBuyerAnswerService.IsV2OfflineFallbackCurrent(", delay)
    send_guard = source.index("Func<bool> preSendGuard", check)
    second = source.index("if (offlineFallbackUsed && !StreamingBuyerAnswerService.IsV2OfflineFallbackCurrent(", send_guard)
    send = source.index("qn.SendTextWithRetryAsync(", second)
    assert delay < check < send_guard < second < send
    assert 'approvedDecision, "final_send_recheck", false' in source
    assert "if (!recordProvenance) return answer" in source
    assert "KnowledgeEngineV2Repository.LoadCommittedRecord(seller, latestMatch.Record.Id)" in source
