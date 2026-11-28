from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_qianniu_system_message_and_workbench_shells_are_never_treated_as_reception_desk():
    finder = read("src/Bot/Automation/ChatDeskNs/Automators/QnAccountFinder.cs")

    assert "IsSystemNotificationTitle" in finder
    assert 'value.Equals("千牛系统消息"' in finder
    assert 'value.Equals("千牛系统通知"' in finder
    assert "IsNonReceptionWorkbenchTitle" in finder
    assert 'value.IndexOf("千牛登录"' in finder
    assert 'value.Equals("千牛工作台"' in finder

    resolver = finder[finder.index("public static string ResolveSellerNameForWindow"):finder.index("private static IList<QN> GetRuntimeQns")]
    assert "IsSystemNotificationTitle(nativeWindowTitle)" in resolver
    assert "IsNonReceptionWorkbenchTitle(nativeWindowTitle)" in resolver
    assert "return string.Empty;" in resolver

    candidate = finder[finder.index("private static bool IsReceptionCandidate"):finder.index("private static int GetReceptionCandidateCount")]
    assert "IsSystemNotificationTitle(title)" in candidate
    assert "IsNonReceptionWorkbenchTitle(title)" in candidate
    assert 'title.IndexOf("千牛"' not in candidate
    assert 'title.IndexOf("接待"' in candidate
    assert 'title.IndexOf("客服"' in candidate
    assert 'if (string.IsNullOrWhiteSpace(seller)) return;' in finder


def test_duplicate_cdp_pages_forward_only_safe_inbound_events_to_authoritative_session():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    client = read("src/Bot/ChromeNs/CDPClient.cs")
    props = read("src/Bot/Directory.Build.props")

    assert "DuplicateCdpInboundRecoveryBridge.InitializeForApp()" in bridge
    assert "MyWebSocketServer.WSocketSvrInst.OnRecieveMessage += OnWebSocketMessage" in bridge
    assert 'string.Equals(type, "receiveNewMsg"' in bridge
    assert 'string.Equals(type, "onShopRobotReceriveNewMsgs"' in bridge
    assert 'string.Equals(type, "onConversationChange"' in bridge
    assert 'string.Equals(type, "messageCenterNotify"' in bridge
    recoverable = bridge[bridge.index("private static bool IsRecoverableInboundType"):bridge.index("private static void ObserveStatusSeller")]
    assert 'string.Equals(type, "onChatDlgActive"' not in recoverable
    assert "target.DispatchInboundEvent(item.Type, item.Response);" in bridge
    assert "重复千牛CDP入站消息已按ShopKey转交权威会话" in bridge
    assert "已补发初始化期间暂存的千牛入站消息" in bridge
    assert "Queue<PendingInboundEvent>" in bridge
    assert "PendingSync" in bridge
    assert "TimeSpan.FromSeconds(15)" in bridge

    assert "internal void DispatchInboundEvent(string type, string response)" in client
    assert 'if (type == "receiveNewMsg")' in client
    assert 'else if (type == "onShopRobotReceriveNewMsgs")' in client
    assert 'else if (type == "messageCenterNotify")' in client

    assert "ChromeNs\\DuplicateCdpInboundRecoveryBridge.cs" in props


def test_duplicate_cdp_bridge_never_replaces_outbound_cdp_ownership():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")

    assert "qn.CDP =" not in bridge
    assert "TryClaimShopSession" not in bridge
    assert "OpenChat(" not in bridge
    assert "SetActiveConversationByNick" not in bridge

    # Existing server still owns the one-authoritative-CDP decision.
    assert "TryClaimShopSession" in server
    assert "重复千牛CDP会话已完成识别但不接管该ShopKey运行通道" in server
    assert "qn.CDP = cdp;" in server


