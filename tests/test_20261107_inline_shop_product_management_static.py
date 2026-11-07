from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def source(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_product_grid_is_directly_editable_with_explicit_commit_and_undo():
    view = source("src/Bot/Options/ShopStrategyOptionsControl.cs")
    assert "IsReadOnly = false" in view
    assert 'MakeButton("新增商品"' in view
    assert 'MakeButton("删除选中"' in view
    assert 'MakeButton("保存商品列表"' in view
    assert 'MakeButton("撤销未保存修改"' in view
    assert "new ObservableCollection<ShopCatalogProduct>" in view
    assert "_items.BeginningEdit" in view
    assert "e.Column == _idColumn" in view
    assert "_originalIds.Contains(row.ItemId)" in view
    assert "_items.CommitEdit(DataGridEditingUnit.Cell, true)" in view
    assert "_items.CommitEdit(DataGridEditingUnit.Row, true)" in view
    assert "ShopProductCatalogService.SaveManagedCatalog" in view
    assert "if (_rows == null || !HasChanges()) return;" in view
    assert "await ShopProductCatalogService.RefreshAsync(_seller, true)" in view
    assert "尚有未保存的商品编辑" in view


def test_managed_catalog_is_shop_scoped_and_survives_a_full_refresh():
    service = source("src/Bot/ChromeNs/ShopProductCatalogService.cs")
    assert 'shop.catalog.managed-products.v1' in service
    assert "ShopScopedSettingsStore" in service
    assert "private static ShopCatalogManagedEdits LoadManagedEdits" in service
    assert "ShopContextLocator.ResolveRuntimeBySellerNick(seller)" in service
    assert "var managed = LoadManagedEdits(seller)" in service
    assert "products[local.ItemId] = local" in service
    assert "products.Remove(hidden)" in service
    assert "internal static void SaveManagedCatalog" in service
    assert "RuleStore(seller).SetString(ManagedProductsKey" in service
    assert "var gate = Gates.GetOrAdd(seller" in service
    assert "ValidateItemUrl(row.ItemId, row.Url)" in service
    assert "hidden.Add(id)" in service
    assert "hidden.Remove(row.ItemId)" in service
    assert "productReplyRulesUntouched=true" in service
    assert "MatchOrderReply(string seller, string itemId)" in service
    assert 'LocalManaged = true' in service


def test_editing_bot_catalog_never_changes_live_taobao_inventory():
    view = source("src/Bot/Options/ShopStrategyOptionsControl.cs")
    service = source("src/Bot/ChromeNs/ShopProductCatalogService.cs")
    assert "不会修改千牛真实商品" in view
    assert "不会删除商品专属回复" in view
    assert "不会更改或下架千牛店铺的真实商品" in view
    assert "ItemId不能修改" in view or "商品ID不能修改" in view
    assert "原订单专属答案将失去匹配依据" in view
    assert "public void Save(string seller)" in view
    assert "RuleStore(seller).SetString(ManagedProductsKey" in service
    assert 'RuleStore(seller).SetString(RulesKey' in service  # separate reply-store path
