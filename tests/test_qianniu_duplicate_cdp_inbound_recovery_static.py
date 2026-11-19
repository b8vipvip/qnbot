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
    assert "RecentInboundFingerprints.TryRemove(pair.Key, out ignored)" in bridge

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



def test_dropped_pending_events_release_only_their_own_accepted_fingerprint():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    release = bridge[
        bridge.index("private static void ReleaseAcceptedFingerprint"):
        bridge.index("private static bool TryAcceptInboundFingerprint")
    ]
    enqueue = bridge[
        bridge.index("private static void EnqueuePendingBounded"):
        bridge.index("private static bool TryDequeuePending")
    ]
    deliver = bridge[
        bridge.index("private static bool TryDeliverLive"):
        bridge.index("private static void DrainPending")
    ]
    drain = bridge[bridge.index("private static void DrainPending"):]

    assert "FingerprintAcceptedAt" in bridge
    assert "ICollection<KeyValuePair<string, DateTime>>" in release
    assert "new KeyValuePair<string, DateTime>(fingerprint, acceptedAt)" in release
    assert "ReleaseAcceptedFingerprint(item);" in enqueue
    assert "ReleaseAcceptedFingerprint(dropped);" in enqueue
    assert "ReleaseAcceptedFingerprint(item);" in deliver
    assert "ReleaseAcceptedFingerprint(item);" in drain
