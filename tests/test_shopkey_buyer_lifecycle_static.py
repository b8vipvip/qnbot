from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_qn_runtime_registry_uses_shopkey_not_nickname_or_display():
    qn = read("src/Bot/ChromeNs/QN.cs")
    locator = read("src/Bot/ShopScope/ShopContextLocator.cs")

    factory = qn[
        qn.index("public static QN GetByShopIdentity"):
        qn.index("public static QN GetByNick")
    ]
    find_by_nick = qn[
        qn.index("public static QN FindExistingBySellerNick"):
        qn.index("private static string TryGetShopKey")
    ]

    assert "ResolveRuntimeBySellerIdentity(seller)" in factory
    assert "TryGetShopKey(q._seller)" in factory
    assert "q._seller.Nick == seller.Nick || q._seller.Display == seller.Display" not in factory
    assert "ShopSettingsScope.Current" in find_by_nick
    assert "ActiveShopSessionRegistry.GetActiveShopKey()" in find_by_nick
    assert "ResolveRuntimeBySellerNick(sellerNick)" in find_by_nick
    assert "ResolveRuntimeBySellerIdentity(DbEntity.LocalUser seller)" in locator
    assert "if (!context.HasStableSellerId)" in locator


def test_websocket_authoritative_session_registry_is_scoped_by_shopkey():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")

    assert "ConcurrentDictionary<string, string> _shopSessions" in server
    assert "ConcurrentDictionary<string, string> _sessionShopKeys" in server
    assert "ConcurrentDictionary<string, string> _duplicateShopSessions" in server
    assert "TryClaimShopSession(shopKey, session.SessionID)" in server
    assert "ResolveRuntimeBySellerIdentity(user.Result)" in server
    assert "QN.GetByShopIdentity(user.Result)" in server
    assert "TryClaimShopSession(loginNick" not in server
    assert "TryClaimShopSession(sellerNick" not in server
    assert 'DiagnosticRef("shop", shopKey)' in server


def test_cdp_preferred_runtime_routing_uses_shopkey_after_exact_login_identity():
    cdp = read("src/Bot/ChromeNs/CDPClient.cs")
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")

    assert "PreferredShopSessions" in cdp
    assert "var shopKey = (ShopKey ?? string.Empty).Trim();" in cdp
    assert "ResolveRuntimeBySellerIdentity(localUser.LoginID).ShopKey" in cdp
    assert "server.IsAuthoritativeShopSession(shopKey, SessionId)" in cdp
    assert "QN.FindExistingByShopKey(shopKey)" in cdp
    assert "IsShopIdentityRetired" in cdp
    assert "allowRetiredIdentityRead" in cdp
    assert "InvokeShopIdentity<LocalUserResponse>" in cdp
    assert "cdp.ShopKey = shopKey;" in server
    assert "client.ShopKey = shopKey;" in server
    assert "CDPClient.ReleasePreferredRuntimeSession(previousShopKey, sessionId)" in server
    assert "previousQn.CDP = null;" in server
    assert "_lastStatusBindings.TryRemove(sessionId, out ignoredStatusBinding)" in server
    assert "!string.Equals(sessionShopKey, shopKey, StringComparison.Ordinal)" in cdp
    assert "return;" in cdp[cdp.index("千牛会话身份与当前CDP绑定不一致"):]


def test_buyer_session_generations_and_deadline_watchers_include_shopkey():
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    bridge = read("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")

    assert "ResolveShopKey(string sellerNick, string shopKey)" in agent
    assert 'shopKey + "#" + Normalize(sellerNick)' in agent
    assert '"unresolved:" + Guid.NewGuid().ToString("N")' in agent
    assert "RegisterAcceptedGeneration(\n                shopKey," in agent
    assert "BuildGenerationWatchKey(shopKey, seller, buyer, generation)" in bridge
    assert "WatchSession(shopKey, seller, buyer)" in bridge
    assert "watched.ShopKey" in bridge
    assert "Agent.RecordEvent" in bridge


def test_buyer_burst_and_deterministic_reply_require_exact_shopkey():
    burst = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")
    autoreply = read("src/Bot/ChromeNs/DeterministicAutoReplyService.cs")

    assert "public string ShopKey { get; set; }" in burst
    assert "public string ShopKey { get; private set; }" in burst
    assert "拒绝跨店合并" in burst
    assert "ResolveRuntimeByShopKey(item.ShopKey)" in burst
    assert "QN.FindExistingByShopKey(item.ShopKey)" in burst
    assert "shop_scope_unresolved" in burst
    assert "ShopKey = shopKey" in qn
    assert "ResolveRuntimeByShopKey(item.ShopKey)" in autoreply
    assert "QN.FindExistingByShopKey" in autoreply
    assert "Key(shop.ShopKey, item.SellerNick, item.BuyerNick)" in autoreply
    assert "已阻断普通Knowledge/AI链路" in autoreply


def test_delivery_watchdog_and_progress_fallback_never_merge_ambiguous_shops():
    watchdog = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    progress = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")

    assert "FindConnectedQnByShopKey(shop.ShopKey)" in watchdog
    assert "current.SellerNick" in watchdog
    assert '"unresolved:" + Guid.NewGuid().ToString("N")' in watchdog
    assert "current.SellerNick" in progress
    assert '"unresolved:" + Guid.NewGuid().ToString("N")' in progress
    assert "DeliveryKey(seller, buyer, answer, shopKey)" in progress
