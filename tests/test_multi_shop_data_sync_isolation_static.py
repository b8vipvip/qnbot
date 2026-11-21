from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_legacy_datadir_is_ambiently_redirected_but_global_source_is_explicit():
    path_ex = read("src/BotLib/Extensions/PathEx.cs")
    router = read("src/BotLib/Extensions/ScopedDataPathRouter.cs")
    bridge = read("src/Bot/ShopScope/ShopScopedRuntimeBridge.cs")
    assert "public static string GlobalDataDir" in path_ex
    assert "ScopedDataPathRouter.TryResolve" in path_ex
    assert "PathEx.GlobalDataDir" in read("src/Bot/ShopScope/ShopScopedPathProvider.cs")
    assert "GetCompatibilityDataRoot" in bridge
    assert "ShopSettingsScope.Current" in bridge
    assert "TryResolveHandler" in router


def test_feature_cloud_and_runtime_settings_are_shop_encrypted():
    bridge = read("src/Bot/ShopScope/ShopScopedParamBridge.cs")
    settings = read("src/Bot/ShopScope/ShopScopedSettingsStore.cs")
    for scope in ("ai", "feature", "shop-cloud", "shop-runtime"):
        assert f'"{scope}"' in bridge
    assert "ProtectedData.Protect" in settings
    assert "ProtectedData.Unprotect" in settings
    # Repository rename must not change DPAPI entropy or existing encrypted settings become unreadable.
    assert '"qianniu" + "-ai-bot|shop-settings|" + _shop.ShopKey' in settings
    assert 'Schema = "qnbot.shop-settings"' in settings
    assert "ExportValues" in settings
    assert "ReplaceValues" in settings


def test_bot_and_auto_reply_switches_are_shop_runtime_state():
    source = read("src/Bot/StartUp/Params.cs")
    assert 'RuntimeScope = "shop-runtime"' in source
    assert "ShopSettingsScope.Current" in source
    assert 'TrySaveParam2Key("BotEnabled", RuntimeScope' in source
    assert 'TrySaveParam2Key("IsAutoReply", RuntimeScope' in source


def test_business_and_handoff_policies_use_shop_rule_paths_and_path_keyed_caches():
    business = read("src/Bot/ChromeNs/BusinessPolicyProfileService.cs")
    handoff = read("src/Bot/ChromeNs/HandoffRuleRemoteConfigService.cs")
    for source in (business, handoff):
        assert "Paths.GetRulesRoot(shop)" in source
        assert "ConcurrentDictionary<string" in source
        assert "ShopSettingsScope.Current" in source
        assert "CanAutoAdoptLegacy" in source
        assert "Profiles.GetAll().Count == 1" in source
    assert '"business-policy.json"' in business
    assert '"handoff-policy.json"' in handoff


def test_knowledge_cloud_sync_has_per_shop_state_token_revision_and_backup():
    source = read("src/Bot/Knowledge/KnowledgeCloudSyncService.cs")
    assert "ConcurrentDictionary<string, ShopSyncState>" in source
    assert "ShopControlPlaneConnectionStore" in source
    assert 'request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey)' in source
    assert 'RevisionKey, Scope' in source
    assert 'LastHashKey, Scope' in source
    assert "Paths.GetBackupRoot(shop)" in source
    assert "ControlPlaneClientToken" not in source
    assert '"ai-control-plane"' not in source


def test_web_console_state_commands_and_qn_lookup_are_shop_bound():
    source = read("src/Bot/ChromeNs/BotWebConsoleSyncService.cs")
    assert "ConcurrentDictionary<string, ShopWebState>" in source
    assert "ShopControlPlaneConnectionStore" in source
    assert 'request.Headers.TryAddWithoutValidation("X-Shop-Key", state.Shop.ShopKey)' in source
    assert "FindQns(state.Shop)" in source
    assert "ShopIdentityResolver.Resolve(qn.Seller)" in source
    assert "current.ShopKey" in source
    assert "QN.CurQN" not in source
    assert "ControlPlaneClientToken" not in source


def test_cloud_backup_is_shop_portable_and_rejects_cross_shop_restore():
    source = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    assert 'Magic = "QABK2"' in source
    assert '"qnbot.shop-data-backup"' in source
    assert '["shopKey"] = shop.ShopKey' in source
    assert "ShopScopedSettingsStore(shop, Paths).ExportValues" in source
    assert "ReplaceValues(current)" in source
    assert "云备份 ShopKey 与当前店铺不匹配" in source
    assert "ShouldExclude" in source
    assert "settings.json" in source
    assert "control-plane-token.json" in source
    assert "PathEx.DataDir" not in source
    assert 'lower == "params.db"' in source
    assert "portableExport ? ShouldExcludePortable(relative) : localRollback ? ShouldExcludeLocalRollback(relative) : ShouldExclude(relative)" in source
    assert "ExportPortable" in source
    assert "ImportPortable" in source


def test_wecom_notifications_and_reply_claims_are_shop_bound():
    source = read("src/Bot/ChromeNs/WeComAppBridgeClient.cs")
    notification = read("src/Bot/ChromeNs/HandoffNotificationService.cs")
    assert "SnapshotOnlineShops" in source
    assert "ShopControlPlaneConnectionStore" in source
    assert 'request.Headers.TryAddWithoutValidation("X-Shop-Key", shop.ShopKey)' in source
    assert "FindQn(shop, seller)" in source
    assert "QN.CurQN" not in source
    assert "shopKey + \"#\"" in notification
    assert "ShopSettingsScope.Enter(shop)" in notification


