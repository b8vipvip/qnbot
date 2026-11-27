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
        service.index("internal static async Task SyncNowAsync"):
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
