from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_v2_cloud_sync_dispatches_by_exact_shopkey_not_seller_nickname():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    queue = service[
        service.index("private static void Queue(ShopContext shop)"):
        service.index("internal static void QueueSync")
    ]

    assert "shop.ShopKey" in queue
    assert "States.GetOrAdd(shop.ShopKey" in queue
    assert "Interlocked.Exchange(ref state.Syncing,1)" in queue
    assert "sellerNick" not in queue
    assert "ResolveBySellerNick" not in queue


def test_v2_cloud_sync_keeps_network_and_repository_operations_in_one_shop_scope():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    sync = service[
        service.index("private static async Task SyncOnce"):
        service.index("private static State LoadState")
    ]

    scope_start = sync.index("using(ShopSettingsScope.Enter(shop))")
    scope_end = sync.rindex("\n            }")
    scoped = sync[scope_start:scope_end]

    assert "new ShopControlPlaneConnectionStore(shop,Paths)" in scoped
    assert "KnowledgeEngineV2Repository.LoadAll(shop.DisplayName)" in scoped
    assert "X-Shop-Key" in scoped
    assert "ShopRuntimeCredentialAuthority.SendWithUnauthorizedRecoveryAsync" in scoped
    assert "KnowledgeEngineV2Repository.TryReplaceAllIfUnchanged" in scoped
    assert "KnowledgeEngineV2Service.ReplaceSnapshot" not in scoped


def test_v2_cloud_sync_serializes_jobs_per_shopkey_and_does_not_drop_manual_sync():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    queue = service[
        service.index("private static void Queue(ShopContext shop)"):
        service.index("internal static void QueueSync")
    ]
    manual = service[
        service.index("internal static async Task<bool> SyncNowAsync"):
        service.index("private static async Task SyncOnce")
    ]

    assert "Interlocked.Exchange(ref state.Syncing,1)" in queue
    assert "finally" in queue
    assert "Interlocked.Exchange(ref state.Syncing,0)" in queue
    assert "Interlocked.CompareExchange(ref state.Syncing, 1, 0)" in manual
    assert "本店 Knowledge V2 正在同步" in manual
    assert "await SyncOnce(shop, state)" in manual


def test_v2_corrupt_list_json_fails_closed_instead_of_becoming_empty_metadata():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    parser = repository[
        repository.index("private static List<string> ParseList"):
        repository.index("private static ShopContext ResolveShopRequired")
    ]
    assert "string.IsNullOrWhiteSpace(json)" in parser
    assert "JsonConvert.DeserializeObject<List<string>>(json)" in parser
    assert "throw new InvalidDataException" in parser
    assert "静默丢失知识元数据" in parser
    assert "catch { return new List<string>(); }" not in parser
    assert "MirrorOneToLegacy" not in repository


def test_manual_sync_never_reports_unconverged_or_missing_credentials_as_success():
    service = read("src/Bot/Knowledge/KnowledgeV2CloudSyncService.cs")
    ui = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    manual = service.split("internal static async Task<bool> SyncNowAsync", 1)[1].split("private static async Task SyncOnce", 1)[0]
    assert 'state.LastStatus == "credentials_missing"' in manual
    assert 'return state.LastStatus == "synced" && state.LastConverged;' in manual
    assert "state.LastConverged = true" not in manual
    assert "var converged = await KnowledgeV2CloudSyncService.SyncNowAsync(_shop)" in ui
    assert "_status.Text = converged" in ui
    assert "本次同步尚未收敛" in ui

def test_runtime_settings_save_is_atomic_and_cache_load_cannot_overwrite_new_value():
    public = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    index = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs")
    save = public.split("public static void SetSettings(", 1)[1].split("public static KnowledgeV2Decision Resolve(", 1)[0]
    assert "lock (SettingsLocks.GetOrAdd(shop.ShopKey" in save
    assert "store.SetString(" not in save
    assert save.index("store.MergeValues(values, true)") < save.index("var persisted = store.ExportValues()") < save.index("SettingsCache[shop.ShopKey] =")
    load = index.split("private static RuntimeSettings GetSettings(", 1)[1].split("private static ShopContext ResolveShop(", 1)[0]
    assert load.index("lock (SettingsLocks.GetOrAdd(shop.ShopKey") < load.index("ExportValues()") < load.index("SettingsCache[shop.ShopKey] = cached")
    resolve = index.split("private static ShopContext ResolveShop(", 1)[1].split("private static ShopContext ResolveShopRequired", 1)[0]
    assert "DirectOrderIdentityResolver.IdentityEquals(shop.SellerNick, seller)" in resolve
    assert "shop.HasStableSellerId" in resolve
    assert "if (current != null) return current" not in resolve
