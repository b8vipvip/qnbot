from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_ai_failure_fallback_uses_50_percent_only_for_authenticated_control_plane_exhaustion():
    client = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    server = read("services/api-control-plane/app.py")

    assert "MinimumFallbackScore = 0.50" in client
    assert "HTTP 502" in client
    assert "upstream_exhausted" in client
    assert 'string.Equals(x.Type, "服务端控制面"' in client
    assert "所有供应商、模型和请求协议均调用失败" in client

    route_pos = server.index('@app.post("/v1/chat/completions")')
    auth_pos = server.index("Depends(require_client)", route_pos)
    dispatch_pos = server.index("dispatch_chat", auth_pos)
    exhausted_pos = server.index('"type": "upstream_exhausted"', dispatch_pos)
    assert route_pos < auth_pos < dispatch_pos < exhausted_pos
    assert "status_code=502" in server[dispatch_pos:exhausted_pos + 400]


def test_normal_knowledge_threshold_is_not_lowered_when_ai_is_healthy():
    normal = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    fallback = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")

    assert "return matched != null && score >= 0.84;" in normal
    assert "score < MinimumFallbackScore" in fallback
    assert "MinimumFallbackScore = 0.50" in fallback


def test_ai_failure_answer_is_replaced_before_production_error_gate_and_is_source_tracked():
    reply = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")

    assert "AiFailureKnowledgeFallbackService.TryResolve" in reply
    assert '"AI异常本地兜底"' in reply
    assert "&& !aiFailureFallbackApplied" in reply
    assert "PreSendAnswerValidator.Validate" in reply

    distinct_pos = qn.index("ReplyDeduplicationService.EnsureDistinct")
    error_gate_pos = qn.index('answer.StartsWith("错误："', distinct_pos)
    send_pos = qn.index("SendTextWithRetryAsync", error_gate_pos)
    assert distinct_pos < error_gate_pos < send_pos


def test_ai_failure_fallback_is_current_shop_scoped_and_blocks_withdrawn_answers():
    source = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")

    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in source
    assert "using (ShopSettingsScope.Enter(shop))" in source
    assert "BotFeatureStore.GetKnowledgeBase()" in source
    assert "ConversationContextStore.IsWithdrawnAnswer(seller, buyer, answer)" in source
    assert "最高匹配不足50%" in source



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


def test_streaming_reply_circuit_open_and_ai_failure_only_use_non_withdrawn_safe_local_knowledge():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    service = source[
        source.index("internal static class StreamingBuyerAnswerService"):
    ]
    assert "CanUseOfflineKnowledgeFallback(plan)" in service
    assert "ConversationContextStore.IsWithdrawnAnswer(seller, buyer, safeFallback)" in service
    assert "AI熔断离线安全兜底" in service
    assert "已阻止自动发送并建议转人工" in service
    assert "ConversationContextStore.IsWithdrawnAnswer(seller, buyer, directAnswer)" in service
