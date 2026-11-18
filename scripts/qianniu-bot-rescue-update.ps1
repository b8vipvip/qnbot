[CmdletBinding()]
param(
    [string]$InstallDir = "",
    [string]$ControlPlaneUrl = "",
    [string]$ReleaseApi = "https://api.github.com/repos/b8vipvip/qnbot/releases/latest",
    [string]$PackagePath = "",
    [string]$ExpectedVersion = "",
    [string]$ExpectedSha256 = ""
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$BuiltInControlPlaneUrl = 'https://qn.numbot.cn'
$ServerUrlEnvironmentKey = 'QIANNIU_BOT_SERVER_URL'
$ObsoleteControlPlaneHost = 'botserver.mv3.cn'
$LegacyControlPlaneHost = 'aboter.mv3.cn'
$CurrentControlPlaneHost = 'qn.numbot.cn'

function Write-Step([string]$Message) {
    Write-Host "`n[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message" -ForegroundColor Cyan
}

function Quote-Arg([string]$Value) {
    return '"' + ([string]$Value).Replace('"', '\"') + '"'
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-Sha256([string]$Value) {
    return -not [string]::IsNullOrWhiteSpace($Value) -and $Value.Trim() -match '^[A-Fa-f0-9]{64}$'
}

function Normalize-Version([string]$Value) {
    $value = ([string]$Value).Trim()
    if ($value.StartsWith('bot-v', [StringComparison]::OrdinalIgnoreCase)) { return $value.Substring(5) }
    if ($value.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) { return $value.Substring(1) }
    return $value
}

function Normalize-ControlPlaneUrl([string]$Value) {
    $value = ([string]$Value).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) { return '' }
    if ($value -notmatch '^[A-Za-z][A-Za-z0-9+.-]*://') {
        $value = 'https://' + $value
    }
    $value = $value.TrimEnd('/')
    if ($value.EndsWith('/v1', [StringComparison]::OrdinalIgnoreCase)) {
        $value = $value.Substring(0, $value.Length - 3).TrimEnd('/')
    }

    $parsed = $null
    if ([Uri]::TryCreate($value, [UriKind]::Absolute, [ref]$parsed)) {
        if ([string]::Equals($parsed.Host, $ObsoleteControlPlaneHost, [StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals($parsed.Host, $LegacyControlPlaneHost, [StringComparison]::OrdinalIgnoreCase)) {
            $builder = New-Object System.UriBuilder -ArgumentList $parsed.AbsoluteUri
            $builder.Scheme = 'https'
            $builder.Host = $CurrentControlPlaneHost
            $builder.Port = -1
            $value = $builder.Uri.AbsoluteUri.TrimEnd('/')
        }
    }
    return $value.TrimEnd('/')
}

function Resolve-InstallDir([string]$Requested) {
    if (-not [string]::IsNullOrWhiteSpace($Requested)) {
        return [IO.Path]::GetFullPath($Requested.Trim().Trim('"'))
    }

    $roots = @()
    try {
        foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='Bot.exe'" -ErrorAction SilentlyContinue)) {
            $exe = [string]$process.ExecutablePath
            if ([string]::IsNullOrWhiteSpace($exe)) { continue }
            try {
                $full = [IO.Path]::GetFullPath($exe)
                if ([IO.Path]::GetFileName($full) -ieq 'Bot.exe' -and
                    [IO.Path]::GetFileName((Split-Path -Parent $full)) -ieq 'Bin') {
                    $root = Split-Path -Parent (Split-Path -Parent $full)
                    if ($roots -notcontains $root) { $roots += $root }
                }
            } catch {}
        }
    } catch {}

    if ($roots.Count -eq 1) { return $roots[0] }
    if ($roots.Count -gt 1) {
        throw '检测到多个 Bot 安装目录，请使用 -InstallDir 明确指定要修复的安装目录。'
    }

    $cwd = [IO.Path]::GetFullPath((Get-Location).Path)
    if (Test-Path -LiteralPath (Join-Path $cwd 'Bin\Bot.exe') -PathType Leaf) { return $cwd }

    $default = 'C:\qianniu-bot-x64'
    if (Test-Path -LiteralPath (Join-Path $default 'Bin\Bot.exe') -PathType Leaf) { return $default }

    throw '未自动找到 Bot 安装目录，请使用 -InstallDir 指定，例如 C:\qianniu-bot-x64。'
}

function Resolve-ControlPlaneUrl([string]$Root) {
    $explicit = Normalize-ControlPlaneUrl $ControlPlaneUrl
    if (-not [string]::IsNullOrWhiteSpace($explicit)) {
        Write-Host "救援更新服务地址：$explicit｜来源=命令参数" -ForegroundColor Green
        return $explicit
    }

    $environment = Normalize-ControlPlaneUrl ([Environment]::GetEnvironmentVariable($ServerUrlEnvironmentKey))
    if (-not [string]::IsNullOrWhiteSpace($environment)) {
        Write-Host "救援更新服务地址：$environment｜来源=环境变量 $ServerUrlEnvironmentKey" -ForegroundColor Green
        return $environment
    }

    $configPath = Join-Path $Root 'Bin\Bot.exe.config'
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        try {
            [xml]$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8
            $node = @($config.configuration.appSettings.add | Where-Object { [string]$_.key -eq 'BotControlPlaneDefaultUrl' } | Select-Object -First 1)
            if ($node.Count -eq 1) {
                $configured = Normalize-ControlPlaneUrl ([string]$node[0].value)
                if (-not [string]::IsNullOrWhiteSpace($configured)) {
                    Write-Host "救援更新服务地址：$configured｜来源=已安装 Bot.exe.config" -ForegroundColor Green
                    return $configured
                }
            }
        }
        catch {
            Write-Host "读取 Bot.exe.config 的更新服务地址失败，将使用内置地址：$($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    $fallback = Normalize-ControlPlaneUrl $BuiltInControlPlaneUrl
    Write-Host "救援更新服务地址：$fallback｜来源=内置默认" -ForegroundColor Green
    return $fallback
}

function Get-TargetBotPid([string]$Root) {
    $expectedExe = [IO.Path]::GetFullPath((Join-Path $Root 'Bin\Bot.exe'))
    $ids = @()
    try {
        foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='Bot.exe'" -ErrorAction SilentlyContinue)) {
            $exe = [string]$process.ExecutablePath
            if ([string]::IsNullOrWhiteSpace($exe)) { continue }
            try {
                if ([string]::Equals([IO.Path]::GetFullPath($exe), $expectedExe, [StringComparison]::OrdinalIgnoreCase)) {
                    $ids += [int]$process.ProcessId
                }
            } catch {}
        }
    } catch {}
    if ($ids.Count -eq 0) { return 0 }
    return [int]($ids | Sort-Object | Select-Object -First 1)
}

function Add-UniqueUrl([System.Collections.ArrayList]$List, [string]$Value) {
    $value = ([string]$Value).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) { return }
    $uri = $null
    if (-not [Uri]::TryCreate($value, [UriKind]::Absolute, [ref]$uri)) { return }
    foreach ($existing in @($List)) {
        if ([string]::Equals([string]$existing, $value, [StringComparison]::OrdinalIgnoreCase)) { return }
    }
    [void]$List.Add($value)
}

function Invoke-JsonRequest([string]$Uri, [hashtable]$Headers, [int]$TimeoutSec = 30, [int]$MaxAttempts = 3) {
    $lastError = ''
    $attempts = [Math]::Max(1, $MaxAttempts)
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            return Invoke-RestMethod -UseBasicParsing -Uri $Uri -Headers $Headers -TimeoutSec $TimeoutSec
        }
        catch {
            $lastError = $_.Exception.Message
            Write-Host "PowerShell 网络请求失败，切换 curl.exe：attempt=$attempt error=$lastError" -ForegroundColor Yellow
        }

        $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
        if ($null -ne $curl) {
            $temp = Join-Path $env:TEMP ("qianniu-rescue-json-" + [Guid]::NewGuid().ToString('N') + '.json')
            try {
                $curlArgs = @(
                    '--fail', '--location', '--silent', '--show-error',
                    '--connect-timeout', '15', '--max-time', [string]$TimeoutSec,
                    '--output', $temp
                )
                foreach ($key in @($Headers.Keys)) {
                    $curlArgs += @('--header', ("${key}: " + [string]$Headers[$key]))
                }
                $curlArgs += $Uri
                & $curl.Source @curlArgs
                if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $temp -PathType Leaf)) {
                    return (Get-Content -LiteralPath $temp -Raw -Encoding UTF8 | ConvertFrom-Json)
                }
                $lastError = "curl.exe exitCode=$LASTEXITCODE"
            }
            catch {
                $lastError = $_.Exception.Message
            }
            finally {
                Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
            }
        }

        if ($attempt -lt 3) { Start-Sleep -Seconds (2 * $attempt) }
    }
    throw "网络请求失败：$Uri；$lastError"
}

