#requires -version 5.1
<#
Compare two privacy-minimized QnBot Knowledge V2 memory-sampler CSV files.
The device-local ShopRef aliases are intentionally independent; pass one alias per
device when that device tracks multiple shops. No raw ShopKey, seller, buyer, or
knowledge content is read or emitted.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$DeviceACsv,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$DeviceBCsv,
    [string]$DeviceAShopRef = '',
    [string]$DeviceBShopRef = '',
    [ValidateRange(1, 60)][int]$MaxSampleSkewMinutes = 5,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'

function Get-LatestDeviceShopState {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RequestedShopRef,
        [Parameter(Mandatory = $true)][string]$DeviceLabel
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw ($DeviceLabel + ' CSV file was not found.')
    }

    $rows = @(Import-Csv -LiteralPath $Path)
    $validRows = @($rows | Where-Object {
        $_.SampleKind -eq 'system' -and
        $_.KnowledgeV2SyncSnapshotStatus -eq 'fresh' -and
        -not [string]::IsNullOrWhiteSpace([string]$_.KnowledgeV2ShopStates)
    })
    if ($validRows.Count -eq 0) {
        throw ($DeviceLabel + ' CSV has no fresh Knowledge V2 sync snapshot rows.')
    }

    $latest = $validRows |
        Sort-Object -Property @{ Expression = { [DateTimeOffset]::Parse([string]$_.Time).UtcDateTime }; Descending = $true } |
        Select-Object -First 1

    $states = @()
    foreach ($entry in ([string]$latest.KnowledgeV2ShopStates -split ';')) {
        if ([string]::IsNullOrWhiteSpace($entry)) { continue }
        $pair = $entry -split '=', 2
        if ($pair.Count -ne 2 -or $pair[0] -notmatch '^shop#[0-9a-f]{16}$') { continue }

        $match = [regex]::Match(
            $pair[1],
            '^(?<revision>[0-9]+):(?<local>[0-9a-fA-F]{4,64})/(?<cloud>[0-9a-fA-F]{4,64}):(?<status>[^:;]*):(?<converged>True|False)$'
        )
        if (-not $match.Success) { continue }

        $states += [pscustomobject]@{
            ShopRef = $pair[0]
            CloudRevision = [long]$match.Groups['revision'].Value
            LocalFingerprintPrefix = $match.Groups['local'].Value.ToLowerInvariant()
            CloudFingerprintPrefix = $match.Groups['cloud'].Value.ToLowerInvariant()
            LastStatus = $match.Groups['status'].Value
            Converged = [bool]::Parse($match.Groups['converged'].Value)
        }
    }

    if ($states.Count -eq 0) {
        throw ($DeviceLabel + ' latest row contains no parseable shop sync states.')
    }

    $selectedShopRef = $RequestedShopRef.Trim()
    if ([string]::IsNullOrWhiteSpace($selectedShopRef)) {
        if ($states.Count -ne 1) {
            throw ($DeviceLabel + ' has multiple shops. Specify its own -' + $DeviceLabel + 'ShopRef alias.')
        }
        $selectedShopRef = [string]$states[0].ShopRef
    }

    if ($selectedShopRef -notmatch '^shop#[0-9a-f]{16}$') {
        throw ($DeviceLabel + ' ShopRef must be a process-local shop# alias from that device CSV.')
    }

    $selected = @($states | Where-Object { $_.ShopRef -eq $selectedShopRef })
    if ($selected.Count -ne 1) {
        throw ($DeviceLabel + ' ShopRef was not found uniquely in the latest fresh sync row.')
    }

    $snapshotAge = 0.0
    if (-not [double]::TryParse(
        [string]$latest.KnowledgeV2SyncSnapshotAgeSeconds,
        [Globalization.NumberStyles]::Float,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$snapshotAge
    ) -or $snapshotAge -lt -30 -or $snapshotAge -gt 180) {
        throw ($DeviceLabel + ' Knowledge V2 sync snapshot age is invalid or stale.')
    }

    return [pscustomobject]@{
        Device = $DeviceLabel
        ShopRef = $selected[0].ShopRef
        SampleTimeUtc = [DateTimeOffset]::Parse([string]$latest.Time).ToUniversalTime().ToString('o')
        SnapshotAgeSeconds = $snapshotAge
        CloudRevision = $selected[0].CloudRevision
        LocalFingerprintPrefix = $selected[0].LocalFingerprintPrefix
        CloudFingerprintPrefix = $selected[0].CloudFingerprintPrefix
        LastStatus = $selected[0].LastStatus
        Converged = $selected[0].Converged
    }
}

$deviceA = Get-LatestDeviceShopState -Path $DeviceACsv -RequestedShopRef $DeviceAShopRef -DeviceLabel 'DeviceA'
$deviceB = Get-LatestDeviceShopState -Path $DeviceBCsv -RequestedShopRef $DeviceBShopRef -DeviceLabel 'DeviceB'

