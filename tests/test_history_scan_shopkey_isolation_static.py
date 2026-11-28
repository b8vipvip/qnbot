from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_history_scan_uses_attached_shopkey_for_mutex_and_authoritative_qn_binding():
    scan = read("src/Bot/Knowledge/ChatHistoryScanService.cs")

    assert "ShopContextLocator.ResolveCurrentOrBySellerNick(seller)" in scan
    assert "ActiveBuyerScans.TryAdd(shopKey, 0)" in scan
    assert "ActiveBuyerScans.TryRemove(shopKey, out ignored)" in scan
    assert "ShopContextLocator.FindConnectedQnByShopKey(shopKey)" in scan
    assert "ShopContextLocator.ResolveRuntimeBySeller(qn)" in scan
    assert "QN.FindExistingBySellerNick(seller)" not in scan
    assert "ShopSettingsScope.Enter(shop)" in scan
    assert "千牛会话登录身份与扫描窗口 ShopKey 不一致" in scan


def test_history_archive_checkpoint_evidence_and_merge_prefer_current_shop_scope():
    files = [
        "src/Bot/Knowledge/BuyerMessageArchiveService.cs",
        "src/Bot/Knowledge/BuyerHistoryScanCheckpointStore.cs",
        "src/Bot/Knowledge/BuyerHistoryScanEvidenceSession.cs",
        "src/Bot/Knowledge/BuyerHistoryScanMessageMergeService.cs",
    ]
    for path in files:
        source = read(path)
        assert "ShopContextLocator.ResolveCurrentOrBySellerNick" in source
        assert "ShopContextLocator.ResolveBySellerNick" not in source


def test_current_shop_scope_resolver_remains_fail_closed_for_legacy_ambiguous_nicknames():
    locator = read("src/Bot/ShopScope/ShopContextLocator.cs")
    helper = locator[
        locator.index("public static ShopContext ResolveCurrentOrBySellerNick"):
        locator.index("// Resolve from the exact QN instance", locator.index("public static ShopContext ResolveCurrentOrBySellerNick"))
    ]

    assert "ShopSettingsScope.Current" in helper
    assert "if (current != null)" in helper
    assert "if (current != null)\n            {" in helper
    assert "if (IsQianniuShopKey(sellerNick)" in helper
    assert "IsQianniuShopKey(sellerNick)" in helper
    assert "ResolveRuntimeByShopKey(sellerNick)" in helper
    assert "current.SellerNick" in helper
    assert "current.DisplayName" in helper
    assert "if (!sellerMatches && !displayNameMatches)" in helper
    assert "请求的ShopKey与当前店铺作用域不一致，已阻止跨店访问。" in helper
    assert "请求的卖家与当前店铺作用域不一致，已阻止跨店访问。" in helper
    assert "return current;" in helper
    assert "return ResolveBySellerNick(sellerNick);" in helper

def test_history_scan_reuses_the_already_verified_shopkey_for_history_store_reads():
    scan = read("src/Bot/Knowledge/ChatHistoryScanService.cs")
    assert "BotConversationHistoryStore.LoadRecentBySeller(shopKey, seller, 5000)" in scan
    assert "var shopKey = ShopIdentityResolver.Resolve(qn.Seller).ShopKey" not in scan

def test_resumable_scan_checks_authoritative_cdp_before_each_history_page_and_checkpoint():
    scan = read("src/Bot/Knowledge/ChatHistoryScanService.cs")
    method = scan.split("public async Task<ChatHistoryScanResult> ScanBuyerMessagesAsync(", 1)[1].split("public async Task<ChatHistoryScanResult> ScanAndImportAsync(", 1)[0]
    assert "IsAuthoritativeShopSession(shopKey, scanSessionId)" in method
    assert "reason => evidence.Excluded(reason), requireOriginalSession)" in method
    assert "requireOriginalSession();\n                        if (successful.Add(buyer))" in method
    assert "&& isOriginalSession()" in method
    assert "history_pagination_incomplete:missing_message_array" in scan
    assert "history_seller_identity_mismatch" in scan