function Resolve-LatestFromControlPlane([string]$BaseUrl) {
    if ([string]::IsNullOrWhiteSpace($BaseUrl)) { return $null }
    $base = (Normalize-ControlPlaneUrl $BaseUrl).TrimEnd('/')
    $url = $base + '/api/public/v1/bot-update/latest'
    Write-Step "优先读取服务器更新缓存：$url"
    # The control plane is an optimization path. Never let an unreachable mirror
    # delay a rescue update for minutes; GitHub is the authoritative fallback.
    $json = Invoke-JsonRequest $url @{ 'User-Agent' = 'QianniuAiBotRescueUpdater/3.0'; 'Accept' = 'application/json' } 3 1

    $version = Normalize-Version ([string]$json.version)
    $sha = ([string]$json.sha256).Trim().ToUpperInvariant()
    if ([string]::IsNullOrWhiteSpace($version)) { throw '服务器更新缓存缺少 version。' }
    if (-not (Test-Sha256 $sha)) { throw '服务器更新缓存缺少有效 SHA-256。' }

    $urls = New-Object System.Collections.ArrayList
    Add-UniqueUrl $urls ([string]$json.mirror_url)
    Add-UniqueUrl $urls ([string]$json.download_url)
    if ($urls.Count -lt 1) { throw '服务器更新缓存没有可用安装包地址。' }

    return [pscustomobject]@{
        Version = $version
        Sha256 = $sha
        Urls = @($urls)
        Source = '服务器更新缓存'
    }
}