def test_duplicate_cdp_bridge_deduplicates_exact_cross_page_replays_with_bounded_state():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")

    assert "InboundFingerprintWindow = TimeSpan.FromMinutes(2)" in bridge
    assert "InboundFingerprintRetention = TimeSpan.FromMinutes(5)" in bridge
    assert "SessionSellerRetention = TimeSpan.FromHours(2)" in bridge
    assert "ConcurrentDictionary<string, DateTime> RecentInboundFingerprints" in bridge
    assert "TryAcceptInboundFingerprint(shopKey, e.Type, response, now)" in bridge
    assert "BuildInboundFingerprint(shopKey, type, response)" in bridge
    assert "StableHash64(raw)" in bridge
    assert "RecentInboundFingerprints.TryUpdate(fingerprint, now, seenAt)" in bridge
    assert "MaybeCleanupTransientState(now)" in bridge
    assert "((ICollection<KeyValuePair<string, DateTime>>)RecentInboundFingerprints)" in bridge
    assert ".Remove(pair);" in bridge

    # The bridge fingerprints the complete event payload, not just human-visible text, so two
    # legitimate same-text messages with different ids/timestamps are not collapsed together.
    fingerprint = bridge[bridge.index("private static string BuildInboundFingerprint"):bridge.index("private static ulong StableHash64")]
    assert '+ (response ?? string.Empty)' in fingerprint


def test_duplicate_cdp_bridge_redacts_identifiers_and_rate_limits_duplicate_logs():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")

    assert "private static string PrivacyToken" in bridge
    assert 'PrivacyToken("seller", seller)' in bridge
    assert 'PrivacyToken("session", sessionId)' in bridge
    assert "Interlocked.Increment(ref _suppressedDuplicateCount)" in bridge
    assert "count > 3 && count % 100 != 0" in bridge
    assert "suppressedTotal=" in bridge
    assert "sellerRef=" in bridge
    assert "sessionRef=" in bridge

    duplicate_log = bridge[bridge.index("private static void MaybeLogSuppressedDuplicate"):bridge.index("private static void MaybeCleanupTransientState")]
    assert ' + seller' not in duplicate_log
    assert ' + sessionId' not in duplicate_log



def test_conversation_change_replay_window_does_not_block_returning_to_recent_buyer():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    accept = bridge[
        bridge.index("private static bool TryAcceptInboundFingerprint"):
        bridge.index("private static string BuildInboundFingerprint")
    ]

    assert "ConversationChangeFingerprintWindow = TimeSpan.FromSeconds(3)" in bridge
    assert 'string.Equals(type, "onConversationChange", StringComparison.Ordinal)' in accept
    assert "? ConversationChangeFingerprintWindow" in accept
    assert ": InboundFingerprintWindow" in accept
    assert "now - seenAt <= fingerprintWindow" in accept



def test_duplicate_cdp_pending_recovery_queue_has_a_real_payload_byte_budget():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    enqueue = bridge[
        bridge.index("private static void EnqueuePendingBounded"):
        bridge.index("private static bool TryDeliverLive")
    ]
    drain = bridge[bridge.index("private static void DrainPending"):]

    assert "MaxPendingInboundEvents = 512" in bridge
    assert "MaxPendingInboundPayloadBytes = 16L * 1024L * 1024L" in bridge
    assert "item.PayloadBytes = (long)(item.Response ?? string.Empty).Length * sizeof(char)" in enqueue
    assert "item.PayloadBytes > MaxPendingInboundPayloadBytes" in enqueue
    assert "_pendingPayloadBytes + item.PayloadBytes > MaxPendingInboundPayloadBytes" in enqueue
    assert "lock (PendingSync)" in enqueue
    assert "_pendingPayloadBytes = Math.Max(0, _pendingPayloadBytes - dropped.PayloadBytes)" in enqueue
    assert "private static bool TryDequeuePending" in enqueue
    assert "_pendingPayloadBytes = Math.Max(0, _pendingPayloadBytes - (item == null ? 0 : item.PayloadBytes))" in enqueue
    assert "TryDequeuePending(out item)" in drain



