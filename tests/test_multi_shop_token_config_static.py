from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_shop_tokens_are_dpapi_current_user_protected_and_shop_bound():
    source = read("src/Bot/ShopScope/ShopTokenStore.cs")
    assert "ProtectedData.Protect" in source
    assert "ProtectedData.Unprotect" in source
    assert "DataProtectionScope.CurrentUser" in source
    # Preserve the pre-rename entropy so existing encrypted token files remain decryptable.
    assert '"qianniu" + "-ai-bot|control-plane-token|" + _shop.ShopKey' in source
    assert 'Schema = "qnbot.shop-token"' in source
    assert 'JsonProperty("protected_token")' in source
    assert 'JsonProperty("fingerprint")' in source
    assert "Array.Clear" in source
    assert 'GetConfigPath(_shop, "control-plane-token.json")' in source
    assert 'JsonProperty("token")' not in source


def test_clearing_shop_token_removes_current_backup_and_temp_copies():
    source = read("src/Bot/ShopScope/ShopTokenStore.cs")
    assert "DeleteIfExists(_path);" in source
    assert 'DeleteIfExists(_path + ".bak")' in source
    assert 'Path.GetFileName(_path) + ".tmp-*"' in source
    assert "Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly)" in source
    assert "if (File.Exists(path)) File.Delete(path);" in source


def test_legacy_global_token_requires_explicit_ui_import():
    connection = read("src/Bot/ShopScope/ShopControlPlaneConnectionStore.cs")
    ui = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    assert "GetLegacyGlobalToken" in connection
    assert 'MakeButton("导入旧全局令牌"' in ui
    assert "ImportLegacy_Click" in ui
    assert "OpenTokenManagement(legacy)" in ui
    assert "class ShopTokenManagementWindow" in ui
    assert "_connection.SaveToken(candidate)" in ui


def test_control_plane_url_is_program_level_with_built_in_default_and_legacy_migration():
    connection = read("src/Bot/ShopScope/ShopControlPlaneConnectionStore.cs")
    config = read("src/Bot/App.config")
    assert 'DefaultUrlSettingKey = "BotControlPlaneDefaultUrl"' in connection
    assert 'ServerUrlEnvironmentKey = "QIANNIU_BOT_SERVER_URL"' in connection
    assert 'UrlOverrideKey = "ControlPlaneUrlOverride"' in connection
    assert "GetProgramServerUrl" in connection
    assert "SaveProgramServerUrl" in connection
    assert "TryNormalizeServerUrl" in connection
    assert "PersistentParams.GetParam2Key(UrlOverrideKey, LegacyScope" in connection
    assert "PersistentParams.GetParam2Key(UrlKey, LegacyScope" in connection
    assert connection.index("PersistentParams.GetParam2Key(UrlOverrideKey") < connection.index("Environment.GetEnvironmentVariable")
    assert connection.index("Environment.GetEnvironmentVariable") < connection.index("PersistentParams.GetParam2Key(UrlKey")
    assert "_settings.TryGetString(UrlKey" in connection  # one-time migration from PR #89
    assert "_settings.SetString(UrlKey" not in connection
    assert 'key="BotControlPlaneDefaultUrl"' in config
    assert 'value="https://qn.numbot.cn"' in config
    assert 'BuiltInDefaultServerUrl = "https://qn.numbot.cn"' in connection
    assert 'ObsoleteBuiltInHost = "botserver.mv3.cn"' in connection
    assert 'LegacyBuiltInHost = "aboter.mv3.cn"' in connection
    assert 'CurrentBuiltInHost = "qn.numbot.cn"' in connection


