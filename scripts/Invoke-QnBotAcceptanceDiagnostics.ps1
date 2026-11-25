#requires -Version 5.1
<#
.SYNOPSIS
    Read-only Windows acceptance diagnostics for QianNiu AI Bot.
.DESCRIPTION
    Samples Bot/Qianniu process health, the local 41010 WebSocket listener,
    and a loopback-only CDP page probe for window.imsdk/invoke/on.
    It does not send chat messages, change settings, stop processes, collect
    logs, save command lines, or upload data. CDP Runtime.evaluate is read-only.
.EXAMPLE
    powershell.exe -NoProfile -File scripts\Invoke-QnBotAcceptanceDiagnostics.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [string]$RuntimeSnapshotPath = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $env:LOCALAPPDATA 'QianniuAiBot\diagnostics'
}
if ([string]::IsNullOrWhiteSpace($RuntimeSnapshotPath)) {
    $RuntimeSnapshotPath = Join-Path $env:LOCALAPPDATA 'QianniuAiBot\diagnostics\runtime-snapshot.json'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Get-RemoteDebuggingPort {
    param([string]$CommandLine)
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $null }
    $match = [regex]::Match($CommandLine, '(?i)(?:^|[\s"])--remote-debugging-port(?:=|\s+)"?(\d+)"?(?=$|[\s"])')
    if (-not $match.Success) { return $null }
    $port = 0
    if ([int]::TryParse($match.Groups[1].Value, [ref]$port) -and $port -ge 1 -and $port -le 65535) {
        return $port
    }
    return $null
}

function Test-BenchImRenderer {
    param([string]$CommandLine)
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $false }
    return [regex]::IsMatch($CommandLine, '(?i)(?:^|[\s"])--render_id(?:=|\s+)"?bench_im"?(?=$|[\s"])')
}

function Get-BotReleaseIdentity {
    param([string]$ExecutablePath)

    $fileVersion = $null
    $version = $null
    $tag = $null
    $commit = $null
    $versionSource = 'not_detected'
    try {
        $fileVersion = (Get-Item -LiteralPath $ExecutablePath -ErrorAction Stop).VersionInfo.FileVersion
        if (-not [string]::IsNullOrWhiteSpace($fileVersion)) {
            $version = $fileVersion
            $versionSource = 'assembly_file_version'
        }
    }
    catch {}

    # The assembly version is intentionally a compatibility baseline (1.1.0.0).
    # The stable product version and exact source SHA live in release-info.json.
    try {
        $binDirectory = Split-Path -Parent $ExecutablePath
        $packageRoot = Split-Path -Parent $binDirectory
        $manifestPath = Join-Path $packageRoot 'release-info.json'
        if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
            $manifest = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($manifestPath)) -ErrorAction Stop
            $manifestVersion = [string]$manifest.version
            $manifestTag = [string]$manifest.tag
            $manifestCommit = [string]$manifest.commit
            if ($manifestVersion -match '^\d+\.\d+\.\d+$' -and $manifestTag -eq ('bot-v' + $manifestVersion) -and $manifestCommit -match '^[0-9a-fA-F]{40}$') {
                $version = $manifestVersion
                $tag = $manifestTag
                $commit = $manifestCommit.ToLowerInvariant()
                $versionSource = 'release_manifest'
            }
        }
    }
    catch {}

    return [pscustomobject]@{
        Version = $version
        FileVersion = $fileVersion
        Tag = $tag
        Commit = $commit
        VersionSource = $versionSource
    }
}
function Test-LoopbackPort {
    param([int]$Port, [int]$TimeoutMs = 1200)
    if ($Port -lt 1 -or $Port -gt 65535) { return $false }
    $client = New-Object System.Net.Sockets.TcpClient
    $waitHandle = $null
    try {
        $pending = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        $waitHandle = $pending.AsyncWaitHandle
        if (-not $waitHandle.WaitOne($TimeoutMs, $false)) { return $false }
        $client.EndConnect($pending)
        return $client.Connected
    }
    catch {
        return $false
    }
    finally {
        if ($waitHandle -ne $null) { $waitHandle.Close() }
        $client.Close()
    }
}

function Invoke-LocalJsonGet {
    param([string]$Url)
    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.Method = 'GET'
    $request.Proxy = $null
    $request.Timeout = 1800
    $request.ReadWriteTimeout = 1800
    $request.KeepAlive = $false
    $response = $null
    $reader = $null
    try {
        $response = $request.GetResponse()
        $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
        $body = $reader.ReadToEnd()
        return (ConvertFrom-Json -InputObject $body -ErrorAction Stop)
    }
    finally {
        if ($reader -ne $null) { $reader.Dispose() }
        if ($response -ne $null) { $response.Dispose() }
    }
}