def test_duplicate_cdp_inbound_routing_is_bound_to_source_shopkey_not_seller_nickname():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    deliver = bridge[
        bridge.index("private static bool TryDeliverLive"):
        bridge.index("private static void DrainPending")
    ]

    assert "GetShopKeyForSession(item.SourceSession)" in deliver
    assert "ShopContextLocator.FindConnectedQnByShopKey(shopKey)" in deliver
    assert "server.IsAuthoritativeShopSession(shopKey, target.SessionId)" in deliver
    assert "target.IsShopIdentityRetired" in deliver
    assert "QN.FindExistingBySellerNick" not in bridge
    assert "BuildInboundFingerprint(shopKey, type, response)" in bridge
    assert "public string ShopKey;" in bridge
    assert "public bool FingerprintAccepted;" in bridge


def test_pending_duplicate_cdp_events_revalidate_shopkey_and_replay_only_to_that_shop():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    deliver = bridge[
        bridge.index("private static bool TryDeliverLive"):
        bridge.index("private static void DrainPending")
    ]
    drain = bridge[bridge.index("private static void DrainPending"):]

    assert "mappedShopKey" in deliver
    assert "拒绝跨店补发" in deliver
    assert "TryDeliverLive(item, true)" in drain
    assert "QN.FindExistingBySellerNick" not in drain
    assert "ShopContextLocator.FindConnectedQnByShopKey(shopKey)" in deliver



def test_duplicate_cdp_fingerprint_cache_has_hard_count_cap_and_closed_sessions_are_released():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    cleanup = bridge[
        bridge.index("private static void MaybeCleanupTransientState"):
        bridge.index("private static void EnqueuePendingBounded")
    ]

    assert "MaxRecentInboundFingerprints = 65536" in bridge
    assert "private static void TrimOldestRecentFingerprints()" in cleanup
    assert "RecentInboundFingerprints.OrderBy(x => x.Value).Take(removeCount)" in cleanup
    assert "targetCount = MaxRecentInboundFingerprints * 3 / 4" in cleanup
    assert "internal static void ForgetClosedSession(string sessionId)" in bridge
    assert "SessionSellers.TryRemove(sessionId, out ignored)" in bridge
    assert server.count("DuplicateCdpInboundRecoveryBridge.ForgetClosedSession(session") == 2



def test_websocket_shop_identity_transition_drops_old_pending_events_until_new_binding():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    on_message = bridge[
        bridge.index("private static void OnWebSocketMessage"):
        bridge.index("private static bool IsRecoverableInboundType")
    ]
    enqueue = bridge[
        bridge.index("private static void EnqueuePendingBounded"):
        bridge.index("private static bool TryDequeuePending")
    ]
    deliver = bridge[
        bridge.index("private static bool TryDeliverLive"):
        bridge.index("private static void DrainPending")
    ]
    transition = bridge[
        bridge.index("internal static void MarkSessionIdentityTransition"):
        bridge.index("private static void MaybeCleanupTransientState")
    ]
    claim = server[
        server.index("private bool TryClaimShopSession"):
        server.index("private async Task<bool> TryPromoteDuplicateShopSessionAsync")
    ]

    assert "ConcurrentDictionary<string, byte> RetiredSourceSessions" in bridge
    assert "if (IsSessionIdentityTransitioning(sessionId)) return;" in on_message
    assert "if (IsSessionIdentityTransitioning(item.SourceSession)" in enqueue
    assert "|| !IsSourceSessionConnected(item.SourceSession))" in enqueue
    assert "if (IsSessionIdentityTransitioning(item.SourceSession)" in deliver
    assert "|| !IsSourceSessionConnected(item.SourceSession)) return false;" in deliver
    assert "RetiredSourceSessions[sessionId] = 0;" in transition
    assert "DropPendingForSourceSession(sessionId)" in transition
    assert "ReleaseAcceptedFingerprint(pending)" in transition
    assert "_pendingPayloadBytes = Math.Max(0, _pendingPayloadBytes - pending.PayloadBytes)" in transition
    assert "RetiredSourceSessions.TryRemove(sessionId, out ignoredTransition)" in transition
    assert "DuplicateCdpInboundRecoveryBridge.MarkSessionIdentityTransition(sessionId)" in claim
    assert claim.count("DuplicateCdpInboundRecoveryBridge.ClearSessionIdentityTransition(sessionId)") == 3



