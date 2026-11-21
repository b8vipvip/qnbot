#requires -version 5.1
<#
Read-only sampler for QianNiu AI Bot / QianNiu workbench process and machine memory.
Also samples the Bot's privacy-safe runtime snapshot to track shop-owner mapping,
duplicate/unmapped sessions and Bot process restarts during A/B switching or reconnect tests.
ShopRef/OwnerSessionRef are process-local pseudonyms and intentionally rotate after Bot restart.
No process changes, forced GC, dumps, customer text, seller nicknames, ShopKeys,
environment variables, command lines, filesystem inventories, or access tokens are collected.
Example: powershell -NoProfile -File scripts\Collect-QnBotMemory.ps1 -Minutes 60 -IntervalSeconds 15 -ScenarioTag shop-switch
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 240)][int]$Minutes = 10,
    [ValidateRange(5, 300)][int]$IntervalSeconds = 30,
    [ValidateRange(5, 50)][int]$TopProcessCount = 15,
    [string]$OutputDirectory = '',
    [string]$RuntimeSnapshotPath = '',
    [ValidateLength(0, 40)][string]$ScenarioTag = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $env:LOCALAPPDATA 'QianniuAiBot\diagnostics'
}
if ([string]::IsNullOrWhiteSpace($RuntimeSnapshotPath)) {
    $RuntimeSnapshotPath = Join-Path $env:LOCALAPPDATA 'QianniuAiBot\diagnostics\runtime-snapshot.json'
}
$ScenarioTag = [regex]::Replace([string]$ScenarioTag, '[^A-Za-z0-9_.-]', '_')
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$csv = Join-Path $OutputDirectory ('qnbot-memory-' + $stamp + '.csv')
$processNames = @('Bot', 'AliWorkbench', 'AliRender', 'wwcmd')
$last = @{}
$previousOwners = @{}
$previousRuntimeProcessStartTicks = 0L
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

    # Read only the Bot's pseudonymized runtime snapshot. This lets a long-running
    # sample observe shop-owner mapping, reconnects and Bot restarts without collecting
    # seller nicknames, ShopKeys, session IDs, chat content or raw logs.
    $runtimeStatus = 'missing'
    $runtimeAgeSeconds = $null
    $runtimeProcessId = $null
    $runtimeProcessStartTicks = $null
    $runtimeConnectedSessions = $null
    $runtimeAuthoritativeShopCount = $null
    $runtimeDuplicateSessions = $null
    $runtimeRecoverableStandbySessions = $null
    $runtimeStandbyCapabilitySessions = $null
    $runtimeUnmappedConnectedSessions = $null
    $runtimeOwnerDisconnectedCount = $null
    $runtimeOwnerNotInitializedCount = $null
    $runtimeOwnerClientInvalidatedCount = $null
    $runtimeOwnerClientRetiredCount = $null
    $runtimeStandbyCount = $null
    $runtimeRecoverableStandbyCount = $null
    $runtimeShopRefs = ''
    $runtimeShopOwners = ''
    $runtimeOwnerChanges = 0
    $runtimeProcessGenerationChanged = $false
    $runtimeSchemaVersion = $null

    if (Test-Path -LiteralPath $RuntimeSnapshotPath -PathType Leaf) {
        try {
            $runtime = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($RuntimeSnapshotPath)) -ErrorAction Stop
            $runtimeSchemaVersion = [int]$runtime.SchemaVersion
            $runtimeCollectedAt = [DateTimeOffset]::Parse([string]$runtime.CollectedAtUtc).UtcDateTime
            $runtimeAgeSeconds = [math]::Round(([DateTime]::UtcNow - $runtimeCollectedAt).TotalSeconds, 1)
            if ($runtimeSchemaVersion -ne 1) {
                $runtimeStatus = 'unsupported_schema'
            }
            elseif ($runtimeAgeSeconds -lt -30 -or $runtimeAgeSeconds -gt 120) {
                $runtimeStatus = 'stale'
            }
            else {
                $runtimeProcessId = [int]$runtime.ProcessId
                $runtimeProcessStartTicks = [long]$runtime.ProcessStartTimeUtcTicks
                $runtimeBotProcess = Get-Process -Id $runtimeProcessId -ErrorAction Stop
                $actualStartTicks = [long]$runtimeBotProcess.StartTime.ToUniversalTime().Ticks
                if ($actualStartTicks -ne $runtimeProcessStartTicks) {
                    $runtimeStatus = 'process_mismatch'
                }
                else {
                    $runtimeStatus = 'fresh'
                    $counts = $runtime.Counts
                    $hasCompleteRuntimeSchema = $null -ne $runtime.PSObject.Properties['Shops'] -and
                        $null -ne $runtime.PSObject.Properties['Counts'] -and
                        $null -ne $counts.PSObject.Properties['StandbyCapabilitySessions']
                    if (-not $hasCompleteRuntimeSchema) { $runtimeStatus = 'partial' }
                    $runtimeConnectedSessions = [int]$counts.ConnectedSessions
                    $runtimeAuthoritativeShopCount = [int]$counts.AuthoritativeShopCount
                    $runtimeDuplicateSessions = [int]$counts.DuplicateSessions
                    $runtimeRecoverableStandbySessions = [int]$counts.RecoverableStandbySessions
                    $runtimeStandbyCapabilitySessions = [int]$counts.StandbyCapabilitySessions
                    $runtimeUnmappedConnectedSessions = [int]$counts.UnmappedConnectedSessions

                    $shopRows = @()
                    if ($null -ne $runtime.Shops) { $shopRows = @($runtime.Shops) }
                    $currentOwners = @{}
                    $shopRefList = @()
                    $ownerPairs = @()
                    foreach ($shop in $shopRows) {
                        if ($null -eq $shop) { continue }
                        $shopRef = [string]$shop.ShopRef
                        if ([string]::IsNullOrWhiteSpace($shopRef)) { continue }
                        $ownerRef = [string]$shop.OwnerSessionRef
                        $currentOwners[$shopRef] = $ownerRef
                        $shopRefList += $shopRef
                        $ownerPairs += ($shopRef + '=' + $(if ([string]::IsNullOrWhiteSpace($ownerRef)) { 'none' } else { $ownerRef }))
                        if ($shop.OwnerConnected -eq $false) { $runtimeOwnerDisconnectedCount++ }
                        if ($shop.OwnerInitialized -eq $false) { $runtimeOwnerNotInitializedCount++ }
                        if ($shop.OwnerClientInvalidated -eq $true) { $runtimeOwnerClientInvalidatedCount++ }
                        if ($shop.OwnerClientIdentityRetired -eq $true) { $runtimeOwnerClientRetiredCount++ }
                        $runtimeStandbyCount += [int]$shop.StandbyCount
                        $runtimeRecoverableStandbyCount += [int]$shop.RecoverableStandbyCount
                    }
                    $runtimeOwnerDisconnectedCount = [int]$runtimeOwnerDisconnectedCount
                    $runtimeOwnerNotInitializedCount = [int]$runtimeOwnerNotInitializedCount
                    $runtimeOwnerClientInvalidatedCount = [int]$runtimeOwnerClientInvalidatedCount
                    $runtimeOwnerClientRetiredCount = [int]$runtimeOwnerClientRetiredCount
                    $runtimeStandbyCount = [int]$runtimeStandbyCount
                    $runtimeRecoverableStandbyCount = [int]$runtimeRecoverableStandbyCount
                    $runtimeShopRefs = (@($shopRefList | Sort-Object -Unique) -join ';')
                    $runtimeShopOwners = ($ownerPairs -join ';')

                    if ($previousRuntimeProcessStartTicks -ne 0L -and
                        $previousRuntimeProcessStartTicks -ne $runtimeProcessStartTicks) {
                        $runtimeProcessGenerationChanged = $true
                        $previousOwners = @{}
                    }
                    if (-not $runtimeProcessGenerationChanged) {
                        foreach ($shopRef in $currentOwners.Keys) {
                            if ($previousOwners.ContainsKey($shopRef) -and
                                $previousOwners[$shopRef] -ne $currentOwners[$shopRef]) {
                                $runtimeOwnerChanges++
                            }
                        }
                    }
                    $previousOwners = $currentOwners
                    $previousRuntimeProcessStartTicks = $runtimeProcessStartTicks
                }
            }
        }
        catch {
            $runtimeStatus = 'invalid'
        }
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
        ScenarioTag         = $ScenarioTag
        RuntimeSnapshotStatus = $runtimeStatus
        RuntimeSnapshotAgeSeconds = $runtimeAgeSeconds
        RuntimeSchemaVersion = $runtimeSchemaVersion
        RuntimeProcessId = $runtimeProcessId
        RuntimeProcessStartUtcTicks = $runtimeProcessStartTicks
        RuntimeProcessGenerationChanged = $runtimeProcessGenerationChanged
        RuntimeConnectedSessions = $runtimeConnectedSessions
        RuntimeAuthoritativeShopCount = $runtimeAuthoritativeShopCount
        RuntimeDuplicateSessions = $runtimeDuplicateSessions
        RuntimeRecoverableStandbySessions = $runtimeRecoverableStandbySessions
        RuntimeStandbyCapabilitySessions = $runtimeStandbyCapabilitySessions
        RuntimeUnmappedConnectedSessions = $runtimeUnmappedConnectedSessions
        RuntimeOwnerDisconnectedCount = $runtimeOwnerDisconnectedCount
        RuntimeOwnerNotInitializedCount = $runtimeOwnerNotInitializedCount
        RuntimeOwnerClientInvalidatedCount = $runtimeOwnerClientInvalidatedCount
        RuntimeOwnerClientRetiredCount = $runtimeOwnerClientRetiredCount
        RuntimeStandbyCount = $runtimeStandbyCount
        RuntimeRecoverableStandbyCount = $runtimeRecoverableStandbyCount
        RuntimeShopRefs = $runtimeShopRefs
        RuntimeShopOwners = $runtimeShopOwners
        RuntimeOwnerChangesSincePreviousSample = $runtimeOwnerChanges
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
        $top.Count + ' system processes, used=' + $usedPercent + '%, runtime=' +
        $runtimeStatus + ', shops=' + $runtimeAuthoritativeShopCount +
        ', ownerChanges=' + $runtimeOwnerChanges + ', CSV=' + $csv)
    if ($sample -lt $loops) { Start-Sleep -Seconds $IntervalSeconds }
}
Write-Host ('Saved privacy-minimized system and process samples: ' + $csv)
