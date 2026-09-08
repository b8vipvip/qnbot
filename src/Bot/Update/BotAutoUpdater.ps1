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
            schema = 1
            status = $Status
            target_version = $TargetVersion
            stage = $Stage
            detail = $Detail
            rollback = $Rollback
            log_path = $UpdaterLog
            created_at = (Get-Date).ToUniversalTime().ToString('o')
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporary -Encoding UTF8
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
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Get-DirectoryFingerprint([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return @() }

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $prefix = $rootFull + '\'
    $entries = @()
    foreach ($item in @(Get-ChildItem -LiteralPath $rootFull -Recurse -Force)) {
        $full = [IO.Path]::GetFullPath($item.FullName)
        $relative = $full.Substring($prefix.Length)
        if ($item.PSIsContainer) {
            $entries += "D|$relative"
            continue
        }

        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToUpperInvariant()
        $entries += "F|$relative|$([int64]$item.Length)|$hash"
    }
    return @($entries | Sort-Object)
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
    $root = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Path))
    if ([string]::IsNullOrWhiteSpace($root)) { return [int64]0 }
    try {
        return [int64](New-Object IO.DriveInfo($root)).AvailableFreeSpace
    }
    catch {
        return [int64]0
    }
}

function Format-Bytes([int64]$Bytes) {
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N2} MB' -f ($Bytes / 1MB)) }
    return ('{0:N0} bytes' -f $Bytes)
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
    if ($sourceState.Count -ne $backupState.Count) {
        throw "Backup validation failed for ${Label}: entry count differs ($($sourceState.Count) != $($backupState.Count))."
    }

    for ($i = 0; $i -lt $sourceState.Count; $i++) {
        if (-not [string]::Equals([string]$sourceState[$i], [string]$backupState[$i], [StringComparison]::Ordinal)) {
            throw "Backup validation failed for $Label at entry $i."
        }
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
        try {
            Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop
            Write-Host "Removed previous updater backup: $($item.Name)"
        }
        catch {
            throw "Unable to remove previous updater backup before creating a new snapshot: $($item.FullName). $($_.Exception.Message)"
        }
    }
}

function Restore-PersistentData([string]$CompleteBackupDir, [string]$PersistentRoot) {
    if (-not (Test-BackupComplete $CompleteBackupDir)) {
        throw "Refusing to restore persistent data from an incomplete backup: $CompleteBackupDir"
    }

    $manifestPath = Join-Path $CompleteBackupDir 'backup-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
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

# Sensor only. This function never decides whether install mutation is allowed.
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
        return [pscustomobject]@{ Known = $true; Processes = @($records) }
    }
    catch {
        return [pscustomobject]@{ Known = $false; Processes = @(); Error = $_.Exception.Message }
    }
}

function Get-InstallProcessIds([string]$TargetInstallDir) {
    $state = Get-InstallProcessState $TargetInstallDir
    if (-not [bool]$state.Known) { return @() }
    return @($state.Processes | ForEach-Object { [int]$_.Pid } | Sort-Object -Unique)
}

function Test-InstallProcessIdAlive([string]$TargetInstallDir, [int]$ProcessId) {
    if ($ProcessId -le 0 -or $ProcessId -eq $PID) { return $false }
    $state = Get-InstallProcessState $TargetInstallDir
    if (-not [bool]$state.Known) { return $false }
    return @($state.Processes | Where-Object { [int]$_.Pid -eq $ProcessId }).Count -gt 0
}

function Stop-BotProcesses([string]$TargetInstallDir) {
    $ids = @(Get-InstallProcessIds $TargetInstallDir)
    foreach ($id in $ids) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        Write-Host "Stopping target-install process PID=$id Name=$($process.ProcessName)"
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }

    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline) {
        $state = Get-InstallProcessState $TargetInstallDir
        if ([bool]$state.Known -and @($state.Processes).Count -eq 0) { return }
        Start-Sleep -Milliseconds 300
    }

    foreach ($id in @(Get-InstallProcessIds $TargetInstallDir)) {
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 700
}