def test_identity_transition_marker_is_set_before_old_shopkey_mapping_is_removed():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    claim = server[
        server.index("private bool TryClaimShopSession"):
        server.index("private async Task<bool> TryPromoteDuplicateShopSessionAsync")
    ]

    marker = claim.index("DuplicateCdpInboundRecoveryBridge.MarkSessionIdentityTransition(sessionId)")
    remove_mapping = claim.index("_sessionShopKeys.TryRemove(sessionId, out ignoredShop)")
    assert marker < remove_mapping



def test_shop_identity_binding_is_periodically_revalidated_even_when_display_nicks_match():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    method = server[
        server.index("private long ShouldRefreshStatusBinding"):
        server.index("private async Task TryBindStatusConversation")
    ]
    assert "StatusBindingRefreshInterval = TimeSpan.FromSeconds(30)" in server
    assert "_lastStatusBindingRefreshUtc" in server
    assert "if (!TryReserveStatusBindingRefresh(sessionId, DateTime.UtcNow, keyChanged)) return 0;" in method
    assert "_lastStatusBindings[sessionId] = key;" in method
    assert "else if (!wasAuthoritative)" in server
    assert "var bindingGeneration = ShouldRefreshStatusBinding(" in server
    assert "Task.Run(() => TryBindStatusConversation(\n                                                    session, loginNick, conversationNick, bindingGeneration, identityProbeGeneration))" in server



def test_periodic_sweeper_revalidates_exact_shopkey_for_every_connected_websocket():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    sweeper = server[
        server.index("private void StartDuplicateSessionSweeper"):
        server.index("private bool TryClaimShopSession")
    ]
    refresh = server[
        server.index("private void RevalidateConnectedShopSessions"):
        server.index("private async Task TryBindStatusConversation")
    ]

    assert "DuplicateSessionSweepInterval = TimeSpan.FromSeconds(30)" in server
    assert "RevalidateConnectedShopSessions();" in sweeper
    assert "foreach (var pair in _liveSessions)" in refresh
    assert "!_connectedSessions.ContainsKey(pair.Key)" in refresh
    assert "_initializing.ContainsKey(pair.Key)" in refresh
    assert "_closingDuplicateSessions.ContainsKey(pair.Key)" in refresh
    assert "TryReserveStatusBindingRefresh(pair.Key, now, false)" in refresh
    assert "_shopIdentityProbeGenerations.TryGetValue(pair.Key, out bindingGeneration)" in refresh
    assert "Task.Run(() => TryRevalidateShopIdentity(session, bindingGeneration))" in refresh
    assert "private async Task TryRevalidateShopIdentity(WebSocketSession session, long bindingGeneration)" in server
    assert "var user = await cdp.GetCurrentUser().ConfigureAwait(false);" in server
    assert "ShopContextLocator.ResolveRuntimeBySellerIdentity(user.Result)" in server
    assert '"periodic_shop_identity_revalidation"' in server
    assert "TargetId/ShopKey" in refresh



def test_periodic_identity_revalidation_does_not_resurrect_closed_websocket_sessions():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    method = server[
        server.index("private async Task TryRevalidateShopIdentity(WebSocketSession session, long bindingGeneration)"):
        server.index("private async Task TryBindStatusConversation")
    ]

    assert "if (session == null" in method
    assert "IsCurrentShopIdentityProbe(session.SessionID, bindingGeneration)" in method
    assert "IsCurrentShopIdentityProbeLocked(session.SessionID, bindingGeneration)" in method
    assert "TryClaimShopSession(shopKey, session.SessionID, bindingGeneration, () =>" in method


