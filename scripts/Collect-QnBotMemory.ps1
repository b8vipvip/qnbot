#requires -version 5.1
<#
Read-only sampler for QianNiu AI Bot / QianNiu workbench process memory.
No process changes, forced GC, dumps, customer text, environment variables,
command lines, filesystem inventories, or access tokens are collected.
Example: powershell -NoProfile -File scripts\Collect-QnBotMemory.ps1 -Minutes 30
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 240)][int]$Minutes = 30,
    [ValidateRange(5, 300)][int]$IntervalSeconds = 60,
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
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $freeMiB = [math]::Round([double]$os.FreePhysicalMemory / 1024.0, 1)
        $totalMiB = [math]::Round([double]$os.TotalVisibleMemorySize / 1024.0, 1)
    } catch {
        Write-Warning 'Could not read system memory counters; process metrics continue.'
    }

    $processes = @(Get-Process -Name $processNames -ErrorAction SilentlyContinue)
    foreach ($p in $processes) {
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
            [pscustomobject]@{
                Time                = $now.ToString('yyyy-MM-ddTHH:mm:ssK')
                Sample              = $sample
                Process             = $p.ProcessName
                Pid                 = $pidValue
                WorkingSetMiB       = [math]::Round($p.WorkingSet64 / 1MB, 1)
                PrivateMiB          = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
                HandleCount         = $p.HandleCount
                ThreadCount         = $p.Threads.Count
                CpuPercentOfMachine = $pct
                SystemFreeMiB       = $freeMiB
                SystemTotalMiB      = $totalMiB
            } | Export-Csv -Path $csv -Encoding UTF8 -NoTypeInformation -Append
        } catch {
            Write-Warning ('Could not sample a process: ' + $_.Exception.GetType().Name)
        }
    }

    Write-Host ('Sample ' + $sample + '/' + $loops + ': ' +
        $processes.Count + ' tracked processes, CSV=' + $csv)
    if ($sample -lt $loops) { Start-Sleep -Seconds $IntervalSeconds }
}
Write-Host ('Saved privacy-minimized resource samples: ' + $csv)
