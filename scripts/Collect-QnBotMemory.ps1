#requires -version 5.1
<#
Read-only sampler for QianNiu AI Bot / QianNiu workbench process and machine memory.
Includes a system row and the largest processes even when Bot/Qianniu are stopped.
No process changes, forced GC, dumps, customer text, environment variables,
command lines, filesystem inventories, or access tokens are collected.
Example: powershell -NoProfile -File scripts\Collect-QnBotMemory.ps1 -Minutes 10 -IntervalSeconds 30
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 240)][int]$Minutes = 10,
    [ValidateRange(5, 300)][int]$IntervalSeconds = 30,
    [ValidateRange(5, 50)][int]$TopProcessCount = 15,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $env:LOCALAPPDATA 'QianniuAiBot\diagnostics'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$csv = Join-Path $OutputDirectory ('qnbot-memory-' + $stamp + '.csv')
$processNames = @('Bot', 'AliWorkbench', 'AliRender', 'wwcmd')
$last = @{}
$loops = [int][Math]::Ceiling(($Minutes * 60.0) / $IntervalSeconds)

for ($sample = 1; $sample -le $loops; $sample++) {
    $now = Get-Date
    $freeMiB = $null
    $totalMiB = $null
    $usedPercent = $null
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $freeMiB = [math]::Round([double]$os.FreePhysicalMemory / 1024.0, 1)
        $totalMiB = [math]::Round([double]$os.TotalVisibleMemorySize / 1024.0, 1)
        if ($totalMiB -gt 0) {
            $usedPercent = [math]::Round((1.0 - ($freeMiB / $totalMiB)) * 100.0, 1)
        }
    } catch {
        Write-Warning 'Could not read system memory counters; process metrics continue.'
    }

    # Always write machine totals, even if the user has closed both applications.
    [pscustomobject]@{
        Time                = $now.ToString('yyyy-MM-ddTHH:mm:ssK')
        Sample              = $sample
        SampleKind          = 'system'
        Process             = '__SYSTEM__'
        Pid                 = 0
        WorkingSetMiB       = $null
        PrivateMiB          = $null
        HandleCount         = $null
        ThreadCount         = $null
        CpuPercentOfMachine = $null
        SystemFreeMiB       = $freeMiB
        SystemTotalMiB      = $totalMiB
        SystemUsedPercent   = $usedPercent
    } | Export-Csv -Path $csv -Encoding UTF8 -NoTypeInformation -Append

    $byPid = @{}
    $targetPids = @{}
    $targets = @(Get-Process -Name $processNames -ErrorAction SilentlyContinue)
    foreach ($p in $targets) {
        $byPid[[int]$p.Id] = $p
        $targetPids[[int]$p.Id] = $true
    }

    # The top list explains high system usage when Bot.exe and AliWorkbench.exe
    # are absent. Only process names and resource counters are collected.
    $top = @(Get-Process -ErrorAction SilentlyContinue |
        Sort-Object -Property WorkingSet64 -Descending |
        Select-Object -First $TopProcessCount)
    foreach ($p in $top) {
        $pidValue = [int]$p.Id
        if (-not $byPid.ContainsKey($pidValue)) {
            $byPid[$pidValue] = $p
        }
    }

    foreach ($p in $byPid.Values) {
        try {
            $p.Refresh()
            $pidValue = [int]$p.Id
            $cpuSeconds = [double]$p.CPU
            $pct = $null
            if ($last.ContainsKey($pidValue)) {
                $old = $last[$pidValue]
                $elapsed = ($now - $old.Time).TotalSeconds
                if ($elapsed -gt 0) {
                    $pct = [math]::Round(
                        [math]::Max(0, ($cpuSeconds - $old.Cpu) * 100.0 /
                            $elapsed / [Environment]::ProcessorCount), 2)
                }
            }
            $last[$pidValue] = @{ Cpu = $cpuSeconds; Time = $now }
            $kind = if ($targetPids.ContainsKey($pidValue)) { 'target' } else { 'top' }
            [pscustomobject]@{
                Time                = $now.ToString('yyyy-MM-ddTHH:mm:ssK')
                Sample              = $sample
                SampleKind          = $kind
                Process             = $p.ProcessName
                Pid                 = $pidValue
                WorkingSetMiB       = [math]::Round($p.WorkingSet64 / 1MB, 1)
                PrivateMiB          = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
                HandleCount         = $p.HandleCount
                ThreadCount         = $p.Threads.Count
                CpuPercentOfMachine = $pct
                SystemFreeMiB       = $freeMiB
                SystemTotalMiB      = $totalMiB
                SystemUsedPercent   = $usedPercent
            } | Export-Csv -Path $csv -Encoding UTF8 -NoTypeInformation -Append
        } catch {
            Write-Warning ('Could not sample a process: ' + $_.Exception.GetType().Name)
        }
    }

    Write-Host ('Sample ' + $sample + '/' + $loops + ': ' +
        $targets.Count + ' QnBot/Qianniu target processes, top ' +
        $top.Count + ' system processes, used=' + $usedPercent + '%, CSV=' + $csv)
    if ($sample -lt $loops) { Start-Sleep -Seconds $IntervalSeconds }
}
Write-Host ('Saved privacy-minimized system and process samples: ' + $csv)
