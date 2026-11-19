from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_v2_legacy_learning_sync_dispatches_by_exact_shopkey_not_seller_nickname():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    dispatch = bridge[
        bridge.index("private static void OnLegacyKnowledgeChanged"):
        bridge.index("private static void QueueSynchronizeShop")
    ]

    assert "QN.CurQN" in dispatch
    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in dispatch
    assert "scheduled.Add(shop.ShopKey)" in dispatch
    assert "QueueSynchronizeShop(shop, qn.Seller.Nick.Trim())" in dispatch
    assert "Distinct(StringComparer.Ordinal)" not in dispatch
    assert "ResolveBySellerNick" not in dispatch


def test_v2_legacy_learning_import_keeps_all_repository_work_in_one_shop_scope():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    importer = bridge[
        bridge.index("private static void ImportLearningChanges(ShopContext shop"):
        bridge.index("private static bool Same")
    ]

    scope_start = importer.index("using (ShopSettingsScope.Enter(shop))")
    scope_end = importer.index("\n                }\n\n                if (changed > 0)", scope_start)
    scoped = importer[scope_start:scope_end]

    assert scoped.index("BotFeatureStore.GetKnowledgeBase()") < scoped.index("KnowledgeEngineV2Repository.LoadAll(seller)")
    assert scoped.index("KnowledgeEngineV2Repository.LoadAll(seller)") < scoped.index("KnowledgeEngineV2Repository.Save(seller, record)")
    assert scoped.index("KnowledgeEngineV2Repository.Save(seller, record)") < scoped.index("KnowledgeEngineV2Service.Warm(seller)")
    assert "KnowledgePolicyProfileService.GetProfile(entry)" in scoped


def test_v2_legacy_learning_sync_coalesces_updates_without_dropping_midflight_changes():
    bridge = read("src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs")
    queue = bridge[
        bridge.index("private static void QueueSynchronizeShop"):
        bridge.index("internal static void SynchronizeSeller")
    ]

    assert "SynchronizingShops.TryAdd(shop.ShopKey, 0)" in queue
    assert "PendingShopSynchronizations[shop.ShopKey] = 0;" in queue
    assert "while (PendingShopSynchronizations.TryRemove(shop.ShopKey, out ignored));" in queue
    assert "if (PendingShopSynchronizations.TryRemove(shop.ShopKey, out ignored))" in queue
    assert "QueueSynchronizeShop(shop, seller);" in queue



def test_knowledge_v2_corrupt_list_json_fails_closed_instead_of_becoming_empty_metadata():
    repository = read("src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs")
    parser = repository[
        repository.index("private static List<string> ParseList"):
        repository.index("private static void MirrorOneToLegacy")
    ]
    assert "string.IsNullOrWhiteSpace(json)" in parser
    assert "JsonConvert.DeserializeObject<List<string>>(json)" in parser
    assert "throw new InvalidDataException" in parser
    assert "静默丢失知识元数据" in parser
    assert "catch { return new List<string>(); }" not in parser
