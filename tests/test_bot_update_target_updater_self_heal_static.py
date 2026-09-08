from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_installer_bootstraps_with_target_package_updater_not_current_updater():
    actions = read("src/Bot/Update/BotUpdateService.Actions.Fast.cs")
    assert "using System.IO.Compression;" in actions
    assert "ExtractTargetUpdaterFromPackage(packagePath, tempUpdaterScript, release);" in actions
    assert "new ZipArchive(" in actions
    assert '"Bin/BotAutoUpdater.ps1"' in actions
    assert '"release-info.json"' in actions
    launch = actions.split("public static void LaunchInstaller", 1)[1].split("private static void ExtractTargetUpdaterFromPackage", 1)[0]
    assert "AppDomain.CurrentDomain.BaseDirectory" not in launch
    assert "File.Copy(sourceScript" not in launch

def test_target_updater_syntax_is_validated_before_handoff_ack():
    actions = read("src/Bot/Update/BotUpdateService.Actions.Fast.cs")
    parser = actions.index("System.Management.Automation.Language.Parser]::ParseFile")
    ready = actions.index(") | Set-Content -LiteralPath $HandoffPath -Encoding UTF8")
    assert parser < ready
    assert "target updater PowerShell syntax validated." in actions

def test_auto_updater_only_stops_target_install_and_requires_explicit_health():
    script = read("src/Bot/Update/BotAutoUpdater.ps1")
    assert "$ids += @(Get-Process -Name 'Bot'" not in script
    assert "if ($CurrentPid -gt 0 -and $CurrentPid -ne $PID)" in script
    assert "Test-BotHealthy([string]$ExpectedExe, [int]$ExpectedPid, [string]$HealthFile, [string]$ExpectedReleaseVersion)" in script
    assert "[string]$health.status -eq 'OK'" in script
    assert "[string]$health.release_version -eq $ExpectedReleaseVersion" in script
    assert "QIANNIU_BOT_UPDATE_EXPECTED_VERSION" in script
    assert "-PassThru" in script
    assert "Installed package version mismatch" in script

def test_update_ui_separates_user_skip_from_failed_install_quarantine():
    ui = read("src/Bot/Options/BotUpdateOptionsControl.cs")
    push = read("src/Bot/Update/BotUpdateService.ServerPush.Fast.cs")
    assert '"用户跳过版本"' in ui
    assert '"安装失败隔离"' in ui
    assert "settings.UserSkippedVersion" in ui
    assert "settings.FailedInstallVersion" in ui
    assert '"清除跳过/失败隔离"' in ui
    assert "因上次安装失败处于隔离状态" in push
