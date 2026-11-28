from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_v2_package_rejects_invalid_shape_and_duplicate_ids_before_replacement():
    source = read("src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs")
    page = source.split("internal sealed class KnowledgeV2ImportExportPage", 1)[1].split("internal sealed class KnowledgeV2SettingsPage", 1)[0]
    assert page.index("var package = ParseValidatedPackage(") < page.index("var backup = CreateAutomaticBackup();") < page.index("KnowledgeEngineV2Repository.ReplaceAll")
    assert "version.Type != JTokenType.Integer" in page
    assert "version.Value<long>() != KnowledgeEngineV2Constants.SchemaVersion || rows == null" in page
    assert "!ids.Add(((string)id).Trim())" in page
    assert "id.Type != JTokenType.String" in page
    assert "answer.Type != JTokenType.String" in page
    assert "package.Records.Count == 0" in page
    assert "Guid.NewGuid().ToString" in page
    assert "ShopContextLocator.ResolveRuntimeByShopKey(_shopKey)" in page
    assert "using (ShopSettingsScope.Enter(RequireShop())) action();" in page
    for operation in ("ExportPackage", "ImportPackage", "ExportRecords", "RebuildIndex"):
        assert "InShop(" + operation + ")" in page

def test_invalid_threshold_is_rejected_before_settings_write():
    source = read("src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs")
    method = source.split("public static void SetSettings(", 1)[1].split("public static KnowledgeV2Decision Resolve(", 1)[0]
    assert method.index("double.IsNaN(threshold)") < method.index("store.SetString")
    assert "double.IsInfinity(minConfidence)" in method
