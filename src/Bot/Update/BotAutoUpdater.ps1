[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [string]$InstallDir,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedSha256,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory = $true)]
    [int]$CurrentPid
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string]$Message) {
    Write-Host "`n[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message" -ForegroundColor Cyan
}

function Get-FailureDetail($ErrorRecord) {
    if ($null -eq $ErrorRecord) { return 'Unknown updater failure.' }
    $parts = @()
    try {
        if ($null -ne $ErrorRecord.Exception -and -not [string]::IsNullOrWhiteSpace([string]$ErrorRecord.Exception.Message)) {
            $parts += [string]$ErrorRecord.Exception.Message
        }
    }
    catch { }
    try {
        if (-not [string]::IsNullOrWhiteSpace([string]$ErrorRecord.ScriptStackTrace)) {
            $parts += ('stack=' + ([string]$ErrorRecord.ScriptStackTrace -replace '[\r\n]+', ' <- '))
        }
    }
    catch { }
    try {
        if ($null -ne $ErrorRecord.InvocationInfo -and -not [string]::IsNullOrWhiteSpace([string]$ErrorRecord.InvocationInfo.PositionMessage)) {
            $parts += ('position=' + ([string]$ErrorRecord.InvocationInfo.PositionMessage -replace '[\r\n]+', ' '))
        }
    }
    catch { }
    if (@($parts).Count -eq 0) { return 'Unknown updater failure.' }
    return ($parts -join ' | ')
}

function Write-UpdateResult(
    [string]$Path,
    [string]$Status,
    [string]$TargetVersion,
    [string]$Stage,
    [string]$Detail,
    [string]$Rollback,
    [string]$UpdaterLog) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    try {
        $directory = Split-Path -Parent $Path
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }
        $temporary = $Path + '.tmp'
        [ordered]@{
            schema = 2
            status = $Status
            target_version = $TargetVersion
            stage = $Stage
            detail = $Detail
            rollback = $Rollback
            log_path = $UpdaterLog
            created_at = (Get-Date).ToUniversalTime().ToString('o')
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporary -Encoding UTF8
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        }
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    catch { }
}

function Copy-DirectoryContents([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { return }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in @(Get-ChildItem -LiteralPath $Source -Force)) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function Get-DirectoryFingerprint([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return @() }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $prefix = $rootFull + '\'
    [string[]]$entries = @()
    foreach ($item in @(Get-ChildItem -LiteralPath $rootFull -Recurse -Force)) {
        $full = [IO.Path]::GetFullPath($item.FullName)
        $relative = $full.Substring($prefix.Length)
        if ($item.PSIsContainer) {
            $entries += "D|$relative"
        }
        else {
            $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToUpperInvariant()
            $entries += "F|$relative|$([int64]$item.Length)|$hash"
        }
    }
    return @($entries | Sort-Object)
}

function Assert-DirectoryCopyMatches([string]$Source, [string]$Backup, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Backup validation source is missing: $Label ($Source)"
    }
    if (-not (Test-Path -LiteralPath $Backup -PathType Container)) {
        throw "Backup validation destination is missing: $Label ($Backup)"
    }
    $sourceState = @(Get-DirectoryFingerprint $Source)
    $backupState = @(Get-DirectoryFingerprint $Backup)
    if (@($sourceState).Count -ne @($backupState).Count) {
        throw "Backup validation failed for ${Label}: entry count differs ($(@($sourceState).Count) != $(@($backupState).Count))."
    }
    for ($i = 0; $i -lt @($sourceState).Count; $i++) {
        if (-not [string]::Equals([string]$sourceState[$i], [string]$backupState[$i], [StringComparison]::Ordinal)) {
            throw "Backup validation failed for $Label at entry $i."
        }
    }
}

function Get-DirectorySizeBytes([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return [int64]0 }
    [int64]$total = 0
    foreach ($item in @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force -ErrorAction SilentlyContinue)) {
        $total += [int64]$item.Length
    }
    return $total
}