def test_duplicate_identity_revalidation_checks_connection_before_restoring_client_binding():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    method = server[
        server.index("private async Task TryRevalidateShopIdentity(WebSocketSession session, long bindingGeneration)"):
        server.index("private async Task TryBindStatusConversation")
    ]

    assert "TryClaimShopSession(shopKey, session.SessionID, bindingGeneration, () =>" in method
    assert "if (!IsCurrentShopIdentityProbeLocked(session.SessionID, bindingGeneration)" in method
    assert method.index("TryClaimShopSession(shopKey, session.SessionID, bindingGeneration, () =>") < method.index(
        "qn.CDP = cdp;"
    )


def test_final_duplicate_cdp_dispatch_is_serialized_with_source_identity_transition():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    mark = bridge[
        bridge.index("internal static void MarkSessionIdentityTransition"):
        bridge.index("internal static void ClearSessionIdentityTransition")
    ]
    deliver = bridge[
        bridge.index("private static bool TryDeliverLive"):
        bridge.index("private static void DrainPending")
    ]

    assert "SessionTransitionLocks" in bridge
    assert "Enumerable.Range(0, 64).Select(_ => new object()).ToArray()" in bridge
    assert "lock (GetSessionTransitionLock(sessionId))" in mark
    assert "WithSessionTransitionLocks(item.SourceSession, target.SessionId" in deliver
    assert deliver.index("WithSessionTransitionLocks(item.SourceSession, target.SessionId") < deliver.index(
        "target.DispatchInboundEvent(item.Type, item.Response)"
    )
    assert "if (IsSessionIdentityTransitioning(item.SourceSession)" in deliver
    assert "|| !IsSourceSessionConnected(item.SourceSession))" in deliver


def test_closed_or_transitioning_source_sessions_cannot_requeue_or_replay_inbound_events():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    enqueue = bridge[
        bridge.index("private static void EnqueuePendingBounded"):
        bridge.index("private static bool TryDequeuePending")
    ]
    forget = bridge[
        bridge.index("internal static void ForgetClosedSession"):
        bridge.index("private static void MaybeCleanupTransientState")
    ]
    on_message = bridge[
        bridge.index("private static void OnWebSocketMessage"):
        bridge.index("private static bool IsRecoverableInboundType")
    ]

    assert "IsWebSocketSessionConnected(sessionId)" in on_message
    assert "internal bool IsWebSocketSessionConnected(string sessionId)" in server
    assert "IsSourceSessionConnected(item.SourceSession)" in enqueue
    assert "ReleaseAcceptedFingerprint(item)" in enqueue
    assert "ReleaseAcceptedFingerprint(dropped)" in enqueue
    assert "DropPendingForSourceSession(sessionId)" in forget
    assert "lock (GetSessionTransitionLock(sessionId))" in forget


def test_rejected_or_expired_pending_events_release_their_dedup_fingerprint():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    enqueue = bridge[
        bridge.index("private static void EnqueuePendingBounded"):
        bridge.index("private static bool TryDequeuePending")
    ]
    drain = bridge[bridge.index("private static void DrainPending"):]

    assert "private static void ReleaseAcceptedFingerprint(PendingInboundEvent item)" in bridge
    assert "ReleaseAcceptedFingerprint(item)" in enqueue
    assert "ReleaseAcceptedFingerprint(dropped)" in enqueue
    assert "ReleaseAcceptedFingerprint(item)" in drain
    assert "ReleaseAcceptedFingerprint(pending)" in bridge



