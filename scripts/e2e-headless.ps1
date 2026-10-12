<#
  LabGuard Server 协议端到端冒烟（无界面，执行计划 LG-006/LG-007 的起点）

  做四件事：① 起 headless 教师机（自签策略 + 导出配对文件）；
  ② 用**独立实现**（PowerShell + .NET，不复用 C# 实现）核对协议契约：
     /api/version 的 sha256 == 信封全文 SHA-256、配对文件指纹 == 公钥 XML SHA-256 前 8 字节、
     /api/policy 前缀 LGSRV1、checkin 正常 200 / 坏 JSON 400 / 超大 413、HEAD 200、未知路径 404；
  ③ 让服务端自然退出并核对它打印的在线学生机列表；
  ④ 退出码 0 = 全通过，1 = 有失败。

  用法：
      powershell -File scripts\e2e-headless.ps1 [-Port 8211] [-Seconds 25] [-DataDir <目录>] [-SkipBuild]
#>
[CmdletBinding()]
param(
    [int]$Port = 8211,
    [int]$Seconds = 25,
    [string]$DataDir,
    [switch]$SkipBuild,
    [switch]$KeepData          # 默认清理临时数据目录（-DataDir 显式指定时不清理）
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$explicitDataDir = $true
if (-not $DataDir) {
    $explicitDataDir = $false
    $DataDir = Join-Path ([System.IO.Path]::GetTempPath()) ('lg-e2e-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
[System.IO.Directory]::CreateDirectory($DataDir) | Out-Null
$pass = 0; $fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name) }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + "  " + $detail) -ForegroundColor Red }
}

Write-Host "数据目录：$DataDir（端口 $Port）"
if (-not $SkipBuild) {
    Write-Host '=== 构建 ==='
    & dotnet build (Join-Path $repo 'LabGuardServer.sln') -c Release --nologo -v minimal | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { Write-Host '构建失败' -ForegroundColor Red; exit 1 }
}

$exe = Join-Path $repo 'src\LabGuardServer\bin\Release\net48\LabGuardServer.exe'
$assets = Join-Path $repo 'assets\catalog.json'
$exeAssets = Join-Path $repo 'src\LabGuardServer\bin\Release\net48\assets'
[System.IO.Directory]::CreateDirectory($exeAssets) | Out-Null
Copy-Item $assets (Join-Path $exeAssets 'catalog.json') -Force

Write-Host '=== 启动 headless 教师机 ==='
$pairing = Join-Path $DataDir 'pairing.json'
$log = Join-Path $DataDir 'srv.log'
$srvArgs = @('--headless', '--data-dir', $DataDir, '--port', "$Port", '--password', 'e2e123456',
          '--policy-default', '--export-pairing', $pairing, '--seconds', "$Seconds")
$proc = Start-Process -FilePath $exe -ArgumentList $srvArgs -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $log -RedirectStandardError (Join-Path $DataDir 'srv.err')
Start-Sleep -Seconds 4
Check '1. 进程在跑' (-not $proc.HasExited)

