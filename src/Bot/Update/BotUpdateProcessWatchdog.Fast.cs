using BotLib;
using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace Bot
{
    public partial class App
    {
        private readonly object _botExternalWatchdogBootstrap =
            UpdateNs.BotProcessWatchdog.InitializeForApp();
    }
}

namespace Bot.UpdateNs
{
    /// <summary>
    /// An external PowerShell watcher survives native crashes / process kills that cannot be
    /// recovered by in-process exception handlers. Each install owns exactly one watcher through
    /// an install-scoped exclusive lock. Startup also reaps historical leaked watcher processes
    /// from older releases before the current watcher is launched.
    /// </summary>
    internal static class BotProcessWatchdog
    {
        private static readonly object Sync = new object();
        private static bool _initialized;
        private static string _expectedExitMarker = string.Empty;
        private static string _expectedExitReason = string.Empty;

        public static object InitializeForApp()
        {
            lock (Sync)
            {
                if (_initialized) return new object();
                _initialized = true;
            }

            try
            {
                var currentProcess = Process.GetCurrentProcess();
                var exe = currentProcess.MainModule.FileName;
                var installKey = BuildInstallKey(exe);
                var runtimeRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "QianniuAiBot",
                    "runtime");
                var runtimeDir = Path.Combine(runtimeRoot, "watchdogs", installKey);
                Directory.CreateDirectory(runtimeDir);

                _expectedExitMarker = Path.Combine(
                    runtimeDir,
                    "expected-exit-" + currentProcess.Id + ".marker");
                try { if (File.Exists(_expectedExitMarker)) File.Delete(_expectedExitMarker); } catch { }

                var scriptPath = Path.Combine(runtimeDir, "bot-process-watchdog.ps1");
                var logPath = Path.Combine(runtimeDir, "bot-process-watchdog.log");
                var restartState = Path.Combine(runtimeDir, "bot-watchdog-restarts.txt");
                var watchdogLockPath = Path.Combine(runtimeDir, "watchdog-owner.lock");

                var cleaned = CleanupStaleWatchdogs(exe, installKey, currentProcess.Id);
                File.WriteAllText(scriptPath, BuildScript(), new UTF8Encoding(false));

                var arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "
                    + Quote(scriptPath)
                    + " -CurrentPid " + currentProcess.Id
                    + " -ExePath " + Quote(exe)
                    + " -WorkingDirectory " + Quote(AppDomain.CurrentDomain.BaseDirectory)
                    + " -ExpectedExitMarker " + Quote(_expectedExitMarker)
                    + " -RestartState " + Quote(restartState)
                    + " -WatchdogLog " + Quote(logPath)
                    + " -InstallKey " + Quote(installKey)
                    + " -WatchdogLockPath " + Quote(watchdogLockPath);
                var watcher = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = runtimeDir
                });
                if (watcher == null) throw new Exception("无法启动外部守护进程");

