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
Assert-Equal ($scope.Invoke($null, @('会员未开通', '会员'))) $false 'suffix-negated condition'
Assert-Equal ($scope.Invoke($null, @('如果有会员', '会员'))) $false 'hypothetical condition'
Assert-Equal ($scope.Invoke($null, @('版本1.50', '版本1.5'))) $false 'numeric prefix'

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
$memoryType = $assembly.GetType('Bot.Knowledge.KnowledgeV2WorkingMemory', $true)
$memory = [Activator]::CreateInstance($memoryType)
$memory.UpdatedAt = [DateTime]::Now
$memory.Entities = [System.Collections.Generic.List[string]]@(1..10 | ForEach-Object { 'product:' + (10000000 + $_) })
$follow = $parse.Invoke($null, [object[]]@('这款支持手机吗', $memory))
Assert-Equal ($follow.Entities.Count) 10 'followup preserves every explicit product'
Assert-Equal ($follow.Entities.Contains('product:10000010')) $true 'tenth product not dropped'
$memoryConflict = $memoryType.GetProperty('ProductEvidenceConflict', [Reflection.BindingFlags]'Instance,Public,NonPublic')
$memoryConflict.SetValue($memory, $true, $null)
$follow = $parse.Invoke($null, [object[]]@('这个呢', $memory))
Assert-Equal ($conflictProperty.GetValue($follow, $null)) $true 'overflow ambiguity survives followup'
$follow = $parse.Invoke($null, [object[]]@('这款商品ID:12345678 是否支持', $memory))
Assert-Equal ($conflictProperty.GetValue($follow, $null)) $false 'explicit fresh product replaces previous ambiguity'


$scopedNumbers = $validator.GetMethod('HasUnverifiedScopedNumbers', $flags)
$price = "V2知识1：`n问题：首单多少钱`n答案：首单价格10元`n商品ID：12345678`n适用条件：首单`n"
Assert-Equal ($scopedNumbers.Invoke($null, @('商品ID:12345678 首单多少钱','首单价格10元',$price,''))) $false 'same product and qualifier'
Assert-Equal ($scopedNumbers.Invoke($null, @('商品ID:87654321 首单多少钱','首单价格10元',$price,''))) $true 'price from wrong product'
Assert-Equal ($scopedNumbers.Invoke($null, @('商品ID:12345678 续费多少钱','续费10元',$price,''))) $true 'renewal cannot inherit first order price'
Assert-Equal ($scopedNumbers.Invoke($null, @('商品ID:12345678 多少钱','价格10元',$price,''))) $true 'omitted first order condition'
Assert-Equal ($scopedNumbers.Invoke($null, @('商品ID:12345678 退款多少','退款10元',$price,''))) $true 'sale price cannot authorize refund'
$shipping = "V2知识1：`n问题：发货时效`n答案：发货2天`n商品ID：12345678`n"
Assert-Equal ($scopedNumbers.Invoke($null, @('商品ID:12345678 发货多久','发货2天',$shipping,''))) $false 'shipping time is supported'
Assert-Equal ($scopedNumbers.Invoke($null, @('商品ID:12345678 退款多久','退款2天',$shipping,''))) $true 'shipping time cannot become refund time'
Assert-Equal ($scopedNumbers.Invoke($null, @('商品ID:12345678 首单多少钱','首单不是10元',$price,''))) $true 'positive evidence cannot authorize negative numeric claim'
$unverified = $validator.GetMethod('HasUnverifiedProductConclusion', $flags)
$promise = $validator.GetMethod('HasTransactionalPromise', $flags)
$overlap = $semantics.GetMethod('ProductScopesOverlap', $flags)
$productQuestion = '商品ID:12345678 是否支持'
Assert-Equal ($unverified.Invoke($null, @($productQuestion, '支持', ''))) $true 'unsupported product claim'
Assert-Equal ($unverified.Invoke($null, @($productQuestion, 'A支持，B不支持', ''))) $true 'mixed unsupported product claims'
Assert-Equal ($unverified.Invoke($null, @($productQuestion, '无法确认是否支持，请提供型号', ''))) $false 'clarification without claim'
$scopedPositive = "V2知识1：`n问题：是否支持`n答案：支持`n商品ID：12345678`n"
$unrelated = "V2知识1：`n问题：如何付款`n答案：请查看页面`n商品ID：12345678`n"
Assert-Equal ($unverified.Invoke($null, @($productQuestion, '支持', $scopedPositive))) $false 'approved capability matches exact product'
Assert-Equal ($unverified.Invoke($null, @($productQuestion, '支持', $unrelated))) $true 'unrelated approved answer cannot authorize capability'
Assert-Equal ($unverified.Invoke($null, @($productQuestion, '不支持', $scopedPositive))) $true 'wrong direction blocked'
Assert-Equal ($unverified.Invoke($null, @($productQuestion, '商品ID:87654321 支持', $scopedPositive))) $true 'generated new product not authorized'
Assert-Equal ($unverified.Invoke($null, @('商品ID:12345678 商品ID:87654321 是否支持', '支持', $scopedPositive))) $true 'partial scope not sufficient'
$tvFact = "V2知识1：`n问题：电视是否支持`n答案：电视支持`n商品ID：12345678`n"
Assert-Equal ($unverified.Invoke($null, @('商品ID:12345678 手机是否支持','支持',$tvFact))) $true 'TV evidence cannot authorize phone capability'
Assert-Equal ($unverified.Invoke($null, @('商品ID:12345678 电视是否支持','支持',$tvFact))) $false 'same feature capability'