function Invoke-CdpReadonlyProbe {
    param([string]$WebSocketUrl)

    $uri = $null
    $uriValid = [Uri]::TryCreate($WebSocketUrl, [UriKind]::Absolute, [ref]$uri)
    if (-not $uriValid) {
        return [pscustomobject]@{ Status = 'invalid_websocket_url'; ErrorType = $null }
    }
    if ($uri.Scheme -ne 'ws' -or -not $uri.IsLoopback) {
        return [pscustomobject]@{ Status = 'rejected_non_loopback'; ErrorType = $null }
    }

    $socket = $null
    $cancellation = $null
    try {
        $socket = New-Object System.Net.WebSockets.ClientWebSocket
        $cancellation = New-Object System.Threading.CancellationTokenSource
        $cancellation.CancelAfter(4000)
        $socket.ConnectAsync($uri, $cancellation.Token).GetAwaiter().GetResult()

        $expression = 'JSON.stringify({documentReadyState:document.readyState,imsdkType:typeof window.imsdk,invokeType:typeof (window.imsdk && window.imsdk.invoke),onType:typeof (window.imsdk && window.imsdk.on),loginIdPresent:!!(window._vs && window._vs.loginID),qnPresent:!!window.QN})'
        $payloadObject = @{
            id = 1
            method = 'Runtime.evaluate'
            params = @{
                expression = $expression
                returnByValue = $true
                awaitPromise = $false
            }
        }
        $payload = ConvertTo-Json -InputObject $payloadObject -Depth 6 -Compress
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
        $sendSegment = [System.ArraySegment[byte]]::new($bytes, 0, $bytes.Length)
        $sendTask = $socket.SendAsync(
            $sendSegment,
            [System.Net.WebSockets.WebSocketMessageType]::Text,
            $true,
            $cancellation.Token
        )
        $sendTask.GetAwaiter().GetResult() | Out-Null

        $buffer = New-Object byte[] 8192
        $receiveSegment = [System.ArraySegment[byte]]::new($buffer, 0, $buffer.Length)
        for ($attempt = 0; $attempt -lt 12; $attempt++) {
            $builder = New-Object System.Text.StringBuilder
            do {
                $receiveTask = $socket.ReceiveAsync($receiveSegment, $cancellation.Token)
                $frame = $receiveTask.GetAwaiter().GetResult()
                if ($frame.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                    return [pscustomobject]@{ Status = 'closed'; ErrorType = $null }
                }
                $builder.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $frame.Count)) | Out-Null
            } while (-not $frame.EndOfMessage)

            $message = ConvertFrom-Json -InputObject $builder.ToString() -ErrorAction Stop
            if ($message.id -ne 1) { continue }
            $value = $message.result.result.value
            if ([string]::IsNullOrWhiteSpace([string]$value)) {
                return [pscustomobject]@{ Status = 'empty_result'; ErrorType = $null }
            }
            $state = ConvertFrom-Json -InputObject ([string]$value) -ErrorAction Stop
            return [pscustomobject]@{
                Status = 'ok'
                ErrorType = $null
                DocumentReadyState = [string]$state.documentReadyState
                ImsdkType = [string]$state.imsdkType
                InvokeType = [string]$state.invokeType
                OnType = [string]$state.onType
                LoginIdPresent = [bool]$state.loginIdPresent
                QnPresent = [bool]$state.qnPresent
            }
        }
        return [pscustomobject]@{ Status = 'response_not_observed'; ErrorType = $null }
    }
    catch {
        return [pscustomobject]@{
            Status = 'failed'
            ErrorType = $_.Exception.GetType().Name
        }
    }
    finally {
        if ($socket -ne $null) { $socket.Dispose() }
        if ($cancellation -ne $null) { $cancellation.Dispose() }
    }
}