function Get-ShaFromReleaseNotes([string]$Notes) {
    $notes = [string]$Notes
    $match = [Regex]::Match($notes, '(?is)SHA-256[^0-9A-Fa-f]{0,120}([0-9A-Fa-f]{64})')
    if ($match.Success) { return $match.Groups[1].Value.ToUpperInvariant() }
    return ''
}

function Normalize-GitHubDigest([string]$Digest) {
    $digest = ([string]$Digest).Trim()
    if ($digest.StartsWith('sha256:', [StringComparison]::OrdinalIgnoreCase)) {
        $digest = $digest.Substring(7).Trim()
    }
    if (Test-Sha256 $digest) { return $digest.ToUpperInvariant() }
    return ''
}

function Resolve-LatestFromGitHub {
    Write-Step '服务器更新缓存不可用，回退 GitHub Release API'
    $headers = @{
        'User-Agent' = 'QianniuAiBotRescueUpdater/3.0'
        'Accept' = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    $release = Invoke-JsonRequest $ReleaseApi $headers 45
    if ([bool]$release.draft -or [bool]$release.prerelease) { throw 'GitHub latest Release 不是稳定版本。' }

    $tag = ([string]$release.tag_name).Trim()
    if (-not $tag.StartsWith('bot-v', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'GitHub latest Release 不是 bot-v* 正式版本。'
    }
    $version = Normalize-Version $tag
    $packageAsset = @($release.assets | Where-Object { $_.name -eq 'qianniu-bot-x64.zip' } | Select-Object -First 1)
    if ($packageAsset.Count -ne 1) { throw '最新正式版本缺少 qianniu-bot-x64.zip。' }

    $sha = Normalize-GitHubDigest ([string]$packageAsset[0].digest)
    if (-not (Test-Sha256 $sha)) { $sha = Get-ShaFromReleaseNotes ([string]$release.body) }

    $urls = New-Object System.Collections.ArrayList
    Add-UniqueUrl $urls ([string]$packageAsset[0].browser_download_url)

    $manifestAsset = @($release.assets | Where-Object { $_.name -eq 'update.json' } | Select-Object -First 1)
    if ($manifestAsset.Count -eq 1) {
        try {
            $manifest = Invoke-JsonRequest ([string]$manifestAsset[0].browser_download_url) @{ 'User-Agent' = 'QianniuAiBotRescueUpdater/3.0'; 'Accept' = 'application/json' } 30
            $manifestVersion = Normalize-Version ([string]$manifest.version)
            if (-not [string]::IsNullOrWhiteSpace($manifestVersion) -and $manifestVersion -ne $version) {
                throw "update.json 版本不一致。release=$version manifest=$manifestVersion"
            }
            $manifestSha = ([string]$manifest.sha256).Trim().ToUpperInvariant()
            if (Test-Sha256 $manifestSha) {
                if ((Test-Sha256 $sha) -and $sha -ne $manifestSha) { throw 'GitHub asset digest 与 update.json SHA-256 不一致。' }
                $sha = $manifestSha
            }
            Add-UniqueUrl $urls ([string]$manifest.download_url)
        }
        catch {
            if (Test-Sha256 $sha) {
                Write-Host "update.json 下载失败，但已有可信 SHA-256，继续救援更新：$($_.Exception.Message)" -ForegroundColor Yellow
            }
            else {
                throw
            }
        }
    }

    if (-not (Test-Sha256 $sha)) {
        throw '无法从 GitHub asset digest、Release 说明或 update.json 获得可信 SHA-256。'
    }
    if ($urls.Count -lt 1) { throw 'GitHub Release 没有可用安装包地址。' }

    return [pscustomobject]@{
        Version = $version
        Sha256 = $sha
        Urls = @($urls)
        Source = 'GitHub Release'
    }
}

function Resolve-LatestRelease([string]$ServerUrl) {
    try {
        $server = Resolve-LatestFromControlPlane $ServerUrl
        if ($null -ne $server) { return $server }
    }
    catch {
        Write-Host "服务器更新缓存暂不可用：$($_.Exception.Message)" -ForegroundColor Yellow
    }
    return Resolve-LatestFromGitHub
}

function Find-VerifiedLocalPackage([string]$ExpectedHash, [string]$RescueRoot) {
    $candidates = New-Object System.Collections.ArrayList

    $cwdPackage = Join-Path ([IO.Path]::GetFullPath((Get-Location).Path)) 'qianniu-bot-x64.zip'
    $downloadPackage = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Downloads\qianniu-bot-x64.zip'
    $scriptPackage = Join-Path $PSScriptRoot 'qianniu-bot-x64.zip'
    $cachePackage = Join-Path $RescueRoot 'qianniu-bot-x64.zip'

    foreach ($candidate in @($cwdPackage, $downloadPackage, $scriptPackage, $cachePackage)) {
        if ([string]::IsNullOrWhiteSpace([string]$candidate)) { continue }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $full = [IO.Path]::GetFullPath($candidate)
        if (@($candidates) -notcontains $full) { [void]$candidates.Add($full) }
    }

    foreach ($candidate in @($candidates)) {
        try {
            $actual = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToUpperInvariant()
            if ($actual -eq $ExpectedHash) {
                Write-Host "检测到本地已存在并通过 SHA-256 校验的安装包，跳过重新下载：$candidate" -ForegroundColor Green
                return $candidate
            }
            Write-Host "发现本地安装包但 SHA-256 不匹配，忽略：$candidate" -ForegroundColor Yellow
        }
        catch {
            Write-Host "检查本地安装包失败，忽略：$candidate；$($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    return ''
}

function Download-One([string]$Uri, [string]$Destination) {
    $lastError = ''
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($null -ne $curl) {
        try {
            Write-Host "尝试 curl.exe 下载：$Uri"
            & $curl.Source '--fail' '--location' '--silent' '--show-error' '--retry' '3' '--retry-delay' '2' '--connect-timeout' '20' '--max-time' '900' '--output' $Destination $Uri
            if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $Destination -PathType Leaf)) { return }
            $lastError = "curl.exe exitCode=$LASTEXITCODE"
        }
        catch {
            $lastError = $_.Exception.Message
        }
        Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
    }

    try {
        Write-Host "curl.exe 未成功，尝试 Invoke-WebRequest：$Uri" -ForegroundColor Yellow
        Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $Destination -TimeoutSec 900
        if (Test-Path -LiteralPath $Destination -PathType Leaf) { return }
        $lastError = 'Invoke-WebRequest 未生成目标文件。'
    }
    catch {
        $lastError = $_.Exception.Message
    }
    throw "下载失败：$Uri；$lastError"
}

function Download-VerifiedPackage([string[]]$Urls, [string]$Destination, [string]$ExpectedHash) {
    $partial = $Destination + '.partial'
    $errors = @()
    foreach ($url in @($Urls)) {
        if ([string]::IsNullOrWhiteSpace([string]$url)) { continue }
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
            try {
                Write-Step "下载安装包｜来源=$url｜attempt=$attempt"
                Download-One ([string]$url) $partial
                $actual = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToUpperInvariant()
                if ($actual -ne $ExpectedHash) {
                    throw "SHA-256 不一致。expected=$ExpectedHash actual=$actual"
                }
                Move-Item -LiteralPath $partial -Destination $Destination -Force
                Write-Host "安装包下载并校验成功：$url" -ForegroundColor Green
                return
            }
            catch {
                $errors += ([string]$url + ' attempt=' + $attempt + ' => ' + $_.Exception.Message)
                Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
                if ($attempt -lt 3) { Start-Sleep -Seconds (2 * $attempt) }
            }
        }
    }
    throw ('所有安装包下载通道均失败：' + ($errors -join ' | '))
}

if (-not (Test-IsAdministrator)) {
    Write-Step '请求管理员权限'
    $self = $MyInvocation.MyCommand.Path
    if ([string]::IsNullOrWhiteSpace($self)) { throw '无法确定当前脚本路径，请以管理员 PowerShell 重新运行。' }
    $args = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-Arg $self)
    if (-not [string]::IsNullOrWhiteSpace($InstallDir)) { $args += ' -InstallDir ' + (Quote-Arg $InstallDir) }
    if (-not [string]::IsNullOrWhiteSpace($ControlPlaneUrl)) { $args += ' -ControlPlaneUrl ' + (Quote-Arg $ControlPlaneUrl) }
    if (-not [string]::IsNullOrWhiteSpace($ReleaseApi)) { $args += ' -ReleaseApi ' + (Quote-Arg $ReleaseApi) }
    if (-not [string]::IsNullOrWhiteSpace($PackagePath)) { $args += ' -PackagePath ' + (Quote-Arg $PackagePath) }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) { $args += ' -ExpectedVersion ' + (Quote-Arg $ExpectedVersion) }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) { $args += ' -ExpectedSha256 ' + (Quote-Arg $ExpectedSha256) }
    $elevated = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $args -PassThru -Wait
    exit $elevated.ExitCode
}