def test_final_dispatch_revalidates_the_exact_current_shopkey_inside_transition_lock():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    deliver = bridge[
        bridge.index("private static bool TryDeliverLive"):
        bridge.index("private static void DrainPending")
    ]
    lock_body = deliver[
        deliver.index("WithSessionTransitionLocks(item.SourceSession, target.SessionId"):
        deliver.index("if (!accepted) return false;")
    ]

    assert "var finalMappedShopKey = server.GetShopKeyForSession(item.SourceSession);" in lock_body
    assert "if (!string.Equals(finalMappedShopKey, shopKey, StringComparison.Ordinal))" in lock_body
    assert "ReleaseAcceptedFingerprint(item);" in lock_body
    assert lock_body.index("finalMappedShopKey") < lock_body.index(
        "target.DispatchInboundEvent(item.Type, item.Response)"
    )


def test_direct_cdpcclient_dispatch_cannot_bypass_shopkey_identity_transition_gate():
    client = read("src/Bot/ChromeNs/CDPClient.cs")
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")

    authority = client[
        client.index("private bool HasCurrentShopAuthority"):
        client.index("private bool CanDispatchInboundEvent")
    ]
    gate = client[
        client.index("private bool CanDispatchInboundEvent"):
        client.index("internal void DispatchInboundEvent")
    ]
    dispatch = client[
        client.index("internal void DispatchInboundEvent"):
        client.index("private void RepairDuplicateStatusDiagnostics")
    ]
    execute = client[
        client.index("private async Task<string> SendExecuteAndWaitCoreAsync"):
        client.index("internal Task<string> EvaluateExpressionAsync")
    ]
    socket_dispatch = client[
        client.index("private void OnWSocketRecieveMessage"):
        client.index("internal void DispatchInboundEvent")
    ]

    assert "if (!CanDispatchInboundEvent(type)) return;" in dispatch
    assert 'string.Equals(type, "qnbotStatus", StringComparison.Ordinal)' in gate
    assert "return HasCurrentShopAuthority();" in gate
    assert "IsInvalidated" in authority
    assert "IsShopIdentityRetired" in authority
    assert "DuplicateCdpInboundRecoveryBridge.IsSessionIdentityTransitioning(sessionId)" in authority
    assert "server.IsWebSocketSessionConnected(sessionId)" in authority
    assert "server.IsWebSocketSessionConnected(sessionId)" in gate
    assert "server.IsAuthoritativeShopSession(shopKey, sessionId)" in authority
    assert "internal static bool IsSessionIdentityTransitioning(string sessionId)" in bridge

    # Outbound commands can wait behind another execute call while the same physical
    # WebSocket changes accounts. Revalidate ownership after acquiring the gate and
    # immediately before sending; only identity probes may read a retired page.
    assert "if (!allowRetiredIdentityRead && !HasCurrentShopAuthority())" in execute
    assert execute.index("!HasCurrentShopAuthority()") < execute.index("socket.Send(")
    identity_probe = client[
        client.index("private async Task<T> InvokeShopIdentity"):
        client.index("public async Task<LocalUserResponse> GetCurrentUser")
    ]
    assert "allowRetiredIdentityRead: true" in identity_probe

    # execute responses still complete the in-flight identity-discovery request before
    # business-event gating, so a retired socket can safely discover its new ShopKey.
    assert socket_dispatch.index('if (e.Type == "execute")') < socket_dispatch.index(
        "TryDispatchRawInbound(this, e.Type, response)"
    )


def test_duplicate_cdp_bridge_drops_events_without_verified_source_shopkey():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    capture = bridge[
        bridge.index("private static void OnWebSocketMessage"):
        bridge.index("private static bool IsRecoverableInboundType")
    ]

    assert "var shopKey = server.GetShopKeyForSession(sessionId);" in capture
    assert "if (shopKey.Length == 0)" in capture
    assert "缺少来源稳定ShopKey，拒绝跨会话暂存" in capture
    assert capture.index("if (shopKey.Length == 0)") < capture.index("var item = new PendingInboundEvent")
    assert "seller.Length == 0 && shopKey.Length == 0" not in capture


