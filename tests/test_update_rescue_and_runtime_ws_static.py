import os
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_rescue_updater_bypasses_broken_legacy_client_bootstrap():
    rescue = read("scripts/qianniu-bot-rescue-update.ps1")

    assert "https://api.github.com/repos/b8vipvip/qnbot/releases/latest" in rescue
    assert "qianniu-bot-x64.zip" in rescue
    assert "BotAutoUpdater.ps1" in rescue
    assert "Windows PowerShell 5.1" in rescue
    assert "Get-FileHash" in rescue
    assert "release-info.json" in rescue
    assert "绕过旧 Bot 更新器执行救援安装" in rescue
    assert "Start-Process -FilePath 'powershell.exe' -ArgumentList $args -PassThru" in rescue
    assert "$installer.WaitForExit()" in rescue
    assert "-PassThru -Wait" not in rescue
    assert "ExpectedVersion" in rescue
    assert "ExpectedSha256" in rescue
    assert "-Verb RunAs" in rescue


def test_rescue_updater_prefers_control_plane_and_does_not_require_manifest_download():
    rescue = read("scripts/qianniu-bot-rescue-update.ps1")

    assert 'ControlPlaneUrl = ""' in rescue
    assert "$BuiltInControlPlaneUrl = 'https://qn.numbot.cn'" in rescue
    assert "/api/public/v1/bot-update/latest" in rescue
    assert "Resolve-LatestFromControlPlane" in rescue
    assert "return Resolve-LatestFromGitHub" in rescue
    assert "mirror_url" in rescue
    assert "download_url" in rescue
    assert "Normalize-GitHubDigest" in rescue
    assert "Get-ShaFromReleaseNotes" in rescue
    assert "update.json 下载失败，但已有可信 SHA-256，继续救援更新" in rescue
    assert "最新正式版本缺少 update.json 或 qianniu-bot-x64.zip" not in rescue


def test_rescue_updater_autodetects_installed_control_plane_and_migrates_old_hostname():
    rescue = read("scripts/qianniu-bot-rescue-update.ps1")

    assert "$ServerUrlEnvironmentKey = 'QIANNIU_BOT_SERVER_URL'" in rescue
    assert "Resolve-ControlPlaneUrl" in rescue
    assert "Bin\\Bot.exe.config" in rescue
    assert "BotControlPlaneDefaultUrl" in rescue
    assert "$ObsoleteControlPlaneHost = 'botserver.mv3.cn'" in rescue
    assert "$LegacyControlPlaneHost = 'aboter.mv3.cn'" in rescue
    assert "$CurrentControlPlaneHost = 'qn.numbot.cn'" in rescue
    assert "$ControlPlaneUrl = Resolve-ControlPlaneUrl $InstallDir" in rescue


def test_client_update_endpoint_uses_correct_default_and_preserves_dynamic_overrides():
    app_config = read("src/Bot/App.config")
    store = read("src/Bot/ShopScope/ShopControlPlaneConnectionStore.cs")
    network = read("src/Bot/Update/BotUpdateService.Network.Fast.cs")

    assert 'BotControlPlaneDefaultUrl" value="https://qn.numbot.cn"' in app_config
    assert 'BuiltInDefaultServerUrl = "https://qn.numbot.cn"' in store
    assert 'ServerUrlEnvironmentKey = "QIANNIU_BOT_SERVER_URL"' in store
    assert 'UrlOverrideKey = "ControlPlaneUrlOverride"' in store
    assert "GetProgramServerUrl()" in store
    assert "PersistentParams.GetParam2Key(UrlOverrideKey, LegacyScope" in store
    assert "PersistentParams.GetParam2Key(UrlKey, LegacyScope" in store
    assert 'ObsoleteBuiltInHost = "botserver.mv3.cn"' in store
    assert 'LegacyBuiltInHost = "aboter.mv3.cn"' in store
    assert 'CurrentBuiltInHost = "qn.numbot.cn"' in store
    assert "TryNormalizeServerUrl" in store
    assert "Uri.TryCreate" in store
    assert "GetConfiguredControlPlaneUrls" in network
    assert "ShopControlPlaneConnectionStore.GetLegacyGlobalServerUrl()" in network


def test_rescue_updater_retries_with_curl_and_verifies_each_download_source():
    rescue = read("scripts/qianniu-bot-rescue-update.ps1")

    assert "PowerShell 网络请求失败，切换 curl.exe" in rescue
    assert "Get-Command curl.exe" in rescue
    assert "--connect-timeout" in rescue
    assert "--max-time" in rescue
    assert "--retry" in rescue
    assert "Download-VerifiedPackage" in rescue
    assert "所有安装包下载通道均失败" in rescue
    assert "SHA-256 不一致" in rescue
    assert "$ProgressPreference = 'SilentlyContinue'" in rescue


def test_rescue_updater_parses_under_windows_powershell_51():
    if os.name != "nt":
        return

    script = str((ROOT / "scripts/qianniu-bot-rescue-update.ps1").resolve())
    escaped = script.replace("'", "''")
    command = (
        "$tokens=$null;$errors=$null;"
        "[System.Management.Automation.Language.Parser]::ParseFile('"
        + escaped
        + "',[ref]$tokens,[ref]$errors)|Out-Null;"
        "if($errors.Count -gt 0){$errors|ForEach-Object{$_.Message}|Write-Error;exit 1}"
    )
    completed = subprocess.run(
        ["powershell.exe", "-NoProfile", "-Command", command],
        capture_output=True,
        timeout=30,
    )
    stdout = (completed.stdout or b"").decode("utf-8", errors="replace")
    stderr = (completed.stderr or b"").decode("utf-8", errors="replace")
    assert completed.returncode == 0, stdout + stderr