function Stop-BotWatchdogs([string]$TargetInstallDir) {
    if ([string]::IsNullOrWhiteSpace($TargetInstallDir)) { return }
    $root = [IO.Path]::GetFullPath($TargetInstallDir).TrimEnd('\')
    try {
        $watchdogs = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            if ($null -eq $_ -or [int]$_.ProcessId -eq $PID) { return $false }
            $name = [string]$_.Name
            if ($name -ne 'powershell.exe' -and $name -ne 'pwsh.exe') { return $false }
            $command = [string]$_.CommandLine
            if ([string]::IsNullOrWhiteSpace($command)) { return $false }
            return $command.IndexOf('bot-process-watchdog.ps1', [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $command.IndexOf($root, [StringComparison]::OrdinalIgnoreCase) -ge 0
        })
        foreach ($watchdog in $watchdogs) {
            $watchdogPid = [int]$watchdog.ProcessId
            Write-Host "Stopping update-handoff watchdog PID=$watchdogPid before program replacement."
            Stop-Process -Id $watchdogPid -Force -ErrorAction SilentlyContinue
        }
        if ($watchdogs.Count -gt 0) { Start-Sleep -Milliseconds 500 }
    }
    catch {
        Write-Host "Unable to stop target-install watchdogs: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# Diagnostic sensor only. Exclusive bindability is not allowed to grant or deny handoff readiness.
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

# Resolve one PID without guessing. Known=true/Exists=false means the OS listener row points at a dead PID.
function Get-LiveProcessStateById([int]$ProcessId) {
    if ($ProcessId -le 0) {
        return [pscustomobject]@{ Known = $true; Exists = $false; Process = $null; Error = '' }
    }
    try {
        $records = @(Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop)
        if ($records.Count -eq 0) {
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

# SINGLE AUTHORITY for the updater's port/process handoff state.
# All sensors feed this function; no caller may independently combine their conditions to decide mutation readiness.
function Get-BotWebSocketHandoffState([string]$TargetInstallDir, [int]$Port) {
    $installState = Get-InstallProcessState $TargetInstallDir
    $listenerState = Get-LoopbackPortListenerState $Port
    $liveOwners = @()
    $staleOwnerPids = @()
    $ownerResolutionKnown = $true
    $ownerResolutionErrors = @()

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

    $liveInstallProcesses = if ([bool]$installState.Known) { @($installState.Processes) } else { @() }
    $sensorKnown = [bool]$installState.Known -and [bool]$listenerState.Known -and $ownerResolutionKnown
    $ready = $sensorKnown -and $liveInstallProcesses.Count -eq 0 -and $liveOwners.Count -eq 0

    # Bindability is recorded for diagnosis only. It never participates in $ready.
    $strictBindable = Test-LoopbackPortBindable $Port

    $parts = @()
    if (-not [bool]$installState.Known) { $parts += "install-process-state=unknown:$($installState.Error)" }
    elseif ($liveInstallProcesses.Count -gt 0) {
        $parts += 'install-processes=' + (($liveInstallProcesses | ForEach-Object { "pid=$($_.Pid),name=$($_.Name)" }) -join ';')
    }
    else { $parts += 'install-processes=none' }

    if (-not [bool]$listenerState.Known) { $parts += "listener-state=unknown:$($listenerState.Error)" }
    elseif ($liveOwners.Count -gt 0) {
        $parts += 'live-listener-owners=' + (($liveOwners | ForEach-Object { "pid=$($_.Pid),name=$($_.Name),exe=$($_.ExecutablePath)" }) -join ';')
    }
    else { $parts += 'live-listener-owners=none' }

    if ($staleOwnerPids.Count -gt 0) {
        $parts += 'stale-listener-pids=' + (($staleOwnerPids | Sort-Object -Unique) -join ',')
    }
    if ($ownerResolutionErrors.Count -gt 0) {
        $parts += 'owner-resolution-errors=' + ($ownerResolutionErrors -join ';')
    }
    $parts += 'strict-bind-diagnostic=' + ($(if ($strictBindable) { 'bindable' } else { 'blocked' }))

    return [pscustomobject]@{
        Ready = [bool]$ready
        SensorStateKnown = [bool]$sensorKnown
        LiveInstallProcesses = @($liveInstallProcesses)
        LiveListenerOwners = @($liveOwners)
        StaleListenerPids = @($staleOwnerPids | Sort-Object -Unique)
        StrictBindableDiagnostic = [bool]$strictBindable
        Summary = ($parts -join ' | ')
    }
}

function Wait-BotWebSocketPortRelease([string]$TargetInstallDir, [int]$Port, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds([Math]::Max(5, $TimeoutSeconds))
    $attempt = 0
    $lastSummary = $null

    while ((Get-Date) -lt $deadline) {
        $attempt++
        Stop-BotWatchdogs $TargetInstallDir
        Stop-BotProcesses $TargetInstallDir

        # There is exactly one yes/no authority. Do not duplicate listener/process decision logic here.
        $handoff = Get-BotWebSocketHandoffState $TargetInstallDir $Port
        if ([bool]$handoff.Ready) {
            if (@($handoff.StaleListenerPids).Count -gt 0) {
                Write-Host "Bot WebSocket handoff ready: stale OS LISTEN row(s) from dead PID(s) were ignored by the canonical authority; $($handoff.Summary); probe=$attempt" -ForegroundColor Yellow
            }
            else {
                Write-Host "Bot WebSocket handoff ready: canonical authority reports no live target-install process and no live LISTEN owner; $($handoff.Summary); probe=$attempt" -ForegroundColor Green
            }
            return $true
        }

        if ($attempt -eq 1 -or $handoff.Summary -ne $lastSummary -or ($attempt % 5) -eq 0) {
            Write-Host "Bot WebSocket handoff waiting: canonical authority is not ready; $($handoff.Summary); probe=$attempt" -ForegroundColor Yellow
            $lastSummary = $handoff.Summary
        }
        Start-Sleep -Milliseconds ([Math]::Min(1500, 250 + ($attempt * 125)))
    }

    return $false
}

function Get-PossibleDirectoryBlockers([string]$Path) {
    $needle = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    try {
        return @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            if ($null -eq $_ -or [int]$_.ProcessId -eq $PID) { return $false }
            $exe = [string]$_.ExecutablePath
            $command = [string]$_.CommandLine
            return ($exe.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) -or
                ($command.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        } | ForEach-Object {
            "PID=$($_.ProcessId) Name=$($_.Name) Exe=$($_.ExecutablePath)"
        })
    }
    catch {
        return @()
    }
}

function Clear-DirectoryContentsWithRetry([string]$Path, [int]$MaxAttempts = 24) {
    if (-not (Test-Path -LiteralPath $Path)) { return }

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        Stop-BotWatchdogs $Path
        Stop-BotProcesses $Path
        $failures = @()
        foreach ($item in @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue)) {
            try {
                Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop
            }
            catch {
                $failures += "$($item.FullName): $($_.Exception.Message)"
            }
        }

        $remaining = @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue)
        if ($remaining.Count -eq 0) { return }

        $summary = if ($failures.Count -gt 0) { $failures -join ' | ' } else { ($remaining.FullName -join ' | ') }
        Write-Host "Install files are still busy; retry $attempt/$MaxAttempts. $summary" -ForegroundColor Yellow
        Start-Sleep -Milliseconds ([Math]::Min(1500, 250 + ($attempt * 75)))
    }

    $blockers = @(Get-PossibleDirectoryBlockers $Path)
    $detail = if ($blockers.Count -gt 0) { $blockers -join '; ' } else { 'No executable-path blocker was found. Close terminals, Explorer windows, antivirus scans, or other programs using this directory.' }
    throw "Unable to clear install directory contents after $MaxAttempts attempts: $Path. $detail"
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
if (-not (Test-Path -LiteralPath $PackagePath)) { throw "Update package does not exist: $PackagePath" }
if (Test-Path -LiteralPath (Join-Path $InstallDir '.git')) { throw "Refusing to overwrite a Git source repository: $InstallDir" }

$actualHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualHash -ne $ExpectedSha256) { throw "SHA256 verification failed. Expected $ExpectedSha256, actual $actualHash" }

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
    $createdNew = $false
    $updaterMutex = New-Object System.Threading.Mutex($true, 'Global\QianniuAiBotUpdater', [ref]$createdNew)
    if (-not $createdNew) { throw 'Another Qianniu AI Bot updater is already running.' }
    $ownsUpdaterMutex = $true

    $stage = 'wait-current-bot-exit'
    Write-Step "Waiting for target-install Bot PID=$CurrentPid to exit"
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-InstallProcessIdAlive $InstallDir $CurrentPid)) { break }
        Start-Sleep -Milliseconds 350
    }
    if (Test-InstallProcessIdAlive $InstallDir $CurrentPid) {
        Write-Host 'Target-install Bot did not exit in time; stopping it now.' -ForegroundColor Yellow
        Stop-Process -Id $CurrentPid -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }
    Stop-BotWatchdogs $InstallDir
    Stop-BotProcesses $InstallDir

    $stage = 'pre-mutation-port-handoff'
    Write-Step 'Confirming canonical Bot WebSocket/process handoff before any install mutation'
    if (-not (Wait-BotWebSocketPortRelease $InstallDir 41010 60)) {
        $handoff = Get-BotWebSocketHandoffState $InstallDir 41010
        throw "Bot WebSocket/process handoff authority is not ready before install mutation ($($handoff.Summary)). Existing program was not replaced."
    }

    Write-Step "Preparing Bot update to version $ExpectedVersion"
    Write-Host "Package: $PackagePath"
    Write-Host "SHA256: $actualHash"
    Write-Host "Install directory: $InstallDir"
    Write-Host "Persistent root: $persistentRoot (data/global/shops)"
    Write-Host "Log: $logPath"

    $stage = 'rollback-backup'
    Write-Step 'Preparing bounded rollback backup'
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    Clear-PreviousUpdaterBackups $backupRoot

    [int64]$estimatedBackupBytes = 0
    if ($oldProgramExisted) { $estimatedBackupBytes += Get-DirectorySizeBytes $InstallDir }
    foreach ($name in $persistentNames) { $estimatedBackupBytes += Get-DirectorySizeBytes (Join-Path $persistentRoot $name) }
    [int64]$backupHeadroomBytes = 512MB
    [int64]$availableBytes = Get-AvailableBytes $backupRoot
    Write-Host "Rollback snapshot estimate: $(Format-Bytes $estimatedBackupBytes); free space after stale-backup cleanup: $(Format-Bytes $availableBytes)"
    if ($availableBytes -gt 0 -and $availableBytes -lt ($estimatedBackupBytes + $backupHeadroomBytes)) {
        throw "Insufficient disk space for validated rollback snapshot. Need approximately $(Format-Bytes ($estimatedBackupBytes + $backupHeadroomBytes)), available $(Format-Bytes $availableBytes). Install directory has not been modified."
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

    $manifest = [ordered]@{
        schema = 1
        created_at = (Get-Date).ToUniversalTime().ToString('o')
        install_dir = $InstallDir
        old_program_existed = [bool]$oldProgramExisted
        persistent_root = $persistentRoot
        persistent = $persistentEntries
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $partialBackupDir 'backup-manifest.json') -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $partialBackupDir '.complete') -Value 'validated' -Encoding ASCII
    Move-Item -LiteralPath $partialBackupDir -Destination $backupDir
    if (-not (Test-BackupComplete $backupDir)) { throw "Backup finalization failed: $backupDir" }
    $backupFinalized = $true
    Write-Host "Validated rollback snapshot: $backupDir" -ForegroundColor Green

    $stage = 'package-validation'
    Write-Step 'Extracting and validating package'
    if (Test-Path -LiteralPath $tempDir) { Remove-Item -LiteralPath $tempDir -Recurse -Force }
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $tempDir -Force

    $packageRoot = $tempDir
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'Bin\Bot.exe'))) {
        $roots = @(Get-ChildItem -LiteralPath $tempDir -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Bin\Bot.exe') })
        if ($roots.Count -ne 1) { throw 'Invalid package layout: expected exactly one Bin\Bot.exe.' }
        $packageRoot = $roots[0].FullName
    }

    $newExe = Join-Path $packageRoot 'Bin\Bot.exe'
    if (-not (Test-Path -LiteralPath $newExe)) { throw "Package does not contain Bot.exe: $newExe" }
    $releaseInfoPath = Join-Path $packageRoot 'release-info.json'
    if (-not (Test-Path -LiteralPath $releaseInfoPath)) { throw 'Package does not contain release-info.json.' }
    $releaseInfo = Get-Content -LiteralPath $releaseInfoPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$releaseInfo.version) -or ([string]$releaseInfo.version -ne $ExpectedVersion)) {
        throw "Package version mismatch. Expected $ExpectedVersion, actual $($releaseInfo.version)"
    }

    $legacyData = Join-Path $InstallDir 'data'
    if (Test-Path -LiteralPath $legacyData) {
        if (Test-Path -LiteralPath $migrationMarker -PathType Leaf) {
            Write-Host 'Persistent-data migration is already complete; legacy install\data will not be copied into the new program directory.' -ForegroundColor Yellow
        }
        else {
            Write-Host 'Legacy runtime data detected before migration; preserving it for first-run migration.'
            Copy-DirectoryContents $legacyData (Join-Path $packageRoot 'data')
        }
    }

    $stage = 'replace-program'
    Write-Step 'Replacing program files'
    if (-not $backupFinalized -or -not (Test-BackupComplete $backupDir)) {
        throw 'Refusing to replace program files because no finalized validated backup is available.'
    }
    Stop-BotWatchdogs $InstallDir
    Stop-BotProcesses $InstallDir
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $installMutationStarted = $true
    Clear-DirectoryContentsWithRetry $InstallDir
    Copy-DirectoryContents $packageRoot $InstallDir

    $installedExe = Join-Path $InstallDir 'Bin\Bot.exe'
    if (-not (Test-Path -LiteralPath $installedExe)) { throw 'Installed package validation failed: Bin\Bot.exe was not found.' }
    $installedReleaseInfo = Join-Path $InstallDir 'release-info.json'
    if (-not (Test-Path -LiteralPath $installedReleaseInfo)) { throw 'Installed package validation failed: release-info.json was not found.' }
    $installedInfo = Get-Content -LiteralPath $installedReleaseInfo -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$installedInfo.version) -or ([string]$installedInfo.version -ne $ExpectedVersion)) {
        throw "Installed package version mismatch. Expected $ExpectedVersion, actual $($installedInfo.version)"
    }

    $stage = 'post-mutation-port-handoff'
    Write-Step 'Reconfirming the same canonical handoff authority before target start'
    if (-not (Wait-BotWebSocketPortRelease $InstallDir 41010 30)) {
        $handoff = Get-BotWebSocketHandoffState $InstallDir 41010
        throw "Bot WebSocket/process handoff authority became not-ready during replacement ($($handoff.Summary)). Automatic rollback will start."
    }

    $stage = 'target-startup-health'
    Write-Step 'Starting and validating new Bot.exe'
    $env:QIANNIU_BOT_UPDATE_HEALTH_FILE = $healthFile
    $env:QIANNIU_BOT_UPDATE_EXPECTED_VERSION = $ExpectedVersion
    $newBot = Start-Process -FilePath $installedExe -WorkingDirectory (Split-Path -Parent $installedExe) -PassThru
    Remove-Item Env:\QIANNIU_BOT_UPDATE_HEALTH_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:\QIANNIU_BOT_UPDATE_EXPECTED_VERSION -ErrorAction SilentlyContinue
    if ($null -eq $newBot) { throw 'New Bot.exe process could not be created. Automatic rollback will start.' }
    Write-Host "Started target Bot PID=$($newBot.Id); waiting for the explicit version-bound startup health contract."
    if (-not (Test-BotHealthy $installedExe $newBot.Id $healthFile $ExpectedVersion)) {
        throw "New Bot.exe did not report version-bound database/configuration/service health for $ExpectedVersion. Automatic rollback will start."
    }

    $stage = 'completed'
    Write-Step "Update to $ExpectedVersion completed successfully"
    Write-Host "Current program: $installedExe" -ForegroundColor Green
    Write-Host "Rollback snapshot retained: $backupDir"
    Write-Host 'Persistent user data remains under %LocalAppData%\QianniuAiBot (data/global/shops).'
    Write-Host 'Updater storage policy: one validated rollback snapshot only; failed .partial snapshots are removed immediately.'
    Write-UpdateResult $resultPath 'success' $ExpectedVersion $stage 'Target version passed version-bound startup health.' 'not-needed' $logPath
}
catch {
    $failure = $_
    Write-Host "`nUpdate failed: $($failure.Exception.Message)" -ForegroundColor Red
    Write-Host 'Starting automatic rollback...' -ForegroundColor Yellow

    $rollbackSucceeded = $false
    $backupUsable = $backupFinalized -and (Test-BackupComplete $backupDir)

    if (-not $installMutationStarted) {
        Write-Host 'Install directory was not modified; destructive rollback is skipped.' -ForegroundColor Yellow
        $rollbackSucceeded = $true
    }
    elseif (-not $backupUsable) {
        Write-Host 'Rollback refused: the only available backup is incomplete or unvalidated. No .partial backup will be used.' -ForegroundColor Red
    }
    else {
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
            Write-Host "Rollback failed: $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    if ($oldProgramExisted -and $rollbackSucceeded -and (Test-Path -LiteralPath $oldExe)) {
        Start-Process -FilePath $oldExe -WorkingDirectory (Split-Path -Parent $oldExe)
    }

    $rollbackText = if ($rollbackSucceeded) { 'completed' } else { 'failed' }
    Write-UpdateResult $resultPath 'failed' $ExpectedVersion $stage $failure.Exception.Message $rollbackText $logPath

    if ($rollbackSucceeded) {
        Write-Host "Rollback completed safely. Log: $logPath" -ForegroundColor Yellow
    }
    else {
        Write-Host "Rollback was not completed. The updater refused to restore from an incomplete backup. Log: $logPath" -ForegroundColor Red
    }
    Start-Sleep -Seconds 8
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