def test_raw_and_forwarded_cdp_dispatch_share_identity_transition_locks():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    client = read("src/Bot/ChromeNs/CDPClient.cs")

    raw_handler = client[
        client.index("private void OnWSocketRecieveMessage"):
        client.index("// The raw WebSocket dispatcher")
    ]
    raw_dispatch = bridge[
        bridge.index("internal static bool TryDispatchRawInbound"):
        bridge.index("private static void ReleaseAcceptedFingerprint")
    ]
    forwarded = bridge[
        bridge.index("private static bool TryDeliverLive"):
        bridge.index("private static void DrainPending")
    ]

    assert "TryDispatchRawInbound(this, e.Type, response)" in raw_handler
    assert "lock (GetSessionTransitionLock(sessionId))" in raw_dispatch
    assert "server.GetShopKeyForSession(sessionId)" in raw_dispatch
    assert "server.IsAuthoritativeShopSession(shopKey, sessionId)" in raw_dispatch
    assert 'string.Equals(type, "qnbotStatus", StringComparison.Ordinal)' in raw_dispatch
    assert "WithSessionTransitionLocks(item.SourceSession, target.SessionId" in forwarded
    assert "IsSessionIdentityTransitioning(target.SessionId)" in forwarded
    assert "server.GetShopKeyForSession(target.SessionId)" in forwarded
    assert "GetSessionTransitionLockIndex(sourceSession)" in bridge
    assert "GetSessionTransitionLockIndex(targetSession)" in bridge



def test_dropped_pending_events_release_only_their_own_fingerprint_timestamp():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    release = bridge[
        bridge.index("private static void ReleaseAcceptedFingerprint"):
        bridge.index("private static bool IsSourceSessionConnected")
    ]
    enqueue = bridge[
        bridge.index("private static void EnqueuePendingBounded"):
        bridge.index("private static bool TryDequeuePending")
    ]
    drain = bridge[bridge.index("private static void DrainPending"):]

    assert "FingerprintAcceptedAt" in bridge
    assert "ICollection<KeyValuePair<string, DateTime>>" in release
    assert "new KeyValuePair<string, DateTime>(fingerprint, acceptedAt)" in release
    assert "RecentInboundFingerprints.TryRemove(fingerprint" not in release
    assert "ReleaseAcceptedFingerprint(item);" in enqueue
    assert "ReleaseAcceptedFingerprint(dropped);" in enqueue
    assert "ReleaseAcceptedFingerprint(item);" in drain



def test_transient_cache_cleanup_removes_only_the_exact_stale_snapshot():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    cleanup = bridge[
        bridge.index("private static void MaybeCleanupTransientState"):
        bridge.index("private static void EnqueuePendingBounded")
    ]
    trim = bridge[
        bridge.index("private static void TrimOldestRecentFingerprints"):
        bridge.index("private static void EnqueuePendingBounded")
    ]

    assert "((ICollection<KeyValuePair<string, DateTime>>)RecentInboundFingerprints)" in cleanup
    assert ".Remove(pair);" in cleanup
    assert "((ICollection<KeyValuePair<string, SessionSellerBinding>>)SessionSellers)" in cleanup
    assert "((ICollection<KeyValuePair<string, DateTime>>)RecentInboundFingerprints)" in trim
    assert "RecentInboundFingerprints.TryRemove(pair.Key" not in cleanup
    assert "SessionSellers.TryRemove(pair.Key" not in cleanup
    assert "RecentInboundFingerprints.TryRemove(pair.Key" not in trim



