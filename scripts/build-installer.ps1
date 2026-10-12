<#
  LabGuard Server 一键打包（执行计划 LG-014 / §A2 的 package 目标）

  顺序**不能反**（本项目既有教训：先打包后构建 → 包里是旧代码）：
      1) 构建服务端本体（Release）
      2) 构建安装器（把本体作为资源内嵌）
      3) 产物落 release\：单文件安装器 + zip；打印 SHA256

  用法： powershell -File scripts\build-installer.ps1 [-SkipBuild]
  退出码：0 成功 / 1 构建失败 / 2 打包后自检失败
#>
[CmdletBinding()]
param([switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo 'release'
[System.IO.Directory]::CreateDirectory($release) | Out-Null

$props = Get-Content (Join-Path $repo 'Directory.Build.props') -Raw
$version = [regex]::Match($props, '<Version>([^<]+)</Version>').Groups[1].Value.Trim()
if (-not $version) { Write-Host '无法从 Directory.Build.props 取版本号' -ForegroundColor Red; exit 1 }

$sln = Join-Path $repo 'LabGuardServer.sln'
$installerProj = Join-Path $repo 'src\LabGuardServer.Installer\LabGuardServer.Installer.csproj'
$installerExe = Join-Path $repo 'src\LabGuardServer.Installer\bin\Release\net48\LabGuardServer.Installer.exe'
$srvExe = Join-Path $repo 'src\LabGuardServer\bin\Release\net48\LabGuardServer.exe'
$srvCore = Join-Path $repo 'src\LabGuardServer.Core\bin\Release\net48\LabGuardServer.Core.dll'

if (-not $SkipBuild) {
    Write-Host "=== 1. 构建服务端本体（Release，含自检）==="
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'build.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Host '服务端构建/自检失败' -ForegroundColor Red; exit 1 }
    foreach ($f in @($srvExe, $srvCore)) {
        if (-not (Test-Path $f)) { Write-Host ("缺少构建产物：" + $f) -ForegroundColor Red; exit 1 }
    }
}

Write-Host '=== 2. 构建安装器（内嵌服务端本体）==='
$payload = Join-Path $repo 'src\LabGuardServer.Installer\payload'
if (Test-Path $payload) { [System.IO.Directory]::Delete($payload, $true) }
[System.IO.Directory]::CreateDirectory((Join-Path $payload 'assets')) | Out-Null
Copy-Item $srvExe (Join-Path $payload 'LabGuardServer.exe') -Force
Copy-Item (Join-Path $repo 'src\LabGuardServer\bin\Release\net48\LabGuardServer.exe.config') (Join-Path $payload 'LabGuardServer.exe.config') -Force -ErrorAction SilentlyContinue
Copy-Item $srvCore (Join-Path $payload 'LabGuardServer.Core.dll') -Force
Copy-Item (Join-Path $repo 'assets\catalog.json') (Join-Path $payload 'assets\catalog.json') -Force
Write-Host ("  已准备包体：" + (Get-ChildItem $payload -Recurse -File | Measure-Object).Count + " 个文件")
# --no-incremental：包体是"新文件"，增量构建可能不重编，结果装上的是没内嵌本体的空壳（实测）
& dotnet build $installerProj -c Release --no-incremental --nologo -v minimal | Select-Object -Last 2
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $installerExe)) { Write-Host '安装器构建失败' -ForegroundColor Red; exit 1 }

Write-Host '=== 3. 打包后自检：安装器里必须真的内嵌了本体 ==='
$bytes = [System.IO.File]::ReadAllBytes($installerExe)
$text = [System.Text.Encoding]::ASCII.GetString($bytes)
$hasExe = $text.Contains('payload/LabGuardServer.exe')
$hasCore = $text.Contains('payload/LabGuardServer.Core.dll')
$hasCatalog = $text.Contains('payload/assets/catalog.json')
if (-not ($hasExe -and $hasCore -and $hasCatalog)) {
    Write-Host ("内嵌资源检查失败：exe=" + $hasExe + " core=" + $hasCore + " catalog=" + $hasCatalog) -ForegroundColor Red
    exit 2
}

$setupName = "LabGuardServer-Setup-v$version.exe"
$setup = Join-Path $release $setupName
Copy-Item $installerExe $setup -Force
$zip = Join-Path $release "labguard-server-v$version.zip"
if (Test-Path $zip) { [System.IO.File]::Delete($zip) }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $repo 'src\LabGuardServer.Installer\bin\Release\net48'), $zip)

Write-Host ''
Write-Host ("产物目录：" + $release)
foreach ($f in @($setup, $zip)) {
    $item = Get-Item $f
    $hash = (Get-FileHash $f -Algorithm SHA256).Hash
    Write-Host ("  " + $item.Name + "  " + $item.Length + " B  SHA256=" + $hash)
}
Write-Host ''
Write-Host '安装（教师机，需管理员。双击也行）：' -ForegroundColor Green
Write-Host ("  " + $setup + " --install --dir `"$env:ProgramFiles\LabGuard Server`" --port 8210")
Write-Host '静默卸载（保留密钥与策略数据，换机重装不用重新配对）：'
Write-Host ("  " + $setup + " --uninstall")
Write-Host '彻底卸载（连密钥一起删）：'
Write-Host ("  " + $setup + " --uninstall --purge")
exit 0
