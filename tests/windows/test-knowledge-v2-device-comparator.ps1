$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot '..\..\scripts\Compare-QnBotKnowledgeV2Devices.ps1'
$scriptPath = (Resolve-Path $scriptPath).Path
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('qnbot-v2-device-compare-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

function Write-TestSamplerCsv {
    param([string]$Path, [string]$ShopRef, [int]$Revision, [string]$LocalFingerprint, [string]$CloudFingerprint)
    $row = [pscustomobject]@{
        Time = [DateTimeOffset]::UtcNow.AddSeconds(-5).ToString('o')
        SampleKind = 'system'
        KnowledgeV2SyncSnapshotStatus = 'fresh'
        KnowledgeV2SyncSnapshotAgeSeconds = '5'
        KnowledgeV2ShopStates = ($ShopRef + '=' + $Revision + ':' + $LocalFingerprint + '/' + $CloudFingerprint + ':synced:True:False')
    }
    $row | Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding UTF8
}

function Invoke-ComparatorChild {
    param([string]$CsvA, [string]$CsvB, [string]$OutputPath)
    $childArguments = @(
        '-NoProfile',
        '-ExecutionPolicy Bypass',
        ('-File "' + $scriptPath + '"'),
        ('-DeviceACsv "' + $CsvA + '"'),
        ('-DeviceBCsv "' + $CsvB + '"'),
        ('-OutputDirectory "' + $OutputPath + '"')
    ) -join ' '
    return Start-Process -FilePath 'powershell.exe' -ArgumentList $childArguments -Wait -PassThru -NoNewWindow
}

try {
    $csvA = Join-Path $tempRoot 'device-a.csv'
    $csvB = Join-Path $tempRoot 'device-b.csv'
    $passOutput = Join-Path $tempRoot 'pass'
    $failOutput = Join-Path $tempRoot 'fail'

    Write-TestSamplerCsv -Path $csvA -ShopRef 'shop#aaaaaaaaaaaaaaaa' -Revision 17 -LocalFingerprint 'abcd1234abcd1234' -CloudFingerprint 'abcd1234abcd1234'
    Write-TestSamplerCsv -Path $csvB -ShopRef 'shop#bbbbbbbbbbbbbbbb' -Revision 17 -LocalFingerprint 'abcd1234abcd1234' -CloudFingerprint 'abcd1234abcd1234'

    $passProcess = Invoke-ComparatorChild -CsvA $csvA -CsvB $csvB -OutputPath $passOutput
    if ($passProcess.ExitCode -ne 0) {
        throw ('Expected converged two-device sample to pass; exitCode=' + $passProcess.ExitCode)
    }
    $passReportPath = Get-ChildItem -LiteralPath $passOutput -Filter 'qnbot-knowledge-v2-device-comparison-*.json' |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($passReportPath)) { throw 'PASS report JSON was not created.' }
    $passReport = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($passReportPath))
    if ($passReport.Result -ne 'PASS') { throw 'Matching converged devices did not produce Result=PASS.' }
    if ($passReport.DeviceA.ShopRef -eq $passReport.DeviceB.ShopRef) { throw 'Device-local ShopRef aliases must remain independent.' }

    Write-TestSamplerCsv -Path $csvB -ShopRef 'shop#bbbbbbbbbbbbbbbb' -Revision 18 -LocalFingerprint 'abcd1234abcd1234' -CloudFingerprint 'abcd1234abcd1234'
    $failProcess = Invoke-ComparatorChild -CsvA $csvA -CsvB $csvB -OutputPath $failOutput
    if ($failProcess.ExitCode -ne 1) {
        throw ('Expected mismatched cloud revisions to fail; exitCode=' + $failProcess.ExitCode)
    }
    $failReportPath = Get-ChildItem -LiteralPath $failOutput -Filter 'qnbot-knowledge-v2-device-comparison-*.json' |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($failReportPath)) { throw 'FAIL report JSON was not created.' }
    $failReport = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($failReportPath))
    if ($failReport.Result -ne 'FAIL') { throw 'Mismatched cloud revisions did not produce Result=FAIL.' }

    # An old good sample must not mask a later missing/stale sample.
    Write-TestSamplerCsv -Path $csvB -ShopRef 'shop#bbbbbbbbbbbbbbbb' -Revision 17 -LocalFingerprint 'abcd1234abcd1234' -CloudFingerprint 'abcd1234abcd1234'
    $stale = Import-Csv -LiteralPath $csvB | Select-Object -First 1
    $stale.Time = [DateTimeOffset]::UtcNow.ToString('o')
    $stale.KnowledgeV2SyncSnapshotStatus = 'stale'
    $stale | Export-Csv -LiteralPath $csvB -Append -NoTypeInformation -Encoding UTF8
    $staleProcess = Invoke-ComparatorChild -CsvA $csvA -CsvB $csvB -OutputPath (Join-Path $tempRoot 'stale')
    if ($staleProcess.ExitCode -eq 0) { throw 'Latest stale sample was incorrectly accepted.' }

    Copy-Item -LiteralPath $csvA -Destination $csvB -Force
    $duplicateProcess = Invoke-ComparatorChild -CsvA $csvA -CsvB $csvB -OutputPath (Join-Path $tempRoot 'duplicate')
    if ($duplicateProcess.ExitCode -eq 0) { throw 'Identical CSVs were incorrectly accepted as two devices.' }

    Write-TestSamplerCsv -Path $csvB -ShopRef 'shop#bbbbbbbbbbbbbbbb' -Revision 17 -LocalFingerprint 'abcd1234abcd1234' -CloudFingerprint 'abcd1234abcd1234'
    $failed = Import-Csv -LiteralPath $csvB | Select-Object -First 1
    $failed.KnowledgeV2ShopStates = $failed.KnowledgeV2ShopStates.Replace(':synced:', ':credentials_missing:')
    $failed | Export-Csv -LiteralPath $csvB -NoTypeInformation -Encoding UTF8
    $failedProcess = Invoke-ComparatorChild -CsvA $csvA -CsvB $csvB -OutputPath (Join-Path $tempRoot 'failed')
    if ($failedProcess.ExitCode -eq 0) { throw 'A failed last sync was incorrectly accepted.' }

    # Run the actual sampler for one iteration. Process rows must use the system
    # CSV header, including empty diagnostics columns, instead of failing append.
    $samplerPath = Join-Path $PSScriptRoot '..\..\scripts\Collect-QnBotMemory.ps1'
    $samplerOutput = Join-Path $tempRoot 'sampler'
    & $samplerPath -Minutes 1 -IntervalSeconds 300 -TopProcessCount 5 -OutputDirectory $samplerOutput -RuntimeSnapshotPath (Join-Path $tempRoot 'missing-runtime.json') -KnowledgeV2SyncSnapshotPath (Join-Path $tempRoot 'missing-sync.json') -KnowledgeV2LearningSnapshotPath (Join-Path $tempRoot 'missing-learning.json')
    $samplerCsv = Get-ChildItem -LiteralPath $samplerOutput -Filter 'qnbot-memory-*.csv' | Select-Object -First 1 -ExpandProperty FullName
    $samples = @(Import-Csv -LiteralPath $samplerCsv)
    if (@($samples | Where-Object { $_.SampleKind -eq 'system' }).Count -ne 1) { throw 'Expected one system sample.' }
    if (@($samples | Where-Object { $_.SampleKind -in @('target','top') -and $_.Pid -gt 0 -and $_.WorkingSetMiB -ne '' }).Count -lt 1) { throw 'Process samples were not appended to the CSV.' }

    Write-Host 'Knowledge V2 two-device convergence comparator runtime tests passed.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