function Read-RuntimeAcceptanceSnapshot {
    param(
        [string]$Path,
        [object[]]$BotProcesses
    )

    $empty = [pscustomobject]@{
        Status = 'not_found'
        AgeSeconds = $null
        ProcessId = $null
        CollectedAtUtc = $null
        Counts = $null
        Shops = @()
        ActiveShopRef = 'shop#none'
        ActiveSessionRef = 'session#none'
        ActiveFocusProofFresh = $false
        ActiveFocusProofAgeSeconds = $null
        ActiveShopEvidenceStatus = 'unavailable'
    }
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $empty
    }

    try {
        $snapshot = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($Path)) -ErrorAction Stop
        if ($null -eq $snapshot -or [int]$snapshot.SchemaVersion -ne 1) {
            $empty.Status = 'invalid_schema'
            return $empty
        }

        $snapshotPid = [int]$snapshot.ProcessId
        $matchingBot = @($BotProcesses | Where-Object { [int]$_.ProcessId -eq $snapshotPid } | Select-Object -First 1)
        if ($matchingBot.Count -eq 0) {
            $empty.Status = 'process_mismatch'
            return $empty
        }
        $liveProcess = Get-Process -Id $snapshotPid -ErrorAction Stop
        $actualStartTicks = $liveProcess.StartTime.ToUniversalTime().Ticks
        $expectedStartTicks = [long]$snapshot.ProcessStartTimeUtcTicks
        if ([math]::Abs([double]($actualStartTicks - $expectedStartTicks)) -gt [TimeSpan]::FromSeconds(2).Ticks) {
            $empty.Status = 'process_mismatch'
            return $empty
        }

        $collectedAt = [DateTime]::Parse([string]$snapshot.CollectedAtUtc).ToUniversalTime()
        $ageSeconds = [int][math]::Floor(((Get-Date).ToUniversalTime() - $collectedAt).TotalSeconds)
        if ($ageSeconds -lt -30 -or $ageSeconds -gt 120) {
            $empty.Status = 'stale'
            $empty.AgeSeconds = $ageSeconds
            return $empty
        }

        $activeShopRef = 'shop#none'
        $activeSessionRef = 'session#none'
        $activeFocusProofFresh = $false
        $activeFocusProofAgeSeconds = $null
        $activeShopEvidenceStatus = 'unavailable'
        if ($null -ne $snapshot.PSObject.Properties['ActiveShopRef'] -and
            $null -ne $snapshot.PSObject.Properties['ActiveSessionRef'] -and
            $null -ne $snapshot.PSObject.Properties['ActiveFocusProofFresh']) {
            $candidateShopRef = [string]$snapshot.ActiveShopRef
            $candidateSessionRef = [string]$snapshot.ActiveSessionRef
            if ($candidateShopRef -match '^shop#(?:[0-9a-f]{16}|none)$' -and
                $candidateSessionRef -match '^session#(?:[0-9a-f]{16}|none)$') {
                $activeShopRef = $candidateShopRef
                $activeSessionRef = $candidateSessionRef
                $activeFocusProofFresh = [bool]$snapshot.ActiveFocusProofFresh
                if ($null -ne $snapshot.ActiveFocusProofAgeSeconds) {
                    $activeFocusProofAgeSeconds = [int]$snapshot.ActiveFocusProofAgeSeconds
                }
                $activeShopEvidenceStatus = if ($activeFocusProofFresh) { 'owner_mapping_unverified' } else { 'stale_or_unfocused' }
            }
            else {
                $activeShopEvidenceStatus = 'invalid_alias'
            }
        }

        $shopRows = @()
        foreach ($shop in @($snapshot.Shops)) {
            if (
                [string]$shop.ShopRef -notmatch '^shop#[0-9a-f]{16}$' -or
                [string]$shop.OwnerSessionRef -notmatch '^session#[0-9a-f]{16}$'
            ) {
                continue
            }
            $standbyRows = @()
            foreach ($standby in @($shop.Standbys)) {
                if ([string]$standby.SessionRef -notmatch '^session#[0-9a-f]{16}$') { continue }
                $standbyRows += [pscustomobject]@{
                    SessionRef = [string]$standby.SessionRef
                    Connected = [bool]$standby.Connected
                    Recoverable = [bool]$standby.Recoverable
                    Initialized = [bool]$standby.Initialized
                    ClientPresent = [bool]$standby.ClientPresent
                    ClientInvalidated = [bool]$standby.ClientInvalidated
                    ClientIdentityRetired = [bool]$standby.ClientIdentityRetired
                    Closing = [bool]$standby.Closing
                    IdentityProbeGeneration = [long]$standby.IdentityProbeGeneration
                    StatusBindingGeneration = [long]$standby.StatusBindingGeneration
                    LastActivityAgeSeconds = $standby.LastActivityAgeSeconds
                }
            }
            $shopRows += [pscustomobject]@{
                ShopRef = [string]$shop.ShopRef
                OwnerSessionRef = [string]$shop.OwnerSessionRef
                OwnerConnected = [bool]$shop.OwnerConnected
                OwnerInitialized = [bool]$shop.OwnerInitialized
                OwnerClientPresent = [bool]$shop.OwnerClientPresent
                OwnerClientInvalidated = [bool]$shop.OwnerClientInvalidated
                OwnerClientIdentityRetired = [bool]$shop.OwnerClientIdentityRetired
                OwnerIdentityProbeGeneration = [long]$shop.OwnerIdentityProbeGeneration
                OwnerStatusBindingGeneration = [long]$shop.OwnerStatusBindingGeneration
                OwnerLastActivityAgeSeconds = $shop.OwnerLastActivityAgeSeconds
                StandbyCount = [int]$shop.StandbyCount
                RecoverableStandbyCount = [int]$shop.RecoverableStandbyCount
                Standbys = $standbyRows
            }
        }

        if ($activeFocusProofFresh) {
            $activeOwnerRows = @($shopRows | Where-Object { $_.ShopRef -eq $activeShopRef })
            if ($activeOwnerRows.Count -eq 1) {
                $activeOwner = $activeOwnerRows[0]
                $activeShopEvidenceStatus = if (
                    [string]::Equals([string]$activeOwner.OwnerSessionRef, $activeSessionRef, [StringComparison]::Ordinal) -and
                    $activeOwner.OwnerConnected -eq $true -and
                    $activeOwner.OwnerInitialized -eq $true -and
                    $activeOwner.OwnerClientPresent -eq $true -and
                    $activeOwner.OwnerClientInvalidated -ne $true -and
                    $activeOwner.OwnerClientIdentityRetired -ne $true
                ) { 'fresh' } else { 'owner_mapping_mismatch' }
            }
            else {
                $activeShopEvidenceStatus = 'owner_mapping_missing_or_ambiguous'
            }
        }

        $rawCounts = $snapshot.Counts
        $counts = [pscustomobject]@{
            ConnectedSessions = [int]$rawCounts.ConnectedSessions
            AuthoritativeShopCount = [int]$rawCounts.AuthoritativeShopCount
            DuplicateSessions = [int]$rawCounts.DuplicateSessions
            RecoverableStandbySessions = [int]$rawCounts.RecoverableStandbySessions
            StandbyCapabilitySessions = [int]$rawCounts.StandbyCapabilitySessions
            InitializedSessions = [int]$rawCounts.InitializedSessions
            ClientSessions = [int]$rawCounts.ClientSessions
            UnmappedConnectedSessions = [int]$rawCounts.UnmappedConnectedSessions
        }
        return [pscustomobject]@{
            Status = 'fresh'
            AgeSeconds = $ageSeconds
            ProcessId = $snapshotPid
            CollectedAtUtc = $collectedAt.ToString('o')
            Counts = $counts
            Shops = $shopRows
            ActiveShopRef = $activeShopRef
            ActiveSessionRef = $activeSessionRef
            ActiveFocusProofFresh = $activeFocusProofFresh
            ActiveFocusProofAgeSeconds = $activeFocusProofAgeSeconds
            ActiveShopEvidenceStatus = $activeShopEvidenceStatus
        }
    }
    catch {
        $empty.Status = 'invalid_or_unreadable'
        return $empty
    }
}

