#Requires -Version 5.1
<#
  LabGuard Server 一键构建与自检（执行计划 LG-002 / §4 A2）

  用法（在仓库根目录）：
      build.cmd                      # 等价于下面的 -Configuration Release
      powershell -File build.ps1
      powershell -File build.ps1 -Clean          # 先清 bin/obj/dist
      powershell -File build.ps1 -NoSelfTest     # 只构建
      powershell -File build.ps1 -Package        # 打包（安装器未实现 → 退出码 3）

  退出码：0 成功 / 1 构建失败 / 2 自检失败 / 3 打包未实现 / 4 前置条件不满足

  纪律：构建产物一律进 dist\（已被 .gitignore 忽略）；不生成也不提交任何密钥、
  配对文件、口令或本机数据；任何一步失败都返回非零退出码，绝不"静默成功"。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Clean,
    [switch]$NoSelfTest,
    [switch]$Package,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'dist' }

function Write-Step([string]$text) { Write-Host ''; Write-Host "=== $text ===" -ForegroundColor Cyan }
function Fail([string]$text, [int]$code) { Write-Host "失败：$text" -ForegroundColor Red; exit $code }

# --- 版本号（唯一来源：Directory.Build.props） -------------------------------
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $propsPath)) { Fail "找不到 $propsPath" 4 }
$propsText = Get-Content -LiteralPath $propsPath -Raw
$m = [regex]::Match($propsText, '<Version>([^<]+)</Version>')
if (-not $m.Success) { Fail "Directory.Build.props 里没有 <Version>" 4 }
$version = $m.Groups[1].Value.Trim()
$sln = Join-Path $repoRoot 'LabGuardServer.sln'
if (-not (Test-Path -LiteralPath $sln)) { Fail "找不到 $sln" 4 }

Write-Host "LabGuard Server 构建：版本 $version / 配置 $Configuration / 仓库 $repoRoot"

# --- 0. 清理（只删仓库内的 bin/obj/dist，删前校验路径） ----------------------
if ($Clean) {
    Write-Step '清理 bin / obj / dist'
    $targets = @()
    $targets += Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' }
    if (Test-Path -LiteralPath $OutDir) { $targets += Get-Item -LiteralPath $OutDir }
    $rootFull = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\')
    $removed = 0
    foreach ($t in $targets) {
        $full = [System.IO.Path]::GetFullPath($t.FullName)
        if (-not $full.StartsWith($rootFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            Fail "拒绝删除仓库之外的路径：$full" 4
        }
        try { [System.IO.Directory]::Delete($full, $true); $removed++ }
        catch { Write-Host "  （跳过 $full：$($_.Exception.Message)）" -ForegroundColor DarkYellow }
    }
    Write-Host "  已删除 $removed 个目录"
}

# --- 1. 还原 + 构建 ----------------------------------------------------------
Write-Step "还原 + 构建（$Configuration）"
& dotnet build $sln -c $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) { Fail "dotnet build 返回 $LASTEXITCODE（构建失败）" 1 }

# --- 2. 自检 -----------------------------------------------------------------
if (-not $NoSelfTest) {
    Write-Step '自检（LabGuardServer.SelfTest）'
    $selfTest = Join-Path $repoRoot "src\LabGuardServer.SelfTest\bin\$Configuration\net48\LabGuardServer.SelfTest.exe"
    if (-not (Test-Path -LiteralPath $selfTest)) { Fail "找不到自检程序：$selfTest" 4 }
    & $selfTest
    if ($LASTEXITCODE -ne 0) { Fail "自检未通过（退出码 $LASTEXITCODE）" 2 }
}

# --- 3. 产物整理（固定目录，不污染源码目录） --------------------------------
Write-Step '整理产物到 dist'
$distVersion = Join-Path $OutDir ("labguard-server-v" + $version)
[System.IO.Directory]::CreateDirectory($distVersion) | Out-Null
$builtDir = Join-Path $repoRoot "src\LabGuardServer\bin\$Configuration\net48"
$wanted = @('LabGuardServer.exe', 'LabGuardServer.exe.config', 'LabGuardServer.Core.dll')
$copied = @()
foreach ($f in $wanted) {
    $src = Join-Path $builtDir $f
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination (Join-Path $distVersion $f) -Force
        $copied += $f
    }
}
$assetsSrc = Join-Path $repoRoot 'assets\catalog.json'
if (Test-Path -LiteralPath $assetsSrc) {
    $assetsDst = Join-Path $distVersion 'assets'
    [System.IO.Directory]::CreateDirectory($assetsDst) | Out-Null
    Copy-Item -LiteralPath $assetsSrc -Destination (Join-Path $assetsDst 'catalog.json') -Force
    $copied += 'assets\catalog.json'
}
Write-Host ("  已复制 " + $copied.Count + " 个文件 -> " + $distVersion)
Get-ChildItem -LiteralPath $distVersion -Recurse -File |
    ForEach-Object { Write-Host ("    " + $_.FullName.Substring($distVersion.Length + 1) + "  " + $_.Length + " B") }

# 敏感文件自查：绝不把密钥/口令/配对/本机数据带进产物
$forbidden = Get-ChildItem -LiteralPath $distVersion -Recurse -File |
    Where-Object { $_.Name -match '(?i)^server-key\.bin$|^server-password\.txt$|pairing.*\.json$|\.envelope$|\.log$' }
if ($forbidden) {
    Fail ("产物里出现禁止分发的文件：" + (($forbidden | ForEach-Object { $_.Name }) -join ', ')) 4
}

# --- 4. 打包（安装器尚未实现） ----------------------------------------------
if ($Package) {
    Write-Step '打包'
    Fail '安装器尚未实现（计划 LG-014）。当前只产出 dist 目录，供人工拷贝部署；' 3
}

Write-Host ''
Write-Host "完成：构建 + 自检通过，产物在 $distVersion" -ForegroundColor Green
exit 0
