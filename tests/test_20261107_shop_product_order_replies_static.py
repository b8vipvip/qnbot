from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def source(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_item_id_scoped_order_answers_keep_existing_delivery_pipeline():
    resolver = source("src/Bot/ChromeNs/OrderPlacedAutoReplyService.cs")
    assert "ShopProductCatalogService.MatchOrderReply(" in resolver
    assert "plan.Snapshot.ItemId" in resolver
    assert "if (!plan.IsBuyerFollowUp && plan.Snapshot != null)" in resolver
    assert resolver.index("ShopProductCatalogService.MatchOrderReply(") < resolver.index(
        'string.Equals(mode, "调用HTTP接口"')
    assert "RenderTemplate(productReply, plan, \"product-fixed-preset\")" in resolver
    assert "ProcessOrderPlacedReplyAsync(OrderPlacedReplyPlan plan)" in resolver
    assert "SendOrderPresetAnswerAsync(plan, resolution.Reply)" in resolver


def test_shop_cache_fail_closed_and_seven_day_refresh():
    service = source("src/Bot/ChromeNs/ShopProductCatalogService.cs")
    assert "TimeSpan.FromDays(7)" in service
    assert "TimeSpan.FromHours(1)" in service
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in service
    assert "QN.FindExistingBySellerNick(seller)" in service
    assert "InvokeMTop<ZnkfItemSearchResponse>" in service
    assert "products.Count == 0" in service
    assert "File.Replace(temp, path, null)" in service
    assert "MatchOrderReply(string seller, string itemId)" in service
    assert "string.Equals(x.ItemId, itemId, StringComparison.Ordinal)" in service
    assert "RuleStore(seller).SetString" in service


def test_ui_shop_strategy_and_product_dropdown_are_connected():
    options = source("src/Bot/Options/WndOption.xaml.cs")
    control = source("src/Bot/Options/FeatureSettingsOptionsControl.cs")
    product_ui = source("src/Bot/Options/ShopOrderProductRulesUi.cs")
    shop_ui = source("src/Bot/Options/ShopStrategyOptionsControl.cs")
    qn = source("src/Bot/ChromeNs/QN.cs")
    assert '"店铺策略"' in options and "OptionEnum.ShopStrategy" in options
    assert "ShopOrderProductRulesUi(Seller)" in control
    assert "_orderProductRulesUi.Save()" in control
    assert '"获取商品"' in product_ui
    assert '"选择本店商品"' in product_ui
    assert "RefreshAsync(_seller, true)" in product_ui
    assert '"更新商品"' in shop_ui
    assert "RefreshAsync(_seller, true)" in shop_ui
    assert "ShopProductCatalogService.EnsureScheduled(" in qn
    targets = source("src/Directory.Build.targets")
    for name in ("ShopProductCatalogService.cs", "ShopOrderProductRulesUi.cs",
                 "ShopStrategyOptionsControl.cs"):
        assert name in targets


def test_new_option_does_not_renumber_legacy_settings():
    text = source("src/Bot/Options/IOptions.cs")
    assert text.index("FeatureSettings,") < text.index("ShopStrategy")
