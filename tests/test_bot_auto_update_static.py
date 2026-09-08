from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def updater_code():
    paths = [
        "src/Bot/Update/BotUpdateModels.Fast.cs",
        "src/Bot/Update/BotUpdateService.Core.Fast.cs",
        "src/Bot/Update/BotUpdateService.Download.Fast.cs",
        "src/Bot/Update/BotUpdateService.Actions.Fast.cs",
        "src/Bot/Update/BotUpdateService.Network.Fast.cs",
        "src/Bot/Update/BotUpdateService.State.Fast.cs",
        "src/Bot/Update/BotUpdateService.ServerPush.Fast.cs",
        "src/Bot/Update/BotUpdatePromptWindow.Fast.cs",
    ]
    return "\n".join(read(path) for path in paths)


def test_background_updates_are_server_push_and_manual_check_keeps_safe_fallbacks():
    code = updater_code()
    core = read("src/Bot/Update/BotUpdateService.Core.Fast.cs")
    state = read("src/Bot/Update/BotUpdateService.State.Fast.cs")
    push = read("src/Bot/Update/BotUpdateService.ServerPush.Fast.cs")
    server_push = read("services/api-control-plane/bot_update_push.py")
    bootstrap = read("services/api-control-plane/bootstrap.py")
    props = read("src/Bot/Directory.Build.props")
    assert "mode=server-push-sse" in core
    assert "RestartServerPushListener()" in core
    assert "clientAutoCheck=False" in core
    assert "CheckNowAsync(false)" not in state
    assert "new Timer(" not in state
    assert "ServerPushEventsPath" in push
    assert "text/event-stream" in push
    assert "/api/public/v1/bot-update/events" in push
    assert "notification_mode" in server_push
    assert "StreamingResponse" in server_push
    assert "bot_update_push.router" in bootstrap
    assert "/api/public/v1/bot-update/latest" in code
    assert "ServiceMetadataTimeoutSeconds = 6" in code
    assert "https://api.github.com/repos/b8vipvip/qnbot/releases/latest" in code
    assert "FetchLatestFromControlPlaneAsync" in code
    assert "FetchLatestFromGitHubAsync" in code
    assert 'Name="UseOptimizedBotUpdateService"' in props
    assert 'Compile Remove="$(MSBuildThisFileDirectory)Update\\BotUpdateService.cs"' in props
    assert "Update\\BotUpdate*.Fast.cs" in props


def test_update_sha_survives_manifest_network_failure_and_shop_scoped_server_urls_are_discovered():
    network = read("src/Bot/Update/BotUpdateService.Network.Fast.cs")
    assert 'package.Value<string>("digest")' in network
    assert "NormalizeGitHubAssetDigest" in network
    assert "ExtractSha256FromReleaseNotes" in network
    assert "安装包 SHA-256" in network
    assert "release.Sha256 = IsSha256(fallbackSha)" in network
    assert "TryBackfillShaFromControlPlaneAsync" in network
    assert "GetConfiguredControlPlaneUrls" in network
    assert "ShopSettingsScope.Current" in network
    assert "new ShopProfileStore(paths)" in network
    assert "profile.ToContext()" in network
    assert "ShopControlPlaneConnectionStore.GetLegacyGlobalServerUrl()" in network


def test_download_is_server_only_and_client_triggers_server_prepare():
    code = updater_code()
    download = read("src/Bot/Update/BotUpdateService.Download.Fast.cs")
    assert "EnsureServerPackageReadyAsync" in download
    assert '"/api/public/v1/bot-update/ensure/"' in download
    assert '"/api/public/v1/bot-update/status/"' in download
    assert "HttpMethod.Post" in download
    assert 'CurrentDownloadChannel = "服务器"' in download
    assert 'CurrentDownloadChannel = "服务器准备中"' in download
    assert "release.PackageUrl" not in download
    assert 'channel="GitHub"' not in download
    assert "客户端已禁止直接从 GitHub 下载安装包" in download
    assert "客户端不会回退到 GitHub" in download
    assert "DownloadConnectTimeoutSeconds = 20" in code
    assert "DownloadReadTimeoutSeconds = 60" in code
    assert "HashFile(partial)" in code
    assert "CurrentDownloadChannel" in download
    assert "CurrentDownloadPercent" in download
    assert "RaiseDownloadStatus" in download
    assert "正在下载更新｜通道：" in download
    assert "DownloadedBytes" in code
    assert "TotalBytes" in code
    assert "if (cancellationToken.IsCancellationRequested) throw;" in download
    assert "Bot更新下载被用户取消" in download