def test_shop_binding_page_owns_token_management_and_portable_shop_transfer():
    ui = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    sync = read("src/Bot/Knowledge/KnowledgeCloudSyncService.cs")
    assert 'Label("Bot 服务端（程序内置）")' in ui
    assert 'Label("本店 Bot 服务端地址")' not in ui
    assert "_connection.SetServerUrl" not in ui
    assert 'Label("本店 Bot 客户端令牌")' in ui
    assert "管理令牌" in ui
    assert "一个令牌只能绑定一个 ShopKey" in ui
    assert "class ShopTokenManagementWindow" in ui
    assert "显示令牌" in ui
    assert "保存令牌" in ui
    assert "ClearRequested" in ui
    assert "是否保留本地原配置信息（包括知识库" in ui
    assert "选择“是”：保留全部本地配置和知识库" in ui
    assert "ClientDataCloudBackupService.ResetLocalConfiguration(_shop)" in ui
    assert "ClientDataCloudBackupService.RestorePortableRollback(_shop, rollbackPath)" in ui
    assert "导出全店信息" in ui and "导入全店信息" in ui
    assert "ExportPortable(_shop, dialog.FileName)" in ui
    assert "ImportPortable(_shop, dialog.FileName)" in ui
    assert "启用本店知识库云同步" in ui
    assert "立即同步知识库" in ui
    assert "保存令牌并立即同步知识库" not in ui
    assert "_token" not in ui
    assert "KnowledgeCloudSyncService.SyncNowAsync(_shop)" in ui
    assert "KnowledgeCloudSyncService.SetEnabledForShop" in ui
    assert "IsEnabledForShop" in sync
    assert "SyncNowAsync" in sync
    assert "ShopSettingsScope.Enter(shop)" in sync


def test_portable_shop_export_excludes_tokens_and_imports_with_shopkey_rollback():
    source = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    assert 'PortableExportSchema = "qnbot.shop-data-export"' in source
    assert "ExportPortable(ShopContext shop, string destinationPath)" in source
    assert "ImportPortable(ShopContext shop, string sourcePath)" in source
    assert "ShouldExcludePortable(relative)" in source
    assert "IsSensitiveSettingName" in source
    assert "RemoveSensitiveJsonProperties" in source
    assert "control-plane-token.json" in source
    assert 'lower == "params.db"' in source
    assert "ProtectedData.Protect" in source
    assert "ProtectedData.Unprotect" in source
    assert "ReadPortableArchive(shop, archive)" in source
    assert "备份 ShopKey 与当前店铺不匹配" in source
    assert "ClearPortableFiles(shop)" in source
    assert "ReplaceValues(plan.Settings)" in source
    assert "RestoreProtectedPortableRollback" in source


def test_token_binding_conflict_is_checked_before_new_token_is_saved():
    ui = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    client = read("src/Bot/ShopScope/ShopTokenBindingService.cs")
    server = read("services/api-control-plane/bot_client_shop_binding.py")
    assert "ValidateTokenBinding(candidate);" in ui
    assert "ShopTokenBindingService" in ui
    assert "是否踢出旧店铺" in ui
    assert "ClaimAsync(_shop, candidate, true)" in ui
    assert "ClearDuplicateLocalTokenCopies" in ui
    assert 'CLAIM_PATH = "/api/runtime/v1/shop-binding/claim"' in server
    assert '"token_bound_to_other_shop"' in server
    assert "bound_shop_key" in server
    assert "force" in server
    assert "_reset_old_shop_server_state" in server
    assert "QueueConflictPrompt" in client


def test_shop_ai_settings_payload_is_dpapi_protected_and_shop_key_bound():
    source = read("src/Bot/ShopScope/ShopScopedSettingsStore.cs")
    assert 'Schema = "qnbot.shop-settings"' in source
    assert 'GetConfigPath(_shop, "settings.json")' in source
    assert "ProtectedData.Protect" in source
    assert "ProtectedData.Unprotect" in source
    assert "DataProtectionScope.CurrentUser" in source
    # New metadata/schema is qnbot, but cryptographic entropy is a compatibility identity.
    assert '"qianniu" + "-ai-bot|shop-settings|" + _shop.ShopKey' in source
    assert 'JsonProperty("protected_values")' in source
    assert 'JsonProperty("values")' not in source
    assert "[JsonIgnore]" in source
    assert "document.ShopKey, _shop.ShopKey" in source
    assert "Array.Clear" in source
    assert "CrossProcessAtomicStateFile.WriteAllTextAtomic" in source
    assert 'path + ".bak"' in source
    assert "File.Copy(temp, path, true)" not in source
    assert "ConcurrentDictionary<string, object>" in source