def test_bootstrap_awaits_injection_and_starts_bounded_listener_self_heal():
    bootstrap = read("src/Bot/StartUp/BootStrap.cs")
    props = read("src/Bot/Directory.Build.props")
    repair = read("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")

    assert "await QNInject.StartInject();" in bootstrap
    assert "QnStartupConnectionSelfHeal.Start();" in bootstrap
    assert "Update\\BotUpdate*.Fast.cs" in props
    assert "GetActiveTcpListeners" in repair
    assert "MyWebSocketServer.WSocketSvrInst.Start();" in repair
    assert "AuthoritativeCdpSessionCount <= 0" in repair
    assert "snapshot.CdpReady" in repair
    assert "snapshot.InjectionConnected" in repair
    assert "UpdateStartupHealthService.IsPostUpdateLaunchAuthorized()" in repair
    assert "UpdateStartupHealthService.IsPostUpdateStartupReady()" in repair
    assert "QianniuRecoveryManager.RequestRecover" not in repair

    degraded = repair[repair.index("private static async Task RunDegradedRecoveryAsync()"):]
    assert "TryRestartQianniuOnceAsync" not in degraded
    assert "保护千牛进程与登录态" in degraded
    assert "不会把standby页面误判为恢复成功" in degraded
    assert "保护千牛进程与登录态" in degraded


def test_self_heal_checks_actual_listener_instead_of_trusting_injection_marker():
    repair = read("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")
    qn_inject = read("src/Bot/Common/QNInject.cs")

    assert "IsLoopbackListenerActive" in repair
    assert "endpoint.Port == WebSocketPort" in repair
    assert "127.0.0.1:41010监听未就绪" in repair
    assert "snapshot.WebSocketSessionCount <= 0" in repair
    assert "snapshot.AuthoritativeCdpSessionCount <= 0" in repair
    assert "needInjectPaths.Count == 0" in qn_inject
    assert "千牛注入资源已是当前版本且sign.json已清理" in qn_inject


def test_post_update_recovery_preserves_qianniu_and_never_handles_login_ui():
    repair = read("src/Bot/Update/BotUpdateStartupConnection.Fast.cs")

    assert "PostUpdateGracePeriod = TimeSpan.FromSeconds(25)" in repair
    assert "PostUpdateReconnectWindow = TimeSpan.FromSeconds(120)" in repair
    assert 'WaitForInjectionAsync(PostUpdateReconnectWindow, "post-update-preserve-session")' in repair
    assert "TryRestartQianniuOnceAsync" not in repair
    assert "Process.Start" not in repair
    assert "PrimaryLoginButtonName" not in repair
    assert "WindowContainsText" not in repair
    assert "Mouse.Click" not in repair
    assert "保持原千牛进程与登录态" in repair
    assert "不重启千牛、不操作登录界面" in repair


def test_rescue_updater_reuses_a_verified_local_package_before_downloading():
    rescue = read("scripts/qianniu-bot-rescue-update.ps1")

    assert "Find-VerifiedLocalPackage" in rescue
    assert "Downloads\\qianniu-bot-x64.zip" in rescue
    assert "qianniu-bot-x64.zip" in rescue
    assert "Get-FileHash -LiteralPath $candidate -Algorithm SHA256" in rescue
    assert "跳过重新下载" in rescue
    assert "$downloadRequired = $false" in rescue
    assert "if ($downloadRequired) {" in rescue


def test_rescue_updater_rejects_stale_local_package_and_falls_back_to_download():
    rescue = read("scripts/qianniu-bot-rescue-update.ps1")

    assert "本地安装包但 SHA-256 不匹配，忽略" in rescue
    assert "Download-VerifiedPackage $packageUrls $PackagePath $ExpectedSha256" in rescue
    assert "qianniu-bot-x64-$ExpectedVersion.zip" in rescue


def test_rescue_control_plane_timeout_fails_fast_to_github():
    rescue = read("scripts/qianniu-bot-rescue-update.ps1")

    assert "[int]$MaxAttempts = 3" in rescue
    assert "$attempts = [Math]::Max(1, $MaxAttempts)" in rescue
    assert "Invoke-JsonRequest $url" in rescue
    assert "} 3 1" in rescue
    assert "GitHub 是权威回退" in rescue or "GitHub is the authoritative fallback" in rescue


def test_rescue_wrapper_waits_for_updater_process_without_waiting_for_bot_descendants():
    rescue = read("scripts/qianniu-bot-rescue-update.ps1")
    launch = rescue[rescue.index("# Wait for the updater process itself only."):rescue.index("$installedInfoPath = Join-Path $InstallDir")]

    assert "Start-Process -FilePath 'powershell.exe' -ArgumentList $args -PassThru" in launch
    assert "$installer.WaitForExit()" in launch
    assert "$installer.Refresh()" in launch
    assert "$installer.ExitCode" in launch
    assert "-PassThru -Wait" not in launch
