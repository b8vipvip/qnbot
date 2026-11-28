param(
    [string]$BotAssembly = 'src\Bot\bin\x64\Release\Bot.exe',
    [string]$DbPath = '',
    [ValidateSet('parent','before','after')][string]$Mode = 'parent'
)
$ErrorActionPreference = 'Stop'
$BotAssembly = (Resolve-Path -LiteralPath $BotAssembly).Path
$bin = Split-Path $BotAssembly
$env:PATH = "$bin;$(Join-Path (Get-Location) 'src\Bin');$env:PATH"
$bot = [Reflection.Assembly]::LoadFrom($BotAssembly)
$lib = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'BotLib.dll'))
$helperType = $lib.GetType('BotLib.Db.Sqlite.SQLiteHelper', $true)
$rowType = $bot.GetType('Bot.Knowledge.KnowledgeV2RecordRow', $true)
$revType = $bot.GetType('Bot.Knowledge.KnowledgeV2ManualEditRevisionRow', $true)
$metaType = $bot.GetType('Bot.Knowledge.KnowledgeV2MetaRow', $true)
$recordType = $bot.GetType('Bot.Knowledge.KnowledgeV2Record', $true)
$repo = $bot.GetType('Bot.Knowledge.KnowledgeEngineV2Repository', $true)
$service = $bot.GetType('Bot.Knowledge.KnowledgeEngineV2Service', $true)
$semantics = $bot.GetType('Bot.Knowledge.KnowledgeEngineV2Semantics', $true)
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static'
$toRow = $repo.GetMethod('ToRow', $flags)
$fromRow = $repo.GetMethod('FromRow', $flags)
$receiptFactory = $repo.GetMethod('RemoteMutationReceipt', $flags)
$receiptKey = 'a' * 64
function Assert-True([bool]$value, [string]$message) {
    if (!$value) { throw $message }
}
function Open-Db([string]$path) {
    $types = New-Object 'System.Collections.Generic.List[System.Type]'
    $types.Add($rowType); $types.Add($revType); $types.Add($metaType)
    $ctor = [object[]]::new(2)
    $ctor[0] = $path
    $ctor[1] = $types
    $constructor = $helperType.GetConstructors() | Where-Object { $_.GetParameters().Count -eq 2 } | Select-Object -First 1
    if ($null -eq $constructor) { throw 'SQLiteHelper constructor not found' }
    return $constructor.Invoke($ctor)
}
function New-TestRow([string]$id, [string]$answer, [string]$product) {
    $r = [Activator]::CreateInstance($recordType)
    $r.Id = $id
    $r.Title = '商品操作说明'
    $r.Answer = $answer
    $r.ShortAnswer = $answer
    $r.Subject = '商品'
    $r.Intent = 'general'
    $r.Predicate = 'operation'
    $r.Enabled = $true
    $r.Status = 'active'
    $r.Type = 'business_fact'
    $r.SourceType = 'manual_reviewed_direct'
    $r.Authority = 1
    $r.Confidence = 1
    $r.ProductIds = [System.Collections.Generic.List[string]]@($product)
    $r.CreatedAt = [DateTime]::Now
    $r.UpdatedAt = $r.CreatedAt
    $r.LastVerifiedAt = $r.CreatedAt
    return $toRow.Invoke($null, [object[]]@($r))
}
function Read-Row($db, $type, [string]$key, [string]$value) {
    return $db.ReadOneRecordByPropertyValue($type, $key, $value)
}
if ($Mode -ne 'parent') {
    $db = Open-Db $DbPath
    $row = New-TestRow 'r-00000' 'committed' '12345678'
    $receipt = $receiptFactory.Invoke($null, [object[]]@($receiptKey, 'rollback'))
    $revision = [Activator]::CreateInstance($revType)
    $revision.Id = 'b' * 32
    $revision.KnowledgeId = $row.Id
    $revision.Action = 'rollback'
    $revision.MessageRecordId = 'synthetic'
    $revision.PreviousJson = '{}'
    $revision.CurrentJson = '{}'
    $revision.CreatedAtTicks = [DateTime]::Now.Ticks
    $writes = New-Object 'System.Collections.Generic.List[System.Object]'
    $writes.Add($row); $writes.Add($revision); $writes.Add($receipt)
    if ($Mode -eq 'before') {
        # Use the same actual SQLite connection transaction helper, then
        # terminate after writing but before its commit.
        $connectionType = $lib.GetType('BotLib.Db.Sqlite.SQLiteConnection', $true)
        $actionType = [System.Action[string]].GetGenericTypeDefinition().MakeGenericType([type[]]@($connectionType))
        $action = [scriptblock]{
            param($conn)
            foreach ($item in $writes) { $null = $conn.InsertOrReplace($item) }
            [IO.File]::WriteAllText($DbPath + '.before-marker', 'inside transaction')
            Stop-Process -Id $PID -Force
        }
        $db.RunInTransaction($action -as $actionType)
        throw 'Crash injection did not terminate child'
    }
    $db.SaveRecordsInTransaction($writes)
    [IO.File]::WriteAllText($DbPath + '.after-marker', 'committed before acknowledgement')
    Stop-Process -Id $PID -Force
    throw 'Crash injection did not terminate child'
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('qnbot-v2-sqlite-' + [Guid]::NewGuid().ToString('N'))
New-Item -Path $root -ItemType Directory | Out-Null
try {
    $DbPath = Join-Path $root 'knowledge.db'
    $db = Open-Db $DbPath
    $writes = New-Object 'System.Collections.Generic.List[System.Object]'
    $timer = [Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt 5001; $i++) {
        $product = [string](20000000 + $i)
        if ($i -eq 0) { $product = '12345678' }
        $writes.Add((New-TestRow ('r-' + $i.ToString('D5')) 'original' $product))
    }
    $db.SaveRecordsInTransaction($writes)
    $writeMs = $timer.ElapsedMilliseconds
    Assert-True ($db.ReadTable($rowType).Count -eq 5001) 'SQLite initial corpus must contain 5001 records'
    $exe = Join-Path $PSHOME 'powershell.exe'
    foreach ($phase in @('before','after')) {
        & $exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $PSCommandPath -BotAssembly $BotAssembly -DbPath $DbPath -Mode $phase
        Assert-True (Test-Path ($DbPath + '.' + $phase + '-marker')) "Crash child must reach $phase boundary"
        Assert-True ($LASTEXITCODE -ne 0) "Child must terminate abnormally at $phase boundary"
        $readback = Open-Db $DbPath
        $row = Read-Row $readback $rowType 'Id' 'r-00000'
        $receipt = Read-Row $readback $metaType 'Key' ('remote_mutation:' + $receiptKey)
        $revision = Read-Row $readback $revType 'Id' ('b' * 32)
        if ($phase -eq 'before') {
            Assert-True ($row.Answer -eq 'original' -and $null -eq $receipt -and $null -eq $revision) 'Interrupted uncommitted mutation must roll back all rows'
        } else {
            Assert-True ($row.Answer -eq 'committed' -and $receipt.Value -eq 'rollback' -and $revision.Action -eq 'rollback') 'Committed mutation and receipt must survive termination together'
        }
        Assert-True ($readback.ReadTable($rowType).Count -eq 5001) 'Crash must not truncate corpus'
    }
    $listType = [System.Collections.Generic.List[string]].GetGenericTypeDefinition().MakeGenericType([type[]]@($recordType))
    $build = $service.GetMethod('BuildSnapshot', $flags)
    $recall = $service.GetMethod('Recall', $flags)
    $parse = $semantics.GetMethod('Parse', $flags)
    $query = $parse.Invoke($null, [object[]]@('商品ID:12345678 商品操作说明', $null))
    $readback = Open-Db $DbPath
    $base = [GC]::GetTotalMemory($true)
    $latencies = New-Object 'System.Collections.Generic.List[double]'
    # Reload and rebuild the actual persisted corpus, not just in-memory fixtures.
    for ($round = 0; $round -lt 20; $round++) {
        $timer.Restart()
        $records = [Activator]::CreateInstance($listType)
        $target = -1
        foreach ($row in $readback.ReadTable($rowType)) {
            if ($row.Id -eq 'r-00000') { $target = $records.Count }
            $records.Add($fromRow.Invoke($null, [object[]]@($row)))
        }
        $args2 = [object[]]::new(2)
        $args2[0] = 'sqlite-synthetic'
        $args2[1] = $records
        $snapshot = $build.Invoke($null, $args2)
        $matches = $recall.Invoke($null, [object[]]@($snapshot, $query))
        Assert-True ($target -ge 0 -and $matches.Contains($target)) 'Persisted applicable product must be retrieved'
        $latencies.Add($timer.Elapsed.TotalMilliseconds)
    }
    $sorted = @($latencies | Sort-Object)
    $p95 = $sorted[[int][Math]::Ceiling($sorted.Count * 0.95) - 1]
    $retained = [GC]::GetTotalMemory($true) - $base
    Write-Host ("Windows SQLite: 5001 records, write_ms={0}, reload_rebuild_recall_p95_ms={1:N1}, retained_bytes_delta={2}" -f $writeMs,$p95,$retained)
    Write-Host 'Windows SQLite commit-boundary crash recovery passed.'
} finally {
    # SQLiteHelper caches its connection for the lifetime of the process;
    # leave any locked temp files to runner teardown, never touch live shop data.
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
