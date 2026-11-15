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
    assert "x.CDP != null && !x.CDP.IsInvalidated && !x.CDP.IsShopIdentityRetired" in locator
    assert "server.IsAuthoritativeShopSession(shopKey, x.CDP.SessionId)" in locator


def test_websocket_authoritative_session_registry_is_scoped_by_shopkey():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    guard = read("src/Bot/ChromeNs/WebSocketPageLifecycleGuard.cs")

    assert "ConcurrentDictionary<string, string> _shopSessions" in server
    assert "ConcurrentDictionary<string, string> _sessionShopKeys" in server
    assert "ConcurrentDictionary<string, string> _duplicateShopSessions" in server
    assert "TryClaimShopSession(shopKey, session.SessionID)" in server
    claim = server[server.index("private bool TryClaimShopSession"):server.index("private async Task<bool> TryPromoteDuplicateShopSessionAsync")]
    assert "if (!_connectedSessions.ContainsKey(sessionId)) return false;" in claim
    assert "ResolveRuntimeBySellerIdentity(user.Result)" in server
    assert "QN.GetByShopIdentity(user.Result)" in server
    assert "TryClaimShopSession(loginNick" not in server
    assert "TryClaimShopSession(sellerNick" not in server
    assert 'DiagnosticRef("shop", shopKey)' in server
    assert "GetShopKeyForSession(sessionId)" in guard
    assert "IsAuthoritativeShopSession(shopKey, sessionId)" in guard
    assert "BuildDuplicateProbeFingerprint(shopKey, type, response)" in guard
    assert "FindConnectedQnByShopKey(shopKey)" in guard
    assert "FindExistingBySellerNick" not in guard


def test_authority_disconnect_promotes_the_best_connected_standby_immediately():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    release = server[
        server.index("private void ReleaseShopSession"):
        server.index("private bool ShouldRefreshStatusBinding")
    ]
    promotion = server[
        server.index("private async Task TryPromoteAvailableStandbyAsync"):
        server.index("private bool ShouldRefreshStatusBinding")
    ]
    identity_change = server[
        server.index("private bool TryClaimShopSession"):
        server.index("private async Task<bool> TryPromoteDuplicateShopSessionAsync")
    ]

    assert "TryPromoteAvailableStandbyAsync(" in release
    assert "releasedAuthority && promoteStandby" in release
    assert "ReleaseShopSession(sessionId, false)" in server
    assert "_duplicateShopSessions" in promotion
    assert "_connectedSessions.ContainsKey(x.Key)" in promotion
    assert "_closingDuplicateSessions.ContainsKey(x.Key)" in promotion
    assert "OrderByDescending" in promotion
    assert "_liveSessions.TryGetValue(candidateId, out candidate)" in promotion
    assert "TryPromoteDuplicateShopSessionAsync(" in promotion
    assert "requireVacant: true" in promotion
    assert "await client.GetCurrentUser().ConfigureAwait(false)" in server
    assert "ResolveRuntimeBySellerIdentity(user.Result)" in server
    assert "verifiedShop.ShopKey, shopKey" in server
    assert "if (requireVacant && !string.IsNullOrWhiteSpace(previousOwner)" in server
    assert "_lastStatusBindings.TryRemove(sessionId, out ignoredStatusBinding)" in server
    assert "releasedPreviousAuthority = true;" in identity_change
    assert "if (releasedPreviousAuthority)" in identity_change
    assert 'TryPromoteAvailableStandbyAsync(previousShopKey, "shop_identity_changed", sessionId)' in identity_change


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
    assert "ResolveRuntimeBySellerIdentity(user.Result)" in cdp
    assert "boundShopKey" in cdp
    assert "cdp.ShopKey = shopKey;" in server
    assert "client.ShopKey = shopKey;" in server
    assert "CDPClient.ReleasePreferredRuntimeSession(previousShopKey, sessionId)" in server
    assert "previousQn.CDP.IsShopIdentityRetired = true;" in server
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
    assert "qn.CDP.IsShopIdentityRetired" in burst
    assert "ShopKey = shopKey" in qn
    assert "ResolveRuntimeByShopKey(item.ShopKey)" in autoreply
    assert "QN.FindExistingByShopKey" in autoreply
    assert "qn.CDP.IsShopIdentityRetired" in autoreply
    assert "Key(shop.ShopKey, item.SellerNick, item.BuyerNick)" in autoreply
    assert "已阻断普通Knowledge/AI链路" in autoreply