Assert-Equal ($promise.Invoke($null, @('我马上给您退款'))) $true 'unauthorized transactional promise'
Assert-Equal ($promise.Invoke($null, @('请提供订单号核实退款情况'))) $false 'refund clarification'
$left = [Activator]::CreateInstance($recordType)
$right = [Activator]::CreateInstance($recordType)
$left.ProductIds = [System.Collections.Generic.List[string]]@('12345678')
$right.ProductIds = [System.Collections.Generic.List[string]]@('87654321')
Assert-Equal ($overlap.Invoke($null, @($left, $right))) $false 'disjoint product scopes'
$right.ProductIds.Add('12345678')
Assert-Equal ($overlap.Invoke($null, @($left, $right))) $true 'overlapping product scopes'
$ruleService = $assembly.GetType('Bot.ChromeNs.VisualUnderstandingRuleService', $true)
$ruleType = $assembly.GetType('Bot.ChromeNs.VisualUnderstandingRuleEntity', $true)
$fingerprint = $ruleService.GetMethod('RuleFingerprint', $flags)
$rule = [Activator]::CreateInstance($ruleType)
$rule.EntityId = 'rule'
$rule.ShopKey = 'shop-a'
$rule.Seller = 'seller-a'
$rule.Name = 'synthetic'
$rule.RequiredKeywords = '账号中心'
$rule.Enabled = $true
$originalFingerprint = $fingerprint.Invoke($null, @($rule))
$rule.ExcludedKeywords = '登录'
Assert-Equal ($fingerprint.Invoke($null, @($rule)) -ne $originalFingerprint) $true 'changed exclusion invalidates rule evidence'
$rule.ExcludedKeywords = $null
$rule.Enabled = $false
Assert-Equal ($fingerprint.Invoke($null, @($rule)) -ne $originalFingerprint) $true 'disabled rule invalidates evidence'
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

# Exercise the actual corpus generation fence before any row is published.
$repoType = $assembly.GetType('Bot.Knowledge.KnowledgeEngineV2Repository', $true)
$stateType = $repoType.GetNestedType('RepositoryState', [Reflection.BindingFlags]'NonPublic')
$state = [Activator]::CreateInstance($stateType, $true)
$version = $stateType.GetField('CorpusVersion')
$mutation = $stateType.GetField('LastMutationId')
$mark = $repoType.GetMethod('MarkCorpusMutation', $flags)
Assert-Equal ($version.GetValue($state)) 0 'initial corpus generation'
$null = $mark.Invoke($null, [object[]]@($state, 'first'))
Assert-Equal ($version.GetValue($state)) 1 'invalidate generation before first write'
Assert-Equal ($mutation.GetValue($state)) 'first' 'single-row identity'
$null = $mark.Invoke($null, [object[]]@($state, $null))
Assert-Equal ($version.GetValue($state)) 2 'bulk commit invalidates prior generation'
Assert-Equal ($mutation.GetValue($state)) $null 'bulk change cannot patch one row'

