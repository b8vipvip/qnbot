param([string]$BotAssembly = 'src\Bot\bin\x64\Release\Bot.exe')
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $BotAssembly).Path)
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static'
$validator = $assembly.GetType('Bot.ChromeNs.PreSendAnswerValidator', $true)
$semantics = $assembly.GetType('Bot.Knowledge.KnowledgeEngineV2Semantics', $true)
$number = $validator.GetMethod('ContainsNumericEvidence', $flags)
$polarity = $validator.GetMethod('DetectPolarity', $flags)
$scope = $semantics.GetMethod('HasAffirmativeTerm', $flags)
$applicable = $semantics.GetMethod('IsApplicable', $flags)
$parse = $semantics.GetMethod('Parse', $flags)
function Assert-Equal($actual, $expected, [string]$label) {
    if ($actual -ne $expected) { throw "$label : expected $expected, actual $actual" }
}
Assert-Equal ($number.Invoke($null, @('价格110元', '10元'))) $false 'numeric suffix'
Assert-Equal ($number.Invoke($null, @('价格1.5元', '15元'))) $false 'decimal point'
Assert-Equal ($number.Invoke($null, @('价格1,100元', '100元'))) $false 'grouped number suffix'
Assert-Equal ($number.Invoke($null, @('价格10 元', '10元'))) $true 'exact numeric token'
Assert-Equal ($number.Invoke($null, @('价格1,100元', '1100元'))) $true 'grouped number equivalence'
Assert-Equal ($number.Invoke($null, @('不是10元', '10元'))) $false 'negated numeric evidence'
Assert-Equal ($polarity.Invoke($null, @('不支持'))) -1 'negative polarity'
Assert-Equal ($polarity.Invoke($null, @('支持'))) 1 'positive polarity'
Assert-Equal ($polarity.Invoke($null, @('A支持，B不支持'))) 0 'mixed polarity'
Assert-Equal ($scope.Invoke($null, @('我没有会员', '会员'))) $false 'negative scope'
Assert-Equal ($scope.Invoke($null, @('我有会员', '会员'))) $true 'affirmative scope'
Assert-Equal ($scope.Invoke($null, @('版本1.5', '版本15'))) $false 'version punctuation'
$recordType = $assembly.GetType('Bot.Knowledge.KnowledgeV2Record', $true)
$queryType = $assembly.GetType('Bot.Knowledge.KnowledgeV2Query', $true)
$record = [Activator]::CreateInstance($recordType)
$query = [Activator]::CreateInstance($queryType, $true)
$query.Original = '商品ID:12345678 商品ID:87654321'
$query.Entities = [System.Collections.Generic.List[string]]@('product:12345678', 'product:87654321')
$record.ProductIds = [System.Collections.Generic.List[string]]@('12345678')
$args1 = [object[]]@($record, $query, '')
Assert-Equal ($applicable.Invoke($null, $args1)) $false 'partial product coverage'
$record.ProductIds = [System.Collections.Generic.List[string]]@('12345678', '87654321')
$record.Exclusions = [System.Collections.Generic.List[string]]@(1..20 | ForEach-Object { "unused-condition-$_" })
$record.Exclusions.Add('会员')
$query.Original = '我有会员'
$args2 = [object[]]@($record, $query, '')
Assert-Equal ($applicable.Invoke($null, $args2)) $false 'late exclusion'
$record.Exclusions.Clear()
$record.RequiredContext = [System.Collections.Generic.List[string]]@('会员')
$query.Original = '我没有会员'
$args3 = [object[]]@($record, $query, '')
Assert-Equal ($applicable.Invoke($null, $args3)) $false 'negated required context'
$query.Original = '我有会员'
$args4 = [object[]]@($record, $query, '')
Assert-Equal ($applicable.Invoke($null, $args4)) $true 'valid required context'
$many = (1..11 | ForEach-Object { '商品ID:' + (10000000 + $_) }) -join ' '
$overflow = $parse.Invoke($null, [object[]]@($many, $null))
$conflictProperty = $queryType.GetProperty('ProductEvidenceConflict', [Reflection.BindingFlags]'Instance,Public,NonPublic')
Assert-Equal ($conflictProperty.GetValue($overflow, $null)) $true 'product evidence overflow'
# Exercise the actual compiled in-memory recall index with a large same-intent bucket.
# This is synthetic index coverage, not a live SQLite/UI or long-duration benchmark.
$service = $assembly.GetType('Bot.Knowledge.KnowledgeEngineV2Service', $true)
$buildSnapshot = $service.GetMethod('BuildSnapshot', $flags)
$recall = $service.GetMethod('Recall', $flags)
$listType = [System.Collections.Generic.List[string]].GetGenericTypeDefinition().MakeGenericType([type[]]@($recordType))
$records = [Activator]::CreateInstance($listType)
$largeQuery = $parse.Invoke($null, [object[]]@('商品ID:12345678 手机怎么使用', $null))
for ($i = 0; $i -le 5000; $i++) {
    $entry = [Activator]::CreateInstance($recordType)
    $entry.Id = 'record-' + $i.ToString('D5')
    $entry.Title = '手机操作说明'
    $entry.Answer = '请查看商品说明'
    $entry.Intent = $largeQuery.Intent
    $entry.Predicate = $largeQuery.Predicate
    $entry.Entities = [System.Collections.Generic.List[string]]@('手机')
    $entry.ProductIds = [System.Collections.Generic.List[string]]@([string](20000000 + $i))
    $entry.Status = 'active'
    $entry.SourceType = 'manual_reviewed_direct'
    $entry.Type = 'business_fact'
    $entry.Enabled = $true
    $entry.Authority = 1
    $entry.Confidence = 1
    if ($i -eq 5000) { $entry.ProductIds = [System.Collections.Generic.List[string]]@('12345678') }
    $records.Add($entry)
}
$beforeBytes = [GC]::GetTotalMemory($true)
$timer = [Diagnostics.Stopwatch]::StartNew()
$snapshot = $buildSnapshot.Invoke($null, [object[]]@('synthetic-benchmark', $records))
$buildMs = $timer.ElapsedMilliseconds
$latencies = @()
for ($i = 0; $i -lt 30; $i++) {
    $timer.Restart()
    $recalled = $recall.Invoke($null, [object[]]@($snapshot, $largeQuery))
    $latencies += $timer.Elapsed.TotalMilliseconds
    Assert-Equal ($recalled.Contains(5000)) $true 'large corpus applicable product retained'
    Assert-Equal ($recalled.Count -le 96) $true 'large corpus bounded ranking'
}
$sorted = @($latencies | Sort-Object)
$p95 = $sorted[[Math]::Ceiling($sorted.Count * 0.95) - 1]
$deltaBytes = [GC]::GetTotalMemory($true) - $beforeBytes
Write-Host ("Synthetic V2 recall: records={0}; build_ms={1}; recall_p95_ms={2:N2}; retained_bytes_delta={3}" -f $records.Count, $buildMs, $p95, $deltaBytes)
Write-Host 'Knowledge V2 compiled fact and scope regression cases passed.'
