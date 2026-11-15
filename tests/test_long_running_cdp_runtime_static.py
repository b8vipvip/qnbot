from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_cdp_execute_wait_does_not_block_thread_pool_workers():
    source = read("src/Bot/ChromeNs/CDPClient.cs")

    assert "TaskCompletionSource<string>" in source
    assert "TaskCreationOptions.RunContinuationsAsynchronously" in source
    assert "Task.WhenAny(requestCompletion.Task, timeoutTask)" in source
    assert "Task.Run(() => requestResetEvent.Wait" not in source
    assert "ManualResetEventSlim" not in source


def test_websocket_dispatch_is_singleton_and_does_not_root_closed_clients():
    source = read("src/Bot/ChromeNs/CDPClient.cs")

    assert "ConcurrentDictionary<string, WeakReference> SessionClients" in source
    assert "EnsureDispatcherInstalled" in source
    assert "OnRecieveMessage += DispatchWebSocketMessage" in source
    assert "OnRecieveMessage += OnWSocketRecieveMessage" not in source
    assert "weak.Target as CDPClient" in source


def test_precise_conversation_change_keeps_authoritative_runtime_command_webview():
    source = read("src/Bot/ChromeNs/CDPClient.cs")

    assert 'PreferRuntimeSession(shopKey, sellerNick, SessionId, buyerNick, "onConversationChange")' in source
    assert 'PreferRuntimeSession(sellerNick, physicalSourceSession, buyerNick, "onConversationChange")' not in source
    assert "ResolvePreferredRuntimeClient" in source
    assert 'desc + "@runtime-active-session"' in source
    assert "活动CDP会话失效，已撤销会话偏好并回退权威通道" in source


def test_duplicate_status_cannot_overwrite_logical_current_buyer():
    source = read("src/Bot/ChromeNs/CDPClient.cs")

    assert "RepairDuplicateStatusDiagnostics" in source
    assert "PreferredSellerSessions" in source
    assert "BotConnectionDiagnostics.RecordBuyerSeller(sellerNick, logicalBuyer)" in source


def test_forwarded_conversation_change_is_ingress_only_and_cannot_rebind_runtime_qn():
    bridge = read("src/Bot/ChromeNs/DuplicateCdpInboundRecoveryBridge.cs")
    client = read("src/Bot/ChromeNs/CDPClient.cs")

    assert "CDPClient.BeginForwardedInbound(item.SourceSession)" in bridge
    assert "target.DispatchInboundEvent(item.Type, item.Response);" in bridge
    assert "SetActiveConversationByNick" not in bridge
    assert "qn.CDP =" not in bridge
    assert "ForwardedInboundSourceSession" in client
    assert "physicalSourceSession = (ForwardedInboundSourceSession.Value" not in client
    assert 'PreferRuntimeSession(shopKey, sellerNick, SessionId, buyerNick, "onConversationChange")' in client
