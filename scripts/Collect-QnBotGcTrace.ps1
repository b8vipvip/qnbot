#requires -Version 5.1
<#
.SYNOPSIS
    Bounded, opt-in CLR GC event trace for the Windows qnbot host.
.DESCRIPTION
    Uses built-in Windows ETW logman; no external install, process termination,
    forced GC, memory dump, upload or restart.
    Start only in elevated PowerShell after Bot is already running.

    This provider is system-wide and can include events from other managed apps;
    treat the resulting ETL as private diagnostic data.
.EXAMPLE
    powershell.exe -NoProfile -File scripts\Collect-QnBotGcTrace.ps1
#>
[CmdletBinding()]
param(
    [ValidateRange(15, 600)][int]$DurationSeconds = 120,
    [ValidateRange(16, 128)][int]$MaxFileMiB = 64,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份打开 PowerShell 后运行此 GC ETW 采样。'
}

$logman = Join-Path $env:SystemRoot 'System32\logman.exe'
if (-not (Test-Path -LiteralPath $logman)) {
    throw '未找到 Windows 自带的 logman.exe。'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $env:LOCALAPPDATA 'QianniuAiBot\diagnostics'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$session = 'QnBotGc-' + $PID + '-' + $stamp
$etl = Join-Path $OutputDirectory ($session + '.etl')
$manifest = Join-Path $OutputDirectory ($session + '.json')
# Microsoft-Windows-DotNETRuntime, GCKeyword=0x1, informational level=4.
$provider = '{e13c0d23-ccbc-4e12-931b-d9cc2eee27e4}'
$started = $false
$startTime = Get-Date

try {
    & $logman start $session -p $provider 0x1 0x4 -ets -ct perf -o $etl -f bincirc -max $MaxFileMiB -bs 64 -nb 4 16 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw ('logman 无法启动 CLR GC ETW 会话，退出码=' + $LASTEXITCODE)
    }
    $started = $true
    Write-Host ('正在采集 .NET GC 事件 ' + $DurationSeconds +
        ' 秒（最大 ' + $MaxFileMiB + ' MiB）: ' + $etl)
    Start-Sleep -Seconds $DurationSeconds
}
finally {
    if ($started) {
        & $logman stop $session -ets | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning '停止 ETW 失败；请运行 logman query -ets 检查当前会话。'
        }
    }
}

[pscustomobject]@{
    Type = 'CLR-GC-ETW'
    Provider = 'Microsoft-Windows-DotNETRuntime'
    Keywords = '0x1 (GC only)'
    StartedAt = $startTime.ToString('o')
    CompletedAt = (Get-Date).ToString('o')
    DurationSeconds = $DurationSeconds
    MaxFileMiB = $MaxFileMiB
    EtlFile = $etl
    Note = 'ETW 可能包含同机其他 .NET 进程事件；请自行审查后分享。'
} | ConvertTo-Json | Out-File -FilePath $manifest -Encoding UTF8

if (-not (Test-Path -LiteralPath $etl)) {
    throw '跟踪已结束，但找不到 ETL 文件。'
}
Write-Host ('GC ETW 文件: ' + $etl)
Write-Host ('元数据: ' + $manifest)
