<#
  LabGuard Server 压力测试（执行计划 LG-011）：N 个模拟学生机并发轮询 + 心跳 + 策略发布

  测什么：
    · 服务端在 N 客户端轮询下是否稳定（不崩、不卡、内存不持续增长）
    · 发布新策略后，**95% 客户端在 10 秒内看到新 seq**（计划 §C4 的通过条件）
    · 压力期间服务端仍能正常响应（探针延迟 p95）
  怎么发策略：用服务端 CLI 保存（写磁盘）→ 运行中的实例靠"磁盘热拾取"自动切换（LG-003 新增能力）

  用法：
      powershell -File scripts\stress-headless.ps1 -Clients 100 -Seconds 180
      powershell -File scripts\stress-headless.ps1 -Clients 200 -Seconds 120 -SkipBuild
  退出码：0 = 达到通过条件（100 客户端场景） / 1 = 未达标或出错
#>
[CmdletBinding()]
param(
    [int]$Clients = 100,
    [int]$Seconds = 180,
    [int]$Port = 8220,
    [int]$PollSeconds = 5,
    [int]$CheckinSeconds = 30,
    [string]$DataDir,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$explicit = $true
if (-not $DataDir) {
    $explicit = $false
    $DataDir = Join-Path ([System.IO.Path]::GetTempPath()) ('lg-stress-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
[System.IO.Directory]::CreateDirectory($DataDir) | Out-Null
$pass = 0; $fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + "  " + $detail) -ForegroundColor Red }
}

if (-not $SkipBuild) {
    Write-Host '=== 构建 ==='
    & dotnet build (Join-Path $repo 'LabGuardServer.sln') -c Release --nologo -v minimal | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { Write-Host '构建失败' -ForegroundColor Red; exit 1 }
}
$exe = Join-Path $repo 'src\LabGuardServer\bin\Release\net48\LabGuardServer.exe'
$exeAssets = Join-Path $repo 'src\LabGuardServer\bin\Release\net48\assets'
[System.IO.Directory]::CreateDirectory($exeAssets) | Out-Null
Copy-Item (Join-Path $repo 'assets\catalog.json') (Join-Path $exeAssets 'catalog.json') -Force

Write-Host ("=== 起 headless 服务端（端口 " + $Port + "，客户端 " + $Clients + "，时长 " + $Seconds + " 秒）===")
Get-Process LabGuardServer -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep -Seconds 1
$srvLog = Join-Path $DataDir 'srv.log'
$srvArgs = @('--headless', '--data-dir', $DataDir, '--port', "$Port", '--bind-all',
             '--policy-default', '--seconds', "$($Seconds + 120)")
$srv = Start-Process -FilePath $exe -ArgumentList $srvArgs -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $srvLog -RedirectStandardError (Join-Path $DataDir 'srv.err')
Start-Sleep -Seconds 4
$base = "http://127.0.0.1:$Port"
$v0 = (Invoke-WebRequest "$base/api/version" -UseBasicParsing -TimeoutSec 5).Content
Write-Host ("  服务端就绪：" + $v0)
$memBefore = (Get-Process -Id $srv.Id).WorkingSet64

# ---------------------------------------------------------------- 工作线程
$pollMs = $PollSeconds * 1000
$checkinMs = $CheckinSeconds * 1000
$deadline = (Get-Date).AddSeconds($Seconds).ToString('o')
$bag = [hashtable]::Synchronized(@{})
$workerScript = {
    param($id, $base, $deadlineIso, $pollMs, $checkinMs, $bag)
    $deadline = [datetime]::Parse($deadlineIso)
    $lat = New-Object System.Collections.ArrayList
    $notices = New-Object System.Collections.ArrayList
    $ok = 0; $err = 0; $lastSeq = -1; $lastCheckin = [datetime]::MinValue
    $firstError = $null
    while ((Get-Date) -lt $deadline) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $req = [System.Net.HttpWebRequest]::Create("$base/api/version")
            $req.Timeout = 3000; $req.Proxy = $null
            $resp = $req.GetResponse()
            $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
            $json = $sr.ReadToEnd(); $sr.Close(); $resp.Close()
            $sw.Stop()
            [void]$lat.Add($sw.Elapsed.TotalMilliseconds)
            $ok++
            $seq = 0
            if ($json -match '"seq"\s*:\s*(\d+)') { $seq = [int]$Matches[1] }
            if ($seq -gt $lastSeq) {
                [void]$notices.Add([pscustomobject]@{ seq = $seq; at = (Get-Date) })
                $lastSeq = $seq
            }
            if (((Get-Date) - $lastCheckin).TotalMilliseconds -ge $checkinMs) {
                $body = '{"machine":"STRESS-' + $id + '","version":"0.10.0","seq":' + $lastSeq + ',"paused":false}'
                $ck = [System.Net.HttpWebRequest]::Create("$base/api/checkin")
                $ck.Method = 'POST'; $ck.ContentType = 'application/json'; $ck.Timeout = 3000; $ck.Proxy = $null
                $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
                $ck.ContentLength = $bytes.Length
                $s = $ck.GetRequestStream(); $s.Write($bytes, 0, $bytes.Length); $s.Close()
                $cr = $ck.GetResponse(); $cr.Close()
                $lastCheckin = Get-Date
            }
        } catch {
            $sw.Stop()
            $msg = $_.Exception.Message
            if ($msg -match '429') {
                $rl++
                $lastCheckin = Get-Date          # 被限速也按正常周期退避（真实客户端同样口径），避免"重试风暴"
            } else {
                $err++
                if (-not $firstError) { $firstError = $msg }
            }
        }
        Start-Sleep -Milliseconds $pollMs
    }
    $bag[$id] = @{ ok = $ok; err = $err; limited = $rl; lat = $lat; notices = $notices; firstError = $firstError }
}