$processNames = @('Bot.exe', 'AliWorkbench.exe', 'AliRender.exe', 'wwcmd.exe')
$cimProcesses = @()
$cimErrorType = $null
try {
    $cimProcesses = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop |
        Where-Object { $processNames -contains $_.Name })
}
catch {
    $cimErrorType = $_.Exception.GetType().Name
}

$listenerRows = @()
$listenerMethod = 'Get-NetTCPConnection'
try {
    $listenerRows = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | ForEach-Object {
        $localAddress = [string]$_.LocalAddress
        $parsedAddress = $null
        $addressKind = 'other'
        if ([System.Net.IPAddress]::TryParse($localAddress, [ref]$parsedAddress)) {
            if ([System.Net.IPAddress]::IsLoopback($parsedAddress)) {
                $addressKind = 'loopback'
            } elseif ($localAddress -eq '0.0.0.0' -or $localAddress -eq '::') {
                $addressKind = 'all_interfaces'
            }
        }
        [pscustomobject]@{
            LocalPort = [int]$_.LocalPort
            OwningProcess = [int]$_.OwningProcess
            AddressKind = $addressKind
        }
    })
}
catch {
    $listenerMethod = 'netstat'
    try {
        foreach ($line in (& netstat.exe -ano -p tcp 2>$null)) {
            if ($line -match '^\s*TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+(\d+)\s*$') {
                $listenerRows += [pscustomobject]@{
                    LocalPort = [int]$Matches[1]
                    OwningProcess = [int]$Matches[2]
                    AddressKind = 'unknown'
                }
            }
        }
    }
    catch {
        $listenerMethod = 'unavailable'
    }
}

$processReports = @()
$processById = @{}
foreach ($process in $cimProcesses) {
    $processById[[int]$process.ProcessId] = $process
}
$benchImRenderers = @($cimProcesses | Where-Object {
    $_.Name -ieq 'AliRender.exe' -and (Test-BenchImRenderer -CommandLine ([string]$_.CommandLine))
})
$botVersion = $null
$botFileVersion = $null
$botReleaseTag = $null
$botReleaseCommit = $null
$botVersionSource = 'not_detected'

