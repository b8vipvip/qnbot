"""Idle memory/IO regression tests from the 2026-11-06 78-minute v1.1.1820 log.

The log showed ~25,000 Gen2 collections during an otherwise idle interval,
65 stable WebSocket channels, zero buyer context cache entries and a small
managed heap. These targeted fixes remove proven unnecessary, *repeated*
registry file I/O and WPF UI list allocations, not a claimed GC root cause.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def src(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_profile_store_does_not_rewrite_unchanged_profiles_every_tick():
    store = src("src/Bot/ShopScope/ShopProfileStore.cs")
    assert "LastSeenWriteInterval = TimeSpan.FromMinutes(1)" in store
    assert "var changed = false;" in store
    assert "registry.Shops.Add(existing);" in store
    assert "changed = true;" in store
    assert "existing.DisplayName = shop.DisplayName;" in store
    assert "existing.HasStableSellerId = existing.HasStableSellerId || shop.HasStableSellerId;" in store
    assert "now - existing.LastSeenAtUtc >= LastSeenWriteInterval" in store
    assert "if (changed) existing.LastSeenAtUtc = now;" in store
    assert "if (changed)\n                {\n                    registry.Shops =" in store
    assert "WriteProfile(existing);" in store
    assert "SaveRegistry(registry);" in store
    assert "Interlocked.Increment(ref _registryWriteCount);" in store
    assert "ShopKey collision detected" in store
    assert "The same seller identity is already registered with another ShopKey." in store


def test_one_second_web_sync_uses_weak_per_qn_shop_snapshot_cache():
    sync = src("src/Bot/ChromeNs/BotWebConsoleSyncService.cs")
    assert "_syncTimer = new Timer(_ => QueueDueSyncs(), null, 1500, 1000);" in sync
    assert "ConditionalWeakTable<QN, CachedShopSnapshot>" in sync
    assert "ShopSnapshotRefreshInterval = TimeSpan.FromMinutes(1)" in sync
    assert "var shop = ResolveShopSnapshot(qn);" in sync
    assert "Profiles.GetOrCreate(ShopIdentityResolver.Resolve(seller)).ToContext()" in sync
    assert "string.Equals(cache.Nick, nick, StringComparison.Ordinal)" in sync
    assert "string.Equals(cache.TargetId, targetId, StringComparison.Ordinal)" in sync
    assert "string.Equals(cache.Display, display, StringComparison.Ordinal)" in sync
    assert "now < cache.NextRefreshUtc" in sync
    assert "lock (cache.Sync)" in sync
    assert "cache.NextRefreshUtc = now.Add(ShopSnapshotRefreshInterval);" in sync
    assert "Interlocked.Increment(ref _shopSnapshotCacheHits);" in sync
    assert "Interlocked.Increment(ref _shopSnapshotRefreshes);" in sync


def test_alias_list_dedupe_is_skipped_when_two_keys_already_share_the_same_list():
    bridge = src("src/Bot/ChromeNs/BuyerIdentityAliasUiBridge.cs")
    before = bridge.index("ReferenceEquals(internalList, displayList)")
    early = bridge.index("return;", before)
    merge = bridge.index("var merged = new List<CtlConversation>();", early)
    assert before < early < merge
    assert "string.Equals(existingSignature, _lastSignature, StringComparison.Ordinal)" in bridge
    assert "Interlocked.Increment(ref _unchangedAliasMergeSkips);" in bridge
    assert "HashSet<CtlConversation>" in bridge
    assert "unique.Add(item)" in bridge
    assert "merged.Any(" not in bridge
    assert "map[internalKey] = merged;" in bridge
    assert "map[displayKey] = merged;" in bridge
    assert "ctl.ReShowAfterQNChange();" in bridge
    assert "Dispatcher.BeginInvoke" in bridge
    assert "if (!ctl.Dispatcher.CheckAccess())" in bridge


def test_minute_telemetry_can_verify_cache_and_registry_optimizations():
    monitor = src("src/Bot/ChromeNs/QnRuntimeSafetyMonitor.cs")
    assert "shopRegistryWrites=" in monitor
    assert "shopSnapshotHits=" in monitor
    assert "shopSnapshotRefreshes=" in monitor
    assert "aliasUiNoopSkips=" in monitor
    assert "GC.CollectionCount(2)" in monitor
    assert "GC.GetTotalMemory(false)" in monitor
    assert "GC.Collect(" not in monitor
