from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_knowledge_v2_delay_is_shop_scoped_and_bound_to_original_authoritative_cdp():
    source = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    method = source[
        source.index("internal static async Task<KnowledgeV2RuntimeResult> TryHandleWithContextAsync"):
        source.index("internal static string BuildContextPromptAddon")
    ]
    assert "ShopSettingsScope.Current" in method
    assert "shopScope.ShopKey, burst.ShopKey" in method
    assert "var sourceClient = qn.CDP;" in method
    assert "sourceServer.IsAuthoritativeShopSession(burst.ShopKey, sourceSessionId)" in method
    ai_first = method.index("if (!ReplyModeService.IsLocalFirst(burst.SellerNick))")
    delay = method.index("await Task.Delay(TimeSpan.FromSeconds(directDelaySeconds)")
    postcheck = method.index("ReferenceEquals(currentClient, sourceClient)", delay)
    send = method.index("qn.SendTextWithRetryAsync", postcheck)
    assert ai_first < delay < postcheck < send
    assert "currentClient.SessionId, sourceSessionId" in method
    assert "currentServer.IsAuthoritativeShopSession(burst.ShopKey, sourceSessionId)" in method
    assert "ResponseProgressTracker.Cancel(" in method[postcheck:send]

def test_streaming_local_knowledge_delay_is_only_owned_by_v2_direct_path():
    runtime = read("src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs")
    pipeline = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    method = runtime[
        runtime.index("internal static async Task<KnowledgeV2RuntimeResult> TryHandleWithContextAsync"):
        runtime.index("internal static string BuildContextPromptAddon")
    ]

    assert "KnowledgeDirectReplyDelaySeconds" in method
    assert "await Task.Delay(TimeSpan.FromSeconds(directDelaySeconds)" in method
    assert "ReplyModeService.IsLocalFirst(burst.SellerNick)" in method
    fallback = pipeline.split("private static bool IsLocalKnowledgeDirectAnswerSource", 1)[1].split("private static async Task<T>", 1)[0]
    assert '"Knowledge V2 AI故障兜底"' in fallback
    assert '"智能路由-本地直答"' not in fallback
    assert '"智能路由-离线知识兜底"' not in fallback
    assert "if (IsLocalKnowledgeDirectAnswerSource(answerSource))" in pipeline
    assert "KnowledgeDirectReplyDelaySeconds" in pipeline
    assert method.index("if (!ReplyModeService.IsLocalFirst") < method.index(
        "await Task.Delay(TimeSpan.FromSeconds(directDelaySeconds)"
    )
    assert "ReferenceEquals(currentClient, sourceClient)" in method
    assert "qn.SendTextWithRetryAsync" in method