foreach ($process in $cimProcesses) {
    $pidValue = [int]$process.ProcessId
    $live = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
    $startedAt = $null
    $workingSetMiB = $null
    $privateMiB = $null
    $handleCount = $null
    $threadCount = $null
    $cpuSeconds = $null
    if ($live -ne $null) {
        try { $startedAt = $live.StartTime.ToString('o') } catch {}
        try { $workingSetMiB = [math]::Round($live.WorkingSet64 / 1MB, 1) } catch {}
        try { $privateMiB = [math]::Round($live.PrivateMemorySize64 / 1MB, 1) } catch {}
        try { $handleCount = $live.HandleCount } catch {}
        try { $threadCount = $live.Threads.Count } catch {}
        try { $cpuSeconds = [math]::Round([double]$live.CPU, 2) } catch {}
    }

    $isBenchIm = Test-BenchImRenderer -CommandLine ([string]$process.CommandLine)
    $debugPort = Get-RemoteDebuggingPort -CommandLine ([string]$process.CommandLine)
    if ($process.Name -ieq 'Bot.exe' -and [string]::IsNullOrWhiteSpace($botVersion) -and $process.ExecutablePath) {
        $identity = Get-BotReleaseIdentity -ExecutablePath ([string]$process.ExecutablePath)
        $botVersion = $identity.Version
        $botFileVersion = $identity.FileVersion
        $botReleaseTag = $identity.Tag
        $botReleaseCommit = $identity.Commit
        $botVersionSource = $identity.VersionSource
    }

    $processReports += [pscustomobject]@{
        Name = [string]$process.Name
        Pid = $pidValue
        ParentPid = [int]$process.ParentProcessId
        CommandLineAvailable = -not [string]::IsNullOrWhiteSpace([string]$process.CommandLine)
        IsBenchImRenderer = [bool]$isBenchIm
        RemoteDebuggingPort = $debugPort
        StartedAt = $startedAt
        WorkingSetMiB = $workingSetMiB
        PrivateMiB = $privateMiB
        HandleCount = $handleCount
        ThreadCount = $threadCount
        CpuSeconds = $cpuSeconds
    }
}

$debugPorts = @()
foreach ($renderer in $benchImRenderers) {
    $rendererPort = Get-RemoteDebuggingPort -CommandLine ([string]$renderer.CommandLine)
    if ($rendererPort) { $debugPorts += $rendererPort }

    $parent = $processById[[int]$renderer.ParentProcessId]
    if ($parent -ne $null) {
        $parentPort = Get-RemoteDebuggingPort -CommandLine ([string]$parent.CommandLine)
        if ($parentPort) { $debugPorts += $parentPort }
        foreach ($listener in $listenerRows | Where-Object { $_.OwningProcess -eq [int]$parent.ProcessId }) {
            if ($listener.LocalPort -ne 41010) { $debugPorts += [int]$listener.LocalPort }
        }
    }
    foreach ($listener in $listenerRows | Where-Object { $_.OwningProcess -eq [int]$renderer.ProcessId }) {
        if ($listener.LocalPort -ne 41010) { $debugPorts += [int]$listener.LocalPort }
    }
}
$debugPorts = @($debugPorts | Where-Object { $_ -ge 1 -and $_ -le 65535 } | Sort-Object -Unique)

$webSocketListeners = @($listenerRows | Where-Object { $_.LocalPort -eq 41010 })
$webSocketOwnerNames = @()
foreach ($listener in $webSocketListeners) {
    $owner = $processById[[int]$listener.OwningProcess]
    if ($owner -ne $null) { $webSocketOwnerNames += [string]$owner.Name }
}
$webSocketReachable = Test-LoopbackPort -Port 41010
$botProcesses = @($cimProcesses | Where-Object { $_.Name -ieq 'Bot.exe' })
$botPids = @($botProcesses | ForEach-Object { [int]$_.ProcessId })
$runtimeSnapshot = Read-RuntimeAcceptanceSnapshot -Path $RuntimeSnapshotPath -BotProcesses $botProcesses
$webSocketOwnedByBot = @($webSocketListeners | Where-Object {
    $botPids -contains [int]$_.OwningProcess
}).Count -gt 0
$qianniuProcessIds = @($cimProcesses | Where-Object {
    $_.Name -ieq 'AliWorkbench.exe' -or $_.Name -ieq 'AliRender.exe' -or $_.Name -ieq 'wwcmd.exe'
} | ForEach-Object { [int]$_.ProcessId })
$qianniuLoopbackListenerCandidates = @($listenerRows | Where-Object {
    $_.AddressKind -eq 'loopback' -and $_.LocalPort -ne 41010 -and
        $qianniuProcessIds -contains [int]$_.OwningProcess
} | ForEach-Object {
    $owner = $processById[[int]$_.OwningProcess]
    [pscustomobject]@{
        Port = [int]$_.LocalPort
        OwnerProcessName = if ($owner -ne $null) { [string]$owner.Name } else { 'unknown' }
    }
} | Sort-Object Port, OwnerProcessName -Unique)

