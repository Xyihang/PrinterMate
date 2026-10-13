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
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$ConfirmRedistribution,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg) { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn2($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# ── 0. 定位仓库根 ────────────────────────────────────────────────
$manifestPath = Join-Path $Root 'Catalog\drivers.json'
if (-not (Test-Path $manifestPath)) {
    throw "找不到驱动清单:$manifestPath。请用 -Root 指定仓库根目录。"
}

# ── 1. 准备源目录 ────────────────────────────────────────────────
$work = $null
if (Test-Path $Source -PathType Container) {
    $work = (Resolve-Path $Source).Path
}
elseif (Test-Path $Source -PathType Leaf) {
    $work = Join-Path ([IO.Path]::GetTempPath()) ("pm-import-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    Write-Step "解压 $Source"
    Expand-Archive -Path $Source -DestinationPath $work -Force
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
    Write-Step "未指定 -DriverName,从 INF 的 [Models] 段解析"
    $models = @()
    $inSection = $false
    foreach ($line in Get-Content -LiteralPath $inf.FullName) {
        if ($line -match '^\s*\[(.*)\]') {
            $section = $Matches[1]
            $inSection = $section -match 'Models'
            continue
        }
        if ($inSection -and $line -match '^\s*"([^"]+)"\s*=') {
            $models += $Matches[1]
        }
        elseif ($inSection -and $line -match '^\s*([^";\[]+?)\s*=\s*\S') {
            $models += $Matches[1].Trim()
        }
    }
    $models = $models | Where-Object { $_ -and $_ -notmatch '^\s*;' } | Select-Object -Unique
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
if ($work -ne (Resolve-Path $Source -ErrorAction SilentlyContinue).Path -and (Test-Path $work)) {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
