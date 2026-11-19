from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


WRITERS = (
    "src/Bot/ShopScope/ShopTokenStore.cs",
    "src/Bot/ShopScope/ShopProfileStore.cs",
    "src/Bot/ShopScope/ShopScopedSettingsStore.cs",
    "src/Bot/ShopScope/ShopLegacyDataMigrationService.cs",
    "src/Bot/ChromeNs/BusinessPolicyProfileService.cs",
    "src/Bot/ChromeNs/HandoffRuleRemoteConfigService.cs",
    "src/Bot/ChromeNs/StorePromptProfileService.cs",
    "src/Bot/ChromeNs/VirtualGoodsAutoDelivery.cs",
    "src/Bot/ChromeNs/AutoDeliveryOrderEventSeed.cs",
    "src/Bot/ChromeNs/AutoDeliveryConfirmationLedger.cs",
    "src/Bot/ChromeNs/DeterministicAutoReplyService.cs",
    "src/Bot/ChromeNs/SlowResponseAnomalyService.cs",
)


def test_persistent_config_and_runtime_state_writers_share_atomic_replace():
    for path in WRITERS:
        source = read(path)
        assert "CrossProcessAtomicStateFile.WriteAllTextAtomic" in source, path
        assert "File.Copy(temp, path, true)" not in source, path
        assert "File.Copy(temp, target, true)" not in source, path


def test_shared_atomic_writer_flushes_and_retries_without_destructive_copy():
    source = read("src/Bot/ChromeNs/CrossProcessAtomicStateFile.cs")
    assert "FileMode.CreateNew" in source
    assert "FileOptions.WriteThrough" in source
    assert "stream.Flush(true)" in source
    assert "File.Replace(temp, path, backupPath, true)" in source
    assert "string backupPath = null" in source
    assert "catch (IOException ex)" in source
    assert "File.Copy(" not in source
    assert "File.Delete(path)" not in source


def test_shop_scoped_store_writes_keep_previous_generation_as_backup():
    for path in (
        "src/Bot/ShopScope/ShopTokenStore.cs",
        "src/Bot/ShopScope/ShopProfileStore.cs",
        "src/Bot/ShopScope/ShopScopedSettingsStore.cs",
        "src/Bot/ShopScope/ShopLegacyDataMigrationService.cs",
        "src/Bot/ChromeNs/BusinessPolicyProfileService.cs",
        "src/Bot/ChromeNs/HandoffRuleRemoteConfigService.cs",
    ):
        source = read(path)
        assert 'path + ".bak"' in source, path