$cdpEndpoints = @()
if ($benchImRenderers.Count -gt 0) {
    foreach ($port in $debugPorts) {
        try {
            $version = Invoke-LocalJsonGet -Url ("http://127.0.0.1:{0}/json/version" -f $port)
            $targets = @(Invoke-LocalJsonGet -Url ("http://127.0.0.1:{0}/json/list" -f $port))
            $pages = @($targets | Where-Object {
                $_.type -eq 'page' -and -not [string]::IsNullOrWhiteSpace([string]$_.webSocketDebuggerUrl)
            })
            $benchPages = @($pages | Where-Object { [string]$_.url -match '(?i)bench_im' })
            $selectedPages = @()
            if ($benchPages.Count -gt 0) {
                $selectedPages = $benchPages
            } elseif ($benchImRenderers.Count -eq 1 -and $pages.Count -eq 1) {
                $selectedPages = $pages
            }

            $probes = @()
            foreach ($target in ($selectedPages | Select-Object -First 2)) {
                $probes += Invoke-CdpReadonlyProbe -WebSocketUrl ([string]$target.webSocketDebuggerUrl)
            }
            $endpointStatus = 'target_not_unambiguously_matched'
            if ($probes.Count -gt 0) { $endpointStatus = 'probed' }
            $cdpEndpoints += [pscustomobject]@{
                Port = [int]$port
                Reachable = Test-LoopbackPort -Port ([int]$port)
                Browser = [string]$version.Browser
                ProtocolVersion = [string]$version.'Protocol-Version'
                PageTargetCount = $pages.Count
                BenchImUrlTargetCount = $benchPages.Count
                ProbeTargetCount = $probes.Count
                Probes = $probes
                Status = $endpointStatus
            }
        }
        catch {
            $cdpEndpoints += [pscustomobject]@{
                Port = [int]$port
                Reachable = Test-LoopbackPort -Port ([int]$port)
                Browser = $null
                ProtocolVersion = $null
                PageTargetCount = 0
                BenchImUrlTargetCount = 0
                ProbeTargetCount = 0
                Probes = @()
                Status = 'unavailable'
                ErrorType = $_.Exception.GetType().Name
            }
        }
    }
}

$os = $null
try { $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop } catch {}
$frameworkRelease = $null
try {
    $framework = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction Stop
    $frameworkRelease = [int]$framework.Release
}
catch {}

$osCaption = $null
$osVersion = $null
$osBuild = $null
$osArchitecture = $null
if ($os -ne $null) {
    $osCaption = [string]$os.Caption
    $osVersion = [string]$os.Version
    $osBuild = [string]$os.BuildNumber
    $osArchitecture = [string]$os.OSArchitecture
}

$botCount = @($cimProcesses | Where-Object { $_.Name -ieq 'Bot.exe' }).Count
$workbenchCount = @($cimProcesses | Where-Object { $_.Name -ieq 'AliWorkbench.exe' }).Count
$renderCount = @($cimProcesses | Where-Object { $_.Name -ieq 'AliRender.exe' }).Count
$aliRenderProcesses = @($cimProcesses | Where-Object { $_.Name -ieq 'AliRender.exe' })
$renderCommandLineVisibleCount = @($aliRenderProcesses | Where-Object {
    -not [string]::IsNullOrWhiteSpace([string]$_.CommandLine)
}).Count
$renderCommandLineUnavailableCount = $renderCount - $renderCommandLineVisibleCount

$botCheckStatus = 'not_running'
if ($botCount -gt 0) { $botCheckStatus = 'present' }
$workbenchCheckStatus = 'not_running'
if ($workbenchCount -gt 0) { $workbenchCheckStatus = 'present' }
$rendererCheckStatus = 'not_detected'
if ($benchImRenderers.Count -gt 0) {
    $rendererCheckStatus = 'present'
} elseif ($renderCommandLineUnavailableCount -gt 0) {
    $rendererCheckStatus = 'indeterminate_command_line_visibility'
}
$webSocketCheckStatus = 'not_reachable'
if ($webSocketReachable) { $webSocketCheckStatus = 'listener_not_confirmed_as_bot' }
if ($webSocketReachable -and $webSocketOwnedByBot) { $webSocketCheckStatus = 'listening_and_bot_owned' }
$cdpCheckStatus = 'not_detected'
$successfulCdpProbes = @($cdpEndpoints | Where-Object {
    @($_.Probes | Where-Object { $_.Status -eq 'ok' }).Count -gt 0
}).Count
if ($successfulCdpProbes -gt 0) {
    $cdpCheckStatus = 'probed'
} elseif ($cdpEndpoints.Count -gt 0) {
    $cdpCheckStatus = 'endpoint_found_probe_incomplete'
}

$activeFocusCheckStatus = 'unavailable'
if ($runtimeSnapshot.Status -eq 'fresh') {
    $activeFocusCheckStatus = [string]$runtimeSnapshot.ActiveShopEvidenceStatus
}