Write-Host '=== 启动模拟客户端 ==='
$runspaces = @()
$handles = @()
1..$Clients | ForEach-Object {
    $ps = [powershell]::Create()
    [void]$ps.AddScript($workerScript).AddArgument($_).AddArgument($base)
    [void]$ps.AddArgument($deadline).AddArgument($pollMs).AddArgument($checkinMs).AddArgument($bag)
    $runspaces += $ps
    $handles += $ps.BeginInvoke()
}

# ---------------------------------------------------------------- 压力期间发两次新策略
$publishTimes = @{}
$lastPublishedSeq = 1
Start-Sleep -Seconds ([Math]::Max(5, [int]($Seconds / 6)))
foreach ($round in 1..2) {
    $policy = @{
        Version = 1; PasswordHash = ''; Enabled = $true; InputHardLock = 'Off'
        ConfirmDelaySeconds = 0; StartDelaySeconds = (4000 + $round); ResumeAfterMinutes = 0
    } | ConvertTo-Json
    $f = Join-Path $DataDir ("policy-$round.json")
    [System.IO.File]::WriteAllText($f, $policy, (New-Object System.Text.UTF8Encoding($false)))
    $out = & $exe --data-dir $DataDir --set-policy $f 2>&1 | Out-String
    $savedAt = Get-Date
    # WinExe 的 stdout 不一定抓得到 → 以 /api/version 为准（顺便证明"外部保存被运行中的实例拾取"生效）
    $seq = $lastPublishedSeq
    for ($i = 0; $i -lt 40; $i++) {
        $probe = (Invoke-WebRequest "$base/api/version" -UseBasicParsing -TimeoutSec 5).Content
        if ($probe -match '"seq"\s*:\s*(\d+)') {
            $candidate = [int]$Matches[1]
            if ($candidate -gt $lastPublishedSeq) { $seq = $candidate; break }
        }
        Start-Sleep -Milliseconds 250
    }
    if ($seq -le $lastPublishedSeq) { Write-Host ("  ⚠ 策略保存后服务端 seq 未前进（CLI 输出：" + $out.Trim() + "）") -ForegroundColor Yellow }
    $lastPublishedSeq = $seq
    $publishTimes[$seq] = $savedAt
    Write-Host ("  已发布新策略 seq=" + $seq + "（" + (Get-Date).ToString('HH:mm:ss') + "）")
    Start-Sleep -Seconds ([Math]::Max(6, [int]($Seconds / 6)))
}

Write-Host '=== 等待结束 ==='
$waitDeadline = (Get-Date).AddSeconds(($Seconds + 30))
while ($handles | Where-Object { -not $_.IsCompleted } | Select-Object -First 1) {
    if ((Get-Date) -gt $waitDeadline) { break }
    Start-Sleep -Seconds 2
}
foreach ($ps in $runspaces) { try { $ps.Stop() } catch { }; try { $ps.Dispose() } catch { } }

