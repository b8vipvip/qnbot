$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot '..\..\scripts\Invoke-QnBotAutoDeliveryDiagnostics.ps1'
$scriptPath = (Resolve-Path $scriptPath).Path
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('qnbot-auto-delivery-diag-' + [Guid]::NewGuid().ToString('N'))
$dataDir = Join-Path $tempRoot 'data'
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null

function Write-TestJson {
    param([string]$Path, $Value)
    $json = $Value | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-DiagnosticsChild {
    param([string]$DataPath, [string]$OutputPath)
    $childArguments = @(
        '-NoProfile',
        '-ExecutionPolicy Bypass',
        ('-File "' + $scriptPath + '"'),
        ('-DataDirectory "' + $DataPath + '"'),
        ('-OutputDirectory "' + $OutputPath + '"')
    ) -join ' '
    return Start-Process -FilePath 'powershell.exe' -ArgumentList $childArguments -Wait -PassThru -NoNewWindow
}

function Get-LatestReport {
    param([string]$Path)
    $reportPath = Get-ChildItem -LiteralPath $Path -Filter 'qnbot-auto-delivery-diagnostics-*.json' |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($reportPath)) { throw 'Auto-delivery diagnostics JSON was not created.' }
    return ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($reportPath))
}

try {
    $now = [DateTimeOffset]::UtcNow.ToString('o')
    Write-TestJson -Path (Join-Path $dataDir 'virtual-goods-auto-delivery-queue.json') -Value ([pscustomobject]@{
        Schema = 2
        Pending = @(
            [pscustomobject]@{
                Key = 'confirmation-key-secret'
                Snapshot = [pscustomobject]@{
                    ShopKey = 'shop-key-secret'
                    Seller = 'seller-name-secret'
                    Buyer = 'buyer-name-secret'
                    OrderId = '123456789012345678'
                }
                Attempts = 3
                ConfirmationUncertainCount = 1
                ConfirmationIntentAt = $now
                NextAttemptAt = $now
                ExpiresAt = [DateTimeOffset]::UtcNow.AddHours(12).ToString('o')
            }
        )
    })
    Write-TestJson -Path (Join-Path $dataDir 'virtual-goods-auto-delivery-confirmation-ledger.json') -Value ([pscustomobject]@{
        Schema = 2
        Records = @(
            [pscustomobject]@{ Key = 'confirmation-key-secret'; ShopKey = 'shop-key-secret'; Seller = 'seller-name-secret'; OrderId = '123456789012345678'; IntentAt = $now; Resolution = '' }
            [pscustomobject]@{ Key = 'confirmation-key-secret-2'; ShopKey = 'shop-key-secret'; Seller = 'seller-name-secret'; OrderId = '123456789012345679'; IntentAt = $now; Resolution = 'confirmed' }
        )
    })
    Write-TestJson -Path (Join-Path $dataDir 'virtual-goods-auto-delivery-event-cursor.json') -Value ([pscustomobject]@{
        Schema = 3
        Sellers = @(
            [pscustomobject]@{ ShopKey = 'shop-key-secret'; Seller = 'seller-name-secret'; EnabledObserved = $true; LastSeenAt = $now }
            [pscustomobject]@{ ShopKey = 'shop-key-secret-2'; Seller = 'seller-name-secret-2'; EnabledObserved = $false; LastSeenAt = $now }
        )
    })
    Write-TestJson -Path (Join-Path $dataDir 'order-event-state.json') -Value ([pscustomobject]@{
        Events = @(
            [pscustomobject]@{ Key = 'event-key-secret'; Snapshot = [pscustomobject]@{ OrderId = '123456789012345678'; Buyer = 'buyer-name-secret'; Message = 'buyer-message-secret' } }
            [pscustomobject]@{ Key = 'event-key-secret-2'; Snapshot = [pscustomobject]@{ OrderId = '123456789012345679'; Buyer = 'buyer-name-secret' } }
        )
    })

    $reviewOutput = Join-Path $tempRoot 'review'
    $reviewProcess = Invoke-DiagnosticsChild -DataPath $dataDir -OutputPath $reviewOutput
    if ($reviewProcess.ExitCode -ne 0) { throw ('Expected readable diagnostic state; exitCode=' + $reviewProcess.ExitCode) }
    $review = Get-LatestReport -Path $reviewOutput
    if ($review.Result -ne 'COLLECTED' -or $review.SafetyAssessment -ne 'REVIEW_REQUIRED') {
        throw 'Unresolved confirmation intent did not produce REVIEW_REQUIRED.'
    }
    if ($review.Queue.PendingCount -ne 1 -or $review.Queue.ConfirmationIntentCount -ne 1 -or $review.Queue.UncertainCount -ne 1) {
        throw 'Queue aggregate counts are incorrect.'
    }
    if ($review.ConfirmationLedger.RecordCount -ne 2 -or $review.ConfirmationLedger.ResolvedCount -ne 1 -or $review.ConfirmationLedger.UnresolvedCount -ne 1) {
        throw 'Confirmation ledger aggregate counts are incorrect.'
    }
    if ($review.EventCursor.ShopCount -ne 2 -or $review.EventCursor.EnabledShopCount -ne 1 -or $review.OrderEventHub.EventCount -ne 2) {
        throw 'Cursor or OrderEventHub aggregate counts are incorrect.'
    }

    $reviewJson = Get-Content -LiteralPath (Get-ChildItem -LiteralPath $reviewOutput -Filter 'qnbot-auto-delivery-diagnostics-*.json' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName) -Raw
    foreach ($secret in @('shop-key-secret','seller-name-secret','buyer-name-secret','123456789012345678','buyer-message-secret','confirmation-key-secret')) {
        if ($reviewJson.Contains($secret)) { throw ('Privacy leak in diagnostics report: ' + $secret) }
    }

    Write-TestJson -Path (Join-Path $dataDir 'virtual-goods-auto-delivery-queue.json') -Value ([pscustomobject]@{ Schema = 2; Pending = @() })
    Write-TestJson -Path (Join-Path $dataDir 'virtual-goods-auto-delivery-confirmation-ledger.json') -Value ([pscustomobject]@{
        Schema = 2
        Records = @(
            [pscustomobject]@{ Key = 'confirmation-key-secret'; ShopKey = 'shop-key-secret'; Seller = 'seller-name-secret'; OrderId = '123456789012345678'; IntentAt = $now; Resolution = 'confirmed' }
        )
    })
    $resolvedOutput = Join-Path $tempRoot 'resolved'
    $resolvedProcess = Invoke-DiagnosticsChild -DataPath $dataDir -OutputPath $resolvedOutput
    if ($resolvedProcess.ExitCode -ne 0) { throw ('Expected resolved diagnostic state; exitCode=' + $resolvedProcess.ExitCode) }
    $resolved = Get-LatestReport -Path $resolvedOutput
    if ($resolved.SafetyAssessment -ne 'NO_UNRESOLVED_CONFIRMATION_INTENTS') {
        throw 'Resolved ledger did not produce NO_UNRESOLVED_CONFIRMATION_INTENTS.'
    }

    Remove-Item -LiteralPath (Join-Path $dataDir 'order-event-state.json') -Force
    $incompleteOutput = Join-Path $tempRoot 'incomplete'
    $incompleteProcess = Invoke-DiagnosticsChild -DataPath $dataDir -OutputPath $incompleteOutput
    if ($incompleteProcess.ExitCode -ne 1) { throw ('Expected missing OrderEventHub file to fail closed; exitCode=' + $incompleteProcess.ExitCode) }
    $incomplete = Get-LatestReport -Path $incompleteOutput
    if ($incomplete.Result -ne 'INCOMPLETE') { throw 'Missing required state file did not produce INCOMPLETE.' }

    Write-Host 'Auto-delivery read-only diagnostics runtime tests passed.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
