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
        "src/Bot/ChromeNs/HandoffRuleRemoteConfigService.cs",
    ):
        source = read(path)
        assert 'path + ".bak"' in source, path

    policy = read("src/Bot/ChromeNs/BusinessPolicyProfileService.cs")
    assert "CrossProcessAtomicStateFile.WriteAllTextAtomic" in policy
    assert 'path + ".bak"' in policy or "File.Copy(path, backup" in policy


def test_confirmation_ledger_never_replaces_a_corrupt_or_incomplete_barrier_file():
    ledger = read("src/Bot/ChromeNs/AutoDeliveryConfirmationLedger.cs")
    initialize = ledger[ledger.index("public static void Initialize"):ledger.index("public static bool IsOperational")]
    loader = ledger[ledger.index("private static LedgerState LoadState"):ledger.index("private static void CleanupLocked")]

    assert "if (!_failClosed)" in initialize
    assert "if (!_failClosed && (migrated || !File.Exists(StatePath()))" in initialize
    assert "loaded == null || loaded.Schema != 1 || loaded.Records == null" in loader
    assert "if (loaded.Records == null) loaded.Records = new List<ConfirmationRecord>();" not in loader
    assert "保留原文件" in loader
