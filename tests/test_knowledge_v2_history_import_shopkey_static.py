from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_history_scan_captures_attached_shopkey_and_refuses_nickname_only_write():
    page = read("src/Bot/Knowledge/ChatHistoryScanWindow.cs")

    assert "private readonly string _shopKey;" in page
    assert "_shopKey = KnowledgeCenterV2Context.ResolveShopKey(owner, _seller);" in page
    assert "string.IsNullOrWhiteSpace(_seller) || string.IsNullOrWhiteSpace(_shopKey)" in page
    assert "PromoteHistoryImport(_seller, _shopKey, scan.ImportResult)" in page


def test_history_promotion_uses_exact_shopkey_and_v2_repository_only():
    service = read("src/Bot/Knowledge/KnowledgeV2HistoryImportService.cs")
    props = read("src/Bot/Directory.Build.props")
    method = service[
        service.index("public static KnowledgeV2HistoryPromotionResult PromoteHistoryImport("):
        service.index("private static List<string> Clean(")
    ]

    assert "ShopContextLocator.ResolveRuntimeByShopKey(shopKey)" in method
    assert "shop == null || !shop.HasStableSellerId" in method
    assert "string.Equals(shop.ShopKey, shopKey, StringComparison.Ordinal)" in method
    assert "using (ShopSettingsScope.Enter(shop))" in method
    assert method.index("KnowledgeEngineV2Repository.LoadAll(seller)") < method.index(
        "KnowledgeEngineV2Repository.Save(seller, record)"
    ) < method.index("KnowledgeEngineV2Service.Warm(seller)") < method.index(
        "AppendAudit(seller, importId, result)"
    )
    assert "KnowledgeV2HistoryImportService.cs" in props
    assert "KnowledgeV2LegacyDeltaImportService.cs" not in props
    assert not (ROOT / "src/Bot/Knowledge/KnowledgeV2LegacyDeltaImportService.cs").exists()
    assert "BotFeatureStore.SaveKnowledgeBase" not in service


def test_history_import_does_not_use_bot_echoes_as_human_answer_evidence():
    source = read("src/Bot/Knowledge/ChatHistoryScanService.cs")
    build_pairs = source[
        source.index("private static List<Tuple<string, string, string>> BuildPairs("):
        source.index("private static bool IsUsefulPair(", source.index("private static List<Tuple<string, string, string>> BuildPairs("))
    ]

    assert "public bool IsBotReply;" in source
    assert "IsBotReply = string.Equals(role, \"seller\"" in source
    assert "IsBotMarkedSellerReply(text)" in source
    assert "turn.IsBotReply || IsBotMarkedSellerReply(turn.Text)" in build_pairs
    assert "[A]/[AI]自动回复不能作为答案证据" in source
    assert "sellers.Add(turn.Text)" in build_pairs
    assert build_pairs.index("turn.IsBotReply || IsBotMarkedSellerReply(turn.Text)") < build_pairs.index(
        "sellers.Add(turn.Text)"
    )