def test_persistent_params_routes_scoped_reads_and_writes_before_global_db():
    source = read("src/BotLib/Db/Sqlite/PersistentParams.cs")
    router = read("src/BotLib/Db/Sqlite/ScopedParamRouter.cs")
    assert "ScopedParamRouter.TryWrite(masterKey, subKey" in source
    assert "ScopedParamRouter.TryRead(masterKey, subKey" in source
    assert "if (ScopedParamRouter.TryWrite(masterKey, subKey, value)) return;" in source
    assert "public delegate bool TryReadHandler" in router
    assert "public delegate bool TryWriteHandler" in router


def test_only_explicit_supported_legacy_scopes_are_shop_routed():
    source = read("src/Bot/ShopScope/ShopScopedParamBridge.cs")
    for scope in ("ai", "feature", "shop-cloud", "shop-runtime"):
        assert f'"{scope}"' in source
    assert '"ai-control-plane"' not in source
    assert "ShopSettingsScope.Current" in source
    assert "ShopScopedSettingsStore" in source


def test_settings_window_scopes_shop_load_and_save_without_process_global_shop_state():
    source = read("src/Bot/Options/WndOption.xaml.cs")
    scope = read("src/Bot/ShopScope/ShopSettingsScope.cs")
    assert "shopBinding = new ShopBindingOptionsControl(Seller);" in source
    assert "aiSettings = new CtlRobotOptions" not in source
    assert "RunInShopScope(delegate" in source
    assert "foreach (var options in _visitedOptions.ToList())" in source
    assert "using (ShopSettingsScope.Enter(_shop))" in source
    assert "AsyncLocal<ShopContext>" in scope
    assert "static ShopContext CurrentShop" not in scope


def test_buyer_reply_runtime_enters_shop_scope_at_the_single_handler_call():
    source = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    locator = read("src/Bot/ShopScope/ShopContextLocator.cs")
    assert "DispatchScopedAsync(burst, lease)" in source
    assert "ShopContextLocator.ResolveRuntimeByShopKey(burst.ShopKey)" in source
    assert "using (ShopSettingsScope.Enter(shop))" in source
    assert "shop_scope_unresolved" in source
    assert "LegacyAiConfigurationGate.WaitAsync" not in source
    assert "LegacyAiConfigurationGate.Release" not in source
    assert "await _handler(lease)" in source
    assert "ResolveRuntimeBySellerNick" in locator


def test_vision_model_selection_uses_exact_shopkey_and_never_global_fallback():
    source = read("src/Bot/ChromeNs/VisionMessageDecision.cs")
    assert "ResolveShopVisionEndpoints(shopKey)" in source
    assert "CanUseServerOcr(shopKey)" in source
    assert "ShopContextLocator.ResolveRuntimeByShopKey(shopKey)" in source
    assert "using (ShopSettingsScope.Enter(shop))" in source
    assert "AiEndpointStore.GetVisionEnabledEndpoints" in source
    assert "new AiEndpointConfig[0]" in source
    assert "fail-closed且不回退全局配置" in source
    assert "ResolveRuntimeBySellerNick" not in source
    assert "已跳过：无法确认图片所属店铺身份" in source


def test_recovered_buyer_messages_reenter_authoritative_shop_scope():
    source = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    method = source[
        source.index("private Task ProcessRecoveredBuyerMessageAfterMissAsync"):
        source.index("\n        }\n\n", source.index("private Task ProcessRecoveredBuyerMessageAfterMissAsync")) + 11
    ]
    assert "ResolveRuntimeBySeller(this).ShopKey" in method
    assert "VisionMessageDecision.Decide(" in method
    assert "decision,\n                shopKey" in method
    assert "ResponseProgressTracker.ObserveQuestion(sellerNick, buyerNick, displayQuestion, detectedAt, shopKey)" in method
    assert "ShopKey = shopKey" in method
    assert "if (string.IsNullOrWhiteSpace(shopKey))" in method


def test_runtime_scope_does_not_add_a_second_reflection_handler_wrapper():
    props = read("src/Bot/Directory.Build.props")
    coordinator = read("src/Bot/ChromeNs/BuyerMessageBurstCoordinator.cs")
    assert "ShopScopedBuyerRuntimeService.cs" not in props
    assert "BindingFlags" not in coordinator
    assert "ShopScopedBuyerRuntimeService" not in coordinator


