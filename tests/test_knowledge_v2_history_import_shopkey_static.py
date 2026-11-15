from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_history_scan_captures_attached_shopkey_and_refuses_nickname_only_write():
    page = read("src/Bot/Knowledge/ChatHistoryScanWindow.cs")

    assert "private readonly string _shopKey;" in page
    assert "_shopKey = KnowledgeCenterV2Context.ResolveShopKey(owner, _seller);" in page
    assert "string.IsNullOrWhiteSpace(_seller) || string.IsNullOrWhiteSpace(_shopKey)" in page
    assert "PromoteHistoryImport(_seller, _shopKey, scan.ImportResult)" in page


def test_history_promotion_uses_exact_shopkey_and_one_scope_for_all_store_operations():
    service = read("src/Bot/Knowledge/KnowledgeV2LegacyDeltaImportService.cs")
    method = service[
        service.index("public static KnowledgeV2HistoryPromotionResult PromoteHistoryImport(\n            string seller, string shopKey"):
        service.index("public static int ImportMissingHistoryKnowledge", service.index("public static KnowledgeV2HistoryPromotionResult PromoteHistoryImport(\n            string seller, string shopKey"))
    ]
    scope_start = method.index("using (ShopSettingsScope.Enter(shop))")
    scope_end = method.index("\n            }\n        }", scope_start)
    scoped = method[scope_start:scope_end]

    assert "ShopContextLocator.ResolveRuntimeByShopKey(shopKey)" in service
    assert "var current = ShopSettingsScope.Current;" in service
    assert scoped.index("KnowledgeEngineV2Repository.LoadAll(seller)") < scoped.index("KnowledgeEngineV2Repository.Save(seller, record)")
    assert scoped.index("KnowledgeEngineV2Repository.Save(seller, record)") < scoped.index("KnowledgeEngineV2Service.Warm(seller)")
    assert scoped.index("KnowledgeEngineV2Service.Warm(seller)") < scoped.index("RemoveCurrentScanLegacyEntries(shop, sourceItems)")
    assert scoped.index("RemoveCurrentScanLegacyEntries(shop, sourceItems)") < scoped.index("AppendAudit(seller, importId, result)")


def test_missing_history_import_reuses_explicit_shopkey_scope():
    service = read("src/Bot/Knowledge/KnowledgeV2LegacyDeltaImportService.cs")
    method = service[
        service.index("public static int ImportMissingHistoryKnowledge(string seller, string shopKey)"):
        service.index("private static ShopContext ResolveShop", service.index("public static int ImportMissingHistoryKnowledge(string seller, string shopKey)"))
    ]

    assert "var shop = ResolveShop(seller, shopKey)" in method
    assert "using (ShopSettingsScope.Enter(shop))" in method
    assert "PromoteHistoryImport(seller, shop.ShopKey," in method
