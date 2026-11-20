from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_websocket_server_keeps_one_authoritative_cdp_session_per_shopkey():
    source = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    assert "_shopSessions" in source
    assert "_sessionShopKeys" in source
    assert "_connectedSessions" in source
    assert "TryClaimShopSession" in source
    assert "ReleaseShopSession" in source
    assert "重复千牛CDP" in source or "重复千牛WebSocket" in source
    assert "ShouldRefreshStatusBinding" in source
    assert "var wasAuthoritative" in source
    assert "已验证当前CDP登录身份并提升ShopKey standby为权威会话" in source
    assert "TryPromoteDuplicateShopSessionAsync" in source
    assert "await client.GetCurrentUser().ConfigureAwait(false)" in source

    # Both assignment points must be protected by the authoritative-session claim.
    # Status binding, initial initialization and periodic exact-ShopKey revalidation
    # are the three verified paths allowed to attach a CDP client to a QN instance.
    assert source.count("qn.CDP = cdp;") == 3
    bind_pos = source.index("qn.CDP = cdp;")
    init_pos = source.index("qn.CDP = cdp;", bind_pos + 1)
    assert source.rfind("TryClaimShopSession", 0, bind_pos) > source.rfind("QN.GetByShopIdentity", 0, bind_pos)
    assert source.rfind("TryClaimShopSession", 0, init_pos) > source.rfind("QN.GetByShopIdentity", 0, init_pos)


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
    assert "Task.Run(() => TryInitSession(session, \"status\", identityProbeGeneration))" in source
    assert "else if (!wasAuthoritative)" in source
    assert "Task.Run(() => TryBindStatusConversation(\n                                                    session, loginNick, conversationNick, bindingGeneration))" in source


def test_status_binding_failure_releases_only_its_own_suppression_key_for_retry():
    source = read("src/Bot/ChromeNs/MyWebSocketServer.cs")
    helper = source[source.index("private void ForgetStatusBindingAttempt"):source.index("private void RevalidateConnectedShopSessions")]
    bind = source[source.index("private async Task TryBindStatusConversation"):source.index("private async Task TryInitSession")]
    invalid_user = bind[bind.index("if (user == null"):bind.index("var shop = ShopContextLocator.ResolveRuntimeBySellerIdentity")]
    failure_catch = bind[bind.index("catch (Exception ex)"):]

    assert "ICollection<KeyValuePair<string, string>>" in helper
    assert "new KeyValuePair<string, string>(session.SessionID, key)" in helper
    assert "ForgetStatusBindingAttempt(session, loginNick, conversationNick);" in invalid_user
    assert invalid_user.index("ForgetStatusBindingAttempt(session, loginNick, conversationNick);") < invalid_user.index("return;")
    assert "ForgetStatusBindingAttempt(session, loginNick, conversationNick);" in failure_catch
    assert "Log.Exception(ex);" in failure_catch