                if (Application.Current != null)
                {
                    Application.Current.Exit += delegate { MarkExpectedExit("normal-app-exit"); };
                    Application.Current.SessionEnding += delegate { MarkExpectedExit("windows-session-ending"); };
                }
                Log.Info(
                    "Bot外部进程守护已启动：同一安装目录仅允许一个watchdog；已自动清理历史泄漏进程="
                    + cleaned
                    + "；异常退出将自动重启；正常退出不会误拉起；自动更新退出进入安全交接恢复监控。watchdogPid="
                    + watcher.Id
                    + ", installKey=" + installKey);
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount("启动Bot外部进程守护失败：" + ex.Message, 5);
            }
            return new object();
        }

        public static void MarkExpectedExit(string reason)
        {
            TryMarkExpectedExit(reason);
        }

        public static bool TryMarkExpectedExit(string reason)
        {
            reason = (reason ?? string.Empty).Trim();
            try
            {
                lock (Sync)
                {
                    // Application.Current.Exit fires after LaunchInstaller has already marked an
                    // auto-update handoff. Do not let that generic normal-exit callback erase the
                    // update reason; the external watcher needs it to know that recovery is allowed.
                    if (_expectedExitReason.StartsWith(
                            "auto-update:",
                            StringComparison.OrdinalIgnoreCase)
                        && !reason.StartsWith(
                            "auto-update:",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        reason = _expectedExitReason;
                    }
                    else
                    {
                        _expectedExitReason = reason;
                    }

                    if (string.IsNullOrWhiteSpace(_expectedExitMarker)) return false;
                    File.WriteAllText(
                        _expectedExitMarker,
                        DateTime.Now.ToString("o") + " " + reason,
                        new UTF8Encoding(false));
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void CancelExpectedExit()
        {
            try
            {
                lock (Sync)
                {
                    _expectedExitReason = string.Empty;
                    if (!string.IsNullOrWhiteSpace(_expectedExitMarker)
                        && File.Exists(_expectedExitMarker))
                    {
                        File.Delete(_expectedExitMarker);
                    }
                }
            }
            catch { }
        }

        private static int CleanupStaleWatchdogs(
            string exePath,
            string installKey,
            int currentBotPid)
        {
            var cleaned = 0;
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='powershell.exe' OR Name='pwsh.exe'"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        try
                        {
                            var pid = Convert.ToInt32(item["ProcessId"]);
                            var commandLine = Convert.ToString(item["CommandLine"]) ?? string.Empty;
                            if (pid <= 0 || pid == currentBotPid) continue;
                            if (commandLine.IndexOf(
                                    "bot-process-watchdog.ps1",
                                    StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                continue;
                            }

                            var sameInstall = commandLine.IndexOf(
                                    exePath,
                                    StringComparison.OrdinalIgnoreCase) >= 0
                                || commandLine.IndexOf(
                                    installKey,
                                    StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!sameInstall) continue;

                            using (var stale = Process.GetProcessById(pid))
                            {
                                if (stale.HasExited) continue;
                                stale.Kill();
                                try { stale.WaitForExit(3000); } catch { }
                                cleaned++;
                            }
                        }
                        catch (ArgumentException)
                        {
                            // Process already exited between WMI enumeration and cleanup.
                        }
                        catch (Exception ex)
                        {
                            Log.ErrorWithMaxCount(
                                "清理历史Bot watchdog失败，已跳过单个进程：" + ex.Message,
                                10);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorWithMaxCount(
                    "枚举历史Bot watchdog失败；当前版本仍会依靠独占锁阻止新watchdog重复："
                    + ex.Message,
                    5);
            }
            return cleaned;
        }

        private static string BuildInstallKey(string exePath)
        {
            var normalized = Path.GetFullPath(exePath ?? string.Empty)
                .Trim()
                .ToLowerInvariant();
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var builder = new StringBuilder(16);
                for (var i = 0; i < 8; i++)
                    builder.Append(hash[i].ToString("x2"));
                return builder.ToString();
            }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string BuildScript()
        {
            return @"param(
    [Parameter(Mandatory=$true)][int]$CurrentPid,
    [Parameter(Mandatory=$true)][string]$ExePath,
    [Parameter(Mandatory=$true)][string]$WorkingDirectory,
    [Parameter(Mandatory=$true)][string]$ExpectedExitMarker,
    [Parameter(Mandatory=$true)][string]$RestartState,
    [Parameter(Mandatory=$true)][string]$WatchdogLog,
    [Parameter(Mandatory=$true)][string]$InstallKey,
    [Parameter(Mandatory=$true)][string]$WatchdogLockPath
)
$ErrorActionPreference = 'SilentlyContinue'
function Write-WatchdogLog([string]$Message) {
    try {
        $line = ('{0:yyyy-MM-dd HH:mm:ss.fff} {1}' -f (Get-Date), $Message)
        Add-Content -LiteralPath $WatchdogLog -Value $line -Encoding UTF8
    } catch {}
}
function Acquire-WatchdogOwnership {
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        try {
            $stream = [System.IO.File]::Open(
                $WatchdogLockPath,
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
            $payload = [System.Text.Encoding]::UTF8.GetBytes(
                ('watchdog_pid=' + $PID + ""`n"" +
                 'bot_pid=' + $CurrentPid + ""`n"" +
                 'install_key=' + $InstallKey + ""`n""))
            $stream.SetLength(0)
            $stream.Write($payload, 0, $payload.Length)
            $stream.Flush()
            return $stream
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }
    return $null
}
function Test-SameBotRunning {
    try {
        $same = Get-CimInstance Win32_Process -Filter 'Name=''Bot.exe''' | Where-Object {
            $_.ExecutablePath -and ([string]::Equals($_.ExecutablePath, $ExePath, [System.StringComparison]::OrdinalIgnoreCase))
        }
        return [bool]$same
    } catch {
        return $false
    }
}
function Get-RelatedUpdaterProcesses {
    $installRoot = Split-Path -Parent $WorkingDirectory
    try {
        return @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            if ($null -eq $_ -or [int]$_.ProcessId -eq $PID) { return $false }
            $cmd = [string]$_.CommandLine
            if ([string]::IsNullOrWhiteSpace($cmd)) { return $false }
            $mentionsInstall = $cmd.IndexOf($installRoot, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
            $looksLikeUpdater =
                $cmd.IndexOf('QianniuAiBotUpdaterBootstrap-', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $cmd.IndexOf('QianniuAiBotUpdater-', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $cmd.IndexOf('BotAutoUpdater.ps1', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
            return $mentionsInstall -and $looksLikeUpdater
        })
    } catch {
        return @()
    }
}
function Restart-Bot([string]$Reason) {
    if (Test-SameBotRunning) {
        Write-WatchdogLog ($Reason + '; Bot already running')
        return $true
    }
    if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
        Write-WatchdogLog ($Reason + '; restart failed because executable is missing: ' + $ExePath)
        return $false
    }
    try {
        $newProcess = Start-Process -FilePath $ExePath -WorkingDirectory $WorkingDirectory -PassThru
        Write-WatchdogLog ($Reason + '; restarted pid=' + $newProcess.Id)
        return $true
    } catch {
        Write-WatchdogLog ($Reason + '; restart failed: ' + $_.Exception.Message)
        return $false
    }
}
$watchdogLock = Acquire-WatchdogOwnership
if ($null -eq $watchdogLock) {
    Write-WatchdogLog ('watchdog ownership acquisition timed out; installKey=' + $InstallKey + '; duplicate watcher will not stay resident')
    exit 5
}
Write-WatchdogLog ('watchdog ownership acquired; installKey=' + $InstallKey + '; watchdogPid=' + $PID + '; botPid=' + $CurrentPid)
try {
    try {
        $p = Get-Process -Id $CurrentPid -ErrorAction SilentlyContinue
        if ($null -ne $p) { Wait-Process -Id $CurrentPid -ErrorAction SilentlyContinue }
    } catch {}
    Start-Sleep -Seconds 2
    if (Test-Path -LiteralPath $ExpectedExitMarker) {
        $expected = ''
        try { $expected = Get-Content -LiteralPath $ExpectedExitMarker -Raw -ErrorAction SilentlyContinue } catch {}
        Remove-Item -LiteralPath $ExpectedExitMarker -Force -ErrorAction SilentlyContinue

        if ($expected.IndexOf('auto-update:', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Write-WatchdogLog ('auto-update expected exit pid=' + $CurrentPid + '; entering handoff recovery watch')
            $softDeadline = (Get-Date).AddSeconds(90)
            $hardDeadline = (Get-Date).AddMinutes(5)
            while ((Get-Date) -lt $hardDeadline) {
                if (Test-SameBotRunning) {
                    Write-WatchdogLog 'auto-update handoff completed; replacement Bot is running'
                    exit 0
                }

                if ((Get-Date) -ge $softDeadline) {
                    $updaters = @(Get-RelatedUpdaterProcesses)
                    if ($updaters.Count -eq 0) {
                        Write-WatchdogLog 'auto-update handoff updater disappeared before Bot returned; starting recovery now'
                        if (Restart-Bot 'auto-update early recovery') { exit 0 }
                        exit 4
                    }
                }
                Start-Sleep -Seconds 2
            }

            $stuck = @(Get-RelatedUpdaterProcesses)
            if ($stuck.Count -gt 0) {
                Write-WatchdogLog ('auto-update handoff exceeded 5 minutes; terminating stuck updater count=' + $stuck.Count)
                foreach ($u in $stuck) {
                    Stop-Process -Id $u.ProcessId -Force -ErrorAction SilentlyContinue
                }
                Start-Sleep -Seconds 2
            }
            if (Restart-Bot 'auto-update timeout recovery') { exit 0 }
            exit 4
        }

        Write-WatchdogLog ('expected exit pid=' + $CurrentPid + '; reason=' + $expected + '; no restart')
        exit 0
    }
    if (Test-SameBotRunning) {
        Write-WatchdogLog 'another Bot.exe instance already owns this install; no restart'
        exit 0
    }
    $now = Get-Date
    $recent = @()
    try {
        if (Test-Path -LiteralPath $RestartState) {
            $recent = @(Get-Content -LiteralPath $RestartState | ForEach-Object {
                try { [DateTime]::Parse($_) } catch { $null }
            } | Where-Object { $_ -and $_ -gt $now.AddMinutes(-10) })
        }
    } catch { $recent = @() }
    if ($recent.Count -ge 5) {
        Write-WatchdogLog 'restart suppressed: reached 5 unexpected exits in 10 minutes'
        exit 2
    }
    $recent += $now
    try { $recent | ForEach-Object { $_.ToString('o') } | Set-Content -LiteralPath $RestartState -Encoding UTF8 } catch {}
    if (Restart-Bot ('unexpected exit pid=' + $CurrentPid)) { exit 0 }
    exit 3
}
finally {
    if ($null -ne $watchdogLock) {
        try { $watchdogLock.Dispose() } catch {}
    }
}
";
        }
    }
}