def test_seller_echo_runtime_monitor_and_learning_are_shopkey_scoped():
    monitor = read("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    learning = read("src/Bot/ChromeNs/KnowledgeLearningService.cs")
    mirror = read("src/Bot/ChromeNs/ConversationContextStore.cs")

    assert "ResolveRuntimeBySeller(qn).ShopKey" in monitor
    assert "ShopSettingsScope.Enter(ShopContextLocator.ResolveRuntimeByShopKey(shopKey))" in monitor
    assert "TryConfirmBotDelivery(seller, buyer, texts, shopKey)" in monitor
    assert "BuyerOrderKey(seller, buyer, shopKey)" in monitor
    assert "return shopKey + \"#\" + seller.ToLowerInvariant()" in monitor
    assert "IsKnownBotAnswer(seller, buyer, sellerEcho, shopKey)" in learning
    assert "RecordObservedSellerMessage(" in mirror
    assert "ResolveShop(seller, shopKey)" in mirror
    assert "ResolveRuntimeByShopKey(shopKey)" in mirror


def test_delivery_echo_and_progress_ui_keys_use_explicit_shopkey():
    bridge = read("src/Bot/ChromeNs/BuyerSessionAgentRuntimeBridge.cs")
    watchdog = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    progress = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")
    send_guard = read("src/Bot/ChromeNs/QNRpa.PlatformSendGuard.cs")
    native_send = read("src/Bot/ChromeNs/QNRpa.NativeSend.cs")
    rpa = read("src/Bot/ChromeNs/QNRpa.cs")
    burst = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    streaming = read("src/Bot/ChromeNs/BuyerStreamingReplyPipeline.cs")
    vision = read("src/Bot/ChromeNs/VisionWithdrawalAwarePipeline.cs")
    qn = read("src/Bot/ChromeNs/QN.cs")

    assert "ObserveQuestion(sellerNick, buyerNick, displayQuestion, detectedAt, shop.ShopKey)" in qn
    assert "BeginAnswer(" in burst and "detectedAt, item.ShopKey" in burst
    assert "DateTime.Now, item.ShopKey" in burst
    assert "detectedAt,\n                answerReadyAt,\n                burst.ShopKey" in streaming
    assert "detectedAt,\n                answerReadyAt,\n                burst.ShopKey" in vision
    assert "Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey)" in streaming
    assert "Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey)" in vision
    assert "SetAnswerReady(" in qn and "answerReadyAt,\n                burst.ShopKey" in qn
    assert "Complete(burst.SellerNick, burst.BuyerNick, burst.ShopKey)" in qn
    assert "SendDeliveryWatchdog.ConfirmDelivery(seller, buyer, text, shopKey)" in bridge
    assert "string shopKey = null" in watchdog
    assert "var shop = ResolveShop(seller, shopKey);" in watchdog
    assert "ResolveRuntimeByShopKey(shopKey)" in watchdog
    assert "ScopeKey(seller, shopKey)" in watchdog
    assert "AnswerKey(seller, buyer, answer, shopKey)" in watchdog
    assert "SendDeliveryWatchdog.ExpectDelivery(" in progress
    assert "answerReadyAt, shopKey: shopKey" in progress
    assert "DeliveryKey(seller, buyer, answer, shopKey)" in progress
    assert "ShopSettingsScope.Enter(shop)" in progress
    assert "MarkDeliveryConfirmedCore(seller, buyer, answer, detail, shopKey)" in progress
    assert "MarkDeliveryTimedOutCore(seller, buyer, answer, detail, shopKey)" in progress
    assert "ResolveOperationOrDetectedTurnKey(seller, buyer, detected, shopKey)" in progress
    assert "ResolveShopKeyForDeliveryWatchdog()" in send_guard
    assert "MarkSubmissionAccepted(" in send_guard
    assert "CancelConversation(" in send_guard
    assert "EnsurePending(" in rpa and "shopKey" in rpa
    assert "CancelPending(" in rpa and "shopKey" in rpa
    assert "if (shopKey.Length == 0)" in rpa
    assert "OpenAndSendText(buyer, text, shopKey)" in rpa
    assert "_qn.CDP.IsShopIdentityRetired" in send_guard
    assert "_qn.CDP.ShopKey" in send_guard
    assert "await client.GetCurrentUser().ConfigureAwait(false)" in send_guard
    assert "ResolveRuntimeBySellerIdentity(current.Result)" in send_guard
    assert "TrySendTextNativeFirstAsync(string buyer, string text, DateTime sendStart, string shopKey)" in native_send
    assert "WaitForTextSubmissionAcceptedAsync(" in send_guard
    assert "string method, int timeoutMs, string shopKey" in send_guard
    assert "ArmLateServiceAttitudeContinuationWatch(buyer, text, method, shopKey)" in send_guard


def test_delivery_watchdog_and_progress_fallback_never_merge_ambiguous_shops():
    watchdog = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    progress = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")

    assert "FindConnectedQnByShopKey(shop.ShopKey)" in watchdog
    assert "current.SellerNick" in watchdog
    assert '"unresolved:" + Guid.NewGuid().ToString("N")' in watchdog
    assert "current.SellerNick" in progress
    assert '"unresolved:" + Guid.NewGuid().ToString("N")' in progress
    assert "DeliveryKey(seller, buyer, answer, shopKey)" in progress