function Get-AvailableBytes([string]$Path) {
    try {
        $root = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Path))
        if ([string]::IsNullOrWhiteSpace($root)) { return [int64]0 }
        return [int64](New-Object IO.DriveInfo($root)).AvailableFreeSpace
    }
    catch {
        return [int64]0
    }
}

function Test-BackupComplete([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if ($Path.EndsWith('.partial', [StringComparison]::OrdinalIgnoreCase)) { return $false }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $false }
    $marker = Join-Path $Path '.complete'
    $manifestPath = Join-Path $Path 'backup-manifest.json'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { return $false }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return $false }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        return ([int]$manifest.schema -eq 1)
    }
    catch {
        return $false
    }
}

function Clear-PreviousUpdaterBackups([string]$BackupRoot) {
    if (-not (Test-Path -LiteralPath $BackupRoot -PathType Container)) { return }
    foreach ($item in @(Get-ChildItem -LiteralPath $BackupRoot -Directory -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop
    }
}

function Restore-PersistentData([string]$CompleteBackupDir, [string]$PersistentRoot) {
    if (-not (Test-BackupComplete $CompleteBackupDir)) {
        throw "Refusing to restore persistent data from an incomplete backup: $CompleteBackupDir"
    }
    $manifest = Get-Content -LiteralPath (Join-Path $CompleteBackupDir 'backup-manifest.json') -Raw | ConvertFrom-Json
    $persistentBackupRoot = Join-Path $CompleteBackupDir 'persistent'
    $allowedNames = @('data', 'global', 'shops')
    foreach ($entry in @($manifest.persistent)) {
        $name = [string]$entry.name
        if ($allowedNames -notcontains $name) {
            throw "Backup manifest contains an unsupported persistent directory: $name"
        }
        $destination = Join-Path $PersistentRoot $name
        $source = Join-Path $persistentBackupRoot $name
        if ([bool]$entry.existed) {
            if (-not (Test-Path -LiteralPath $source -PathType Container)) {
                throw "Completed backup is missing persistent directory: $name"
            }
            if (Test-Path -LiteralPath $destination) {
                Remove-Item -LiteralPath $destination -Recurse -Force
            }
            Copy-DirectoryContents $source $destination
            Assert-DirectoryCopyMatches $source $destination "persistent/$name restore"
        }
        elseif (Test-Path -LiteralPath $destination) {
            Remove-Item -LiteralPath $destination -Recurse -Force
        }
    }
}

function Test-PathUnderInstallRoot([string]$ExecutablePath, [string]$TargetInstallDir) {
    if ([string]::IsNullOrWhiteSpace($ExecutablePath) -or [string]::IsNullOrWhiteSpace($TargetInstallDir)) { return $false }
    try {
        $root = [IO.Path]::GetFullPath($TargetInstallDir).TrimEnd('\') + '\'
        $exe = [IO.Path]::GetFullPath($ExecutablePath)
        return $exe.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

# Sensor only. It reports target-install processes and never decides mutation readiness.
function Get-InstallProcessState([string]$TargetInstallDir) {
    try {
        $records = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
            if ($null -eq $_ -or [int]$_.ProcessId -eq $PID) { return $false }
            return Test-PathUnderInstallRoot ([string]$_.ExecutablePath) $TargetInstallDir
        } | ForEach-Object {
            [pscustomobject]@{
                Pid = [int]$_.ProcessId
                Name = [string]$_.Name
                ExecutablePath = [string]$_.ExecutablePath
            }
        })
        return [pscustomobject]@{ Known = $true; Processes = @($records); Error = '' }
    }
    catch {
        return [pscustomobject]@{ Known = $false; Processes = @(); Error = $_.Exception.Message }
    }
}

function Test-InstallProcessIdAlive([string]$TargetInstallDir, [int]$ProcessId) {
    if ($ProcessId -le 0 -or $ProcessId -eq $PID) { return $false }
    $state = Get-InstallProcessState $TargetInstallDir
    if (-not [bool]$state.Known) { return $false }
    return @($state.Processes | Where-Object { [int]$_.Pid -eq $ProcessId }).Count -gt 0
}

# Sensor only. A live watchdog can reintroduce the old Bot, so it is part of the same canonical authority.
function Get-WatchdogState([string]$TargetInstallDir) {
    if ([string]::IsNullOrWhiteSpace($TargetInstallDir)) {
        return [pscustomobject]@{ Known = $false; Processes = @(); Error = 'Install root is empty.' }
    }
    try {
        $root = [IO.Path]::GetFullPath($TargetInstallDir).TrimEnd('\')
        $records = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
            if ($null -eq $_ -or [int]$_.ProcessId -eq $PID) { return $false }
            $name = [string]$_.Name
            if ($name -ne 'powershell.exe' -and $name -ne 'pwsh.exe') { return $false }
            $command = [string]$_.CommandLine
            if ([string]::IsNullOrWhiteSpace($command)) { return $false }
            return $command.IndexOf('bot-process-watchdog.ps1', [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $command.IndexOf($root, [StringComparison]::OrdinalIgnoreCase) -ge 0
        } | ForEach-Object {
            [pscustomobject]@{
                Pid = [int]$_.ProcessId
                Name = [string]$_.Name
                CommandLine = [string]$_.CommandLine
            }
        })
        return [pscustomobject]@{ Known = $true; Processes = @($records); Error = '' }
    }
    catch {
        return [pscustomobject]@{ Known = $false; Processes = @(); Error = $_.Exception.Message }
    }
}

function Stop-BotProcesses([string]$TargetInstallDir) {
    $state = Get-InstallProcessState $TargetInstallDir
    if (-not [bool]$state.Known) { return }
    foreach ($process in @($state.Processes)) {
        $processId = [int]$process.Pid
        if ($processId -le 0 -or $processId -eq $PID) { continue }
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }
}

function Stop-BotWatchdogs([string]$TargetInstallDir) {
    $state = Get-WatchdogState $TargetInstallDir
    if (-not [bool]$state.Known) { return }
    foreach ($watchdog in @($state.Processes)) {
        $watchdogPid = [int]$watchdog.Pid
        if ($watchdogPid -le 0 -or $watchdogPid -eq $PID) { continue }
        Stop-Process -Id $watchdogPid -Force -ErrorAction SilentlyContinue
    }
}

# Diagnostic sensor only. Exclusive bindability never grants or denies handoff readiness.
function Test-LoopbackPortBindable([int]$Port) {
    $listener = $null
    try {
        $listener = New-Object System.Net.Sockets.TcpListener -ArgumentList @([System.Net.IPAddress]::Loopback, [int]$Port)
        $listener.Server.ExclusiveAddressUse = $true
        $listener.Start()
        return $true
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $listener) {
            try { $listener.Stop() } catch { }
        }
    }
}

# Sensor only. The canonical handoff authority below owns the final Ready decision.
function Get-LoopbackPortListenerState([int]$Port) {
    try {
        $command = Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue
        if ($null -eq $command) {
            return [pscustomobject]@{ Known = $false; Listeners = @(); Error = 'Get-NetTCPConnection unavailable' }
        }
        $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop | Where-Object {
            $_.LocalAddress -eq '127.0.0.1' -or
            $_.LocalAddress -eq '0.0.0.0' -or
            $_.LocalAddress -eq '::1' -or
            $_.LocalAddress -eq '::'
        })
        return [pscustomobject]@{ Known = $true; Listeners = @($listeners); Error = '' }
    }
    catch {
        return [pscustomobject]@{ Known = $false; Listeners = @(); Error = $_.Exception.Message }
    }
}

# Known=true/Exists=false means the OS listener row points at a dead PID.
function Get-LiveProcessStateById([int]$ProcessId) {
    if ($ProcessId -le 0) {
        return [pscustomobject]@{ Known = $true; Exists = $false; Process = $null; Error = '' }
    }
    try {
        $records = @(Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop)
        if (@($records).Count -eq 0) {
            return [pscustomobject]@{ Known = $true; Exists = $false; Process = $null; Error = '' }
        }
        $record = $records[0]
        return [pscustomobject]@{
            Known = $true
            Exists = $true
            Process = [pscustomobject]@{
                Pid = [int]$record.ProcessId
                Name = [string]$record.Name
                ExecutablePath = [string]$record.ExecutablePath
            }
            Error = ''
        }
    }
    catch {
        return [pscustomobject]@{ Known = $false; Exists = $false; Process = $null; Error = $_.Exception.Message }
    }
}

# SINGLE AUTHORITY for updater handoff. All sensors report facts; only this function owns Ready.
function Get-BotWebSocketHandoffState([string]$TargetInstallDir, [int]$Port) {
    $installState = Get-InstallProcessState $TargetInstallDir
    $watchdogState = Get-WatchdogState $TargetInstallDir
    $listenerState = Get-LoopbackPortListenerState $Port

    [object[]]$liveInstallProcesses = @()
    [object[]]$liveWatchdogs = @()
    [object[]]$liveOwners = @()
    [int[]]$staleOwnerPids = @()
    [string[]]$ownerResolutionErrors = @()
    $ownerResolutionKnown = $true

    if ([bool]$installState.Known) {
        $liveInstallProcesses = @($installState.Processes)
    }
    if ([bool]$watchdogState.Known) {
        $liveWatchdogs = @($watchdogState.Processes)
    }
    if ([bool]$listenerState.Known) {
        foreach ($entry in @($listenerState.Listeners)) {
            $ownerPid = 0
            try { $ownerPid = [int]$entry.OwningProcess } catch { $ownerPid = 0 }
            $ownerState = Get-LiveProcessStateById $ownerPid
            if (-not [bool]$ownerState.Known) {
                $ownerResolutionKnown = $false
                $ownerResolutionErrors += "pid=${ownerPid}:$($ownerState.Error)"
                continue
            }
            if (-not [bool]$ownerState.Exists) {
                $staleOwnerPids += $ownerPid
                continue
            }
            $liveOwners += $ownerState.Process
        }
    }

    $sensorKnown = [bool]$installState.Known -and [bool]$watchdogState.Known -and [bool]$listenerState.Known -and $ownerResolutionKnown
    $ready = $sensorKnown -and @($liveInstallProcesses).Count -eq 0 -and @($liveWatchdogs).Count -eq 0 -and @($liveOwners).Count -eq 0

    # Bindability is recorded for diagnosis only. It never participates in $ready.
    $strictBindable = Test-LoopbackPortBindable $Port

    [string[]]$parts = @()
    if (-not [bool]$installState.Known) { $parts += "install-process-state=unknown:$($installState.Error)" }
    elseif (@($liveInstallProcesses).Count -gt 0) {
        $parts += 'install-processes=' + (($liveInstallProcesses | ForEach-Object { "pid=$($_.Pid),name=$($_.Name)" }) -join ';')
    }
    else { $parts += 'install-processes=none' }

    if (-not [bool]$watchdogState.Known) { $parts += "watchdog-state=unknown:$($watchdogState.Error)" }
    elseif (@($liveWatchdogs).Count -gt 0) {
        $parts += 'watchdogs=' + (($liveWatchdogs | ForEach-Object { "pid=$($_.Pid),name=$($_.Name)" }) -join ';')
    }
    else { $parts += 'watchdogs=none' }

    if (-not [bool]$listenerState.Known) { $parts += "listener-state=unknown:$($listenerState.Error)" }
    elseif (@($liveOwners).Count -gt 0) {
        $parts += 'live-listener-owners=' + (($liveOwners | ForEach-Object { "pid=$($_.Pid),name=$($_.Name),exe=$($_.ExecutablePath)" }) -join ';')
    }
    else { $parts += 'live-listener-owners=none' }

    if (@($staleOwnerPids).Count -gt 0) {
        $parts += 'stale-listener-pids=' + (($staleOwnerPids | Sort-Object -Unique) -join ',')
    }
    if (@($ownerResolutionErrors).Count -gt 0) {
        $parts += 'owner-resolution-errors=' + ($ownerResolutionErrors -join ';')
    }
    $parts += 'strict-bind-diagnostic=' + ($(if ($strictBindable) { 'bindable' } else { 'blocked' }))

    return [pscustomobject]@{
        Ready = [bool]$ready
        SensorStateKnown = [bool]$sensorKnown
        LiveInstallProcesses = @($liveInstallProcesses)
        LiveWatchdogs = @($liveWatchdogs)
        LiveListenerOwners = @($liveOwners)
        StaleListenerPids = @($staleOwnerPids | Sort-Object -Unique)
        StrictBindableDiagnostic = [bool]$strictBindable
        Summary = ($parts -join ' | ')
    }
}

function Wait-BotWebSocketPortRelease([string]$TargetInstallDir, [int]$Port, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds([Math]::Max(5, $TimeoutSeconds))
    $attempt = 0
    $lastSummary = ''
    while ((Get-Date) -lt $deadline) {
        $attempt++
        Stop-BotWatchdogs $TargetInstallDir
        Stop-BotProcesses $TargetInstallDir
        Start-Sleep -Milliseconds 200

        # There is exactly one yes/no authority. Actuators above never decide readiness.
        $handoff = Get-BotWebSocketHandoffState $TargetInstallDir $Port
        if ([bool]$handoff.Ready) {
            Write-Host "Bot updater handoff ready: $($handoff.Summary); probe=$attempt" -ForegroundColor Green
            return $true
        }
        if ($attempt -eq 1 -or $handoff.Summary -ne $lastSummary -or ($attempt % 5) -eq 0) {
            Write-Host "Bot updater handoff waiting: $($handoff.Summary); probe=$attempt" -ForegroundColor Yellow
            $lastSummary = [string]$handoff.Summary
        }
        Start-Sleep -Milliseconds ([Math]::Min(1500, 250 + ($attempt * 125)))
    }
    return $false
}

function Clear-DirectoryContentsWithRetry([string]$Path, [int]$MaxAttempts = 24) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        foreach ($item in @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue)) {
            try { Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop } catch { }
        }
        $remaining = @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue)
        if (@($remaining).Count -eq 0) { return }
        Start-Sleep -Milliseconds ([Math]::Min(1500, 250 + ($attempt * 75)))
    }
    throw "Unable to clear install directory contents after $MaxAttempts attempts: $Path"
}