def test_periodic_shop_identity_probe_cannot_overwrite_a_newer_status_generation():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    claim = server[
        server.index("private bool TryClaimShopSession"):
        server.index("private async Task<bool> TryPromoteDuplicateShopSessionAsync")
    ]
    revalidate = server[
        server.index("private async Task TryRevalidateShopIdentity(WebSocketSession session, long bindingGeneration)"):
        server.index("private async Task TryBindStatusConversation")
    ]

    assert "long? expectedIdentityProbeGeneration = null" in claim
    assert "currentIdentityProbeGeneration != expectedIdentityProbeGeneration.Value" in claim
    assert "TryClaimShopSession(shopKey, session.SessionID, bindingGeneration, () =>" in revalidate
    assert "IsCurrentShopIdentityProbeLocked(session.SessionID, bindingGeneration)" in revalidate
    assert "TryClaimShopSession(shopKey, session.SessionID, bindingGeneration, () =>" in revalidate
    assert "if (onClaimed != null) onClaimed();" in claim
    assert "QN.GetByShopIdentity(user.Result)" in revalidate



def test_every_status_heartbeat_invalidates_old_shop_identity_reads_even_when_display_nicks_match():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    status = server[
        server.index('if (wMsg.Type == "qnbotStatus")'):
        server.index('else if (wMsg.Type == "imsdkApiScan")')
    ]
    init = server[
        server.index("private async Task TryInitSession"):
        server.index("private static bool IsLoopbackListenerActive")
    ]
    promotion = server[
        server.index("private async Task<bool> TryPromoteDuplicateShopSessionAsync"):
        server.index("internal bool IsWebSocketSessionConnected")
    ]

    assert "var identityProbeGeneration = BeginShopIdentityProbe(session.SessionID)" in status
    assert 'TryInitSession(session, "status", identityProbeGeneration)' in status
    assert "private long BeginShopIdentityProbe(string sessionId)" in server
    assert "NextShopIdentityProbeGenerationLocked(sessionId)" in server
    assert "IsCurrentShopIdentityProbe(session.SessionID, identityProbeGeneration)" in init
    assert "TryClaimShopSession(shopKey, session.SessionID, identityProbeGeneration, () =>" in init
    assert "expectedIdentityProbeGeneration: identityProbeGeneration" in status or "expectedIdentityProbeGeneration: identityProbeGeneration" in server
    assert "IsCurrentShopIdentityProbeLocked(sessionId, expectedIdentityProbeGeneration.Value)" in promotion



def test_periodic_identity_claim_and_qn_binding_are_atomic_against_new_status_events():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    claim = server[
        server.index("private bool TryClaimShopSession"):
        server.index("private async Task<bool> TryPromoteDuplicateShopSessionAsync")
    ]
    periodic = server[
        server.index("private async Task TryRevalidateShopIdentity(WebSocketSession session, long bindingGeneration)"):
        server.index("private async Task TryBindStatusConversation")
    ]

    assert "Action onClaimed = null" in claim
    assert "if (onClaimed != null) onClaimed();" in claim
    assert claim.index("lock (_sellerSessionSync)") < claim.index("if (onClaimed != null) onClaimed();")
    assert "TryClaimShopSession(shopKey, session.SessionID, bindingGeneration, () =>" in periodic
    assert periodic.index("TryClaimShopSession(shopKey, session.SessionID, bindingGeneration, () =>") < periodic.index("qn.CDP = cdp;")
    assert "CDPClient.PreferRuntimeSession(" in periodic


def test_initial_identity_claim_and_qn_binding_are_atomic_against_new_status_events():
    server = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    init = server[
        server.index("private async Task TryInitSession(WebSocketSession session, string reason, long identityProbeGeneration)"):
        server.index("private static bool IsLoopbackListenerActive")
    ]

    assert "TryClaimShopSession(shopKey, session.SessionID, identityProbeGeneration, () =>" in init
    assert "qn.QnVersion = ver != null ? ver.version : string.Empty;" in init
    assert "qn.CDP = cdp;" in init
    assert "_initialized[session.SessionID] = true;" in init
    assert "if (!IsCurrentShopIdentityProbeLocked(session.SessionID, identityProbeGeneration)) return;" in init
