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

    # The live install is not mutated until the new backup has been completely copied, hashed and
    # finalized. Therefore old updater snapshots are not needed while creating the next snapshot.
    # Keeping eight full copies (plus seven days of .partial copies) caused multi-gigabyte user data
    # to accumulate on every update and eventually fill the system drive.
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

function Get-InstallProcessIds([string]$TargetInstallDir) {
    $ids = @()
    # Never terminate unrelated Bot.exe instances from other installations. The handoff PID is
    # explicit, and additional cleanup is scoped strictly to the target install directory.
    if ($CurrentPid -gt 0 -and $CurrentPid -ne $PID) {
        $ids += [int]$CurrentPid
    }

    if (-not [string]::IsNullOrWhiteSpace($TargetInstallDir)) {
        $root = [IO.Path]::GetFullPath($TargetInstallDir).TrimEnd('\') + '\'
        try {
            $ids += @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
                if ($null -eq $_ -or [string]::IsNullOrWhiteSpace([string]$_.ExecutablePath)) { return $false }
                try {
                    $exe = [IO.Path]::GetFullPath([string]$_.ExecutablePath)
                    return $exe.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
                }
                catch {
                    return $false
                }
            } | ForEach-Object { [int]$_.ProcessId })
        }
        catch {
            Write-Host "Unable to enumerate processes under install directory: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    return @($ids | Where-Object { $_ -gt 0 -and $_ -ne $PID } | Sort-Object -Unique)
}

function Stop-BotProcesses([string]$TargetInstallDir) {
    $ids = @(Get-InstallProcessIds $TargetInstallDir)
    foreach ($id in $ids) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        Write-Host "Stopping process PID=$id Name=$($process.ProcessName)"
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }

    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline) {
        $alive = @($ids | Where-Object { $null -ne (Get-Process -Id $_ -ErrorAction SilentlyContinue) })
        if ($alive.Count -eq 0) { return }
        Start-Sleep -Milliseconds 300
    }

    foreach ($id in $ids) {
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 700
}

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

function Get-LoopbackPortListenerState([int]$Port) {
    try {
        $command = Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue
        if ($null -eq $command) {
            return [pscustomobject]@{ Known = $false; Listeners = @() }
        }

        $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop | Where-Object {
            $_.LocalAddress -eq '127.0.0.1' -or
            $_.LocalAddress -eq '0.0.0.0' -or
            $_.LocalAddress -eq '::1' -or
            $_.LocalAddress -eq '::'
        })
        return [pscustomobject]@{ Known = $true; Listeners = @($listeners) }
    }
    catch {
        return [pscustomobject]@{ Known = $false; Listeners = @() }
    }
}

function Get-LoopbackPortOwnerSummary([int]$Port) {
    try {
        $state = Get-LoopbackPortListenerState $Port
        if (-not [bool]$state.Known) { return 'owner=unknown' }
        $listeners = @($state.Listeners)
        if ($listeners.Count -eq 0) { return 'owner=none' }

        $parts = @()
        foreach ($entry in $listeners) {
            $ownerPid = [int]$entry.OwningProcess
            $process = Get-CimInstance Win32_Process -Filter "ProcessId = $ownerPid" -ErrorAction SilentlyContinue
            if ($null -ne $process) {
                $parts += "pid=$ownerPid,name=$($process.Name),exe=$($process.ExecutablePath)"
            }
            else {
                $parts += "pid=$ownerPid,name=unknown"
            }
        }
        return ($parts -join '; ')
    }
    catch {
        return 'owner=unknown'
    }
}

