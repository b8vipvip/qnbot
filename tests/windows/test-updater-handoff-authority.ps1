$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$updaterPath = Join-Path $root 'src\Bot\Update\BotAutoUpdater.ps1'
if (-not (Test-Path -LiteralPath $updaterPath -PathType Leaf)) {
    throw "Updater script not found: $updaterPath"
}

$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $updaterPath,
    [ref]$tokens,
    [ref]$errors)
if (@($errors).Count -gt 0) {
    $errors | Format-List *
    throw 'BotAutoUpdater.ps1 has parser errors.'
}

$authorityAst = $ast.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-BotWebSocketHandoffState'
}, $true)
if ($null -eq $authorityAst) {
    throw 'Get-BotWebSocketHandoffState was not found.'
}
Invoke-Expression $authorityAst.Extent.Text

$script:InstallState = $null
$script:WatchdogState = $null
$script:ListenerState = $null
$script:OwnerStates = @{}
$script:StrictBindable = $true

function Get-InstallProcessState([string]$TargetInstallDir) { return $script:InstallState }
function Get-WatchdogState([string]$TargetInstallDir) { return $script:WatchdogState }
function Get-LoopbackPortListenerState([int]$Port) { return $script:ListenerState }
function Get-LiveProcessStateById([int]$ProcessId) {
    if ($script:OwnerStates.ContainsKey($ProcessId)) { return $script:OwnerStates[$ProcessId] }
    return [pscustomobject]@{ Known = $true; Exists = $false; Process = $null; Error = '' }
}
function Test-LoopbackPortBindable([int]$Port) { return [bool]$script:StrictBindable }

function Assert-True([bool]$Value, [string]$Message) {
    if (-not $Value) { throw "ASSERT TRUE FAILED: $Message" }
}
function Assert-False([bool]$Value, [string]$Message) {
    if ($Value) { throw "ASSERT FALSE FAILED: $Message" }
}
function Reset-State {
    $script:InstallState = [pscustomobject]@{ Known = $true; Processes = @(); Error = '' }
    $script:WatchdogState = [pscustomobject]@{ Known = $true; Processes = @(); Error = '' }
    $script:ListenerState = [pscustomobject]@{ Known = $true; Listeners = @(); Error = '' }
    $script:OwnerStates = @{}
    $script:StrictBindable = $true
}

# Empty state: ready.
Reset-State
$state = Get-BotWebSocketHandoffState 'C:\qianniu-bot-x64' 41010
Assert-True ([bool]$state.Ready) 'empty known state must be ready'

# The exact cardinality that failed in the field: one target-install process must not throw Count/property errors.
Reset-State
$script:InstallState = [pscustomobject]@{
    Known = $true
    Processes = @([pscustomobject]@{ Pid = 101; Name = 'Bot.exe'; ExecutablePath = 'C:\qianniu-bot-x64\Bin\Bot.exe' })
    Error = ''
}
$state = Get-BotWebSocketHandoffState 'C:\qianniu-bot-x64' 41010
Assert-False ([bool]$state.Ready) 'one live target-install process must block readiness'
Assert-True (@($state.LiveInstallProcesses).Count -eq 1) 'one live process must stay a collection'

# One target watchdog is part of the same authority and blocks readiness.
Reset-State
$script:WatchdogState = [pscustomobject]@{
    Known = $true
    Processes = @([pscustomobject]@{ Pid = 202; Name = 'powershell.exe'; CommandLine = 'bot-process-watchdog.ps1 C:\qianniu-bot-x64' })
    Error = ''
}
$state = Get-BotWebSocketHandoffState 'C:\qianniu-bot-x64' 41010
Assert-False ([bool]$state.Ready) 'one live watchdog must block readiness'
Assert-True (@($state.LiveWatchdogs).Count -eq 1) 'one watchdog must stay a collection'

# A stale LISTEN row points at a dead PID: diagnostic only, no veto.
Reset-State
$script:ListenerState = [pscustomobject]@{
    Known = $true
    Listeners = @([pscustomobject]@{ OwningProcess = 303; LocalAddress = '127.0.0.1' })
    Error = ''
}
$script:OwnerStates[303] = [pscustomobject]@{ Known = $true; Exists = $false; Process = $null; Error = '' }
$script:StrictBindable = $false
$state = Get-BotWebSocketHandoffState 'C:\qianniu-bot-x64' 41010
Assert-True ([bool]$state.Ready) 'dead PID LISTEN row must not veto the canonical authority'
Assert-True (@($state.StaleListenerPids).Count -eq 1) 'stale PID must remain diagnostic evidence'
Assert-False ([bool]$state.StrictBindableDiagnostic) 'strict bind can be blocked while readiness remains true'

# A real foreign live listener owner blocks readiness.
Reset-State
$script:ListenerState = [pscustomobject]@{
    Known = $true
    Listeners = @([pscustomobject]@{ OwningProcess = 404; LocalAddress = '127.0.0.1' })
    Error = ''
}
$script:OwnerStates[404] = [pscustomobject]@{
    Known = $true
    Exists = $true
    Process = [pscustomobject]@{ Pid = 404; Name = 'foreign.exe'; ExecutablePath = 'C:\other\foreign.exe' }
    Error = ''
}
$state = Get-BotWebSocketHandoffState 'C:\qianniu-bot-x64' 41010
Assert-False ([bool]$state.Ready) 'real live listener owner must block readiness'
Assert-True (@($state.LiveListenerOwners).Count -eq 1) 'one live owner must stay a collection'

# Unknown sensor state fails closed.
Reset-State
$script:ListenerState = [pscustomobject]@{ Known = $false; Listeners = @(); Error = 'sensor unavailable' }
$state = Get-BotWebSocketHandoffState 'C:\qianniu-bot-x64' 41010
Assert-False ([bool]$state.Ready) 'unknown listener sensor must fail closed'
Assert-False ([bool]$state.SensorStateKnown) 'unknown sensor must be visible in state'

# Multiple items remain stable as arrays too.
Reset-State
$script:InstallState = [pscustomobject]@{
    Known = $true
    Processes = @(
        [pscustomobject]@{ Pid = 501; Name = 'Bot.exe'; ExecutablePath = 'C:\qianniu-bot-x64\Bin\Bot.exe' },
        [pscustomobject]@{ Pid = 502; Name = 'helper.exe'; ExecutablePath = 'C:\qianniu-bot-x64\Bin\helper.exe' }
    )
    Error = ''
}
$state = Get-BotWebSocketHandoffState 'C:\qianniu-bot-x64' 41010
Assert-False ([bool]$state.Ready) 'multiple target processes must block readiness'
Assert-True (@($state.LiveInstallProcesses).Count -eq 2) 'multiple process cardinality must be preserved'

Write-Host 'Updater canonical handoff authority PowerShell 5.1 runtime tests passed.'
