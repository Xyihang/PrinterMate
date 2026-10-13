<#
.SYNOPSIS
    把厂商通用驱动(UPD)导入 PrinterMate 内置驱动库,并回填 Catalog/drivers.json。

.DESCRIPTION
    导入流程(每一步都会校验,任一失败即中止):
      1. 解析驱动包(目录或 zip),定位 .inf 与 .cat;
      2. 用 Get-AuthenticodeSignature 校验 .cat 的 Authenticode 签名(等价于 WinVerifyTrust);
      3. 解析 INF 的 [Manufacturer]/[Models] 段,列出驱动名,确认清单里的 driverName 与之逐字一致;
      4. 按架构计算 INF 的 SHA256;
      5. 复制到 Drivers/<Brand>/<slug>/,回填 drivers.json 的 infRelPath / sha256 / version / signerSubject;
      6. 只有在显式传入 -ConfirmRedistribution 时才把 redistributable 置为 true。

.NOTES
    授权红线:向第三方分发厂商驱动前必须核对再分发许可。
    未确认授权的品牌请保持 redistributable=false,程序会自动降级到在线源(L2)。

.EXAMPLE
    .\Import-DriverPackage.ps1 -Brand HP -Family "Universal Print Driver" -Pdl PCL6 `
        -Source C:\Download\hp-upd-pcl6 -ConfirmRedistribution
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Brand,
    [Parameter(Mandatory)][string]$Family,
    [Parameter(Mandatory)][ValidateSet('PCL6', 'PS', 'GDI', 'ClassDriver')][string]$Pdl,
    [Parameter(Mandatory)][string]$Source,
    [string]$DriverName,
    [string[]]$Arch = @('x64'),
    [string]$Version = '',
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'PrinterMate'),
    [switch]$ConfirmRedistribution,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg) { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# ── 驱动包解包(只使用"不执行"的解包工具) ────────────────────────
# 设计取舍:自解压 .exe 绝不用"盲猜开关去运行"的方式解包 ——
# 那等于让厂商安装器直接改系统,绕过本脚本的签名与哈希校验。
# 只用 7-Zip 与 expand.exe 这类纯解包工具;两者都失败就明确报错。
function Expand-DriverPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $ext = [IO.Path]::GetExtension($Source).ToLowerInvariant()

    function Test-Extracted {
        return @(Get-ChildItem -Path $Destination -Recurse -Filter *.inf -File -ErrorAction SilentlyContinue).Count -gt 0
    }

    if ($ext -in @('.zip', '.cab')) {
        if ($ext -eq '.zip') {
            Expand-Archive -LiteralPath $Source -DestinationPath $Destination -Force
        }
        else {
            $expand = Join-Path $env:SystemRoot 'System32\expand.exe'
            & $expand -F:* $Source $Destination | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "expand.exe 解压 CAB 失败(退出码 $LASTEXITCODE)。" }
        }
        if (Test-Extracted) { return $true }
        throw "解包后未发现 .inf 文件,该文件可能不是驱动包。"
    }

    if ($ext -ne '.exe') {
        throw "不支持的驱动包格式:$ext(支持文件夹 / .zip / .cab / .exe)。"
    }

    # 策略 1:7-Zip(能识别大量厂商自解压格式)
    $sevenZip = @(
        "$env:ProgramFiles\7-Zip\7z.exe",
        "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if ($sevenZip) {
        Write-Step "用 7-Zip 解包:$Source"
        & $sevenZip x -y "-o$Destination" $Source | Out-Null
        if (Test-Extracted) {
            Write-Ok "7-Zip 解包成功。"
            return $true
        }
        Write-Warn2 "7-Zip 未能从该 exe 中取出 .inf。"
    }
    else {
        Write-Warn2 "未安装 7-Zip,跳过该方式。"
    }

    # 策略 2:expand.exe(对 CAB 型自解压有效,不会执行任何安装逻辑)
    Write-Step "用 expand.exe 尝试解包"
    $expandExe = Join-Path $env:SystemRoot 'System32\expand.exe'
    & $expandExe -F:* $Source $Destination 2>$null | Out-Null
    if (Test-Extracted) {
        Write-Ok "expand.exe 解包成功。"
        return $true
    }

    throw @"
无法在不执行安装器的前提下解开该 exe,已停止。

这不是脚本的 bug,而是有意的取舍:盲猜开关去运行厂商安装器,
等于让它直接改系统并绕过本脚本的 SHA256 与 .cat 签名校验。

请改用以下任一方式(都不需要运行安装器):
  1. 安装 7-Zip 后重跑本脚本(推荐);
  2. 用 7-Zip 或 Windows 11 资源管理器的「全部解压缩」手工解开,
     再用 -Source 指定解压出来的文件夹;
  3. 直接从厂商官网下载"包含 INF/CAT 的驱动包"而非自解压安装器。
"@
}

# ── 0. 定位驱动库根目录与清单 ────────────────────────────────────
# 目标根目录必须与程序 DriverLibrary.ResolveDefaultRoot() 的查找顺序一致:
#   1) 环境变量 PRINTERMATE_ROOT
#   2) %LOCALAPPDATA%\PrinterMate        ← 本脚本默认写这里
#   3) 程序所在目录
#   4) 从程序目录逐级上溯找到 Catalog\drivers.json
$manifestRel = 'Catalog\drivers.json'
$manifestPath = Join-Path $Root $manifestRel

if (-not (Test-Path $manifestPath)) {
    Write-Step "目标驱动库尚无清单,正在从随包清单播种"
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $seedCandidates = @(
        @(
            (Join-Path $repoRoot $manifestRel),
            (Join-Path $repoRoot 'src\PrinterMate.Catalog\Data\drivers.json')
        ) | Where-Object { Test-Path $_ }
    )

    if ($seedCandidates.Count -eq 0) {
        throw "找不到驱动清单,也没有可用的随包清单副本。请用 -Root 指定驱动库根目录。"
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $manifestPath) -Force | Out-Null
    Copy-Item -LiteralPath $seedCandidates[0] -Destination $manifestPath -Force
    Write-Ok "已从随包清单播种:$($seedCandidates[0])"
}

Write-Ok "驱动库根目录:$Root"
Write-Ok "清单文件:$manifestPath"

# ── 1. 准备源目录 ────────────────────────────────────────────────
$work = $null
$extractedTemp = $null
if (Test-Path $Source -PathType Container) {
    $work = (Resolve-Path $Source).Path
}
elseif (Test-Path $Source -PathType Leaf) {
    $work = Join-Path ([IO.Path]::GetTempPath()) ("pm-import-" + [Guid]::NewGuid().ToString('N'))
    $extractedTemp = $work
    Write-Step "解包 $Source"
    Expand-DriverPackage -Source (Resolve-Path $Source).Path -Destination $work | Out-Null
}
else {
    throw "源路径不存在:$Source"
}

$infFiles = @(Get-ChildItem -Path $work -Recurse -Filter *.inf -File)
if ($infFiles.Count -eq 0) { throw "在 $work 中找不到任何 .inf 文件,这不是一个有效的驱动包。" }

Write-Step "发现 $($infFiles.Count) 个 INF 文件"
$infFiles | ForEach-Object { Write-Ok $_.FullName.Replace($work, '.') }

# ── 2. 选择 INF(同名多架构时优先 ntamd64) ───────────────────────
$inf = $infFiles |
    Sort-Object -Property @{ Expression = { if ($_.Name -match 'ntamd64|ntarm64') { 0 } else { 1 } } }, Name |
    Select-Object -First 1

if (-not $DriverName) {
    Write-Step "未指定 -DriverName,正在解析 INF 中的驱动名"
    # 注意:厂商 INF 的驱动名不一定在 [Models] 段 —— HP 用的是 [HP] / [HP.NTamd64]。
    # 因此这里扫描除 [Version]/[Strings]/[Manufacturer] 之外的所有段。
    $models = @()
    $skipSection = $false
    foreach ($line in (Get-Content -LiteralPath $inf.FullName)) {
        if ($line -match '^\s*\[(.+?)\]\s*$') {
            $section = $Matches[1].Trim()
            $skipSection = $section -match '^(Version|Strings|Manufacturer)$'
            continue
        }
        if ($skipSection -or $line -match '^\s*;') { continue }
        # 设备条目形如:「"驱动名" = 安装节, 硬件ID」;借此排除 CopyFiles=@x.dll 这类指令。
        if ($line -match '^\s*(%[^%]+%|"[^"]+"|[^";\[]+?)\s*=\s*[^,\r\n]+,\s*\S') {
            $left = $Matches[1].Trim().Trim('"')
            if ($left -and $left -notmatch '^%') { $models += $left }
        }
    }
    $models = @($models | Where-Object { $_ -and $_ -notmatch '^\s*;' } | Select-Object -Unique)
    if ($models.Count -eq 0) { throw "无法从 $($inf.Name) 解析出驱动名,请用 -DriverName 显式指定。" }

    Write-Ok "候选驱动名:"
    $models | ForEach-Object { Write-Ok "  - $_" }
    $DriverName = $models[0]
    Write-Warn2 "自动选用第一个:$DriverName(如有异议请用 -DriverName 指定后重跑)"
}

# ── 3. 签名校验(签名在 .cat 上,不在 .inf 上) ────────────────────
Write-Step "校验驱动包数字签名"
$catFiles = @(Get-ChildItem -Path $inf.DirectoryName -Filter *.cat -File)
if ($catFiles.Count -eq 0) {
    $catFiles = @(Get-ChildItem -Path $work -Recurse -Filter *.cat -File)
}
if ($catFiles.Count -eq 0) {
    throw "驱动包内没有 .cat 目录文件,无法验证签名来源,拒绝导入。"
}

$signedCat = $null
foreach ($cat in $catFiles) {
    $sig = Get-AuthenticodeSignature -LiteralPath $cat.FullName
    if ($sig.Status -eq 'Valid' -and $sig.SignerCertificate) {
        $signedCat = $cat
        break
    }
    Write-Warn2 "$($cat.Name) 签名状态:$($sig.Status)"
}
if (-not $signedCat) {
    throw "驱动包内所有 .cat 文件签名校验均未通过,拒绝导入。"
}

$signerSubject = $signedCat | Get-AuthenticodeSignature | ForEach-Object { $_.SignerCertificate.Subject }
Write-Ok "签名有效:$($signedCat.Name)"
Write-Ok "签名主体:$signerSubject"

# ── 4. 复制到驱动库 ──────────────────────────────────────────────
$slug = ($Family -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
$pdlSlug = "$slug-$($Pdl.ToLowerInvariant())"
$target = Join-Path (Join-Path $Root "Drivers\$Brand") $pdlSlug

Write-Step "复制驱动文件到 $target"
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -Path (Join-Path $inf.DirectoryName '*') -Destination $target -Recurse -Force

$targetInf = Join-Path $target $inf.Name
$targetInfRel = ($targetInf.Replace($Root, '').TrimStart('\')).Replace('\', '/')
Write-Ok "已复制到 $targetInfRel"

# ── 5. 计算 SHA256 ──────────────────────────────────────────────
Write-Step "计算 INF 的 SHA256"
$sha = (Get-FileHash -LiteralPath $targetInf -Algorithm SHA256).Hash
Write-Ok $sha

# ── 6. 回填 drivers.json ────────────────────────────────────────
Write-Step "更新 $manifestPath"
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$entry = $manifest.entries | Where-Object { $_.brand -eq $Brand -and $_.pdl -eq $Pdl } | Select-Object -First 1

if (-not $entry) {
    Write-Warn2 "清单中没有 $Brand / $Pdl 的条目,正在新增。"
    $entry = [pscustomobject]@{
        brand               = $Brand
        family              = $Family
        pdl                 = $Pdl
        driverName          = $DriverName
        infRelPath          = $targetInfRel
        arch                = $Arch
        os                  = @('win10', 'win11')
        version             = $Version
        signerSubject       = $signerSubject
        redistributable     = [bool]$ConfirmRedistribution
        modelPatterns       = @('*')
        hardwareIdPatterns  = @("USBPRINT\$Brand*", "WSDPRINT\*$Brand*")
        sha256              = [pscustomobject]@{ x64 = ''; arm64 = '' }
        priority            = 100
    }
    $manifest.entries = @($manifest.entries) + $entry
}
else {
    if ($entry.infRelPath -and $entry.infRelPath -ne $targetInfRel -and -not $Force) {
        throw "清单中该条目已指向 $($entry.infRelPath);如需替换请加 -Force。"
    }
    $entry.infRelPath = $targetInfRel
    $entry.family = $Family
    if ($DriverName) { $entry.driverName = $DriverName }
    if ($Version) { $entry.version = $Version }
    $entry.signerSubject = $signerSubject
    $entry.arch = $Arch
    $entry.redistributable = [bool]$ConfirmRedistribution
    foreach ($a in $Arch) {
        if ($entry.sha256.PSObject.Properties.Name -contains $a) { $entry.sha256.$a = $sha }
        else { $entry.sha256 | Add-Member -NotePropertyName $a -NotePropertyValue $sha }
    }
}

$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host ''
Write-Host "导入完成。" -ForegroundColor Green
Write-Host "  品牌/PDL : $Brand / $Pdl"
Write-Host "  驱动名   : $DriverName"
Write-Host "  INF      : $targetInfRel"
Write-Host "  SHA256   : $sha"
Write-Host "  签名主体 : $signerSubject"
Write-Host "  可再分发 : $([bool]$ConfirmRedistribution)"
if (-not $ConfirmRedistribution) {
    Write-Warn2 "redistributable=false:程序不会从本地安装该驱动,将自动降级到官方在线源(L2)。"
    Write-Warn2 "完成再分发授权核对后,请重新执行并加上 -ConfirmRedistribution。"
}
if ($extractedTemp -and (Test-Path $extractedTemp)) {
    Remove-Item -LiteralPath $extractedTemp -Recurse -Force -ErrorAction SilentlyContinue
}
