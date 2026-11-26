#requires -version 5.1
<#
Read-only, privacy-minimized diagnostics for the durable automatic-delivery pipeline.
Reads only aggregate state from the queue, confirmation ledger, event cursor and
OrderEventHub. It never emits ShopKeys, seller/buyer names, order IDs or event payloads.
#>
[CmdletBinding()]
param(
    [string]$DataDirectory = '',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
    $DataDirectory = Join-Path (Join-Path $env:LOCALAPPDATA 'QianniuAiBot') 'data'
}

function Read-DeliveryStateFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{
            Status = 'missing'
            Document = $null
            AgeSeconds = $null
        }
    }

    try {
        $raw = [System.IO.File]::ReadAllText($Path)
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return [pscustomobject]@{
                Status = 'invalid'
                Document = $null
                AgeSeconds = $null
            }
        }
        $document = ConvertFrom-Json -InputObject $raw -ErrorAction Stop
        if ($null -eq $document) {
            return [pscustomobject]@{
                Status = 'invalid'
                Document = $null
                AgeSeconds = $null
            }
        }
        $age = [math]::Round(([DateTime]::UtcNow - (Get-Item -LiteralPath $Path).LastWriteTimeUtc).TotalSeconds, 1)
        return [pscustomobject]@{
            Status = 'readable'
            Document = $document
            AgeSeconds = $age
        }
    }
    catch {
        # Do not include exception text; parser errors may contain file contents.
        return [pscustomobject]@{
            Status = 'invalid'
            Document = $null
            AgeSeconds = $null
        }
    }
}

$queuePath = Join-Path $DataDirectory 'virtual-goods-auto-delivery-queue.json'
$ledgerPath = Join-Path $DataDirectory 'virtual-goods-auto-delivery-confirmation-ledger.json'
$cursorPath = Join-Path $DataDirectory 'virtual-goods-auto-delivery-event-cursor.json'
$hubPath = Join-Path $DataDirectory 'order-event-state.json'

$queueFile = Read-DeliveryStateFile -Path $queuePath
$ledgerFile = Read-DeliveryStateFile -Path $ledgerPath
$cursorFile = Read-DeliveryStateFile -Path $cursorPath
$hubFile = Read-DeliveryStateFile -Path $hubPath

$queueStatus = 'invalid'
$queuePendingCount = $null
$queueConfirmationIntentCount = $null
$queueUncertainCount = $null
$queueMaxAttempts = $null
$queueDueNowCount = $null
if ($queueFile.Status -eq 'readable' -and
    [int]$queueFile.Document.Schema -eq 2 -and
    $null -ne $queueFile.Document.PSObject.Properties['Pending']) {
    $pending = @()
    if ($null -ne $queueFile.Document.Pending) {
        $pending = @($queueFile.Document.Pending | Where-Object { $null -ne $_ })
    }
    $queueStatus = 'readable'
    $queuePendingCount = $pending.Count
    $queueConfirmationIntentCount = @($pending | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.ConfirmationIntentAt)
    }).Count
    $queueUncertainCount = @($pending | Where-Object {
        [int]$_.ConfirmationUncertainCount -gt 0
    }).Count
    $queueMaxAttempts = if ($pending.Count -gt 0) {
        [int](($pending | Measure-Object -Property Attempts -Maximum).Maximum)
    } else { 0 }
    $now = [DateTime]::Now
    $queueDueNowCount = @($pending | Where-Object {
        $next = [DateTime]::MinValue
        [DateTime]::TryParse([string]$_.NextAttemptAt, [ref]$next) -and $next -le $now
    }).Count
}
elseif ($queueFile.Status -eq 'missing') {
    $queueStatus = 'missing'
}

$ledgerStatus = 'invalid'
$ledgerRecordCount = $null
$ledgerResolvedCount = $null
$ledgerUnresolvedCount = $null
$ledgerRecentIntentCount = $null
if ($ledgerFile.Status -eq 'readable' -and
    ([int]$ledgerFile.Document.Schema -eq 1 -or [int]$ledgerFile.Document.Schema -eq 2) -and
    $null -ne $ledgerFile.Document.PSObject.Properties['Records']) {
    $records = @()
    if ($null -ne $ledgerFile.Document.Records) {
        $records = @($ledgerFile.Document.Records | Where-Object { $null -ne $_ })
    }
    $ledgerStatus = 'readable'
    $ledgerRecordCount = $records.Count
    $ledgerResolvedCount = @($records | Where-Object {
        # A persisted/migrated intent has a nonempty Resolution but is not resolved.
        # Require the actual durable terminal timestamp written by MarkResolved.
        $resolvedAt = [DateTime]::MinValue
        -not [string]::IsNullOrWhiteSpace([string]$_.Resolution) -and
            $_.Resolution -notin @('intent_persisted','migrated_pending_intent') -and
            [DateTime]::TryParse([string]$_.ResolvedAt, [ref]$resolvedAt) -and
            $resolvedAt -gt [DateTime]::MinValue
    }).Count
    $ledgerUnresolvedCount = $ledgerRecordCount - $ledgerResolvedCount
    $cutoff = [DateTime]::Now.AddHours(-24)
    $ledgerRecentIntentCount = @($records | Where-Object {
        $intent = [DateTime]::MinValue
        [DateTime]::TryParse([string]$_.IntentAt, [ref]$intent) -and $intent -ge $cutoff
    }).Count
}
elseif ($ledgerFile.Status -eq 'missing') {
    $ledgerStatus = 'missing'
}

