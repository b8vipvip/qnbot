from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_shop_catalog_query_is_seller_listing_not_buyer_item_search():
    parser = read("src/Bot/ChromeNs/ShopCatalogMtopReader.cs")
    service = read("src/Bot/ChromeNs/ShopProductCatalogService.cs")
    assert "mtop.tmall.sell.pc.manage.async" in parser
    assert '"/tmall/manager/table.htm?tab=all"' in parser
    assert "jsonBody = JsonConvert.SerializeObject" in parser
    assert "mtop.taobao.qianniu.cs.item.recommend.query" in parser
    assert "Partial = true" in parser
    assert "ParsePage(response)" in parser
    assert "JToken.Parse(text)" in parser
    assert "string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)" in parser
    assert "var fetched = await ShopCatalogMtopReader.FetchAsync(qn.CDP)" in service
    assert "mtop.taobao.qianniu.cs.item.search" not in service.replace(
        "// is not a seller-wide item list (and returned no data.data list).", ""
    )
    assert "var keptFullCatalog = fetched.Partial && !snapshot.Partial" in service
    assert "if (!mergedProducts.ContainsKey(cached.ItemId))" in service
    assert "if (!force && !snapshot.Partial" in service
    assert "qn.CDP.IsInvalidated" in service


def test_existing_order_fields_are_the_only_answer_editor():
    feature = read("src/Bot/Options/FeatureSettingsOptionsControl.cs")
    control = read("src/Bot/Options/ShopOrderProductRulesUi.cs")
    assert "new ShopOrderProductRulesUi(Seller, orderReply, orderMode)" in feature
    assert "legacyStack.Children.Insert(orderIndex + 1, _orderProductRulesUi)" in feature
    assert "legacyStack.Children.Add(_orderProductRulesUi)" not in feature
    assert "body.Children.Add(_orderProductRulesUi)" not in feature
    assert "GetPrivateField<TextBox>(_legacyWindow, \"_orderPlacedReplyText\")" in feature
    assert "GetPrivateField<ComboBox>(_legacyWindow, \"_orderPlacedReplyMode\")" in feature
    assert "_orderProductRulesUi.BeginSave()" in feature
    assert "_orderProductRulesUi.EndSave()" in feature
    assert "_sharedReply.Text = _globalReply" in control
    assert "_sharedReply.Text = rule == null ? \"\" : rule.Reply" in control
    assert '_sharedMode.SelectedItem = "固定预设答案"' in control
    assert "全部商品（通用回复）" in control
    assert "适用商品" in control
    assert 'Text = "不同商品的下单自动回复"' not in control
    assert "new TextBox" not in control  # must reuse the legacy answer input


def test_shop_strategy_uses_real_grid_not_blank_listbox():
    view = read("src/Bot/Options/ShopStrategyOptionsControl.cs")
    assert 'Text = "商品列表"' in view
    assert 'Header = "商品名称（双击修改）"' in view
    assert 'Header = "商品ID（已有商品不可改）"' in view
    assert 'Header = "商品链接（双击修改）"' in view
    assert 'Header = "状态"' in view
    assert "new DataGrid" in view
    assert "new ListBox" not in view
    assert "ShopProductCatalogService.RefreshAsync(_seller, true)" in view
    assert "snapshot.Partial" in view


def test_reader_included_in_production_windows_build():
    targets = read("src/Directory.Build.targets")
    assert "ShopCatalogMtopReader.cs" in targets
    assert "ShopProductCatalogService.cs" in targets
    assert "ShopOrderProductRulesUi.cs" in targets
    assert "ShopStrategyOptionsControl.cs" in targets