$InstallDir = Resolve-InstallDir $InstallDir
if (Test-Path -LiteralPath (Join-Path $InstallDir '.git')) { throw "拒绝覆盖 Git 源码目录：$InstallDir" }
$ControlPlaneUrl = Resolve-ControlPlaneUrl $InstallDir

$downloadRequired = [string]::IsNullOrWhiteSpace($PackagePath)
$packageUrls = @()
if ($downloadRequired) {
    $releaseInfo = Resolve-LatestRelease $ControlPlaneUrl
    $ExpectedVersion = $releaseInfo.Version
    $ExpectedSha256 = $releaseInfo.Sha256
    $packageUrls = @($releaseInfo.Urls)
    Write-Host "已解析正式版本：$ExpectedVersion｜来源=$($releaseInfo.Source)" -ForegroundColor Green
} else {
    $PackagePath = [IO.Path]::GetFullPath($PackagePath)
    if ([string]::IsNullOrWhiteSpace($ExpectedVersion) -or [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        throw '使用本地 -PackagePath 时必须同时提供 -ExpectedVersion 和 -ExpectedSha256。'
    }
}

$ExpectedVersion = Normalize-Version $ExpectedVersion
$ExpectedSha256 = $ExpectedSha256.Trim().ToUpperInvariant()
if (-not (Test-Sha256 $ExpectedSha256)) { throw 'ExpectedSha256 必须是 64 位十六进制 SHA-256。' }

$root = Join-Path $env:LOCALAPPDATA 'QianniuAiBotUpdater\rescue'
New-Item -ItemType Directory -Path $root -Force | Out-Null

if ($downloadRequired) {
    $localPackage = Find-VerifiedLocalPackage $ExpectedSha256 $root
    if (-not [string]::IsNullOrWhiteSpace($localPackage)) {
        $PackagePath = $localPackage
        $downloadRequired = $false
    }
}

$partialPath = Join-Path $root ("qianniu-bot-x64-$ExpectedVersion.zip.partial")
$updaterPath = Join-Path $root ("BotAutoUpdater-$ExpectedVersion-" + [Guid]::NewGuid().ToString('N') + '.ps1')
if ($downloadRequired) { $PackagePath = Join-Path $root ("qianniu-bot-x64-$ExpectedVersion.zip") }

try {
    if ($downloadRequired) {
        Download-VerifiedPackage $packageUrls $PackagePath $ExpectedSha256
    }

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw "安装包不存在：$PackagePath" }
    $actualHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $ExpectedSha256) {
        throw "安装包 SHA-256 校验失败。expected=$ExpectedSha256 actual=$actualHash"
    }
    Write-Host "SHA-256 校验通过：$actualHash" -ForegroundColor Green

    Write-Step '提取并预检目标版本自己的更新器'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $updaterEntries = @($archive.Entries | Where-Object { ([string]$_.FullName).Replace('\', '/').TrimStart('/') -ieq 'Bin/BotAutoUpdater.ps1' })
        $releaseEntries = @($archive.Entries | Where-Object { ([string]$_.FullName).Replace('\', '/').TrimStart('/') -ieq 'release-info.json' })
        if ($updaterEntries.Count -ne 1) { throw "目标包必须且只能包含一个 Bin/BotAutoUpdater.ps1，actual=$($updaterEntries.Count)" }
        if ($releaseEntries.Count -ne 1) { throw "目标包必须且只能包含一个 release-info.json，actual=$($releaseEntries.Count)" }

        $reader = New-Object IO.StreamReader($releaseEntries[0].Open(), [Text.Encoding]::UTF8, $true)
        try { $packageInfo = ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
        if ((Normalize-Version ([string]$packageInfo.version)) -ne $ExpectedVersion) {
            throw "目标包版本不匹配。expected=$ExpectedVersion actual=$($packageInfo.version)"
        }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($updaterEntries[0], $updaterPath, $true)
    } finally { $archive.Dispose() }

    $tokens = $null
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile($updaterPath, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) {
        throw ('目标更新器 Windows PowerShell 5.1 语法预检失败：' + (@($parseErrors | ForEach-Object { $_.Message }) -join ' | '))
    }

    $currentPid = Get-TargetBotPid $InstallDir
    Write-Step "绕过旧 Bot 更新器执行救援安装；install=$InstallDir currentPid=$currentPid"
    $args = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-Arg $updaterPath)
    $args += ' -PackagePath ' + (Quote-Arg $PackagePath)
    $args += ' -InstallDir ' + (Quote-Arg $InstallDir)
    $args += ' -ExpectedSha256 ' + (Quote-Arg $ExpectedSha256)
    $args += ' -ExpectedVersion ' + (Quote-Arg $ExpectedVersion)
    $args += ' -CurrentPid ' + $currentPid
    # Wait for the updater process itself only. Start-Process -Wait also waits for
    # descendant processes; the updater launches the long-running Bot.exe, so
    # that option leaves this rescue wrapper open for the entire Bot session.
    $installer = Start-Process -FilePath 'powershell.exe' -ArgumentList $args -PassThru
    if ($null -eq $installer) { throw '无法启动目标版本更新器。' }
    $installer.WaitForExit()
    $installer.Refresh()
    if ($installer.ExitCode -ne 0) { throw "目标更新器执行失败，exitCode=$($installer.ExitCode)。目标更新器会按事务备份自动回滚。" }

    $installedInfoPath = Join-Path $InstallDir 'release-info.json'
    if (-not (Test-Path -LiteralPath $installedInfoPath -PathType Leaf)) { throw '救援更新结束但 release-info.json 不存在。' }
    $installedInfo = Get-Content -LiteralPath $installedInfoPath -Raw | ConvertFrom-Json
    if ((Normalize-Version ([string]$installedInfo.version)) -ne $ExpectedVersion) {
        throw "救援更新版本校验失败。expected=$ExpectedVersion actual=$($installedInfo.version)"
    }

    Write-Step "救援更新成功：$ExpectedVersion"
    Write-Host '新 Bot 已由目标更新器启动；永久用户数据仍保存在 %LOCALAPPDATA%\QianniuAiBot。' -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $updaterPath -Force -ErrorAction SilentlyContinue
}
