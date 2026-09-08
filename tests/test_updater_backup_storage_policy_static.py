from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
UPDATER = ROOT / "src" / "Bot" / "Update" / "BotAutoUpdater.ps1"


def source() -> str:
    return UPDATER.read_text(encoding="utf-8-sig")


def test_updater_keeps_only_current_rollback_snapshot_and_removes_partials():
    text = source()
    assert "function Clear-PreviousUpdaterBackups" in text
    assert "Clear-PreviousUpdaterBackups $backupRoot" in text
    assert "Select-Object -Skip 8" not in text
    assert "AddDays(-7)" not in text
    cleanup_fn = text[text.index("function Clear-PreviousUpdaterBackups"):text.index("function Restore-PersistentData")]
    assert "Get-ChildItem -LiteralPath $BackupRoot -Directory" in cleanup_fn
    assert "Remove-Item -LiteralPath $item.FullName -Recurse -Force" in cleanup_fn
    finally_block = text[text.index("finally {"):]
    assert "Test-Path -LiteralPath $partialBackupDir" in finally_block
    assert "Remove-Item -LiteralPath $partialBackupDir -Recurse -Force" in finally_block


def test_stale_backups_are_removed_before_new_snapshot_and_before_install_mutation():
    text = source()
    cleanup = text.index("Clear-PreviousUpdaterBackups $backupRoot")
    create_partial = text.index("New-Item -ItemType Directory -Path $partialBackupDir -Force")
    finalize = text.index("$backupFinalized = $true")
    mutate = text.index("$installMutationStarted = $true")
    assert cleanup < create_partial < finalize < mutate


def test_updater_preflights_required_backup_space_after_cleanup():
    text = source()
    cleanup = text.index("Clear-PreviousUpdaterBackups $backupRoot")
    estimate = text.index("$estimatedBackupBytes")
    free_space = text.index("Get-AvailableBytes $backupRoot")
    fail_closed = text.index("Insufficient disk space for validated rollback snapshot")
    create_partial = text.index("New-Item -ItemType Directory -Path $partialBackupDir -Force")
    assert cleanup < estimate < free_space < fail_closed < create_partial
    assert "$availableBytes -lt ($estimatedBackupBytes + 512MB)" in text


def test_migrated_legacy_install_data_is_not_carried_forward_forever():
    text = source()
    assert "$migrationMarker = Join-Path $persistentRoot 'data-migration-v2.done'" in text
    legacy = text.index("$legacyData = Join-Path $InstallDir 'data'")
    condition = text.index("-and -not (Test-Path -LiteralPath $migrationMarker -PathType Leaf)", legacy)
    copy = text.index("Copy-DirectoryContents $legacyData (Join-Path $packageRoot 'data')", condition)
    assert legacy < condition < copy
