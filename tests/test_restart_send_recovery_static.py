from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_websocket_server_keeps_one_authoritative_cdp_session_per_seller():
    source = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    assert "_sellerSessions" in source
    assert "_sessionSellers" in source
    assert "_connectedSessions" in source
    assert "TryClaimSellerSession" in source
    assert "ReleaseSellerSession" in source
    assert "重复千牛CDP" in source or "重复千牛WebSocket" in source
    assert "ShouldRefreshStatusBinding" in source
    assert "var wasAuthoritative" in source
    assert "千牛standby页面接管权威会话，强制重建客服/CDP绑定" in source

    # Both assignment points must be protected by the authoritative-session claim.
    assert source.count("qn.CDP = cdp;") == 2
    bind_pos = source.index("qn.CDP = cdp;")
    init_pos = source.index("qn.CDP = cdp;", bind_pos + 1)
    assert source.rfind("TryClaimSellerSession", 0, bind_pos) > source.rfind("if (qn == null)", 0, bind_pos)
    assert source.rfind("TryClaimSellerSession", 0, init_pos) > source.rfind("var sellerNick", 0, init_pos)


def test_knowledge_cloud_hash_matches_server_sorted_json_and_avoids_repeat_apply():
    client = read("src/Bot/Knowledge/KnowledgeCloudSyncService.cs")
    server = read("services/api-control-plane/bot_web_conversation_knowledge.py")
    assert "CanonicalizeJson" in client
    assert "CanonicalizeToken" in client
    assert "OrderBy(x => x.Name, StringComparer.Ordinal)" in client
    assert "canonicalCloudHash" in client
    assert "知识库云同步哈希已收敛，无需重复覆盖本地" in client
    assert "sort_keys=True" in server
    assert "json.dumps" in server


def test_restart_recovery_does_not_reinitialize_same_authority_forever_but_rebinds_on_promotion():
    source = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    assert "_lastStatusBindings" in source
    assert "ShouldRefreshStatusBinding" in source
    assert "var wasInitialized = _initialized.ContainsKey(session.SessionID);" in source
    assert "Task.Run(() => TryInitSession(session, \"status\"))" in source
    assert "else if (!wasAuthoritative && !string.IsNullOrWhiteSpace(loginNick))" in source
    assert "Task.Run(() => TryBindStatusConversation(session, loginNick, conversationNick))" in source