$cursorStatus = 'invalid'
$cursorShopCount = $null
$cursorEnabledShopCount = $null
if ($cursorFile.Status -eq 'readable' -and
    [int]$cursorFile.Document.Schema -eq 3 -and
    $null -ne $cursorFile.Document.PSObject.Properties['Sellers']) {
    $sellers = @()
    if ($null -ne $cursorFile.Document.Sellers) {
        $sellers = @($cursorFile.Document.Sellers | Where-Object { $null -ne $_ })
    }
    $cursorStatus = 'readable'
    $cursorShopCount = $sellers.Count
    $cursorEnabledShopCount = @($sellers | Where-Object { $_.EnabledObserved -eq $true }).Count
}
elseif ($cursorFile.Status -eq 'missing') {
    $cursorStatus = 'missing'
}

$hubStatus = 'invalid'
$hubEventCount = $null
if ($hubFile.Status -eq 'readable' -and
    $null -ne $hubFile.Document.PSObject.Properties['Events']) {
    $events = @()
    if ($null -ne $hubFile.Document.Events) {
        $events = @($hubFile.Document.Events | Where-Object { $null -ne $_ })
    }
    $hubStatus = 'readable'
    $hubEventCount = $events.Count
}
elseif ($hubFile.Status -eq 'missing') {
    $hubStatus = 'missing'
}

$fileStatuses = @($queueStatus, $ledgerStatus, $cursorStatus, $hubStatus)
$result = if (@($fileStatuses | Where-Object { $_ -eq 'readable' }).Count -eq 4) {
    'COLLECTED'
} else {
    'INCOMPLETE'
}

$safetyAssessment = 'UNKNOWN'
if ($result -eq 'COLLECTED') {
    if ($ledgerUnresolvedCount -gt 0 -or $queueConfirmationIntentCount -gt 0 -or $queueUncertainCount -gt 0) {
        $safetyAssessment = 'REVIEW_REQUIRED'
    }
    elseif ($queuePendingCount -gt 0) {
        $safetyAssessment = 'PENDING_WORK'
    }
    else {
        $safetyAssessment = 'NO_UNRESOLVED_CONFIRMATION_INTENTS'
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = (Get-Location).Path
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $OutputDirectory ('qnbot-auto-delivery-diagnostics-' + $stamp + '.json')
$markdownPath = Join-Path $OutputDirectory ('qnbot-auto-delivery-diagnostics-' + $stamp + '.md')

$report = [ordered]@{
    SchemaVersion = 1
    CollectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Result = $result
    SafetyAssessment = $safetyAssessment
    Queue = [ordered]@{
        Status = $queueStatus
        FileAgeSeconds = $queueFile.AgeSeconds
        PendingCount = $queuePendingCount
        ConfirmationIntentCount = $queueConfirmationIntentCount
        UncertainCount = $queueUncertainCount
        MaxAttempts = $queueMaxAttempts
        DueNowCount = $queueDueNowCount
    }
    ConfirmationLedger = [ordered]@{
        Status = $ledgerStatus
        FileAgeSeconds = $ledgerFile.AgeSeconds
        RecordCount = $ledgerRecordCount
        ResolvedCount = $ledgerResolvedCount
        UnresolvedCount = $ledgerUnresolvedCount
        RecentIntentCount24h = $ledgerRecentIntentCount
    }
    EventCursor = [ordered]@{
        Status = $cursorStatus
        FileAgeSeconds = $cursorFile.AgeSeconds
        ShopCount = $cursorShopCount
        EnabledShopCount = $cursorEnabledShopCount
    }
    OrderEventHub = [ordered]@{
        Status = $hubStatus
        FileAgeSeconds = $hubFile.AgeSeconds
        EventCount = $hubEventCount
    }
    Privacy = [ordered]@{
        RawShopKeysIncluded = $false
        SellerNicknamesIncluded = $false
        BuyerNamesIncluded = $false
        OrderIdsIncluded = $false
        EventPayloadsIncluded = $false
        ConfirmationKeysIncluded = $false
    }
}
$json = $report | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($jsonPath, $json, [System.Text.UTF8Encoding]::new($false))

$markdown = @(
    '# QnBot automatic-delivery read-only diagnostics'
    ''
    ('- Collection result: **' + $result + '**')
    ('- Safety assessment: **' + $safetyAssessment + '**')
    ''
    '## Queue'
    ('- Status: ' + $queueStatus)
    ('- Pending / confirmation intent / uncertain: ' + $queuePendingCount + ' / ' + $queueConfirmationIntentCount + ' / ' + $queueUncertainCount)
    ('- Max attempts / due now: ' + $queueMaxAttempts + ' / ' + $queueDueNowCount)
    ''
    '## Durable confirmation ledger'
    ('- Status: ' + $ledgerStatus)
    ('- Records / resolved / unresolved: ' + $ledgerRecordCount + ' / ' + $ledgerResolvedCount + ' / ' + $ledgerUnresolvedCount)
    ('- Intents in last 24 hours: ' + $ledgerRecentIntentCount)
    ''
    '## Event cursor and OrderEventHub'
    ('- Cursor status / shops / enabled shops: ' + $cursorStatus + ' / ' + $cursorShopCount + ' / ' + $cursorEnabledShopCount)
    ('- OrderEventHub status / events: ' + $hubStatus + ' / ' + $hubEventCount)
    ''
    '## Privacy'
    '- No raw ShopKeys, seller/buyer names, order IDs, confirmation keys or event payloads are included.'
    '- This script is read-only and never changes queue, ledger, cursor or event-hub state.'
)
[System.IO.File]::WriteAllLines($markdownPath, [string[]]$markdown, [System.Text.UTF8Encoding]::new($false))

Write-Host ('Auto-delivery diagnostics: ' + $result + ' / ' + $safetyAssessment)
Write-Host ('JSON: ' + $jsonPath)
Write-Host ('Markdown: ' + $markdownPath)
if ($result -ne 'COLLECTED') { exit 1 }
