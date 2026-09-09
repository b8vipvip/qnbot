$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$injectPath = Join-Path $root 'src\Bin\inject.js'
$qnPath = Join-Path $root 'src\Bot\Common\QNInject.cs'
$oldMarker = '20260906-zh-cn-ws-retire-v10'
$newMarker = '20260909-ws-recoverable-standby-v11'

function Replace-Required([string]$Text, [string]$Old, [string]$New, [string]$Name) {
    if ($Text.Contains($Old)) { return $Text.Replace($Old, $New) }
    if ($Text.Contains($New)) { return $Text }
    throw "Required patch fragment missing: $Name"
}

$inject = [IO.File]::ReadAllText($injectPath).Replace("`r`n", "`n")
$inject = Replace-Required $inject "window.__qnbotInjectVersion = `"$oldMarker`";" "window.__qnbotInjectVersion = `"$newMarker`";" 'inject marker'
$inject = Replace-Required $inject '  var websocketRetired = false;' '  var websocketStandbyUntil = 0;' 'retired state'
$inject = Replace-Required $inject "    if (websocketRetired) return;`n    var payload = JSON.stringify({ type: type, response: response || `"`" });" '    var payload = JSON.stringify({ type: type, response: response || "" });' 'send retired guard'
$inject = Replace-Required $inject '  function scheduleReconnect() {' '  function scheduleReconnect(delayMs) {' 'reconnect signature'
$inject = Replace-Required $inject '    if (websocketRetired || reconnectTimer) return;' "    if (reconnectTimer) return;`n    var delay = Math.max(1000, Number(delayMs) || 3000);`n    if (websocketStandbyUntil > Date.now()) {`n      delay = Math.max(delay, websocketStandbyUntil - Date.now());`n    }" 'reconnect lease'
$inject = Replace-Required $inject "    }, 3000);`n  }`n`n  function setupWebSocket() {" "    }, delay);`n  }`n`n  function setupWebSocket() {" 'reconnect delay'
$inject = Replace-Required $inject "  function setupWebSocket() {`n    if (websocketRetired) return;`n    var old = window.chatWebsocket;" "  function setupWebSocket() {`n    if (websocketStandbyUntil > Date.now()) {`n      scheduleReconnect(websocketStandbyUntil - Date.now());`n      return;`n    }`n    var old = window.chatWebsocket;" 'setup standby lease'
$inject = Replace-Required $inject "        window.chatWebsocket = socket;`n        log(`"websocket connected`");" "        window.chatWebsocket = socket;`n        websocketStandbyUntil = 0;`n        log(`"websocket connected`", `"recoverable-standby-v11`");" 'onopen resync'
$inject = Replace-Required $inject '            websocketRetired = true;' '            websocketStandbyUntil = Date.now() + 15000;' 'duplicate standby state'
$inject = Replace-Required $inject '            log("websocket retired by Bot", param.reason || "duplicate");' '            log("websocket standby requested by Bot", param.reason || "duplicate");' 'duplicate standby log'
$inject = Replace-Required $inject '            try { socket.close(1000, "qnbot-duplicate-retired"); } catch (e) {}' "            try { socket.close(1000, `"qnbot-duplicate-standby`"); } catch (e) {}`n            scheduleReconnect(15000);" 'duplicate standby reconnect'
$inject = Replace-Required $inject '        if (!websocketRetired) scheduleReconnect();' '        scheduleReconnect();' 'onclose reconnect'
$inject = Replace-Required $inject '      duplicateRetire: true,' "      duplicateRetire: false,`n      recoverableStandby: true," 'status capability'
if ($inject.Contains('websocketRetired')) { throw 'Permanent websocketRetired state remains after v11 patch' }
[IO.File]::WriteAllText($injectPath, $inject, (New-Object Text.UTF8Encoding($true)))

$qn = [IO.File]::ReadAllText($qnPath).Replace("`r`n", "`n")
$qn = Replace-Required $qn "private const string injectVersionMarker = `"$oldMarker`";" "private const string injectVersionMarker = `"$newMarker`";" 'QNInject marker'

$runningReplacement = @'
                if (IsWorkbenchRunning())
                {
                    // v11 is non-destructive. Patch the on-disk WebView payload without
                    // terminating the logged-in Qianniu process. A loaded v10 page can
                    // consume v11 after a local page reload instead of a full process restart.
                    Log.Info("Qianniu inject v11 migration: live patch; no close/kill/restart. marker=" + injectVersionMarker);
                }
'@
if (-not $qn.Contains('Qianniu inject v11 migration: live patch; no close/kill/restart.')) {
    $runningPattern = '(?ms)^                if \(IsWorkbenchRunning\(\)\)\n                \{.*?^                    await Task\.Delay\(3000\);\n                \}'
    $updated = [regex]::Replace($qn, $runningPattern, $runningReplacement, 1)
    if ($updated -eq $qn) { throw 'Required patch fragment missing: running Qianniu migration' }
    $qn = $updated
}

$resultReplacement = @'
                if (success == needInjectPaths.Count)
                {
                    Log.Info("Qianniu inject v11 written to disk; no process restart required. A loaded v10 WebView switches after local page reload. marker=" + injectVersionMarker);
                }
                else if (success > 0)
                {
                    Log.Error("Qianniu inject v11 partial migration; keep Qianniu running and retry non-destructively. success=" + success + ", total=" + needInjectPaths.Count);
                }
                else
                {
                    Log.Error("Qianniu inject v11 migration failed; keep Qianniu running and retry non-destructively. marker=" + injectVersionMarker);
                }
'@
if (-not $qn.Contains('Qianniu inject v11 written to disk; no process restart required.')) {
    $resultPattern = '(?ms)^                if \(success == needInjectPaths\.Count\)\n                \{\n                    MessageBox\.Show\(".*?"\);\n                \}\n                else if \(success > 0\)\n                \{\n                    MessageBox\.Show\(".*?"\);\n                \}\n                else\n                \{\n                    MessageBox\.Show\(".*?"\);\n                \}'
    $updated = [regex]::Replace($qn, $resultPattern, $resultReplacement, 1)
    if ($updated -eq $qn) { throw 'Required patch fragment missing: migration result' }
    $qn = $updated
}

$start = $qn.IndexOf('public static async Task StartInject()')
$end = $qn.IndexOf('private static string FindInstallPath()', $start)
if ($start -lt 0 -or $end -le $start) { throw 'Cannot locate QNInject.StartInject for safety validation' }
$startBody = $qn.Substring($start, $end - $start)
if ($startBody.Contains('KillWorkbenchProcesses();')) { throw 'Legacy destructive QNInject migration remains in StartInject' }
if ($startBody.Contains('MessageBox.Show(') -and $startBody.Contains('success == needInjectPaths.Count')) {
    # The early install/resource error dialogs are allowed; the success/restart result block is not.
    $resultStart = $startBody.IndexOf('if (success == needInjectPaths.Count)')
    if ($resultStart -ge 0 -and $startBody.Substring($resultStart).Contains('MessageBox.Show(')) { throw 'Legacy restart result dialog remains in StartInject' }
}
[IO.File]::WriteAllText($qnPath, $qn, (New-Object Text.UTF8Encoding($true)))

Write-Host "Applied recoverable Qianniu inject migration: $newMarker"
