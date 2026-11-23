from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_knowledge_v2_delay_is_shop_scoped_and_bound_to_original_authoritative_cdp():
    source = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    method = source[source.index("internal static async Task<bool> TryHandleAsync"): ]
    assert "ShopSettingsScope.Current" in method
    assert "shopScope.ShopKey, burst.ShopKey" in method
    assert "var sourceClient = qn.CDP;" in method
    assert "sourceServer.IsAuthoritativeShopSession(burst.ShopKey, sourceSessionId)" in method
    delay = method.index("await Task.Delay(TimeSpan.FromSeconds(directDelaySeconds)")
    postcheck = method.index("ReferenceEquals(currentClient, sourceClient)", delay)
    send = method.index("qn.SendTextWithRetryAsync", postcheck)
    assert delay < postcheck < send
    assert "currentClient.SessionId, sourceSessionId" in method
    assert "currentServer.IsAuthoritativeShopSession(burst.ShopKey, sourceSessionId)" in method
    assert "ResponseProgressTracker.Cancel(" in method[postcheck:send]


def test_streaming_local_knowledge_delay_is_only_used_for_direct_answers_and_rechecks_same_cdp():
    source = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    method = source[source.index("private static async Task ProcessTextBurstStreamingAsync"): ]
    assert "var sourceClient = qn.CDP;" in method
    assert "if (IsLocalKnowledgeDirectAnswerSource(answerSource))" in method
    delay = method.index("await Task.Delay(TimeSpan.FromSeconds(directDelaySeconds)")
    preflight = method.index("sourceServer.IsAuthoritativeShopSession(burst.ShopKey, sourceSessionId)")
    postcheck = method.index("ReferenceEquals(currentClient, sourceClient)", delay)
    send = method.index("qn.SendTextWithRetryAsync", postcheck)
    assert preflight < delay < postcheck < send
    assert "currentClient.SessionId, sourceSessionId" in method
    assert "currentServer.IsAuthoritativeShopSession(burst.ShopKey, sourceSessionId)" in method