def test_unstable_nickname_binding_requires_explicit_confirmation_and_keeps_window():
    ui = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    window = read("src/Bot/Options/WndOption.xaml.cs")
    assert "!_shop.HasStableSellerId" in ui
    assert "_allowNicknameFallback.IsChecked != true" in ui
    assert "当前千牛身份没有 TargetId" in ui
    assert "临时按规范化昵称绑定" in ui
    assert "窗口已保留，请修正后重试" in window


def test_ui_claims_shop_data_isolation_while_service_endpoint_is_shared():
    ui = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    assert "Bot 服务端地址由程序统一配置" in ui
    assert "本店 Token、知识库、规则、消息状态和云数据继续按 ShopKey 隔离" in ui


def test_build_includes_shop_scope_and_botlib_router_sources():
    bot_props = read("src/Bot/Directory.Build.props")
    botlib_props = read("src/BotLib/Directory.Build.props")
    for filename in (
        "ShopContextLocator.cs",
        "ShopControlPlaneConnectionStore.cs",
        "ShopLegacyDataMigrationService.cs",
        "ShopScopedParamBridge.cs",
        "ShopScopedRuntimeBridge.cs",
        "ShopScopedSettingsStore.cs",
        "ShopScopedUiBridge.cs",
        "ShopSettingsScope.cs",
        "ShopTokenStore.cs",
        "ShopTokenBindingService.cs",
        "ShopBindingOptionsControl.cs",
    ):
        assert filename in bot_props
    for filename in ("ScopedParamRouter.cs", "ScopedDataPathRouter.cs", "ScopedLogRouter.cs"):
        assert filename in botlib_props


def test_web_cloud_and_backup_use_per_shop_connection_store_without_global_token():
    web = read("src/Bot/ChromeNs/BotWebConsoleSyncService.cs")
    knowledge = read("src/Bot/Knowledge/KnowledgeCloudSyncService.cs")
    backup = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    for source in (web, knowledge, backup):
        assert "ShopControlPlaneConnectionStore" in source
        assert "ControlPlaneClientToken" not in source
        assert '"ai-control-plane"' not in source
        assert "X-Shop-Key" in source


def test_rejected_shop_token_cannot_replace_previous_saved_credentials():
    source = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    # Save must validate before touching DPAPI storage; an explicit HTTP response is
    # authoritative even if network-unreachable settings may still be staged offline.
    assert source.index("ValidateTokenBinding(candidate);") < source.index("_connection.SaveToken(candidate);")
    start = source.index("private bool ValidateTokenBinding(string candidate)")
    end = source.index("public void RestoreDefault()", start)
    validation = source[start:end]
    assert "if (result.Success) return true;" in validation
    assert "return ShopTokenBindingService.ClearDuplicateLocalTokenCopies(_shop, candidate);" in validation
    assert "return true; // Preserve the existing offline-staging behavior" in validation
    assert "if (result.Conflict)" in validation
    assert 'throw new InvalidOperationException(\n                    "服务端拒绝本店令牌，未保存新令牌：" + result.Error)' in validation
    assert "catch (InvalidOperationException)" in validation
    assert '令牌会先保存在本机，联网后自动再次校验' in validation
    assert '暂时无法验证令牌绑定：' not in validation


def test_runtime_connection_status_surfaces_explicit_unauthorized():
    source = read("src/Bot/ShopScope/ShopTokenBindingService.cs")
    segment = source[source.index("public static string GetStatusText"):source.index("public static string GetStatusToolTip")]
    assert "state.LastFailureUtc > state.LastSuccessUtc" in segment
    assert 'state.LastError.IndexOf("HTTP 401", StringComparison.OrdinalIgnoreCase)' in segment
    assert 'return "令牌已失效";' in segment
    assert segment.index('return "令牌已失效";') < segment.index('return "已连接";')



def test_tray_help_menu_exposes_validated_persistent_server_configuration():
    menu = read("src/Bot/AssistWindow/NotifyIcon/MenuCreator/HelpMenuCreator.cs")
    store = read("src/Bot/ShopScope/ShopControlPlaneConnectionStore.cs")
    assert 'CreateItem("服务端配置", OnServerConfigClicked)' in menu
    assert "GetLegacyGlobalServerUrl()" in menu
    assert "TryNormalizeServerUrl" in menu
    assert "SaveProgramServerUrl" in menu
    assert '"/healthz"' in menu
    assert "ControlPlaneUrlOverride" in store
    assert "后续请求将使用新地址" in menu



