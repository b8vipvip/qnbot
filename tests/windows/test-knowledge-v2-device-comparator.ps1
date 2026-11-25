$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot '..\..\scripts\Compare-QnBotKnowledgeV2Devices.ps1'
$scriptPath = (Resolve-Path $scriptPath).Path
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('qnbot-v2-device-compare-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

function Write-TestSamplerCsv {
    param([string]$Path, [string]$ShopRef, [int]$Revision, [string]$LocalFingerprint, [string]$CloudFingerprint)
    $row = [pscustomobject]@{
        Time = '2026-11-25T12:00:00+00:00'
        SampleKind = 'system'
        KnowledgeV2SyncSnapshotStatus = 'fresh'
        KnowledgeV2SyncSnapshotAgeSeconds = '5'
        KnowledgeV2ShopStates = ($ShopRef + '=' + $Revision + ':' + $LocalFingerprint + '/' + $CloudFingerprint + ':converged:True')
    }
    $row | Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding UTF8
}

function Invoke-ComparatorChild {
    param([string]$CsvA, [string]$CsvB, [string]$OutputPath)
    $args = @(
        '-NoProfile',
        '-ExecutionPolicy Bypass',
        ('-File "' + $scriptPath + '"'),
        ('-DeviceACsv "' + $CsvA + '"'),
        ('-DeviceBCsv "' + $CsvB + '"'),
        ('-OutputDirectory "' + $OutputPath + '"')
    ) -join ' '
    return Start-Process -FilePath 'powershell.exe' -ArgumentList $args -Wait -PassThru -NoNewWindow
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

    Write-Host 'Knowledge V2 two-device convergence comparator runtime tests passed.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