function Test-BotHealthy([string]$ExpectedExe, [int]$ExpectedPid, [string]$HealthFile, [string]$ExpectedReleaseVersion) {
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        $process = Get-Process -Id $ExpectedPid -ErrorAction SilentlyContinue
        if ($null -eq $process) { return $false }
        $pathMatches = $false
        try {
            $pathMatches = $process.Path -and ([IO.Path]::GetFullPath($process.Path) -ieq [IO.Path]::GetFullPath($ExpectedExe))
        }
        catch { $pathMatches = $false }
        if (-not $pathMatches) { return $false }

        if (Test-Path -LiteralPath $HealthFile -PathType Leaf) {
            try {
                $health = Get-Content -LiteralPath $HealthFile -Raw | ConvertFrom-Json
                if ([string]$health.status -eq 'OK' -and
                    [int]$health.pid -eq $ExpectedPid -and
                    [string]$health.release_version -eq $ExpectedReleaseVersion -and
                    [bool]$health.database_initialized -and
                    [bool]$health.configuration_loaded -and
                    [bool]$health.services_started) {
                    return $true
                }
            }
            catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$ExpectedSha256 = $ExpectedSha256.Trim().ToUpperInvariant()

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$updaterRoot = Join-Path $env:LOCALAPPDATA 'QianniuAiBotUpdater'
$backupRoot = Join-Path $updaterRoot 'backups'
$backupDir = Join-Path $backupRoot $timestamp
$partialBackupDir = "$backupDir.partial"
$programBackup = Join-Path $backupDir 'program'
$persistentRoot = Join-Path $env:LOCALAPPDATA 'QianniuAiBot'
$persistentNames = @('data', 'global', 'shops')
$migrationMarker = Join-Path $persistentRoot 'data-migration-v2.done'
$tempDir = Join-Path $env:TEMP "qianniu-bot-auto-update-$timestamp"
$logDir = Join-Path $updaterRoot 'logs'
$logPath = Join-Path $logDir "auto-update-$timestamp.log"
$resultPath = Join-Path $updaterRoot 'last-update-result.json'
$oldProgramExisted = Test-Path -LiteralPath $InstallDir -PathType Container
$oldExe = Join-Path $InstallDir 'Bin\Bot.exe'
$backupFinalized = $false
$installMutationStarted = $false
$healthFile = Join-Path $tempDir 'startup-health.json'
$updaterMutex = $null
$ownsUpdaterMutex = $false
$stage = 'preflight'

New-Item -ItemType Directory -Path $logDir -Force | Out-Null
try { Start-Transcript -Path $logPath -Force | Out-Null } catch { }

try {
    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw "Update package does not exist: $PackagePath" }
    if (Test-Path -LiteralPath (Join-Path $InstallDir '.git')) { throw "Refusing to overwrite a Git source repository: $InstallDir" }
    $actualHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $ExpectedSha256) { throw "SHA256 verification failed. Expected $ExpectedSha256, actual $actualHash" }

    $createdNew = $false
    $updaterMutex = New-Object System.Threading.Mutex($true, 'Global\QianniuAiBotUpdater', [ref]$createdNew)
    if (-not $createdNew) { throw 'Another Qianniu AI Bot updater is already running.' }
    $ownsUpdaterMutex = $true

    $stage = 'wait-current-bot-exit'
    Write-Step "Waiting briefly for target-install Bot PID=$CurrentPid to exit normally"
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-InstallProcessIdAlive $InstallDir $CurrentPid)) { break }
        Start-Sleep -Milliseconds 250
    }
    if (Test-InstallProcessIdAlive $InstallDir $CurrentPid) {
        Stop-Process -Id $CurrentPid -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }

    $stage = 'pre-mutation-port-handoff'
    Write-Step 'Confirming canonical Bot WebSocket/process handoff before any install mutation'
    if (-not (Wait-BotWebSocketPortRelease $InstallDir 41010 60)) {
        $handoff = Get-BotWebSocketHandoffState $InstallDir 41010
        throw "Canonical updater handoff did not become ready before backup/mutation ($($handoff.Summary)). Existing program was not replaced."
    }

    $stage = 'rollback-backup'
    Write-Step 'Preparing bounded rollback backup'
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    Clear-PreviousUpdaterBackups $backupRoot

    [int64]$estimatedBackupBytes = 0
    if ($oldProgramExisted) { $estimatedBackupBytes += Get-DirectorySizeBytes $InstallDir }
    foreach ($name in $persistentNames) { $estimatedBackupBytes += Get-DirectorySizeBytes (Join-Path $persistentRoot $name) }
    [int64]$availableBytes = Get-AvailableBytes $backupRoot
    if ($availableBytes -gt 0 -and $availableBytes -lt ($estimatedBackupBytes + 512MB)) {
        throw "Insufficient disk space for validated rollback snapshot. Required headroom is 512 MB plus current program/persistent data."
    }

    if (Test-Path -LiteralPath $partialBackupDir) { Remove-Item -LiteralPath $partialBackupDir -Recurse -Force }
    New-Item -ItemType Directory -Path $partialBackupDir -Force | Out-Null

    if ($oldProgramExisted) {
        $partialProgramBackup = Join-Path $partialBackupDir 'program'
        Copy-DirectoryContents $InstallDir $partialProgramBackup
        Assert-DirectoryCopyMatches $InstallDir $partialProgramBackup 'program'
    }

    $persistentEntries = @()
    $partialPersistentRoot = Join-Path $partialBackupDir 'persistent'
    foreach ($name in $persistentNames) {
        $source = Join-Path $persistentRoot $name
        $existed = Test-Path -LiteralPath $source -PathType Container
        $persistentEntries += [pscustomobject]@{ name = $name; existed = [bool]$existed }
        if (-not $existed) { continue }
        $destination = Join-Path $partialPersistentRoot $name
        Copy-DirectoryContents $source $destination
        Assert-DirectoryCopyMatches $source $destination "persistent/$name"
    }

    [ordered]@{
        schema = 1
        created_at = (Get-Date).ToUniversalTime().ToString('o')
        install_dir = $InstallDir
        old_program_existed = [bool]$oldProgramExisted
        persistent_root = $persistentRoot
        persistent = $persistentEntries
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $partialBackupDir 'backup-manifest.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $partialBackupDir '.complete') -Value 'validated' -Encoding ASCII
    Move-Item -LiteralPath $partialBackupDir -Destination $backupDir
    if (-not (Test-BackupComplete $backupDir)) { throw "Backup finalization failed: $backupDir" }
    $backupFinalized = $true

    $stage = 'package-validation'
    Write-Step 'Extracting and validating package'
    if (Test-Path -LiteralPath $tempDir) { Remove-Item -LiteralPath $tempDir -Recurse -Force }
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $tempDir -Force

    $packageRoot = $tempDir
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'Bin\Bot.exe'))) {
        $roots = @(Get-ChildItem -LiteralPath $tempDir -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Bin\Bot.exe') })
        if (@($roots).Count -ne 1) { throw 'Invalid package layout: expected exactly one Bin\Bot.exe.' }
        $packageRoot = $roots[0].FullName
    }
    $newExe = Join-Path $packageRoot 'Bin\Bot.exe'
    $releaseInfoPath = Join-Path $packageRoot 'release-info.json'
    if (-not (Test-Path -LiteralPath $newExe -PathType Leaf)) { throw "Package does not contain Bot.exe: $newExe" }
    if (-not (Test-Path -LiteralPath $releaseInfoPath -PathType Leaf)) { throw 'Package does not contain release-info.json.' }
    $releaseInfo = Get-Content -LiteralPath $releaseInfoPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$releaseInfo.version) -or ([string]$releaseInfo.version -ne $ExpectedVersion)) {
        throw "Package version mismatch. Expected $ExpectedVersion, actual $($releaseInfo.version)"
    }

    $legacyData = Join-Path $InstallDir 'data'
    if ((Test-Path -LiteralPath $legacyData -PathType Container) -and -not (Test-Path -LiteralPath $migrationMarker -PathType Leaf)) {
        Copy-DirectoryContents $legacyData (Join-Path $packageRoot 'data')
    }

    $stage = 'mutation-commit-handoff'
    Write-Step 'Reconfirming the same canonical handoff authority at the mutation commit point'
    if (-not (Wait-BotWebSocketPortRelease $InstallDir 41010 30)) {
        $handoff = Get-BotWebSocketHandoffState $InstallDir 41010
        throw "Canonical updater handoff was lost before program replacement ($($handoff.Summary)). Existing program was not replaced."
    }

    $stage = 'replace-program'
    Write-Step 'Replacing program files'
    if (-not $backupFinalized -or -not (Test-BackupComplete $backupDir)) {
        throw 'Refusing to replace program files because no finalized validated backup is available.'
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $installMutationStarted = $true
    Clear-DirectoryContentsWithRetry $InstallDir
    Copy-DirectoryContents $packageRoot $InstallDir

    $installedExe = Join-Path $InstallDir 'Bin\Bot.exe'
    $installedReleaseInfo = Join-Path $InstallDir 'release-info.json'
    if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) { throw 'Installed package validation failed: Bin\Bot.exe was not found.' }
    if (-not (Test-Path -LiteralPath $installedReleaseInfo -PathType Leaf)) { throw 'Installed package validation failed: release-info.json was not found.' }
    $installedInfo = Get-Content -LiteralPath $installedReleaseInfo -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$installedInfo.version) -or ([string]$installedInfo.version -ne $ExpectedVersion)) {
        throw "Installed package version mismatch. Expected $ExpectedVersion, actual $($installedInfo.version)"
    }

    $stage = 'post-mutation-port-handoff'
    Write-Step 'Reconfirming the same canonical handoff authority before target start'
    if (-not (Wait-BotWebSocketPortRelease $InstallDir 41010 30)) {
        $handoff = Get-BotWebSocketHandoffState $InstallDir 41010
        throw "Canonical updater handoff became not-ready after replacement ($($handoff.Summary)). Automatic rollback will start."
    }

    $stage = 'target-startup-health'
    Write-Step 'Starting and validating new Bot.exe'
    $env:QIANNIU_BOT_UPDATE_HEALTH_FILE = $healthFile
    $env:QIANNIU_BOT_UPDATE_EXPECTED_VERSION = $ExpectedVersion
    $newBot = Start-Process -FilePath $installedExe -WorkingDirectory (Split-Path -Parent $installedExe) -PassThru
    Remove-Item Env:\QIANNIU_BOT_UPDATE_HEALTH_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:\QIANNIU_BOT_UPDATE_EXPECTED_VERSION -ErrorAction SilentlyContinue
    if ($null -eq $newBot) { throw 'New Bot.exe process could not be created. Automatic rollback will start.' }
    if (-not (Test-BotHealthy $installedExe $newBot.Id $healthFile $ExpectedVersion)) {
        throw "New Bot.exe did not report version-bound database/configuration/service health for $ExpectedVersion. Automatic rollback will start."
    }

    $stage = 'completed'
    Write-Step "Update to $ExpectedVersion completed successfully"
    Write-UpdateResult $resultPath 'success' $ExpectedVersion $stage 'Target version passed version-bound startup health.' 'not-needed' $logPath
}
catch {
    $failure = $_
    $failureDetail = Get-FailureDetail $failure
    Write-Host "`nUpdate failed: $failureDetail" -ForegroundColor Red

    $rollbackSucceeded = $false
    $backupUsable = $backupFinalized -and (Test-BackupComplete $backupDir)
    if (-not $installMutationStarted) {
        $rollbackSucceeded = $true
    }
    elseif ($backupUsable) {
        try {
            Stop-BotWatchdogs $InstallDir
            Stop-BotProcesses $InstallDir
            New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
            Clear-DirectoryContentsWithRetry $InstallDir
            if ($oldProgramExisted) {
                if (-not (Test-Path -LiteralPath $programBackup -PathType Container)) {
                    throw "Completed backup is missing the program directory: $programBackup"
                }
                Copy-DirectoryContents $programBackup $InstallDir
                Assert-DirectoryCopyMatches $programBackup $InstallDir 'program restore'
            }
            Restore-PersistentData $backupDir $persistentRoot
            $rollbackSucceeded = $true
        }
        catch {
            $failureDetail += ' | rollback=' + (Get-FailureDetail $_)
        }
    }

    if ($oldProgramExisted -and $rollbackSucceeded -and (Test-Path -LiteralPath $oldExe -PathType Leaf)) {
        Start-Process -FilePath $oldExe -WorkingDirectory (Split-Path -Parent $oldExe)
    }

    $rollbackText = if ($rollbackSucceeded) { 'completed' } else { 'failed' }
    Write-UpdateResult $resultPath 'failed' $ExpectedVersion $stage $failureDetail $rollbackText $logPath
    Start-Sleep -Seconds 4
    throw $failure
}
finally {
    Remove-Item Env:\QIANNIU_BOT_UPDATE_HEALTH_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:\QIANNIU_BOT_UPDATE_EXPECTED_VERSION -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $partialBackupDir) { Remove-Item -LiteralPath $partialBackupDir -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $tempDir) { Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue }
    try { Stop-Transcript | Out-Null } catch { }
    if ($ownsUpdaterMutex -and $null -ne $updaterMutex) { try { $updaterMutex.ReleaseMutex() } catch { } }
    if ($null -ne $updaterMutex) { $updaterMutex.Dispose() }
    try { Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue } catch { }
}