def test_conversation_alias_learning_dedup_progress_and_watchdogs_include_shop_key():
    files = {
        "conversation": read("src/Bot/ChromeNs/ConversationContextStore.cs"),
        "alias": read("src/Bot/ChromeNs/BuyerIdentityAliasService.cs"),
        "learning": read("src/Bot/ChromeNs/KnowledgeLearningService.cs"),
        "dedup": read("src/Bot/ChromeNs/ReplyDeduplicationService.cs"),
        "progress": read("src/Bot/ChromeNs/ResponseProgressTracker.cs"),
        "watchdog": read("src/Bot/ChromeNs/SendDeliveryWatchdog.cs"),
    }
    for source in files.values():
        assert "ShopSettingsScope.Current" in source
        assert "ShopKey" in source
    assert "QN.CurQN" not in files["conversation"]
    assert "FindQn(pending.Shop, pending.Seller)" in files["watchdog"]
    assert "SaveLocks.GetOrAdd(ScopeKey()" in files["learning"]


def test_scoped_logs_mirror_to_shop_log_root():
    router = read("src/BotLib/ScopedLogRouter.cs")
    log = read("src/BotLib/Log.cs")
    bridge = read("src/Bot/ShopScope/ShopScopedRuntimeBridge.cs")
    assert "ScopedLogRouter.TryWrite" in log
    assert "WriteHandler" in router
    assert "Paths.GetLogRoot(shop)" in bridge
    assert '"runtime.txt"' in bridge


def test_legacy_migration_is_single_shop_auto_or_explicit_multi_shop():
    migration = read("src/Bot/ShopScope/ShopLegacyDataMigrationService.cs")
    server = read("src/Bot/ChromeNs/HandoffPolicyLegacyMigrationService.cs")
    props = read("src/Bot/Directory.Build.props")
    assert "profiles.Count != 1" in migration
    assert "ShopBindingOptionsControl" in migration
    assert "将旧全局数据迁移到本店" in migration
    assert "IsSecretOrTransient" in migration
    assert "ControlPlane" in migration
    assert "legacy-data-migration.json" in migration
    assert "ShopControlPlaneConnectionStore" in server
    assert "X-Shop-Key" in server
    assert "ShopLegacyDataMigrationService.cs" in props


def test_ui_events_enter_owner_shop_scope_without_process_global_current_shop():
    source = read("src/Bot/ShopScope/ShopScopedUiBridge.cs")
    assert "ConditionalWeakTable<Window, ContextHolder>" in source
    assert "ShopSettingsScope.Enter(shop)" in source
    assert "GetFromOwner" in source
    assert "ResolveFromWindow" in source
    assert "static ShopContext CurrentShop" not in source


def test_botlib_build_includes_new_scope_routers():
    props = read("src/BotLib/Directory.Build.props")
    assert "ScopedDataPathRouter.cs" in props
    assert "ScopedLogRouter.cs" in props

def test_raw_qn_message_pipeline_enters_exact_source_shop_scope():
    qn = read("src/Bot/ChromeNs/QN.cs")
    recovery = read("src/Bot/ChromeNs/QN.MessageRecovery.cs")
    assert "ShopContextLocator.ResolveRuntimeBySeller(this)" in qn
    assert "using (ShopSettingsScope.Enter(shop))" in qn
    assert "ShopSettingsScope.Enter(ShopContextLocator.ResolveRuntimeBySeller(this))" in recovery



def test_reply_dedup_and_buyer_alias_never_share_unresolved_nickname_scope_across_shops():
    alias = read("src/Bot/ChromeNs/BuyerIdentityAliasService.cs")
    dedup = read("src/Bot/ChromeNs/ReplyDeduplicationService.cs")

    alias_scope = alias[
        alias.index("private static string ScopeKey"):
        alias.index("private static bool Same", alias.index("private static string ScopeKey"))
    ]
    dedup_scope = dedup[
        dedup.index("private static string ScopeKey"):
        dedup.index("private static void Cleanup", dedup.index("private static string ScopeKey"))
    ]
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in alias_scope
    assert "catch (AmbiguousShopIdentityException) { return string.Empty; }" in alias_scope
    assert "legacy-" in alias_scope  # legacy only when no matching registered identity exists
    assert "ShopContextLocator.ResolveCurrentOrBySellerNick(seller)" in dedup_scope
    assert "catch { return string.Empty; }" in dedup_scope
    assert "var key = Key(seller, buyer);" in dedup
    assert "if (key.Length == 0) return;" in dedup
    assert "if (key.Length == 0) return false;" in dedup
    assert 'return shopKey + "|" +' in dedup


def test_buyer_identity_alias_cache_is_bounded_under_sustained_traffic():
    alias = read("src/Bot/ChromeNs/BuyerIdentityAliasService.cs")
    observe = alias[
        alias.index("public static void Observe(string seller"):
        alias.index("public static string ResolveConversationKey")
    ]
    cleanup = alias[
        alias.index("private static void Cleanup()"):
        alias.index("private static void RemoveExact")
    ]

    # Expiration alone is not a capacity limit when every alias remains active.
    assert "AliasCleanupThreshold = 5000" in alias
    assert "AliasCleanupTarget = 4000" in alias
    assert "Interlocked.CompareExchange(ref _cleanupRunning, 1, 0)" in cleanup
    assert "Aliases.ToArray()" in cleanup
    assert "candidates.Sort((left, right) => left.UpdatedAt.CompareTo(right.UpdatedAt))" in cleanup
    assert "if (Aliases.Count <= AliasCleanupTarget) break;" in cleanup
    assert "record.UpdatedAt != candidate.UpdatedAt" in cleanup
    assert "((ICollection<KeyValuePair<string, AliasRecord>>)Aliases).Remove(pair)" in alias
    assert "lock (record)" in observe
    assert "Cleanup();" in observe
    assert "Interlocked.Exchange(ref _cleanupRunning, 0);" in cleanup