def test_shop_binding_reuses_the_exact_current_scope_when_available():
    ui = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    init = ui[ui.index("public void InitUI(string seller)"):ui.index("public void Save(string seller)")]
    assert "ShopSettingsScope.Current" in init
    assert "scoped.ShopKey" in init
    assert "_shop = scoped;" in init
    assert init.index("_shop = scoped;") < init.index("ShopContextLocator.ResolveBySellerNick(_seller)")
    assert "authoritative" in init


def test_shopkey_is_used_for_settings_window_identity_without_overwriting_seller_nick():
    window = read("src/Bot/Options/WndOption.xaml.cs")
    show = window[window.index("internal static void MyShow(ShopContext shop"):window.index("public static void MyShow(string seller")]
    assert "Application.Current.Windows.OfType<WndOption>()" in show
    assert "x._shop.ShopKey" in show
    assert "FirstShow(wndOp.Seller" in show
    assert "ShowSameNickOneInstance<WndOption>" not in show
    ctor = window[window.index("private WndOption(ShopContext shop)"):window.index("private WndOption(string seller)")]
    assert "FindConnectedQnByShopKey(shop.ShopKey)" in ctor
    assert "liveSellerNick" in ctor


def test_shop_scoped_catalog_and_reply_preferences_do_not_re_resolve_shopkey_as_nickname():
    catalog = read("src/Bot/ChromeNs/ShopProductCatalogService.cs")
    resolver = catalog[catalog.index("private static ShopContext ResolveShopForSeller"):catalog.index("private static string CachePath")]
    assert "if (current != null && !string.IsNullOrWhiteSpace(current.ShopKey))" in resolver
    assert "return current;" in resolver
    reply = read("src/Bot/ChromeNs/ReplyModeService.cs")
    assert "ResolveCurrentOrBySellerNick(seller)" in reply
    assert "ShopSettingsScope.Current == null" in reply

def test_portable_import_preserves_local_sensitive_settings_and_nested_api_keys():
    source = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    assert "PreserveLocalSensitiveSettings(shop, plan)" in source
    assert "MergeSensitiveJsonProperties(imported, pair.Value)" in source
    assert "FindSensitiveMergeMatch(incomingItem, currentArray)" in source
    assert "!IsBotClientTokenSettingName(currentProperty.Name)" in source
    assert "IsBotClientTokenSettingName(pair.Key)" in source
    assert "当前设备已有 API Key/密码等敏感凭据" in read("src/Bot/Options/ShopBindingOptionsControl.cs")


def test_local_token_reset_uses_a_secret_preserving_dpapi_rollback():
    source = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    assert "localRollback: true" in source
    assert "ExportLocalRollbackSettings(shop)" in source
    assert "ClearLocalConfigurationFiles(shop)" in source
    assert "RestoreZip(shop, plain)" in source
    assert "ShouldExcludeLocalRollback(relative)" in source
    assert "DataProtectionScope.CurrentUser" in source
    assert "ClearPortableFiles(shop);" in source  # portable imports still preserve local credentials


def test_portable_import_honors_the_documented_128mb_single_file_limit():
    source = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    reader = source[source.index("private static byte[] ReadPortableEntry"):source.index("private static byte[] SanitizePortableJsonFile")]
    assert "total > MaxPortableSingleFileBytes" in reader
    assert "total > MaxSingleFileBytes" not in reader
    assert "超过128MB的文件" in reader


def test_portable_export_excludes_sensitive_directory_components_and_credential_formats():
    source = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    predicate = source[source.index("private static bool IsSensitiveSettingName"):source.index("private static bool ShouldExcludePortable")]
    filter_source = source[source.index("private static bool ShouldExcludePortable"):source.index("private static bool IsSafePortableRelativePath")]
    assert 'compact.Contains("cookie")' in predicate
    assert 'compact.Contains("bearer")' in predicate
    assert 'compact.Contains("jwt")' in predicate
    assert "parts.Any(IsSensitiveSettingName)" in filter_source
    assert 'string.Equals(extension, ".pem"' in filter_source
    assert 'string.Equals(extension, ".pfx"' in filter_source
