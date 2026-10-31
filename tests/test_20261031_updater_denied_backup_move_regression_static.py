"""Field regression: 1.1.1743 downloaded but failed at PowerShell 5.1 Move-Item backup rename.
Never mutate a running installation until the on-disk rollback snapshot is finalized.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UPDATER = ROOT / "src/Bot/Update/BotAutoUpdater.ps1"
SOURCE = UPDATER.read_text(encoding="utf-8-sig")

def test_no_directory_rename_is_required_to_finalize_rollback_backup():
    assert 'Move-Item -LiteralPath $partialBackupDir -Destination $backupDir' not in SOURCE
    assert '$partialBackupDir' not in SOURCE
    assert 'New-Item -ItemType Directory -Path $backupDir -ErrorAction Stop' in SOURCE
    assert 'Set-Content -LiteralPath (Join-Path $backupDir \'.complete\')' in SOURCE
    assert 'Set-Content -LiteralPath (Join-Path $backupDir \'backup-manifest.json\')' in SOURCE

def test_snapshot_verification_and_final_marker_precede_install_mutation():
    stage = SOURCE.index("$stage = 'rollback-backup'")
    assert stage < SOURCE.index("Clear-PreviousUpdaterBackups $backupRoot")
    assert SOURCE.index("Assert-DirectoryCopyMatches $InstallDir $programBackup 'program'") < SOURCE.index(
        "Set-Content -LiteralPath (Join-Path $backupDir '.complete')")
    assert SOURCE.index('Assert-DirectoryCopyMatches $source $destination "persistent/$name"') < SOURCE.index(
        "Set-Content -LiteralPath (Join-Path $backupDir '.complete')")
    marker = SOURCE.index("Set-Content -LiteralPath (Join-Path $backupDir '.complete')")
    verified = SOURCE.index("if (-not (Test-BackupComplete $backupDir))")
    finalized = SOURCE.index("$backupFinalized = $true")
    mutation = SOURCE.index("$installMutationStarted = $true")
    assert stage < marker < verified < finalized < mutation
    assert 'if (-not $backupFinalized -or -not (Test-BackupComplete $backupDir))' in SOURCE

def test_incomplete_snapshot_is_not_accepted_for_rollback_and_cleanup_is_bounded():
    test_complete = SOURCE.split('function Test-BackupComplete', 1)[1].split(
        'function Clear-PreviousUpdaterBackups', 1)[0]
    assert ".EndsWith('.partial'" in test_complete
    assert "'backup-manifest.json'" in test_complete
    assert "'.complete'" in test_complete
    exception = SOURCE.split("catch {\n    $failure = $_",1)[1]
    assert 'if (-not $installMutationStarted)' in exception
    assert '$backupUsable = $backupFinalized -and (Test-BackupComplete $backupDir)' in exception
    assert 'elseif ($backupUsable)' in exception
    assert '-not $backupFinalized -and (Test-Path -LiteralPath $backupDir)' in exception
    assert 'Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue' in exception