$base = "http://127.0.0.1:$Port"
Write-Host '=== HTTP 契约核对（独立实现）==='
try {
    $versionRaw = (Invoke-WebRequest "$base/api/version" -UseBasicParsing -TimeoutSec 5).Content
    $version = $versionRaw | ConvertFrom-Json
    Check '2. /api/version 带 seq/sha256/paused' ($null -ne $version.seq -and $null -ne $version.sha256 -and $null -ne $version.paused)

    $policy = (Invoke-WebRequest "$base/api/policy" -UseBasicParsing -TimeoutSec 5).Content
    Check '3. /api/policy 是 LGSRV1 信封' ($policy.StartsWith('LGSRV1|'))

    $sha = [System.Security.Cryptography.SHA256]::Create()
    $expect = ($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($policy)) | ForEach-Object { $_.ToString('x2') }) -join ''
    Check '4. sha256 == 信封全文 SHA-256（协议 §3 定义）' ($version.sha256 -eq $expect) ("got " + $version.sha256 + " expect " + $expect)

    $body = '{"machine":"E2E-PS","version":"0.10","seq":1,"paused":false}'
    $ok = (Invoke-WebRequest "$base/api/checkin" -Method POST -Body $body -ContentType 'application/json' -UseBasicParsing -TimeoutSec 5).Content
    Check '5. 正常 checkin → {"ok":true}' ($ok -match 'ok')

    try { Invoke-WebRequest "$base/api/checkin" -Method POST -Body '{not json' -ContentType 'application/json' -UseBasicParsing -TimeoutSec 5 | Out-Null; Check '6. 坏 JSON → 400' $false }
    catch { Check '6. 坏 JSON → 400' ($_.Exception.Response.StatusCode.value__ -eq 400) }

    $big = 'x' * 9000
    try { Invoke-WebRequest "$base/api/checkin" -Method POST -Body $big -ContentType 'application/json' -UseBasicParsing -TimeoutSec 5 | Out-Null; Check '7. 超大 body → 413' $false }
    catch { Check '7. 超大 body → 413' ($_.Exception.Response.StatusCode.value__ -eq 413) }

    $head = Invoke-WebRequest "$base/api/version" -Method HEAD -UseBasicParsing -TimeoutSec 5
    Check '8. HEAD /api/version → 200' ($head.StatusCode -eq 200)

    try { Invoke-WebRequest "$base/api/nope" -UseBasicParsing -TimeoutSec 5 | Out-Null; Check '9. 未知路径 → 404' $false }
    catch { Check '9. 未知路径 → 404' ($_.Exception.Response.StatusCode.value__ -eq 404) }
} catch {
    Check 'HTTP 探针整体' $false $_.Exception.Message
}

Write-Host '=== 配对文件核对（指纹算法端到端）==='
if (Test-Path $pairing) {
    $pj = Get-Content $pairing -Raw | ConvertFrom-Json
    Check '10. format = labguard-pairing/1' ($pj.format -eq 'labguard-pairing/1')
    Check '11. 含公钥与端口' ((-not [string]::IsNullOrEmpty($pj.publicKeyXml)) -and $pj.port -gt 0)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fp = (($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($pj.publicKeyXml)))[0..7] | ForEach-Object { $_.ToString('x2') }) -join ''
    Check '12. 指纹 == 公钥 XML SHA-256 前 8 字节' ($fp -eq $pj.fingerprint) ("got " + $pj.fingerprint + " expect " + $fp)
    Check '13. 配对文件不含私钥（无 <D> 段）' (-not ($pj.publicKeyXml -match '<D>'))
} else { Check '10. 配对文件已导出' $false }

Write-Host '=== 等服务端自然退出（含在线列表）==='
# 用"轮询 HasExited"而不是 WaitForExit(timeout)：PS 5.1 下 WaitForExit(int) 之后 ExitCode 常常读不到（实测）
$deadline = (Get-Date).AddSeconds($Seconds + 30)
while (-not $proc.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500; $proc.Refresh() }
Start-Sleep -Milliseconds 500
$proc.Refresh()
$exitCode = $proc.ExitCode
$logText = Get-Content $log -Encoding UTF8 -Raw
Check '14. 在线列表含 E2E-PS' ($logText -match 'E2E-PS') ("exit=" + $exitCode)
# 注：PS 5.1 下 Start-Process -PassThru 的对象即使进程已退出，ExitCode 也常常读不到（本机实测），
# 这里是**环境限制**而不是服务端问题；用"进程确实退出了 + 日志打印到最后一屏"来判正常收尾。
$stillRunning = $null -ne (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)
Check '15. 服务端到点正常退出（日志打完整屏）' ((-not $stillRunning) -and ($logText -match '在线学生机')) `
    ("exit=" + $exitCode + " stillRunning=" + $stillRunning)

Write-Host ''
Write-Host ("结果：" + $pass + " 通过，" + $fail + " 失败") -ForegroundColor ($(if ($fail -eq 0) { 'Green' } else { 'Red' }))
if ($KeepData -or $explicitDataDir -or $fail -ne 0) {
    Write-Host ("数据目录保留供排查：" + $DataDir)
} else {
    try { [System.IO.Directory]::Delete($DataDir, $true) } catch { }
}
exit ($(if ($fail -eq 0) { 0 } else { 1 }))
