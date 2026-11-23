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
    assert "TryClaimShopSession(shopKey, session.SessionID, identityProbeGeneration, () =>" in server
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
        server.index("private long ShouldRefreshStatusBinding")
    ]
    promotion = server[
        server.index("private async Task TryPromoteAvailableStandbyAsync"):
        server.index("private long ShouldRefreshStatusBinding")
    ]
    identity_change = server[
        server.index("private bool TryClaimShopSession"):
        server.index("private async Task<bool> TryPromoteDuplicateShopSessionAsync")
    ]
    duplicate_promotion = server[
        server.index("private async Task<bool> TryPromoteDuplicateShopSessionAsync"):
        server.index("internal string GetShopKeyForSession")
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
    assert "var alreadyOwner = string.Equals(previousOwner, sessionId, StringComparison.Ordinal);" in duplicate_promotion
    assert "if (string.Equals(previousOwner, sessionId, StringComparison.Ordinal)) return true;" not in duplicate_promotion
    assert "await client.GetCurrentUser().ConfigureAwait(false)" in server
    assert "ResolveRuntimeBySellerIdentity(user.Result)" in server
    assert "verifiedShop.ShopKey, shopKey" in server
    assert "if (requireVacant" in server
    assert "!string.Equals(previousOwner, sessionId, StringComparison.Ordinal)" in server
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


def test_native_desk_guards_count_shopkeys_not_colliding_seller_nicks():
    rpa_desk = read("src/Bot/ChromeNs/QNRpa.MultiShopDeskBinding.cs")
    desk_registry = read("src/Bot/Automation/ChatDeskNs/DeskSellerBindingRegistry.cs")
    coordinator = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")

    assert "ShopContextLocator.ResolveRuntimeBySeller(qn).ShopKey" in rpa_desk
    assert "ShopContextLocator.ResolveRuntimeBySeller(qn).ShopKey" in desk_registry
    assert "return 2;" in rpa_desk and "return 2;" in desk_registry
    assert ".Select(qn => qn.Seller.Nick.Trim())" not in rpa_desk
    assert ".Select(qn => qn.Seller.Nick.Trim())" not in desk_registry
    assert "ActiveShopSessionRegistry.ValidateNativeSend(_qn, out isolationReason)" in rpa_desk
    assert "expectedTargetId.Length > 0" in coordinator
    assert "targetId.Length == 0" in coordinator
    assert "FocusProofMaxAge = TimeSpan.FromSeconds(5)" in coordinator
    assert "HasFreshFocusProofLocked()" in coordinator
    assert "else if (changed) _lastFocusProofUtc = DateTime.MinValue;" in coordinator
    assert "_activeSessionId.Length > 0" in coordinator


def test_native_desk_registry_uses_shopkey_for_window_and_composer_ownership():
    registry = read("src/Bot/Automation/ChatDeskNs/DeskSellerBindingRegistry.cs")
    rpa_desk = read("src/Bot/ChromeNs/QNRpa.MultiShopDeskBinding.cs")
    coordinator = read("src/Bot/ChromeNs/MultiShopRuntimeSessionCoordinator.cs")
    desk = read("src/Bot/Automation/ChatDeskNs/Desk.cs")
    robot_ui = read("src/Bot/AssistWindow/Widget/Robot/CtlRobot.MultiShopSession.cs")

    assert "ConcurrentDictionary<string, int> ShopToHwnd" in registry
    assert "ConcurrentDictionary<int, string> ActiveShopByHwnd" in registry
    assert "FindSellerDesk(seller, ResolveUniqueShopKey(seller))" in registry
    assert "IsSellerForDesk(Desk desk, string seller, string shopKey)" in registry
    assert "Desk desk, string seller, string evidence, string shopKey, bool allowRebind = false" in registry
    assert "ShopToHwnd.TryGetValue(shopKey, out hwnd)" in registry
    assert "ActiveShopByHwnd.TryGetValue(desk.Hwnd.Handle, out activeShopKey)" in registry
    assert "desk.IsForeground" in registry
    assert "bool allowRebind = false" in registry
    assert "allowRebind: existing != null" in registry
    assert "Desk.FindExistingBySellerNick" not in registry
    assert "DeskSellerBindingRegistry.FindSellerDesk(seller, shopKey)" in rpa_desk
    assert "_sellerDeskBoundShopKey" in rpa_desk
    assert "IsSellerForDesk(desk, seller, shopKey)" in rpa_desk
    assert "if (focusProof)\n                DeskSellerBindingRegistry.BindForegroundSeller" in coordinator
    assert coordinator.count("DeskSellerBindingRegistry.BindForegroundSeller(") == 1
    assert "!ShopToHwnd.TryGetValue(shopKey, out mappedHwnd) || mappedHwnd != hwnd" in registry
    assert "FindSellerDesk(sellerNick, scopedShop.ShopKey)" in desk
    assert "DeskSellerBindingRegistry.IsBoundToDeskForReadOnlyUi(" in robot_ui
    assert "_desk, seller, shopKey)" in robot_ui
    assert "DeskSellerBindingRegistry.IsSingleShopRuntime" in desk


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

    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in monitor
    assert "var shopKey = shop.ShopKey;" in monitor
    assert "ShopSettingsScope.Enter(shop)" in monitor
    assert "var incomingBuyer = BuyerIdentityAliasService.ResolveInternalNick(seller, from);" in monitor
    assert "RecordBuyerSourceSort(seller, incomingBuyer, message, shopKey);" in monitor
    assert "TryConfirmBotDelivery(seller, buyer, texts, shopKey)" in monitor
    assert "BuyerOrderKey(seller, buyer, shopKey)" in monitor
    assert "return shopKey + \"#\" + seller.ToLowerInvariant()" in monitor
    assert "IsBotAnswer(seller, buyer, sellerEcho, echoTime, shopKey, botCards)" in learning
    classifier = learning[
        learning.index("private static bool IsBotAnswer("):
        learning.index("private static string NormalizeBotAnswer(")
    ]
    assert "SendDeliveryWatchdog.IsKnownBotAnswer(seller, buyer, answer, shopKey)" in classifier
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

    assert "ResolveRuntimeBySeller(this).ShopKey" in qn
    assert "ObserveNewBuyerTurn(sellerNick, buyerNick, shopKey)" in qn
    assert "ObserveQuestion(sellerNick, buyerNick, displayQuestion, detectedAt, shopKey)" in qn
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
    assert "string.Equals(operationKey, detectedTurnKey, StringComparison.Ordinal)" in progress
    assert "ResolveShopKeyForDeliveryWatchdog()" in send_guard
    assert "MarkSubmissionAccepted(" in send_guard
    assert "CancelConversation(" in send_guard
    assert "EnsurePending(" in rpa and "shopKey" in rpa
    assert "CancelPending(" in rpa and "shopKey" in rpa
    assert "if (shopKey.Length == 0)" in rpa
    assert "OpenAndSendText(buyer, text, shopKey, manualBypassConsumed, preSendGuard)" in rpa
    assert "client.IsShopIdentityRetired" in send_guard
    assert "client.ShopKey" in send_guard
    assert "server.IsAuthoritativeShopSession(expectedShopKey, client.SessionId)" in send_guard
    assert "server.IsAuthoritativeShopSession(bound, client.SessionId)" in send_guard
    assert "await client.GetCurrentUser().ConfigureAwait(false)" in send_guard
    assert "ResolveRuntimeBySellerIdentity(current.Result)" in send_guard
    assert "TrySendTextNativeFirstAsync(" in native_send
    assert "string buyer, string text, DateTime sendStart, string shopKey," in native_send
    assert "Func<bool> manualTakeoverCheck, Func<bool> preSendGuard)" in native_send
    assert "WaitForTextSubmissionAcceptedAsync(" in send_guard
    assert "string method, int timeoutMs, string shopKey" in send_guard
    assert "ArmLateServiceAttitudeContinuationWatch(buyer, text, method, shopKey)" in send_guard
    assert "VerifyShopIdentityBeforeSendAsync(shop.ShopKey)" in qn
    assert "client.SendTimiMsg(buyer, text)" in qn
    assert "IsAuthoritativeShopSession(shop.ShopKey, client.SessionId)" in qn
    assert "SendDeliveryWatchdog.EnsurePending(seller, buyer, text, shop.ShopKey)" in qn
    assert "SendDeliveryWatchdog.MarkSubmissionAccepted(" in qn


def test_delivery_watchdog_and_progress_fallback_never_merge_ambiguous_shops():
    watchdog = read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs")
    progress = read("src/Bot/ChromeNs/ResponseProgressTracker.cs")

    assert "FindConnectedQnByShopKey(shop.ShopKey)" in watchdog
    assert "current.SellerNick" in watchdog
    assert '"unresolved:" + Guid.NewGuid().ToString("N")' in watchdog
    assert "current.SellerNick" in progress
    assert '"unresolved:" + Guid.NewGuid().ToString("N")' in progress
    assert "DeliveryKey(seller, buyer, answer, shopKey)" in progress



def test_buyer_session_agent_prune_cannot_remove_a_refreshed_state_or_orphan_new_generation():
    agent = read("src/Bot/ChromeNs/BuyerSessionAgent.cs")
    observe = agent[
        agent.index("public BuyerSessionAgentObservation ObserveBuyerMessage"):
        agent.index("public BuyerSessionEventResult RecordEvent")
    ]
    record = agent[
        agent.index("public BuyerSessionEventResult RecordEvent"):
        agent.index("public bool IsCurrent")
    ]
    prune = agent[
        agent.index("public void Prune"):
        agent.index("private static SessionState GetOrCreateState")
    ]

    assert "Sessions.TryGetValue(key, out current)" in observe
    assert "ReferenceEquals(current, state)" in observe
    assert "Sessions.TryGetValue(key, out current)" in record
    assert "ReferenceEquals(current, state)" in record
    assert "lock (pair.Value.SyncRoot)" in prune
    assert "((ICollection<KeyValuePair<string, SessionState>>)Sessions).Remove(pair)" in prune
    assert "Sessions.TryRemove(pair.Key, out removed)" not in prune



def test_async_status_binding_uses_per_session_generation_to_reject_out_of_order_results():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    generation = server[
        server.index("private long NextStatusBindingGenerationLocked"):
        server.index("private void ForgetStatusBindingAttempt")
    ]
    bind = server[
        server.index("private async Task TryBindStatusConversation"):
        server.index("private void ReleaseStaleSessions")
    ]

    assert "_statusBindingGenerations" in server
    assert "NextStatusBindingGenerationLocked(sessionId)" in generation
    assert "IsCurrentStatusBindingGenerationLocked" in generation
    assert "private long ShouldRefreshStatusBinding(" in generation
    assert "bindingGeneration" in bind
    assert "IsCurrentStatusBindingGenerationLocked(session.SessionID, bindingGeneration)" in bind
    assert "lock (_sellerSessionSync)" in bind
    assert "IsAuthoritativeShopSession(shopKey, session.SessionID)" in bind
    assert "TryBindStatusConversation(\n                                                    session, loginNick, conversationNick, bindingGeneration, identityProbeGeneration)" in server
    assert "BeginStatusBindingAttempt(session.SessionID)" in server
    assert "NextStatusBindingGenerationLocked(session.SessionID)" in server


def test_burst_coordinator_state_and_semantic_continuation_are_shopkey_scoped():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    coordinator = source[source.index("internal sealed class BuyerMessageBurstCoordinator"):]

    assert "private static string Key(string shopKey, string seller, string buyer)" in coordinator
    assert 'return shopKey + "#" + (seller ?? string.Empty).Trim().ToLowerInvariant()' in coordinator
    assert "Key(item.ShopKey, item.SellerNick, item.BuyerNick)" in coordinator
    assert "Key(shopKey, seller, buyer)" in coordinator
    assert "Key(item.SellerNick, item.BuyerNick)" not in coordinator
    assert "Key(seller, buyer)" not in coordinator
    assert "HasPendingBuyerMessages(string shopKey, string seller, string buyer)" in coordinator
    cancel = coordinator[
        coordinator.index("public void CancelBuyer("):
        coordinator.index("private void FinalizeReplyOutcome")
    ]
    assert "if (shopKey.Length == 0)" in cancel
    assert "Key(shopKey, seller, buyer)" in cancel
    assert "_sessionAgent.CancelAll(seller, buyer, reason, shopKey)" in cancel


def test_background_recovery_and_burst_activity_are_shopkey_scoped():
    recovery = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    method = recovery[
        recovery.index("private async Task<bool> RecoverMissedBuyerMessagesAsync"):
        recovery.index("private Task ProcessRecoveredBuyerMessageAfterMissAsync")
    ]
    assert "ResolveRuntimeBySeller(this)" in method
    assert "shopKey = shop.ShopKey;" in method
    assert 'BotActivityCoordinator.Begin("后台消息补偿", seller, buyer, shopKey)' in method

    burst = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert 'BotActivityCoordinator.Begin("买家消息聚合/回复", item.SellerNick, item.BuyerNick, item.ShopKey)' in burst
    template = read("src/Bot/ChromeNs/OrderTemplateRequiredFieldsV2.cs")
    assert 'BotActivityCoordinator.Begin("订单模板字段发送V2", plan.Seller, plan.Buyer, plan.ShopKey)' in template