function Wait-BotWebSocketPortRelease([string]$TargetInstallDir, [int]$Port, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds([Math]::Max(5, $TimeoutSeconds))
    $attempt = 0
    $lastOwner = $null

    while ((Get-Date) -lt $deadline) {
        $attempt++

        # A stale watchdog or helper can race the first process sweep. Re-scan the target install
        # immediately before every listener probe so a resurrected old Bot cannot retain the
        # authoritative LISTEN socket between package replacement and the new process launch.
        Stop-BotProcesses $TargetInstallDir

        $listenerState = Get-LoopbackPortListenerState $Port
        if ([bool]$listenerState.Known) {
            if (@($listenerState.Listeners).Count -eq 0) {
                # Get-NetTCPConnection gives us the authoritative Windows LISTEN state. A strict
                # ExclusiveAddressUse probe can still fail briefly after shutdown because of
                # transient TCP endpoint teardown. With no LISTEN owner, do not roll back before the
                # target process even gets a chance to start; the target's own bounded WebSocket
                # retry plus explicit startup-health contract remains the final safety authority.
                if (Test-LoopbackPortBindable $Port) {
                    Write-Host "Bot WebSocket handoff ready: 127.0.0.1:$Port has no LISTEN owner and is strictly bindable after $attempt probe(s)." -ForegroundColor Green
                }
                else {
                    Write-Host "Bot WebSocket handoff ready: 127.0.0.1:$Port has no LISTEN owner; strict exclusive bind probe is still blocked by transient TCP state, so startup health will perform the final bind validation." -ForegroundColor Yellow
                }
                return $true
            }
        }
        elseif (Test-LoopbackPortBindable $Port) {
            # Older Windows environments without Get-NetTCPConnection keep the previous conservative
            # behavior: only proceed when the strict bind probe itself succeeds.
            Write-Host "Bot WebSocket handoff ready: listener state unavailable, but 127.0.0.1:$Port is exclusively bindable after $attempt probe(s)." -ForegroundColor Green
            return $true
        }

        $owner = Get-LoopbackPortOwnerSummary $Port
        if ($attempt -eq 1 -or $owner -ne $lastOwner -or ($attempt % 5) -eq 0) {
            Write-Host "Bot WebSocket handoff waiting: 127.0.0.1:$Port still has a LISTEN owner or listener state is unavailable; $owner; probe=$attempt" -ForegroundColor Yellow
            $lastOwner = $owner
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

function Test-BotHealthy([string]$ExpectedExe, [int]$ExpectedPid, [string]$HealthFile) {
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        $process = Get-Process -Id $ExpectedPid -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            return $false
        }

        $pathMatches = $false
        try {
            $pathMatches = $process.Path -and
                ([IO.Path]::GetFullPath($process.Path) -ieq [IO.Path]::GetFullPath($ExpectedExe))
        }
        catch {
            $pathMatches = $false
        }
        if (-not $pathMatches) {
            return $false
        }

        if (Test-Path -LiteralPath $HealthFile -PathType Leaf) {
            try {
                $health = Get-Content -LiteralPath $HealthFile -Raw | ConvertFrom-Json
                if ([string]$health.status -eq 'OK' -and
                    [int]$health.pid -eq $ExpectedPid -and
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
if (-not (Test-Path -LiteralPath $PackagePath)) {
    throw "Update package does not exist: $PackagePath"
}
if (Test-Path -LiteralPath (Join-Path $InstallDir '.git')) {
    throw "Refusing to overwrite a Git source repository: $InstallDir"
}

$actualHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualHash -ne $ExpectedSha256) {
    throw "SHA256 verification failed. Expected $ExpectedSha256, actual $actualHash"
}

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
$oldProgramExisted = Test-Path -LiteralPath $InstallDir -PathType Container
$oldExe = Join-Path $InstallDir 'Bin\Bot.exe'
$backupFinalized = $false
$installMutationStarted = $false
$healthFile = Join-Path $tempDir 'startup-health.json'
$updaterMutex = $null
$ownsUpdaterMutex = $false

New-Item -ItemType Directory -Path $logDir -Force | Out-Null
try { Start-Transcript -Path $logPath -Force | Out-Null } catch { }

try {
    $createdNew = $false
    $updaterMutex = New-Object System.Threading.Mutex($true, 'Global\QianniuAiBotUpdater', [ref]$createdNew)
    if (-not $createdNew) { throw 'Another Qianniu AI Bot updater is already running.' }
    $ownsUpdaterMutex = $true
    Write-Step "Waiting for Bot.exe PID=$CurrentPid to exit"
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ($null -eq (Get-Process -Id $CurrentPid -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 350
    }
    if ($null -ne (Get-Process -Id $CurrentPid -ErrorAction SilentlyContinue)) {
        Write-Host 'Bot did not exit in time; stopping it now.' -ForegroundColor Yellow
        Stop-Process -Id $CurrentPid -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }
    Stop-BotProcesses $InstallDir

    Write-Step "Preparing Bot update to version $ExpectedVersion"
    Write-Host "Package: $PackagePath"
    Write-Host "SHA256: $actualHash"
    Write-Host "Install directory: $InstallDir"
    Write-Host "Persistent root: $persistentRoot (data/global/shops)"
    Write-Host "Log: $logPath"

    Write-Step 'Preparing bounded rollback backup'
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

    # Only one updater snapshot is useful: the snapshot for the update that is about to mutate the
    # install. Old complete copies and failed .partial copies are pure disk growth at this point.
    # The live install is still intact here, so deleting old snapshots cannot make this update unsafe.
    Clear-PreviousUpdaterBackups $backupRoot

    [int64]$estimatedBackupBytes = 0
    if ($oldProgramExisted) {
        $estimatedBackupBytes += Get-DirectorySizeBytes $InstallDir
    }
    foreach ($name in $persistentNames) {
        $estimatedBackupBytes += Get-DirectorySizeBytes (Join-Path $persistentRoot $name)
    }
    [int64]$backupHeadroomBytes = 512MB
    [int64]$availableBytes = Get-AvailableBytes $backupRoot
    Write-Host "Rollback snapshot estimate: $(Format-Bytes $estimatedBackupBytes); free space after stale-backup cleanup: $(Format-Bytes $availableBytes)"
    if ($availableBytes -gt 0 -and $availableBytes -lt ($estimatedBackupBytes + $backupHeadroomBytes)) {
        throw "Insufficient disk space for validated rollback snapshot. Need approximately $(Format-Bytes ($estimatedBackupBytes + $backupHeadroomBytes)), available $(Format-Bytes $availableBytes). Install directory has not been modified."
    }

    if (Test-Path -LiteralPath $partialBackupDir) {
        Remove-Item -LiteralPath $partialBackupDir -Recurse -Force
    }
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
    if (-not (Test-BackupComplete $backupDir)) {
        throw "Backup finalization failed: $backupDir"
    }
    $backupFinalized = $true
    Write-Host "Validated rollback snapshot: $backupDir" -ForegroundColor Green

    Write-Step 'Extracting and validating package'
    if (Test-Path -LiteralPath $tempDir) { Remove-Item -LiteralPath $tempDir -Recurse -Force }
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $tempDir -Force

    $packageRoot = $tempDir
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'Bin\Bot.exe'))) {
        $roots = @(Get-ChildItem -LiteralPath $tempDir -Directory | Where-Object {
            Test-Path -LiteralPath (Join-Path $_.FullName 'Bin\Bot.exe')
        })
        if ($roots.Count -ne 1) {
            throw 'Invalid package layout: expected exactly one Bin\Bot.exe.'
        }
        $packageRoot = $roots[0].FullName
    }

    $newExe = Join-Path $packageRoot 'Bin\Bot.exe'
    if (-not (Test-Path -LiteralPath $newExe)) {
        throw "Package does not contain Bot.exe: $newExe"
    }
    $releaseInfoPath = Join-Path $packageRoot 'release-info.json'
    if (-not (Test-Path -LiteralPath $releaseInfoPath)) {
        throw 'Package does not contain release-info.json.'
    }
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

    Write-Step 'Replacing program files'
    if (-not $backupFinalized -or -not (Test-BackupComplete $backupDir)) {
        throw 'Refusing to replace program files because no finalized validated backup is available.'
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $installMutationStarted = $true
    # Keep the install root itself. A shell/helper may retain a transient directory handle
    # after Bot.exe exits; deleting only children avoids treating that harmless root lock as failure.
    Clear-DirectoryContentsWithRetry $InstallDir
    Copy-DirectoryContents $packageRoot $InstallDir

    $installedExe = Join-Path $InstallDir 'Bin\Bot.exe'
    if (-not (Test-Path -LiteralPath $installedExe)) {
        throw 'Installed package validation failed: Bin\Bot.exe was not found.'
    }
    $installedReleaseInfo = Join-Path $InstallDir 'release-info.json'
    if (-not (Test-Path -LiteralPath $installedReleaseInfo)) {
        throw 'Installed package validation failed: release-info.json was not found.'
    }
    $installedInfo = Get-Content -LiteralPath $installedReleaseInfo -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$installedInfo.version) -or
        ([string]$installedInfo.version -ne $ExpectedVersion)) {
        throw "Installed package version mismatch. Expected $ExpectedVersion, actual $($installedInfo.version)"
    }

    Write-Step 'Waiting for Bot WebSocket port handoff'
    if (-not (Wait-BotWebSocketPortRelease $InstallDir 41010 45)) {
        $portOwner = Get-LoopbackPortOwnerSummary 41010
        throw "Bot WebSocket port 41010 still has a real LISTEN owner after old Bot shutdown ($portOwner). Automatic rollback will start."
    }

    Write-Step 'Starting and validating new Bot.exe'
    $env:QIANNIU_BOT_UPDATE_HEALTH_FILE = $healthFile
    $newBot = Start-Process -FilePath $installedExe -WorkingDirectory (Split-Path -Parent $installedExe) -PassThru
    Remove-Item Env:\QIANNIU_BOT_UPDATE_HEALTH_FILE -ErrorAction SilentlyContinue
    if ($null -eq $newBot) {
        throw 'New Bot.exe process could not be created. Automatic rollback will start.'
    }
    Write-Host "Started target Bot PID=$($newBot.Id); waiting for the explicit startup health contract."
    if (-not (Test-BotHealthy $installedExe $newBot.Id $healthFile)) {
        throw 'New Bot.exe did not report database/configuration/service health. Automatic rollback will start.'
    }

    Write-Step "Update to $ExpectedVersion completed successfully"
    Write-Host "Current program: $installedExe" -ForegroundColor Green
    Write-Host "Rollback snapshot retained: $backupDir"
    Write-Host 'Persistent user data remains under %LocalAppData%\QianniuAiBot (data/global/shops).'
    Write-Host 'Updater storage policy: one validated rollback snapshot only; failed .partial snapshots are removed immediately.'
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
    if (Test-Path -LiteralPath $partialBackupDir) {
        Remove-Item -LiteralPath $partialBackupDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $tempDir) {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    try { Stop-Transcript | Out-Null } catch { }
    if ($ownsUpdaterMutex -and $null -ne $updaterMutex) { try { $updaterMutex.ReleaseMutex() } catch { } }
    if ($null -ne $updaterMutex) { $updaterMutex.Dispose() }
    try { Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue } catch { }
}