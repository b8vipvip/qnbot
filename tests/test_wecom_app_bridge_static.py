from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_windows_bridge_uses_each_shop_control_plane_token_only():
    source = read("src/Bot/ChromeNs/WeComAppBridgeClient.cs")
    assert "ShopControlPlaneConnectionStore" in source
    assert "SnapshotOnlineShops" in source
    assert '"X-Shop-Key"' in source
    assert "ControlPlaneClientToken" not in source
    assert "WECOM_APP_SECRET" not in source
    assert "WECOM_CALLBACK_AES_KEY" not in source
    assert "/api/runtime/v1/handoff/replies/next" in source
    assert "/complete" in source


def test_handoff_reply_is_sent_to_exact_shop_seller_and_buyer_then_learned():
    source = read("src/Bot/ChromeNs/WeComAppBridgeClient.cs")
    locate = source.index("FindQn(shop, seller)")
    scope = source.index("ShopSettingsScope.Enter(shop)", locate)
    send = source.index("SendTextWithRetryAsync(buyer, reply", scope)
    learn = source.index("KnowledgeLearningService.RegisterAnswerSource", send)
    complete = source.index("CompleteAsync(shop", learn)
    assert locate < scope < send < learn < complete
    assert "if (!success)" in source[send:learn]
    assert "KnowledgeLearningService.QueueLearn" not in source
    assert '"人工回复-企业微信应用"' in source
    assert "ReplyDeduplicationService.RememberDelivered" in source
    assert "ShopIdentityResolver.Resolve(qn.Seller).ShopKey" in source
    assert "QN.CurQN" not in source


def test_notification_channel_creates_shop_bound_server_ticket():
    source = read("src/Bot/ChromeNs/HandoffNotificationService.cs")
    assert "企业微信应用消息=" in source
    assert "WeComAppBridgeClient.SendNotificationAsync" in source
    assert "BuildMessage" in source
    assert "ShopKey" in source
    assert "ShopSettingsScope.Enter(shop)" in source


def test_server_uses_ticket_bound_reply_queue_and_encrypted_callback():
    bridge = read("services/api-control-plane/wecom_bridge.py")
    crypto = read("services/api-control-plane/wecom_crypto.py")
    assert "wecom_handoff_tickets" in bridge
    assert "wecom_handoff_commands" in bridge
    assert '"QN-" + secrets.token_hex(4).upper()' in bridge
    assert '"QN-XXXXXXXX 回复内容"' in bridge
    assert "sha1_signature" in bridge
    assert "decrypt_callback" in bridge
    assert "claim_token" in bridge
    assert "t.client_id=?" in bridge
    assert "WECOM_BLOCK_SIZE = 32" in crypto
    assert "pkcs7_pad_32" in crypto
    assert "pkcs7_unpad_32" in crypto


def test_control_plane_container_registers_bridge_routes():
    dockerfile = read("services/api-control-plane/Dockerfile")
    bootstrap = read("services/api-control-plane/bootstrap.py")
    workflow = read(".github/workflows/api-control-plane-ci.yml")
    assert 'CMD ["python", "bootstrap.py"]' in dockerfile
    assert "include_router(wecom_bridge.router)" in bootstrap
    assert "install_on_bridge(wecom_bridge)" in bootstrap
    assert "wecom_bridge.py" in workflow
    assert "wecom_crypto.py" in workflow
    assert "docker-compose.bt.yml" in workflow


def test_windows_build_includes_bridge_client():
    targets = read("src/Directory.Build.targets")
    startup = read("src/Bot/StartUp/BootStrap.cs")
    assert "WeComAppBridgeClient.cs" in targets
    assert "WeComAppBridgeClient.Start();" in startup
