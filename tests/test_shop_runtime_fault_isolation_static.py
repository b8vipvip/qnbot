from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_one_bad_qn_subscription_does_not_starve_other_shop_sessions():
    source = read("src/Bot/ShopScope/ShopScopedRuntimeBridge.cs")
    refresh = source.split("private static void RefreshForegroundSubscriptions()", 1)[1].split(
        "private static void Qn_EvBuyerSwitched", 1
    )[0]
    assert "sessions = QN.GetRuntimeSafetySnapshot();" in refresh
    assert "foreach (var qn in sessions ?? new List<QN>())" in refresh
    assert "跳过异常千牛会话的活动店铺事件订阅并继续" in refresh
    assert refresh.index("catch (Exception ex)") < refresh.index("RefreshProfileCacheIfNeeded();")
    assert "EnsureShopLogWriters();" in refresh


def test_single_shop_log_writer_failure_is_retried_without_blocking_other_shops():
    source = read("src/Bot/ShopScope/ShopScopedRuntimeBridge.cs")
    writers = source.split("private static void EnsureShopLogWriters()", 1)[1].split(
        "private static string ResolveShopFromSafeLog", 1
    )[0]
    assert "foreach (var shop in ShopByKey.Values" in writers
    assert "catch (Exception ex)" in writers
    assert "单店运行日志初始化失败，继续处理其他店铺" in writers
    assert "LogReadyMarkers.TryRemove(shop.ShopKey, out ignored);" in writers


def test_successful_server_rebind_is_not_reported_as_failed_when_local_cleanup_is_incomplete():
    service = read("src/Bot/ShopScope/ShopTokenBindingService.cs")
    cleanup = service.split("internal static bool ClearDuplicateLocalTokenCopies", 1)[1].split(
        "private static string ExtractBoundShopKey", 1
    )[0]
    assert "profiles = Profiles.GetAll();" in cleanup
    assert "catch (Exception ex)" in cleanup
    assert "无法确认旧令牌清理" in cleanup
    assert "cleanupComplete = false;" in cleanup
    assert "return cleanupComplete;" in cleanup
    assert "var localCleanupComplete = ClearDuplicateLocalTokenCopies(shop, token);" in service

    ui = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    assert "private bool ValidateTokenBinding(string candidate)" in ui
    assert "return ShopTokenBindingService.ClearDuplicateLocalTokenCopies(_shop, candidate);" in ui
    assert "localDuplicateCleanupComplete = ValidateTokenBinding(candidate)" in ui
    assert "令牌已绑定，需检查本机清理" in ui


def test_shop_identity_cache_keeps_last_known_aliases_for_offline_but_registered_shops():
    bridge = read("src/Bot/ShopScope/ShopScopedRuntimeBridge.cs")
    refresh = bridge.split("private static void RefreshProfileCacheIfNeeded()", 1)[1].split(
        "private static string StableSellerRef", 1
    )[0]
    assert "AddSellerRefAlias(StableSellerRef(profile.SellerNick), shop);" in refresh
    assert "AddSellerRefAlias(StableSellerRef(profile.DisplayName), shop);" in refresh
    preserve = refresh.split("// Keep last-known seller aliases", 1)[1].split(
        "// Apply the completed snapshot", 1
    )[0]
    assert "foreach (var pair in ShopBySellerRef)" in preserve
    assert "nextByKey.ContainsKey(pair.Value.ShopKey)" in preserve
    assert "!ambiguousSellerRefs.Contains(pair.Key)" in preserve
    assert "runtimeSnapshotSucceeded" not in refresh