# A very large exact alias bucket must remain bounded and cannot imply consensus.
$exactRecords = [Activator]::CreateInstance($listType)
$exactQuery = $parse.Invoke($null, [object[]]@('手机操作说明', $null))
for ($i = 0; $i -lt 120; $i++) {
    $entry = [Activator]::CreateInstance($recordType)
    $entry.Id = 'same-' + $i.ToString('D3')
    $entry.Title = '手机操作说明'
    $entry.Answer = '支持'
    if ($i -eq 119) { $entry.Answer = '不支持' }
    $entry.Intent = $exactQuery.Intent
    $entry.Predicate = $exactQuery.Predicate
    $entry.Enabled = $true
    $entry.Status = 'active'
    $entry.Type = 'business_fact'
    $entry.SourceType = 'manual_reviewed_direct'
    $entry.Confidence = 1
    $entry.Authority = 1
    $exactRecords.Add($entry)
}
$exactSnapshot = $buildSnapshot.Invoke($null, [object[]]@('exact-overflow', $exactRecords))
$recalled = $recall.Invoke($null, [object[]]@($exactSnapshot, $exactQuery))
Assert-Equal ($recalled.Count -le 96) $true 'exact match overflow bounded'
$ambiguous = $queryType.GetProperty('RecallBudgetAmbiguous', [Reflection.BindingFlags]'Instance,Public,NonPublic')
Assert-Equal ($ambiguous.GetValue($exactQuery, $null)) $true 'exact overflow cannot hide conflict'

# Independent changes relative to the same cloud baseline must converge without
# keeping an arbitrary contradictory production answer.
$cloudType = $assembly.GetType('Bot.Knowledge.KnowledgeV2CloudSyncService', $true)
$merge = $cloudType.GetMethod('MergeCloudAndLocal', $flags)
$contentFingerprint = $cloudType.GetMethod('RecordContentFingerprint', $flags)
function New-CloudRecord([string]$answer) {
    $r = [Activator]::CreateInstance($recordType)
    $r.Id = 'stable-cloud-id'
    $r.Title = '商品是否支持'
    $r.Answer = $answer
    $r.ShortAnswer = $answer
    $r.Type = 'business_fact'
    $r.Status = 'active'
    $r.Enabled = $true
    $r.SourceType = 'manual_reviewed_direct'
    $r.Authority = 1
    $r.Confidence = 1
    return $r
}
$baseRecord = New-CloudRecord '旧答案'
$baseline = [System.Collections.Generic.Dictionary[string,string]]::new()
$baseline.Add($baseRecord.Id, $contentFingerprint.Invoke($null, [object[]]@($baseRecord)))
$cloud = [Activator]::CreateInstance($listType)
$local = [Activator]::CreateInstance($listType)
$cloud.Add((New-CloudRecord '支持'))
$local.Add((New-CloudRecord '不支持'))
$mergeArgs = [object[]]::new(4)
$mergeArgs[0] = $cloud
$mergeArgs[1] = $local
$mergeArgs[2] = $true
$mergeArgs[3] = $baseline
$merged = $merge.Invoke($null, $mergeArgs)
Assert-Equal ($merged.Count) 2 'both conflicting variants retained'
Assert-Equal (@($merged | Where-Object { $_.Enabled }).Count) 0 'no arbitrary active cloud winner'
Assert-Equal (@($merged | Where-Object { $_.Id -eq 'stable-cloud-id' }).Count) 1 'stable ID retained'

Write-Host 'Knowledge V2 compiled fact and scope regression cases passed.'
