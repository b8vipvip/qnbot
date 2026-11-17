#requires -version 5.1
<#
Safely stops the QnBot Bot.exe process without allowing its external watchdog to
mistake a manual stop for a crash and relaunch it.
By default, only the standard QnBot install paths are matched. Use -BotExePath
to target a non-standard installation explicitly.
#>
[CmdletBinding()]
param(
    [string]$BotExePath = '',
    [ValidateRange(1, 30)][int]$GraceSeconds = 10
)

$ErrorActionPreference = 'Stop'

function Get-InstallKey([string]$Path) {
    $normalized = [System.IO.Path]::GetFullPath($Path).Trim().ToLowerInvariant()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized))
        return (($hash[0..7] | ForEach-Object { $_.ToString('x2') }) -join '')
    } finally {
        $sha.Dispose()
    }
}

function Get-TargetProcesses {
    $items = @(Get-CimInstance Win32_Process -Filter "Name='Bot.exe'" -ErrorAction SilentlyContinue)
    foreach ($item in $items) {
        $path = [string]$item.ExecutablePath
        if ([string]::IsNullOrWhiteSpace($path)) { continue }

        if (-not [string]::IsNullOrWhiteSpace($BotExePath)) {
            $expected = [System.IO.Path]::GetFullPath($BotExePath)
            $actual = [System.IO.Path]::GetFullPath($path)
            if (-not [string]::Equals(
                $actual,
                $expected,
                [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
        } elseif ($path -notmatch '\\qianniu-bot-x64\\Bin\\Bot\.exe$' -and
                  $path -notmatch '\\QianniuAiBot\\Bin\\Bot\.exe$') {
            continue
        }

        $item
    }
}

$stopped = @()
for ($attempt = 0; $attempt -lt 5; $attempt++) {
    $processes = @(Get-TargetProcesses)
    if ($processes.Count -eq 0) {
        if ($attempt -eq 0) { Write-Host 'No matching QnBot Bot.exe process found.' }
        break
    }

    foreach ($item in $processes) {
        $pidValue = [int]$item.ProcessId
        $exe = [System.IO.Path]::GetFullPath([string]$item.ExecutablePath)
        $installKey = Get-InstallKey $exe
        $runtimeDir = Join-Path $env:LOCALAPPDATA (
            'QianniuAiBot\runtime\watchdogs\' + $installKey)
        New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null

        # The watchdog checks this exact per-PID marker after the process exits.
        # Write it before graceful close or forced termination.
        $marker = Join-Path $runtimeDir ('expected-exit-' + $pidValue + '.marker')
        [System.IO.File]::WriteAllText(
            $marker,
            ((Get-Date).ToString('o') + ' manual-stop'),
            (New-Object System.Text.UTF8Encoding($false)))

        $process = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        try {
            if ($process.MainWindowHandle -ne 0) {
                [void]$process.CloseMainWindow()
            }
            try {
                Wait-Process -Id $pidValue -Timeout $GraceSeconds -ErrorAction SilentlyContinue
            } catch {}

            if (@(Get-Process -Id $pidValue -ErrorAction SilentlyContinue).Count -gt 0) {
                Stop-Process -Id $pidValue -Force -ErrorAction SilentlyContinue
                try {
                    Wait-Process -Id $pidValue -Timeout 5 -ErrorAction SilentlyContinue
                } catch {}
            }
            $stopped += $pidValue
        } finally {
            $process.Dispose()
        }
    }

    # A watchdog that was already between its exit check and restart may have
    # raced the first pass. Re-enumerate and stop any replacement Bot.exe too.
    Start-Sleep -Seconds 1
}

$remaining = @(Get-TargetProcesses)
if ($remaining.Count -gt 0) {
    throw 'A matching QnBot process is still running or repeatedly respawned. Inspect the watchdog log.'
}

if ($stopped.Count -gt 0) {
    Write-Host ('Stopped QnBot process(es): ' + (($stopped | Select-Object -Unique) -join ', '))
}
Write-Host 'Expected-exit markers were written before shutdown; the watchdog should not relaunch these manual stops.'