$report = [ordered]@{
    SchemaVersion = 1
    CollectedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    Machine = [ordered]@{
        OsCaption = $osCaption
        OsVersion = $osVersion
        OsBuild = $osBuild
        OsArchitecture = $osArchitecture
        PowerShellVersion = $PSVersionTable.PSVersion.ToString()
        DotNetFrameworkV4Release = $frameworkRelease
    }
    Application = [ordered]@{
        BotVersion = $botVersion
        BotFileVersion = $botFileVersion
        BotReleaseTag = $botReleaseTag
        BotReleaseCommit = $botReleaseCommit
        BotVersionSource = $botVersionSource
        BotProcessCount = $botCount
        QianniuWorkbenchProcessCount = $workbenchCount
        AliRenderProcessCount = $renderCount
        AliRenderCommandLineVisibleCount = $renderCommandLineVisibleCount
        AliRenderCommandLineUnavailableCount = $renderCommandLineUnavailableCount
        BenchImRendererCount = $benchImRenderers.Count
        Processes = $processReports
        ProcessInspectionErrorType = $cimErrorType
    }
    Connectivity = [ordered]@{
        ListenerInspectionMethod = $listenerMethod
        WebSocket41010 = [ordered]@{
            ListenerCount = $webSocketListeners.Count
            LoopbackReachable = $webSocketReachable
            OwnedByBotProcess = $webSocketOwnedByBot
            OwnerProcessNames = $webSocketOwnerNames
        }
        CdpEndpoints = $cdpEndpoints
        QianniuLoopbackListenerCandidates = $qianniuLoopbackListenerCandidates
    }
    Checks = @(
        [pscustomobject]@{ Name = 'Bot process'; Status = $botCheckStatus }
        [pscustomobject]@{ Name = 'Qianniu workbench'; Status = $workbenchCheckStatus }
        [pscustomobject]@{ Name = 'bench_im renderer'; Status = $rendererCheckStatus }
        [pscustomobject]@{ Name = 'WebSocket 41010'; Status = $webSocketCheckStatus }
        [pscustomobject]@{ Name = 'CDP/IMSDK probe'; Status = $cdpCheckStatus }
        [pscustomobject]@{ Name = 'ShopKey runtime mapping'; Status = [string]$runtimeSnapshot.Status }
        [pscustomobject]@{ Name = 'Active shop focus proof'; Status = $activeFocusCheckStatus }
    )
    RuntimeShopSessions = $runtimeSnapshot
    Privacy = [ordered]@{
        CustomerMessagesIncluded = $false
        CredentialsIncluded = $false
        CookiesIncluded = $false
        TargetUrlsIncluded = $false
        RawCommandLinesIncluded = $false
        RawShopKeysIncluded = $false
        RawWebSocketSessionIdsIncluded = $false
        RawLogsIncluded = $false
        ScreenshotsIncluded = $false
        Uploaded = $false
        Note = '只保存进程资源计数、端口状态、CDP目标数量、IMSDK属性类型/布尔值，以及进程生命周期内HMAC化的ShopKey/会话引用和状态计数；不保存聊天正文、账号昵称、原始ShopKey、原始会话ID、目标URL、命令行、Cookie、令牌、原始日志或截图。'
    }
    Limitations = @(
        '此报告只做本机只读探测，不会发送测试消息，也不能证明真实买家消息已经送达。',
        'CDP仅在定位到bench_im渲染进程及其本机调试端口时探测；目标不唯一时会跳过，不猜测页面身份。未识别bench_im时只列出千牛进程拥有的loopback监听候选端口，不连接探测。',
        '需要真实千牛会话的切店、重连、消息投递、知识学习和人工接管仍需单独验收。'
    )
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $OutputDirectory ('qnbot-acceptance-' + $stamp + '.json')
$markdownPath = Join-Path $OutputDirectory ('qnbot-acceptance-' + $stamp + '.md')
$report | ConvertTo-Json -Depth 9 | Out-File -LiteralPath $jsonPath -Encoding UTF8

$markdown = @(
    '# qnbot Windows Acceptance Diagnostics',
    '',
    ('Collected UTC: ' + $report.CollectedAtUtc),
    '',
    '## Summary',
    ('- Bot.exe processes: ' + $botCount),
    ('- AliWorkbench.exe processes: ' + $workbenchCount),
    ('- AliRender.exe processes: ' + $renderCount),
    ('- AliRender command lines visible: ' + $renderCommandLineVisibleCount + ' / ' + $renderCount),
    ('- AliRender command lines unavailable: ' + $renderCommandLineUnavailableCount),
    ('- bench_im renderers: ' + $benchImRenderers.Count),
    ('- Qianniu-owned loopback listener candidates (not probed without bench_im identity): ' + $qianniuLoopbackListenerCandidates.Count),
    ('- Bot product version: ' + $(if ($botVersion) { $botVersion } else { 'not detected' })),
    ('- Bot version source: ' + $botVersionSource),
    ('- Bot executable file version: ' + $(if ($botFileVersion) { $botFileVersion } else { 'not detected' })),
    ('- Bot release tag: ' + $(if ($botReleaseTag) { $botReleaseTag } else { 'not detected' })),
    ('- Bot release commit: ' + $(if ($botReleaseCommit) { $botReleaseCommit } else { 'not detected' })),
    ('- WebSocket 127.0.0.1:41010 reachable: ' + $webSocketReachable),
    ('- WebSocket listener owned by Bot: ' + $webSocketOwnedByBot),
    ('- CDP endpoints inspected: ' + $cdpEndpoints.Count),
    '',
    '## CDP / IMSDK'
)
if ($cdpEndpoints.Count -eq 0) {
    $markdown += '- No unambiguous local CDP debug endpoint was detected.'
    foreach ($candidate in $qianniuLoopbackListenerCandidates) {
        $markdown += ('- Candidate loopback port ' + $candidate.Port + ' owned by ' + $candidate.OwnerProcessName + '; not probed because bench_im identity was not established.')
    }
} else {
    foreach ($endpoint in $cdpEndpoints) {
        $markdown += ('- Port ' + $endpoint.Port + ': ' + $endpoint.Status + '; pages=' + $endpoint.PageTargetCount + '; bench_im URL matches=' + $endpoint.BenchImUrlTargetCount)
        foreach ($probe in $endpoint.Probes) {
            $markdown += ('  - Probe=' + $probe.Status + '; imsdk=' + $probe.ImsdkType + '; invoke=' + $probe.InvokeType + '; on=' + $probe.OnType + '; loginID present=' + $probe.LoginIdPresent)
        }
    }
}
$markdown += @(
    '',
    '## ShopKey runtime mappings',
    ('- Runtime snapshot: ' + $runtimeSnapshot.Status + $(if ($null -ne $runtimeSnapshot.AgeSeconds) { '; ageSeconds=' + $runtimeSnapshot.AgeSeconds } else { '' }))
)
if ($runtimeSnapshot.Status -eq 'fresh') {
    $counts = $runtimeSnapshot.Counts
    $markdown += ('- Active shop=' + $runtimeSnapshot.ActiveShopRef + '; active session=' + $runtimeSnapshot.ActiveSessionRef + '; focus proof=' + $runtimeSnapshot.ActiveShopEvidenceStatus + '; focus age seconds=' + $runtimeSnapshot.ActiveFocusProofAgeSeconds)
    $markdown += ('- Connected sessions=' + $counts.ConnectedSessions + '; authoritative shops=' + $counts.AuthoritativeShopCount + '; duplicate sessions=' + $counts.DuplicateSessions + '; recoverable standbys=' + $counts.RecoverableStandbySessions + '; standby-capable sessions=' + $counts.StandbyCapabilitySessions + '; unmapped connected=' + $counts.UnmappedConnectedSessions)
    foreach ($shop in $runtimeSnapshot.Shops) {
        $markdown += ('- ' + $shop.ShopRef + ': owner=' + $shop.OwnerSessionRef + '; connected=' + $shop.OwnerConnected + '; initialized=' + $shop.OwnerInitialized + '; client=' + $shop.OwnerClientPresent + '; invalidated=' + $shop.OwnerClientInvalidated + '; retired=' + $shop.OwnerClientIdentityRetired + '; standbys=' + $shop.StandbyCount + '; recoverable=' + $shop.RecoverableStandbyCount)
        foreach ($standby in $shop.Standbys) {
            $markdown += ('  - ' + $standby.SessionRef + ': connected=' + $standby.Connected + '; recoverable=' + $standby.Recoverable + '; initialized=' + $standby.Initialized + '; closing=' + $standby.Closing)
        }
    }
} else {
    $markdown += '- Runtime ShopKey mapping snapshot unavailable or stale; no mapping conclusion is made.'
}
$markdown += @(
    '',
    '## Privacy',
    '- Customer messages, account nicknames, credentials, cookies, target URLs, raw ShopKeys, raw WebSocket session IDs, raw command lines, raw logs and screenshots are excluded.',
    '- Shop and session references are HMAC aliases with a process-local random salt; aliases are not stable across Bot restarts.',
    '- No network upload or chat send is performed. CDP requests are loopback-only and read-only.',
    '',
    '## Limitations'
)
foreach ($limitation in $report.Limitations) {
    $markdown += ('- ' + $limitation)
}
$markdown | Out-File -LiteralPath $markdownPath -Encoding UTF8

Write-Host ('JSON report: ' + $jsonPath)
Write-Host ('Markdown report: ' + $markdownPath)
Write-Host ('Bot=' + $botCount + '; Qianniu=' + $workbenchCount + '; bench_im=' + $benchImRenderers.Count + '; WS41010=' + $webSocketReachable)
