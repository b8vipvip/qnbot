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
Assert-Equal $overflow.ProductEvidenceConflict $true 'product evidence overflow'
Write-Host 'Knowledge V2 compiled fact and scope regression cases passed.'
