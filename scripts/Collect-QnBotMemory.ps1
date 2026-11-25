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
    [string]$KnowledgeV2SyncSnapshotPath = '',
    [string]$KnowledgeV2LearningSnapshotPath = '',
    [ValidateLength(0, 40)][string]$ScenarioTag = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $env:LOCALAPPDATA 'QianniuAiBot\diagnostics'
}
if ([string]::IsNullOrWhiteSpace($RuntimeSnapshotPath)) {
    $RuntimeSnapshotPath = Join-Path $env:LOCALAPPDATA 'QianniuAiBot\diagnostics\runtime-snapshot.json'
}
if ([string]::IsNullOrWhiteSpace($KnowledgeV2SyncSnapshotPath)) {
    $KnowledgeV2SyncSnapshotPath = Join-Path $env:LOCALAPPDATA 'QianniuAiBot\diagnostics\knowledge-v2-sync-snapshot.json'
}
if ([string]::IsNullOrWhiteSpace($KnowledgeV2LearningSnapshotPath)) {
    $KnowledgeV2LearningSnapshotPath = Join-Path $env:LOCALAPPDATA 'QianniuAiBot\diagnostics\knowledge-v2-learning-snapshot.json'
}
$ScenarioTag = [regex]::Replace([string]$ScenarioTag, '[^A-Za-z0-9_.-]', '_')
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$csv = Join-Path $OutputDirectory ('qnbot-memory-' + $stamp + '.csv')
$processNames = @('Bot', 'AliWorkbench', 'AliRender', 'wwcmd')
$last = @{}
$previousOwners = @{}
$previousActiveShopRef = ''
$activeShopSequence = New-Object System.Collections.Generic.List[string]
$previousRuntimeProcessStartTicks = [long]0
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
    $runtimeActiveShopRef = ''
    $runtimeActiveSessionRef = ''
    $runtimeActiveFocusProofFresh = $null
    $runtimeActiveFocusProofAgeSeconds = $null
    $runtimeActiveShopChanged = $false
    $runtimeActiveShopTransitions = 0
    $runtimeActiveShopSequence = ''
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
                    $hasActiveShopEvidence = ($null -ne $runtime.PSObject.Properties['ActiveShopRef']) -and
                        ($null -ne $runtime.PSObject.Properties['ActiveSessionRef']) -and
                        ($null -ne $runtime.PSObject.Properties['ActiveFocusProofFresh'])
                    $hasCompleteRuntimeSchema = ($null -ne $runtime.PSObject.Properties['Shops']) -and
                        ($null -ne $runtime.PSObject.Properties['Counts']) -and
                        ($null -ne $counts.PSObject.Properties['StandbyCapabilitySessions']) -and
                        $hasActiveShopEvidence
                    if (-not $hasCompleteRuntimeSchema) { $runtimeStatus = 'partial' }
                    if ($hasActiveShopEvidence) {
                        $candidateActiveShopRef = [string]$runtime.ActiveShopRef
                        if ($candidateActiveShopRef -match '^shop#(?:[0-9a-f]{16}|none)$') {
                            $runtimeActiveShopRef = $candidateActiveShopRef
                        }
                        $candidateActiveSessionRef = [string]$runtime.ActiveSessionRef
                        if ($candidateActiveSessionRef -match '^session#(?:[0-9a-f]{16}|none)$') {
                            $runtimeActiveSessionRef = $candidateActiveSessionRef
                        }
                        $runtimeActiveFocusProofFresh = [bool]$runtime.ActiveFocusProofFresh
                        if ($null -ne $runtime.ActiveFocusProofAgeSeconds) {
                            $runtimeActiveFocusProofAgeSeconds = [int]$runtime.ActiveFocusProofAgeSeconds
                        }
                    }
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
                        $ownerDisplay = 'none'
                        if (-not [string]::IsNullOrWhiteSpace($ownerRef)) { $ownerDisplay = $ownerRef }
                        $ownerPairs += ($shopRef + '=' + $ownerDisplay)
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

                    if ($previousRuntimeProcessStartTicks -ne [long]0 -and $previousRuntimeProcessStartTicks -ne $runtimeProcessStartTicks) {
                        $runtimeProcessGenerationChanged = $true
                        $previousOwners = @{}
                        $previousActiveShopRef = ''
                        $activeShopSequence.Clear()
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
                    # Count a foreground switch only when the 5-second focus proof is fresh.
                    # A stale remembered active shop is diagnostic context, not A/B/A evidence.
                    if ($runtimeActiveFocusProofFresh -eq $true -and $runtimeActiveShopRef -match '^shop#[0-9a-f]{16}$') {
                        if ($previousActiveShopRef.Length -gt 0 -and
                            -not [string]::Equals($previousActiveShopRef, $runtimeActiveShopRef, [StringComparison]::Ordinal)) {
                            $runtimeActiveShopChanged = $true
                            $activeShopSequence.Add($runtimeActiveShopRef)
                        }
                        elseif ($previousActiveShopRef.Length -eq 0) {
                            $activeShopSequence.Add($runtimeActiveShopRef)
                        }
                        $previousActiveShopRef = $runtimeActiveShopRef
                    }
                    while ($activeShopSequence.Count -gt 32) { $activeShopSequence.RemoveAt(0) }
                    $runtimeActiveShopTransitions = [math]::Max(0, $activeShopSequence.Count - 1)
                    $runtimeActiveShopSequence = $activeShopSequence -join '>'
                    $previousRuntimeProcessStartTicks = $runtimeProcessStartTicks
                }
            }
        }
        catch {
            $runtimeStatus = 'invalid'
        }
    }

    # Compare the V2 cloud-sync fingerprint/revision snapshot only when it belongs
    # to the same live Bot process as the runtime snapshot above.
    $knowledgeSyncStatus = 'missing'
    $knowledgeSyncAgeSeconds = $null
    $knowledgeSyncShopCount = $null
    $knowledgeSyncConvergedShopCount = $null
    $knowledgeSyncDivergentShopCount = $null
    $knowledgeSyncSyncingShopCount = $null
    $knowledgeSyncLatestRevision = $null
    $knowledgeSyncShopStates = ''
    $knowledgeSyncStatuses = ''

    if (Test-Path -LiteralPath $KnowledgeV2SyncSnapshotPath -PathType Leaf) {
        try {
            $knowledgeSync = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($KnowledgeV2SyncSnapshotPath)) -ErrorAction Stop
            $knowledgeSyncCollectedAt = [DateTimeOffset]::Parse([string]$knowledgeSync.CollectedAtUtc).UtcDateTime
            $knowledgeSyncAgeSeconds = [math]::Round(([DateTime]::UtcNow - $knowledgeSyncCollectedAt).TotalSeconds, 1)
            if ([int]$knowledgeSync.SchemaVersion -ne 1) {
                $knowledgeSyncStatus = 'unsupported_schema'
            }
            elseif ($knowledgeSyncAgeSeconds -lt -30 -or $knowledgeSyncAgeSeconds -gt 180) {
                $knowledgeSyncStatus = 'stale'
            }
            elseif (($runtimeStatus -ne 'fresh' -and $runtimeStatus -ne 'partial') -or [int]$knowledgeSync.ProcessId -ne $runtimeProcessId -or [long]$knowledgeSync.ProcessStartTimeUtcTicks -ne $runtimeProcessStartTicks) {
                $knowledgeSyncStatus = 'process_mismatch'
            }
            else {
                $knowledgeSyncStatus = 'fresh'
                $syncShops = @()
                if ($null -ne $knowledgeSync.Shops) { $syncShops = @($knowledgeSync.Shops) }
                $knowledgeSyncShopCount = $syncShops.Count
                $knowledgeSyncConvergedShopCount = @($syncShops | Where-Object { $_.Converged -eq $true }).Count
                $knowledgeSyncDivergentShopCount = @($syncShops | Where-Object { $_.Converged -eq $false }).Count
                $knowledgeSyncSyncingShopCount = @($syncShops | Where-Object { $_.Syncing -eq $true }).Count
                if ($syncShops.Count -gt 0) {
                    $knowledgeSyncLatestRevision = ($syncShops | Measure-Object -Property CloudRevision -Maximum).Maximum
                }
                $syncShopStates = @()
                $syncStatuses = @()
                foreach ($syncShop in $syncShops) {
                    if ($null -eq $syncShop) { continue }
                    $syncShopStates += ([string]$syncShop.ShopRef + '=' + [string]$syncShop.CloudRevision + ':' + [string]$syncShop.LocalFingerprintPrefix + '/' + [string]$syncShop.CloudFingerprintPrefix + ':' + [string]$syncShop.LastStatus + ':' + [string]$syncShop.Converged)
                    if (-not [string]::IsNullOrWhiteSpace([string]$syncShop.LastStatus)) {
                        $syncStatuses += [string]$syncShop.LastStatus
                    }
                }
                $knowledgeSyncShopStates = $syncShopStates -join ';'
                $knowledgeSyncStatuses = (@($syncStatuses | Sort-Object -Unique) -join ';')
            }
        }
        catch {
            $knowledgeSyncStatus = 'invalid'
        }
    }

    # Read the anonymized full-corpus learning report for this same Bot process.
    # It exposes counts, checkpoint progress and resume evidence only; no message text.
    $learningSnapshotStatus = 'missing'
    $learningSnapshotAgeSeconds = $null
    $learningReportCount = $null
    $learningLatestShopRef = ''
    $learningLatestSessionRef = ''
    $learningLatestReportStatus = ''
    $learningLatestUpdatedAtUtc = ''
    $learningLatestHistoryComplete = $null
    $learningLatestHistoryMessagesFetched = $null
    $learningLatestHistoryMessagesInWindow = $null
    $learningLatestCorpusComplete = $null
    $learningLatestCorpusRecords = $null
    $learningLatestRecordsReviewed = $null
    $learningLatestBatchCount = $null
    $learningLatestBatchesCompleted = $null
    $learningLatestFailedBatches = $null
    $learningLatestCoveragePercent = $null
    $learningLatestResumeCount = $null
    $learningLatestRevisionStable = $null
    $learningLatestSourceFingerprintPrefix = ''
    $learningLatestTranscriptFingerprintPrefix = ''
    $learningReportStates = ''

    if (Test-Path -LiteralPath $KnowledgeV2LearningSnapshotPath -PathType Leaf) {
        try {
            $learningSnapshot = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($KnowledgeV2LearningSnapshotPath)) -ErrorAction Stop
            $learningCollectedAt = [DateTimeOffset]::Parse([string]$learningSnapshot.CollectedAtUtc).UtcDateTime
            $learningSnapshotAgeSeconds = [math]::Round(([DateTime]::UtcNow - $learningCollectedAt).TotalSeconds, 1)
            if ([int]$learningSnapshot.SchemaVersion -ne 1) {
                $learningSnapshotStatus = 'unsupported_schema'
            }
            elseif ($learningSnapshotAgeSeconds -lt -30 -or $learningSnapshotAgeSeconds -gt 604800) {
                $learningSnapshotStatus = 'stale'
            }
            elseif (($runtimeStatus -ne 'fresh' -and $runtimeStatus -ne 'partial') -or [int]$learningSnapshot.ProcessId -ne $runtimeProcessId -or [long]$learningSnapshot.ProcessStartTimeUtcTicks -ne $runtimeProcessStartTicks) {
                $learningSnapshotStatus = 'process_mismatch'
            }
            else {
                $learningSnapshotStatus = 'fresh'
                $learningReports = @()
                if ($null -ne $learningSnapshot.Reports) { $learningReports = @($learningSnapshot.Reports) }
                $learningReportCount = $learningReports.Count
                $learningStates = @()
                foreach ($learningReport in $learningReports) {
                    if ($null -eq $learningReport) { continue }
                    $learningStates += ([string]$learningReport.ShopRef + '=' + [string]$learningReport.SessionRef + ':' + [string]$learningReport.Status + ':' + [string]$learningReport.HistoryMessagesInWindow + ':' + [string]$learningReport.KnowledgeCorpusRecordsReviewed + '/' + [string]$learningReport.KnowledgeCorpusRecords + ':' + [string]$learningReport.KnowledgeCorpusBatchesCompleted + '/' + [string]$learningReport.KnowledgeCorpusBatches + ':' + [string]$learningReport.KnowledgeCorpusComplete + ':resume=' + [string]$learningReport.KnowledgeCorpusResumeCount)
                }
                $learningReportStates = $learningStates -join ';'
                if ($learningReports.Count -gt 0) {
                    $latestLearning = $learningReports[0]
                    $learningLatestShopRef = [string]$latestLearning.ShopRef
                    $learningLatestSessionRef = [string]$latestLearning.SessionRef
                    $learningLatestReportStatus = [string]$latestLearning.Status
                    $learningLatestUpdatedAtUtc = [string]$latestLearning.UpdatedAtUtc
                    $learningLatestHistoryComplete = [bool]$latestLearning.HistoryComplete
                    $learningLatestHistoryMessagesFetched = [int]$latestLearning.HistoryMessagesFetched
                    $learningLatestHistoryMessagesInWindow = [int]$latestLearning.HistoryMessagesInWindow
                    $learningLatestCorpusComplete = [bool]$latestLearning.KnowledgeCorpusComplete
                    $learningLatestCorpusRecords = [int]$latestLearning.KnowledgeCorpusRecords
                    $learningLatestRecordsReviewed = [int]$latestLearning.KnowledgeCorpusRecordsReviewed
                    $learningLatestBatchCount = [int]$latestLearning.KnowledgeCorpusBatches
                    $learningLatestBatchesCompleted = [int]$latestLearning.KnowledgeCorpusBatchesCompleted
                    $learningLatestFailedBatches = [int]$latestLearning.KnowledgeCorpusFailedBatches
                    $learningLatestCoveragePercent = [double]$latestLearning.KnowledgeCorpusCoveragePercent
                    $learningLatestResumeCount = [int]$latestLearning.KnowledgeCorpusResumeCount
                    $learningLatestRevisionStable = [bool]$latestLearning.KnowledgeCorpusRevisionStable
                    $learningLatestSourceFingerprintPrefix = [string]$latestLearning.KnowledgeCorpusSourceFingerprintPrefix
                    $learningLatestTranscriptFingerprintPrefix = [string]$latestLearning.KnowledgeCorpusTranscriptFingerprintPrefix
                }
            }
        }
        catch {
            $learningSnapshotStatus = 'invalid'
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
        RuntimeActiveShopRef = $runtimeActiveShopRef
        RuntimeActiveSessionRef = $runtimeActiveSessionRef
        RuntimeActiveFocusProofFresh = $runtimeActiveFocusProofFresh
        RuntimeActiveFocusProofAgeSeconds = $runtimeActiveFocusProofAgeSeconds
        RuntimeActiveShopChangedSincePreviousSample = $runtimeActiveShopChanged
        RuntimeActiveShopTransitions = $runtimeActiveShopTransitions
        RuntimeActiveShopSequence = $runtimeActiveShopSequence
        RuntimeOwnerChangesSincePreviousSample = $runtimeOwnerChanges
        KnowledgeV2SyncSnapshotStatus = $knowledgeSyncStatus
        KnowledgeV2SyncSnapshotAgeSeconds = $knowledgeSyncAgeSeconds
        KnowledgeV2SyncShopCount = $knowledgeSyncShopCount
        KnowledgeV2ConvergedShopCount = $knowledgeSyncConvergedShopCount
        KnowledgeV2DivergentShopCount = $knowledgeSyncDivergentShopCount
        KnowledgeV2SyncingShopCount = $knowledgeSyncSyncingShopCount
        KnowledgeV2LatestCloudRevision = $knowledgeSyncLatestRevision
        KnowledgeV2ShopStates = $knowledgeSyncShopStates
        KnowledgeV2SyncStatuses = $knowledgeSyncStatuses
        KnowledgeV2LearningSnapshotStatus = $learningSnapshotStatus
        KnowledgeV2LearningSnapshotAgeSeconds = $learningSnapshotAgeSeconds
        KnowledgeV2LearningReportCount = $learningReportCount
        KnowledgeV2LatestShopRef = $learningLatestShopRef
        KnowledgeV2LatestSessionRef = $learningLatestSessionRef
        KnowledgeV2LatestReportStatus = $learningLatestReportStatus
        KnowledgeV2LatestUpdatedAtUtc = $learningLatestUpdatedAtUtc
        KnowledgeV2LatestHistoryComplete = $learningLatestHistoryComplete
        KnowledgeV2LatestHistoryMessagesFetched = $learningLatestHistoryMessagesFetched
        KnowledgeV2LatestHistoryMessagesInWindow = $learningLatestHistoryMessagesInWindow
        KnowledgeV2LatestCorpusComplete = $learningLatestCorpusComplete
        KnowledgeV2LatestCorpusRecords = $learningLatestCorpusRecords
        KnowledgeV2LatestRecordsReviewed = $learningLatestRecordsReviewed
        KnowledgeV2LatestBatchCount = $learningLatestBatchCount
        KnowledgeV2LatestBatchesCompleted = $learningLatestBatchesCompleted
        KnowledgeV2LatestFailedBatches = $learningLatestFailedBatches
        KnowledgeV2LatestCoveragePercent = $learningLatestCoveragePercent
        KnowledgeV2LatestResumeCount = $learningLatestResumeCount
        KnowledgeV2LatestRevisionStable = $learningLatestRevisionStable
        KnowledgeV2LatestSourceFingerprintPrefix = $learningLatestSourceFingerprintPrefix
        KnowledgeV2LatestTranscriptFingerprintPrefix = $learningLatestTranscriptFingerprintPrefix
        KnowledgeV2LearningReportStates = $learningReportStates
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
        ', ownerChanges=' + $runtimeOwnerChanges + ', activeShop=' + $runtimeActiveShopRef +
        ', activeChanged=' + $runtimeActiveShopChanged + ', activeTransitions=' + $runtimeActiveShopTransitions +
        ', knowledgeSync=' + $knowledgeSyncStatus +
        ', converged=' + $knowledgeSyncConvergedShopCount + '/' + $knowledgeSyncShopCount +
        ', learning=' + $learningSnapshotStatus + ', history=' + $learningLatestHistoryMessagesInWindow +
        ', corpus=' + $learningLatestRecordsReviewed + '/' + $learningLatestCorpusRecords +
        ', resumed=' + $learningLatestResumeCount + ', CSV=' + $csv)
    if ($sample -lt $loops) { Start-Sleep -Seconds $IntervalSeconds }
}
Write-Host ('Saved privacy-minimized system and process samples: ' + $csv)
