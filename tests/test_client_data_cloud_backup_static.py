from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_windows_client_exposes_per_shop_backup_and_restore():
    source = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    settings = read("src/Bot/Options/ShopBindingOptionsControl.cs")
    assert 'MakeButton("本店云备份/换机", 150)' in settings
    assert "new ClientDataBackupWindow(shop)" in settings
    assert "ShopScopedUiBridge.Attach(window, shop)" in settings
    assert '"上传本店数据"' in source
    assert '"恢复本店云备份"' in source
    assert "GetStatusAsync(ShopContext shop)" in source
    assert "UploadAsync(ShopContext shop" in source
    assert "DownloadAndRestoreAsync(ShopContext shop" in source
    assert "RestartApplication" in source
    assert "ShopSettingsScope.Enter(shop)" in settings


def test_backup_is_encrypted_with_current_shop_token_and_shop_key_integrity_checked():
    source = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    assert 'private const string Magic = "QABK2"' in source
    assert "Rfc2898DeriveBytes" in source
    assert '"|qianniu-shop-backup|" + (shopKey ?? string.Empty)' in source
    assert "Aes.Create" in source
    assert "HMACSHA256" in source
    assert "ConstantTimeEquals" in source
    assert "本店 Bot 令牌/ShopKey 不一致或云备份已损坏" in source


def test_logs_tokens_dpapi_files_and_transient_cloud_state_are_excluded():
    source = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    for value in (
        'new[] { "logs", "backup", "cache" }',
        'string.Equals(relative, "profile.json"',
        'string.Equals(relative, "config\\\\settings.json"',
        'string.Equals(relative, "config\\\\control-plane-token.json"',
        'string.Equals(extension, ".log"',
        'string.Equals(extension, ".tmp"',
        'string.Equals(extension, ".bak"',
        'IsTransientSetting',
        '"ProcessedCommand"',
        '"BotWebRemotePause"',
    ):
        assert value in source
    assert "PathEx.DataDir" not in source
    assert 'WriteTextEntry(zip, "params.json"' not in source


def test_logical_shop_settings_and_business_files_are_included_with_local_rollback():
    source = read("src/Bot/Knowledge/ClientDataCloudBackupService.cs")
    assert "ExportPortableSettings" in source
    assert "ShopScopedSettingsStore(shop, Paths).ExportValues" in source
    assert 'WriteTextEntry(zip, "settings.json"' in source
    assert 'zip.CreateEntry("files/"' in source
    assert "CreateRollbackBackup" in source
    assert "before-cloud-restore-" in source
    assert "ReplaceValues(current)" in source
    assert "云备份 ShopKey 与当前店铺不匹配" in source


def test_build_includes_client_backup_service():
    props = read("src/Bot/Directory.Build.props")
    assert "Knowledge\\ClientDataCloudBackupService.cs" in props


def test_server_registers_binary_backup_storage_by_client_token():
    service = read("services/api-control-plane/client_data_backup.py")
    bootstrap = read("services/api-control-plane/bootstrap.py")
    dockerfile = read("services/api-control-plane/Dockerfile")
    assert '"/api/runtime/v1/client-data-backup/status"' in service
    assert '"/api/runtime/v1/client-data-backup"' in service
    assert "Depends(core._runtime_client)" in service
    assert "bot_client_data_backups" in service
    assert "client-data-backups" in service
    assert "hashlib.sha256" in service
    assert "client_data_backup.install(control_plane)" in bootstrap
    assert "client_data_backup.init_db()" in bootstrap
    assert "client_data_backup.py" in dockerfile