def test_update_download_is_single_flight_and_auto_install_enables_server_push():
    download = read("src/Bot/Update/BotUpdateService.Download.Fast.cs")
    state = read("src/Bot/Update/BotUpdateService.State.Fast.cs")
    assert "private static readonly SemaphoreSlim DownloadGate" in download
    assert "DownloadGate.WaitAsync(cancellationToken)" in download
    assert "DownloadGate.Release()" in download
    assert "已有Bot更新下载任务正在进行，当前请求等待复用结果" in download
    assert "Bot更新下载任务已复用已完成安装包" in download
    assert "if (settings.AutoInstall)" in state
    assert "settings.AutoCheck = true" in state
    assert "settings.AutoDownload = false" in state
    assert "Never reintroduce a client-side periodic version check" in state


def test_server_caches_latest_metadata_and_verified_packages():
    module = read("services/api-control-plane/bot_update_cache.py")
    prefetch = read("services/api-control-plane/bot_update_prefetch.py")
    bootstrap = read("services/api-control-plane/bootstrap.py")
    dockerfile = read("services/api-control-plane/Dockerfile")
    env = read("services/api-control-plane/.env.example")
    assert 'GITHUB_LATEST_RELEASE_API = f"https://api.github.com/repos/{REPOSITORY}/releases/latest"' in module
    assert '@router.get("/api/public/v1/bot-update/latest"' in module
    assert '"/api/public/v1/bot-update/download/{tag}"' in module
    assert '@router.post("/api/public/v1/bot-update/ensure/{tag}"' in module
    assert '@router.get("/api/public/v1/bot-update/status/{tag}"' in module
    assert "METADATA_CACHE_SECONDS" in module
    assert "METADATA_STALE_SECONDS" in module
    assert "ensure_cached_package" in module
    assert "start_cached_package" in module
    assert "_PACKAGE_THREADS" in module
    assert "_hash_file(target)" in module
    assert "服务端镜像安装包 SHA-256 校验失败" in module
    assert "bot_update_cache.get_latest_metadata()" in prefetch
    assert "bot_update_cache.start_cached_package(metadata)" in prefetch
    assert "BOT_UPDATE_PREFETCH_ENABLED" in prefetch
    assert "BOT_UPDATE_PREFETCH_POLL_SECONDS" in prefetch
    assert 'os.getenv("BOT_UPDATE_PREFETCH_POLL_SECONDS", "300")' in prefetch
    assert "bot_update_prefetch.init_bot_update_prefetch()" in bootstrap
    assert "bot_update_prefetch.stop_bot_update_prefetch()" in bootstrap
    assert "bot_update_prefetch.py" in dockerfile
    assert "bot_update_cache.router" in bootstrap
    assert "bot_update_cache.init_bot_update_cache()" in bootstrap
    assert "BOT_UPDATE_METADATA_CACHE_SECONDS=300" in env
    assert "BOT_UPDATE_PREFETCH_ENABLED=true" in env
    assert "BOT_UPDATE_PREFETCH_POLL_SECONDS=300" in env


def test_update_defaults_keep_auto_install_opt_in_and_remove_second_confirmation():
    code = updater_code()
    core = read("src/Bot/Update/BotUpdateService.Core.Fast.cs")
    prompt = read("src/Bot/Update/BotUpdatePromptWindow.Fast.cs")
    ui = read("src/Bot/Options/BotUpdateOptionsControl.cs")
    ui_bridge = read("src/Bot/Update/BotUpdateSettingsUi.Fast.cs")
    assert "AutoCheck = true" in code
    assert "NotifyPopup = true" in code
    assert "AutoDownload = false" in code
    assert "AutoInstall = false" in code
    assert 'Content = "自动更新（发现新版本后自动下载安装并重启，无需确认）"' in ui
    assert "settings.AutoInstall = _autoUpdate.IsChecked == true" in ui
    assert "if (settings.AutoInstall) settings.AutoCheck = true" in ui
    assert "接收服务端新版本通知（客户端不主动检查版本）" in ui_bridge
    assert "Visibility.Collapsed" in ui_bridge
    assert "if (settings.AutoInstall" in core
    assert "result.InstallStarted = true" in core
    assert "LaunchInstaller(package, release)" in core
    assert "MessageBoxButton.YesNo" not in prompt
    assert '"确认更新"' not in prompt
    assert "下载通道：" in prompt
    assert "Application.Current.Shutdown()" in code


