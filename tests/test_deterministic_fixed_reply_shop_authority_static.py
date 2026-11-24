from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_delayed_first_inquiry_and_offhours_fixed_replies_keep_original_shop_session():
    source = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")
    method = source[source.index("private static async Task<bool> SendFixedAsync"):source.index("public static async Task<bool> TryHandleAsync")]
    assert "var sourceClient = qn == null ? null : qn.CDP;" in method
    assert "sourceClient.IsInvalidated || sourceClient.IsShopIdentityRetired" in method
    assert "sourceClient.ShopKey, item.ShopKey" in method
    assert "sourceServer.IsAuthoritativeShopSession(item.ShopKey, sourceSessionId)" in method
    delay = method.index("await Task.Delay(TimeSpan.FromSeconds(boundedDelay), generationToken)")
    postcheck = method.index("ReferenceEquals(currentClient, sourceClient)", delay)
    send = method.index("qn.SendTextWithRetryAsync(", postcheck)
    assert delay < postcheck < send
    assert "ParallelReplyRelevanceGate.ShouldSend(" in method
    assert "preSendGuard" in method[send:]
    assert "currentClient.SessionId, sourceSessionId" in method
    assert "currentServer.IsAuthoritativeShopSession(item.ShopKey, sourceSessionId)" in method