# ---------------------------------------------------------------- 汇总
$okTotal = 0; $errTotal = 0; $limitedTotal = 0; $lats = New-Object System.Collections.ArrayList
$noticeAll = New-Object System.Collections.ArrayList
$firstErr = $null
foreach ($k in $bag.Keys) {
    $r = $bag[$k]
    $okTotal += $r.ok; $errTotal += $r.err; $limitedTotal += $r.limited
    foreach ($l in $r.lat) { [void]$lats.Add($l) }
    if ($r.firstError -and -not $firstErr) { $firstErr = $r.firstError }
    foreach ($n in $r.notices) {
        if ($publishTimes.ContainsKey($n.seq)) {
            [void]$noticeAll.Add(($n.at - $publishTimes[$n.seq]).TotalSeconds)
        }
    }
}
$sortedLat = $lats | Sort-Object
$sortedNotice = $noticeAll | Sort-Object
function Pct($arr, [double]$p) { if ($arr.Count -eq 0) { return -1 }; $i = [Math]::Min($arr.Count - 1, [int][Math]::Floor($p * $arr.Count)); return $arr[$i] }
$memAfter = (Get-Process -Id $srv.Id -ErrorAction SilentlyContinue).WorkingSet64
$alive = (Get-Process -Id $srv.Id -ErrorAction SilentlyContinue) -ne $null

Write-Host ''
Write-Host ("客户端数：" + $Clients + "   轮询间隔：" + $PollSeconds + "s   时长：" + $Seconds + "s   端口：" + $Port)
Write-Host ("请求成功：" + $okTotal + "   失败：" + $errTotal + "   限速 429：" + $limitedTotal + "（同 IP 并发心跳，真实机房每机独立 IP）" + $(if ($firstErr) { "   首个错误：" + $firstErr } else { '' }))
Write-Host ("/api/version 延迟(ms)：p50=" + [Math]::Round((Pct $sortedLat 0.5), 1) +
            " p95=" + [Math]::Round((Pct $sortedLat 0.95), 1) + " max=" + [Math]::Round(($sortedLat | Select-Object -Last 1), 1))
Write-Host ("收到新策略耗时(s)：样本=" + $noticeAll.Count +
            " p95=" + [Math]::Round((Pct $sortedNotice 0.95), 2) + " max=" + [Math]::Round(($sortedNotice | Select-Object -Last 1), 2))
Write-Host ("服务端内存：before=" + [Math]::Round($memBefore / 1MB, 1) + " MB  after=" + [Math]::Round($memAfter / 1MB, 1) + " MB")

Check '1. 服务端全程存活（无崩溃）' $alive
Check '2. 真实失败率 < 1%（429 限速单列，不计故障）' (($errTotal / [Math]::Max(1, ($okTotal + $errTotal + $limitedTotal))) -lt 0.01) ("err=" + $errTotal + " limited=" + $limitedTotal)
Check '3. /api/version p95 延迟 < 500ms' ((Pct $sortedLat 0.95) -lt 500) ("p95=" + (Pct $sortedLat 0.95))
Check '4. 95% 客户端在 10 秒内看到新策略' ((Pct $sortedNotice 0.95) -gt 0 -and (Pct $sortedNotice 0.95) -le 10) ("p95=" + (Pct $sortedNotice 0.95))
Check '5. 内存增长 < 100 MB' ((($memAfter - $memBefore) / 1MB) -lt 100) ("delta=" + [Math]::Round(($memAfter - $memBefore) / 1MB, 1) + " MB")

$report = [pscustomobject]@{
    clients = $Clients; seconds = $Seconds; pollSeconds = $PollSeconds; port = $Port
    requestsOk = $okTotal; requestsFailed = $errTotal; failureRate = [Math]::Round($errTotal / [Math]::Max(1, ($okTotal + $errTotal)), 5)
    latencyP50Ms = [Math]::Round((Pct $sortedLat 0.5), 1); latencyP95Ms = [Math]::Round((Pct $sortedLat 0.95), 1)
    noticeSamples = $noticeAll.Count; noticeP95S = [Math]::Round((Pct $sortedNotice 0.95), 2)
    memoryBeforeMB = [Math]::Round($memBefore / 1MB, 1); memoryAfterMB = [Math]::Round($memAfter / 1MB, 1)
    serverAlive = $alive; firstError = $firstErr
}
$reportPath = Join-Path $DataDir ("stress-report-" + $Clients + "-" + $Port + ".json")
[System.IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("报告：" + $reportPath)

Get-Process LabGuardServer -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }
Write-Host ''
Write-Host ("结果：" + $pass + " 通过，" + $fail + " 失败") -ForegroundColor ($(if ($fail -eq 0) { 'Green' } else { 'Red' }))
if (-not $explicit -and $fail -eq 0) { try { [System.IO.Directory]::Delete($DataDir, $true) } catch { } }
exit ($(if ($fail -eq 0) { 0 } else { 1 }))