def test_updater_backs_up_validates_restarts_and_rolls_back():
    script = read("src/Bot/Update/BotAutoUpdater.ps1")
    assert "Get-FileHash" in script
    assert "ExpectedSha256" in script
    assert "release-info.json" in script
    assert "Preparing bounded rollback backup" in script
    assert "Test-BotHealthy" in script
    assert "database_initialized" in script
    assert "Clear-PreviousUpdaterBackups $backupRoot" in script
    assert "Restore-PersistentData $backupDir $persistentRoot" in script
    assert "$rollbackSucceeded = $true" in script
    assert "Start-Process -FilePath $oldExe" in script
    assert "Write-UpdateResult $resultPath 'failed'" in script
    assert "Select-Object -Skip 8" not in script


def test_auto_updater_backup_is_transactional_and_never_rolls_back_from_partial_copy():
    script = read("src/Bot/Update/BotAutoUpdater.ps1")
    assert '$partialBackupDir = "$backupDir.partial"' in script
    assert "$persistentNames = @('data', 'global', 'shops')" in script
    assert "Get-DirectoryFingerprint" in script
    assert "Assert-DirectoryCopyMatches" in script
    assert "backup-manifest.json" in script
    assert "Test-BackupComplete" in script
    assert ".EndsWith('.partial', [StringComparison]::OrdinalIgnoreCase)" in script
    assert "Join-Path $partialBackupDir '.complete'" in script
    assert "Move-Item -LiteralPath $partialBackupDir -Destination $backupDir" in script
    assert "$backupFinalized = $true" in script
    assert "$installMutationStarted = $false" in script
    assert "$installMutationStarted = $true" in script
    assert "$backupUsable = $backupFinalized -and (Test-BackupComplete $backupDir)" in script
    assert "if (-not $installMutationStarted)" in script
    assert "elseif ($backupUsable)" in script
    assert "Restore-PersistentData $backupDir $persistentRoot" in script
    finalized_guard = script.index("if (-not $backupFinalized -or -not (Test-BackupComplete $backupDir))")
    mutation_flag = script.index("$installMutationStarted = $true")
    destructive_clear = script.index("Clear-DirectoryContentsWithRetry $InstallDir", mutation_flag)
    assert finalized_guard < mutation_flag < destructive_clear


def test_updaters_keep_install_root_and_clear_only_children():
    auto = read("src/Bot/Update/BotAutoUpdater.ps1")
    manual = read("scripts/update-bot.ps1")
    for script in (auto, manual):
        assert "Clear-DirectoryContentsWithRetry" in script
        assert "Get-CimInstance Win32_Process" in script
        assert "Clear-DirectoryContentsWithRetry $InstallDir" in script
        assert "Remove-Item -LiteralPath $InstallDir -Recurse -Force" not in script
    assert "Get-InstallProcessState" in auto
    assert "Stop-BotProcesses" in auto
    assert "Get-InstallProcessIds" in manual
    assert "Get-PossibleDirectoryBlockers" in manual


def test_settings_page_and_startup_are_wired():
    app = read("src/Bot/App.xaml.cs")
    wnd = read("src/Bot/Options/WndOption.xaml.cs")
    options = read("src/Bot/Options/IOptions.cs")
    targets = read("src/Directory.Build.targets")
    props = read("src/Bot/Directory.Build.props")
    assert "BotUpdateService.Initialize()" in app
    assert "aboutUpdate = new BotUpdateOptionsControl();" in wnd
    assert 'AddPage("系统", "关于与更新"' in wnd
    assert "AboutUpdate" in options
    assert "Update\\BotUpdateService.cs" in targets
    assert "Update\\BotUpdate*.Fast.cs" in props
    assert "Options\\BotUpdateOptionsControl.cs" in targets
    assert "Update\\BotAutoUpdater.ps1" in targets
    assert "CopyToOutputDirectory" in targets


def test_release_workflow_publishes_stable_asset_and_manifest_from_verified_build():
    workflow = read(".github/workflows/publish-bot-auto-update-release.yml")
    assert 'workflows: ["Windows x64 release build"]' in workflow
    assert "github.event.workflow_run.conclusion == 'success'" in workflow
    assert "github.event.workflow_run.head_branch == 'master'" in workflow
    assert "actions: read" in workflow
    assert "contents: write" in workflow
    assert 'version="1.1.${run_number}"' in workflow
    assert 'tag="bot-v${version}"' in workflow
    assert "qianniu-bot-complete-x64-" in workflow
    assert "release-info.json" in workflow
    assert "qianniu-bot-x64.zip" in workflow
    assert "update.json" in workflow
    assert "sha256sum" in workflow
    assert "gh release create" in workflow
    assert "--latest" in workflow


def test_assembly_has_nonlegacy_update_baseline():
    assembly = read("src/Bot/Properties/AssemblyInfo.cs")
    assert 'AssemblyVersion("1.1.0.0")' in assembly
    assert 'AssemblyFileVersion("1.1.0.0")' in assembly
