from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_tray_exit_never_reopens_a_closed_wpf_window():
    source = read("src/Bot/AssistWindow/NotifyIcon/WndNotifyIcon.xaml.cs")
    handler = source[source.index("private void btnExit_Click"):source.index("public void AddSellerMenuItem")]
    assert "_exitRequested" in handler
    assert "Application.Current" in handler
    assert "app.Shutdown()" in handler
    assert "Visibility = Visibility.Visible" not in handler
    assert "tbkClose.Visibility" not in handler
    assert "xMoveToWorkAreaCenter" not in handler
    assert "DelayCaller.CallAfterDelay" not in handler


def test_store_rule_profile_path_and_cache_are_shop_scoped():
    service = read("src/Bot/ChromeNs/StorePromptProfileService.cs")
    ui = read("src/Bot/Knowledge/StorePromptProfileUi.cs")
    assert "ShopSettingsScope.Current" in service
    assert "Paths.GetRulesRoot(shop)" in service
    assert 'ProfileFileName = "store-prompt-profile.json"' in service
    assert "ConcurrentDictionary<string, StorePromptProfile>" in service
    assert "Cache.TryGetValue(path" in service
    assert '"QianniuAiBot",\n                "data",\n                "store-prompt-profile.json"' not in service
    assert "profiles.Count != 1" in service
    assert "GetCompatibilityDataRoot(shop)" in service
    assert "ShopScopedUiBridge.Attach(window, shop)" in ui
    assert "new StorePromptProfileWindow(shop)" in ui
    assert "ShopSettingsScope.Enter(_shop)" in ui


def test_store_rule_profile_is_automatically_included_in_shop_cloud_backup():
    service = read("src/Bot/ChromeNs/StorePromptProfileService.cs")
    backup = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    assert "Paths.GetRulesRoot(shop)" in service
    assert "EnumerateShopFiles(shop)" in backup
    assert "Paths.GetShopRoot(shop)" in backup
    assert 'new[] { "logs", "backup", "cache" }' in backup
    assert 'string.Equals(relative, "profile.json"' in backup
    assert "store-prompt-profile.json" not in backup  # not excluded: rules files are backed up generically


def test_store_rule_cloud_sync_is_per_shop_and_has_apply_backup():
    client = read("src/Bot/Knowledge/StoreRuleCloudSyncService.cs")
    service = read("src/Bot/ChromeNs/StorePromptProfileService.cs")
    props = read("src/Bot/Directory.Build.props")
    assert "ConcurrentDictionary<string, ShopSyncState>" in client
    assert "StoreRuleCloudRevision" in client
    assert "StoreRuleCloudLastHash" in client
    assert "KnowledgeV2CloudSyncService.IsEnabledForShop(shop)" in client
    assert "ShopControlPlaneConnectionStore(shop, Paths)" in client
    assert '"X-Shop-Key", shop.ShopKey' in client
    assert '"/api/runtime/v1/bot-web/store-rule-sync"' in client
    assert "Paths.GetBackupRoot(shop)" in client
    assert "StorePromptProfileService.ApplyCloudPayload" in client
    assert "ProfileChanged" in service
    assert "BuildCloudPayload" in service
    assert "StoreRuleCloudSyncService.cs" in props


def test_server_store_rule_sync_uses_token_scoped_state_and_rebind_cleanup():
    server = read("services/api-control-plane/store_rule_sync.py")
    binding = read("services/api-control-plane/bot_client_shop_binding.py")
    bootstrap = read("services/api-control-plane/bootstrap.py")
    dockerfile = read("services/api-control-plane/Dockerfile")
    assert "bot_store_rule_state" in server
    assert "client_id INTEGER PRIMARY KEY" in server
    assert "shop_key TEXT" not in server
    assert "Depends(core._runtime_client)" in server
    assert 'request.headers.get("x-shop-key")' in server
    assert 'detail="缺少 X-Shop-Key"' in server
    assert '"/api/runtime/v1/bot-web/store-rule-sync"' in server
    assert '"bot_store_rule_state"' in binding
    assert "import store_rule_sync" in bootstrap
    assert "store_rule_sync.install(control_plane)" in bootstrap
    assert "store_rule_sync.init_db()" in bootstrap
    assert "store_rule_sync.py" in dockerfile
