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
$qn = Replace-Required $qn @'
                if (IsWorkbenchRunning())
                {
                    if (MessageBox.Show("检测到千牛正在运行。需要先退出千牛后注入插件，是否现在关闭千牛并继续？", "提示", MessageBoxButton.YesNo)
                        == MessageBoxResult.No)
                    {
                        return;
                    }
                    else
                    {
                        KillWorkbenchProcesses();
                    }
                    await Task.Delay(3000);
                }
'@ @'
                if (IsWorkbenchRunning())
                {
                    // v11 migration is non-destructive: update the on-disk WebView payload without
                    // terminating the already logged-in Qianniu process. A currently loaded v10
                    // page can consume v11 after a local page reload instead of a full Qianniu restart.
                    Log.Info("检测到千牛正在运行：执行无破坏注入升级，不关闭、不Kill、不重启千牛。marker=" + injectVersionMarker);
                }
'@ 'running Qianniu migration'
$qn = Replace-Required $qn @'
                if (success == needInjectPaths.Count)
                {
                    MessageBox.Show("千牛插件注入成功，请重新启动千牛!!");
                }
                else if (success > 0)
                {
                    MessageBox.Show("千牛插件部分注入成功，请重新启动千牛后检查连接状态。");
                }
                else
                {
                    MessageBox.Show("千牛插件注入失败!!");
                }
'@ @'
                if (success == needInjectPaths.Count)
                {
                    Log.Info("千牛注入升级已写入磁盘：无需重启千牛；当前仍运行旧脚本的WebView在局部页面重新加载后切换到 " + injectVersionMarker);
                }
                else if (success > 0)
                {
                    Log.Error("千牛注入仅部分升级成功：保持千牛运行，不自动重启；等待后续无破坏重试。 success=" + success + ", total=" + needInjectPaths.Count);
                }
                else
                {
                    Log.Error("千牛注入升级失败：保持千牛运行，不自动重启；等待后续无破坏重试。 marker=" + injectVersionMarker);
                }
'@ 'migration result'
[IO.File]::WriteAllText($qnPath, $qn, (New-Object Text.UTF8Encoding($true)))

Write-Host "Applied recoverable Qianniu inject migration: $newMarker"
