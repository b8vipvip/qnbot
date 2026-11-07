from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_full_source_failure_not_silenced_by_recommendation_fallback():
    reader = read("src/Bot/ChromeNs/ShopCatalogMtopReader.cs")
    assert "shop_catalog_full_source_failed" in reader
    assert "FullCatalogError = string.Join" in reader
    assert "fullError" in reader
    assert "partial_recommendations_only" in reader
    assert 'Partial = true' in reader
    assert "ProductCandidateCount" in reader
    assert "UnreadableProductCount" in reader
    assert "CollectionKeys" in reader
    assert '"itemNumId", "id"' in reader
    # The failed authoritative call is still visible even when fallback succeeds.
    assert reader.index("shop_catalog_full_source_failed") < reader.index("if (page.Products.Count > 0)")


def test_incomplete_catalog_is_never_reported_as_complete():
    service = read("src/Bot/ChromeNs/ShopProductCatalogService.cs")
    view = read("src/Bot/Options/ShopStrategyOptionsControl.cs")
    chooser = read("src/Bot/Options/ShopOrderProductRulesUi.cs")
    assert "LastFullCatalogError" in service
    assert "LastFullCatalogAttemptUtc" in service
    assert "fullSourceSucceeded=" in service
    assert "shop_catalog_partial_only" in service
    assert "if (fetched.Partial)" in service
    assert "if (!mergedProducts.ContainsKey(cached.ItemId))" in service
    assert "if (!force && !snapshot.Partial" in service
    assert 'if (snapshot.Partial)' in view
    assert '尚未完成本店商品全量同步' in view
    assert '本店商品已完整更新' in view
    assert '不是经过验证的本店完整商品列表' in view
    assert 'snapshot.Partial' in chooser
    assert '本次仅获取到推荐商品' in chooser


def test_optional_manual_product_is_shop_scoped_and_survives_partial_refresh():
    service = read("src/Bot/ChromeNs/ShopProductCatalogService.cs")
    view = read("src/Bot/Options/ShopStrategyOptionsControl.cs")
    assert 'shop.catalog.manual-products.v1' in service
    assert "AddManualProduct(string seller, string productUrl, string productTitle)" in service
    assert "IsTaobaoItemHost(uri.Host)" in service
    assert "ManualProductsKey" in service
    assert "GetOrAdd(seller, _ => new SemaphoreSlim(1, 1))" in service
    assert "ShopScopedSettingsStore" in service
    assert "RuleStore(seller).SetString(ManualProductsKey" in service
    assert "ManuallyAdded = true" in service
    assert "!x.ManuallyAdded" in service
    assert "RemoveManualProduct(string seller, string itemId)" in service
    assert "补录商品" in view
    assert "移除选中的补录商品" in view
    assert "当前列表共" in view


def test_order_item_reply_still_uses_canonical_item_id_and_same_send():
    resolver = read("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "ShopProductCatalogService.MatchOrderReply(" in resolver
    assert "plan.Snapshot.ItemId" in resolver
    assert "RenderTemplate(productReply, plan" in resolver
    assert "SendOrderPresetAnswerAsync(plan, answer)" in resolver