$timeA = [DateTimeOffset]::Parse($deviceA.SampleTimeUtc)
$timeB = [DateTimeOffset]::Parse($deviceB.SampleTimeUtc)
$sampleSkewSeconds = [math]::Abs(($timeA - $timeB).TotalSeconds)
$maxSkewSeconds = $MaxSampleSkewMinutes * 60

$checks = @(
    [pscustomobject]@{ Name = 'Device A cloud sync converged'; Passed = ($deviceA.Converged -eq $true) }
    [pscustomobject]@{ Name = 'Device B cloud sync converged'; Passed = ($deviceB.Converged -eq $true) }
    [pscustomobject]@{ Name = 'Cloud revision matches'; Passed = ($deviceA.CloudRevision -eq $deviceB.CloudRevision) }
    [pscustomobject]@{ Name = 'Local fingerprint matches'; Passed = ($deviceA.LocalFingerprintPrefix -eq $deviceB.LocalFingerprintPrefix) }
    [pscustomobject]@{ Name = 'Cloud fingerprint matches'; Passed = ($deviceA.CloudFingerprintPrefix -eq $deviceB.CloudFingerprintPrefix) }
    [pscustomobject]@{ Name = 'Each local fingerprint matches its cloud fingerprint'; Passed = (
        $deviceA.LocalFingerprintPrefix -eq $deviceA.CloudFingerprintPrefix -and
        $deviceB.LocalFingerprintPrefix -eq $deviceB.CloudFingerprintPrefix
    ) }
    [pscustomobject]@{ Name = 'Sample timestamps are close enough'; Passed = ($sampleSkewSeconds -le $maxSkewSeconds) }
)

$passed = @($checks | Where-Object { $_.Passed -eq $true }).Count
$result = if ($passed -eq $checks.Count) { 'PASS' } else { 'FAIL' }

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = (Get-Location).Path
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $OutputDirectory ('qnbot-knowledge-v2-device-comparison-' + $stamp + '.json')
$markdownPath = Join-Path $OutputDirectory ('qnbot-knowledge-v2-device-comparison-' + $stamp + '.md')

$report = [ordered]@{
    SchemaVersion = 1
    CollectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Result = $result
    SampleSkewSeconds = [math]::Round($sampleSkewSeconds, 1)
    MaxSampleSkewSeconds = $maxSkewSeconds
    DeviceA = $deviceA
    DeviceB = $deviceB
    Checks = $checks
    Privacy = [ordered]@{
        RawShopKeysIncluded = $false
        SellerNicknamesIncluded = $false
        BuyerMessagesIncluded = $false
        KnowledgeContentIncluded = $false
        Note = 'Only process-local ShopRef aliases, cloud revision, truncated fingerprints, convergence flags and sample timestamps are included. ShopRef aliases are not compared across devices.'
    }
}
$json = $report | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($jsonPath, $json, (New-Object System.Text.UTF8Encoding($false)))

$markdown = @(
    '# QnBot Knowledge V2 two-device convergence report'
    ''
    ('- Result: **' + $result + '**')
    ('- Sample skew: ' + [math]::Round($sampleSkewSeconds, 1) + ' seconds')
    ''
    '## Device A'
    ('- ShopRef: ' + $deviceA.ShopRef)
    ('- Cloud revision: ' + $deviceA.CloudRevision)
    ('- Local / cloud fingerprint: ' + $deviceA.LocalFingerprintPrefix + ' / ' + $deviceA.CloudFingerprintPrefix)
    ('- Converged: ' + $deviceA.Converged + '; last status: ' + $deviceA.LastStatus)
    ''
    '## Device B'
    ('- ShopRef: ' + $deviceB.ShopRef)
    ('- Cloud revision: ' + $deviceB.CloudRevision)
    ('- Local / cloud fingerprint: ' + $deviceB.LocalFingerprintPrefix + ' / ' + $deviceB.CloudFingerprintPrefix)
    ('- Converged: ' + $deviceB.Converged + '; last status: ' + $deviceB.LastStatus)
    ''
    '## Checks'
)
foreach ($check in $checks) {
    $state = if ($check.Passed) { 'PASS' } else { 'FAIL' }
    $markdown += ('- ' + $state + ': ' + $check.Name)
}
$markdown += @(
    ''
    '## Privacy'
    '- Raw ShopKeys, seller nicknames, buyer messages and knowledge content are not included.'
    '- Device-local ShopRef aliases are intentionally independent and are selected separately.'
)
[System.IO.File]::WriteAllLines($markdownPath, [string[]]$markdown, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ('Knowledge V2 two-device convergence: ' + $result)
Write-Host ('JSON: ' + $jsonPath)
Write-Host ('Markdown: ' + $markdownPath)
if ($result -ne 'PASS') { exit 1 }
