#Requires -Version 5.1
<#
  两间机房同网段的"互不干扰"验收（真实拓扑：A202 教师机 .10/学生 11-99；A203 教师机 .100/学生 101-199）

  为什么单独有这一个脚本：同一网段的两间机房，最怕的是
    ① 配对文件装错（把 A 房的配对装到 B 房学生机上）→ 那台机会真的被另一间管着；
    ② 有人以为"同网段就会串"——**不会**：学生端 pin 的是教师机公钥，别的教师机发策略一律验签失败。
  这两件事都很难靠"看起来正常/不正常"判断，所以做成可执行断言，装完机房跑一遍就有答案。

  本脚本用两个教师端 + 若干沙箱模拟两间房（沙箱互相独立、不碰真机，同 testbed-clients.ps1）。

  用法（在 labguard-server 仓库根目录）：
      powershell -File scripts\two-room-isolation.ps1
      powershell -File scripts\two-room-isolation.ps1 -SkipIsolationGate -KeepRoot
  退出码：0 = 全过；1 = 有失败。
#>
[CmdletBinding()]
param(
    [string]$ClientRepo,
    [int]$PortA = 8232,
    [int]$PortB = 8233,
    [switch]$SkipBuild,
    [switch]$SkipIsolationGate,
    [switch]$KeepRoot
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $ClientRepo) { $ClientRepo = Join-Path (Split-Path -Parent $repo) '机房助手复刻' }
$root = Join-Path $env:TEMP ('lg-2room-' + (Get-Date -Format 'MMdd-HHmmss'))
$pass = 0; $fail = 0

function Say([string]$t) { Write-Host $t }
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + "  " + $detail) -ForegroundColor Red }
}

if (-not $SkipBuild) {
    Say '=== 构建两仓（Release）==='
    & dotnet build (Join-Path $repo 'LabGuardServer.sln') -c Release --nologo -v minimal | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { Say '服务端构建失败'; exit 1 }
    & dotnet build (Join-Path $ClientRepo 'LabGuard.sln') -c Release --nologo -v minimal | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { Say '客户端构建失败'; exit 1 }
}

$srvExe = Join-Path $repo 'src\LabGuardServer\bin\Release\net48\LabGuardServer.exe'
$svcExe = Join-Path $ClientRepo 'src\LabGuard.Service\bin\Release\net48\LabGuard.Service.exe'
$setExe = Join-Path $ClientRepo 'src\LabGuard.Settings\bin\Release\net48\LabGuard.Settings.exe'
foreach ($e in @($srvExe, $svcExe, $setExe)) {
    if (-not (Test-Path -LiteralPath $e)) { Say ('缺少可执行文件：' + $e); exit 1 }
}
foreach ($d in @('assets')) {
    $a = Join-Path $repo ('src\LabGuardServer\bin\Release\net48\' + $d)
    [System.IO.Directory]::CreateDirectory($a) | Out-Null
    Copy-Item (Join-Path $repo ('assets\catalog.json')) (Join-Path $a 'catalog.json') -Force
}

$hklmBefore = Test-Path 'HKLM:\SOFTWARE\LabGuard'
$usbBefore = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\usbstor' -Name Start -ErrorAction SilentlyContinue).Start

if (-not $SkipIsolationGate) {
    Say '=== ① 安全闸：先证明沙箱不碰真机 ==='
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-sandbox.ps1') `
        -ClientRepo $ClientRepo -SkipBuild -Seconds 8 | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { Say '隔离验真未通过，禁止继续（先修隔离）'; exit 1 }
}

[System.IO.Directory]::CreateDirectory($root) | Out-Null
$dirA = Join-Path $root 'roomA'; $dirB = Join-Path $root 'roomB'
foreach ($d in @($dirA, $dirB)) { [System.IO.Directory]::CreateDirectory($d) | Out-Null }

# 两间房的策略用不同标记（A=8201 / B=8302），用来判断"这台机器到底听了谁的话"
function Policy([int]$mark) {
    return '{"Version":1,"PasswordHash":"","Enabled":false,"InputHardLock":"Off","ConfirmDelaySeconds":0,' +
           '"StartDelaySeconds":' + $mark + ',"ResumeAfterMinutes":0,"Classroom":{"Enabled":false},' +
           '"Network":{"Enabled":false,"DnsServers":[],"WatchedInterfaces":[],"ExcludedInterfaces":[]},' +
           '"ProcessBlock":{"Enabled":false},"FileCreation":{"Enabled":false,"WatchPaths":[]},' +
           '"Usb":{"Enabled":false,"AllowStorage":true},"Hosts":{"Enabled":false},"Browser":{"Enabled":false},' +
           '"Shell":{"Enabled":false},"SafeMode":{"Enabled":false},"Site":{"ShowMachineNumber":true},' +
           '"RegistryAcl":{"Enabled":false},"Watchdog":{"Enabled":false},"AntiTamper":{"Enabled":false},' +
           '"Server":{"Enabled":true,"Address":"","PollSeconds":2,"CheckinSeconds":5,"FailuresBeforeFallback":3}}'
}
$policyA = Join-Path $root 'policyA.json'; $policyB = Join-Path $root 'policyB.json'
$pairA = Join-Path $root 'pairing-A202.json'; $pairB = Join-Path $root 'pairing-A203.json'
[System.IO.File]::WriteAllText($policyA, (Policy 8201), (New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText($policyB, (Policy 8302), (New-Object System.Text.UTF8Encoding($false)))

# 沙箱本机预设：心跳间隔调到 5 秒，否则默认 60 秒 —— 观察窗里一台设备都不会进在线列表
# （教训：Server 段是**本机连接配置**，策略下发不会覆盖它，只能在 --init 时用 --import 灌进去）
$preset = Join-Path $root 'sandbox-preset.json'
[System.IO.File]::WriteAllText($preset,
    '{"Server":{"Enabled":true,"Address":"","PollSeconds":2,"CheckinSeconds":5,"FailuresBeforeFallback":3}}',
    (New-Object System.Text.UTF8Encoding($false)))

Say '=== ② 起两间房的教师端（各自的密钥 / 各自的配对文件）==='
$srvA = Start-Process -FilePath $srvExe -WindowStyle Hidden -PassThru `
    -ArgumentList @('--headless','--data-dir',$dirA,'--port',"$PortA",'--set-policy',$policyA,
                    '--export-pairing',$pairA,'--pairing-address','127.0.0.1',
                    '--room-set','A202','--expect-ips','10.46.254.11-99','--seconds','170') `
    -RedirectStandardOutput (Join-Path $root 'srvA.out') -RedirectStandardError (Join-Path $root 'srvA.err')
$srvB = Start-Process -FilePath $srvExe -WindowStyle Hidden -PassThru `
    -ArgumentList @('--headless','--data-dir',$dirB,'--port',"$PortB",'--set-policy',$policyB,
                    '--export-pairing',$pairB,'--pairing-address','127.0.0.1',
                    '--room-set','A203','--expect-ips','10.46.254.101-199','--seconds','170') `
    -RedirectStandardOutput (Join-Path $root 'srvB.out') -RedirectStandardError (Join-Path $root 'srvB.err')
# 记录"两间房教师端预计退出"的时刻（--seconds 170）；⑥ 收尾就等到这个点，
# 不用去读进程的 StartTime（PS 5.1 下对已退出/隐藏进程读 StartTime 可能拿到 $null，
# 接着 `170 - $null` 会报 op_Subtraction 找不到重载 —— 本次踩过）
$srvDeadline = (Get-Date).AddSeconds(174)

$ready = $false
for ($i = 0; $i -lt 40 -and -not $ready; $i++) {
    Start-Sleep -Milliseconds 500
    $ready = (Test-Path -LiteralPath $pairA) -and (Test-Path -LiteralPath $pairB)
}
Check 'S0 两间房的教师端都就绪（各自签发了策略 + 导出了配对文件）' $ready

$fa = (Get-Content -LiteralPath $pairA -Raw -Encoding UTF8 | ConvertFrom-Json).fingerprint
$fb = (Get-Content -LiteralPath $pairB -Raw -Encoding UTF8 | ConvertFrom-Json).fingerprint
Say ('      A202 公钥指纹：' + $fa)
Say ('      A203 公钥指纹：' + $fb)
Check 'S1 两间房的公钥指纹**不同**（相同 = 教师机用了同一个系统镜像，必须删密钥重生成）' ($fa -ne $fb)

# 本机房信息直接写在教师端自己的命令行里（少起两个进程，少一处出错的地方）：
# 本地沙箱都是 127.0.0.1，不在 10.46.254.x 内 → 它们会被判"越界"，这正是要断言的效果。

function Cache([string]$dir) {
    $p = Join-Path $dir 'server-policy.cache'
    if (Test-Path -LiteralPath $p) { return (Get-Content -LiteralPath $p -Raw -Encoding UTF8) }
    return ''
}
function State([string]$dir) {
    $p = Join-Path $dir 'server-state.json'
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    try { return (Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null }
}
function Wait-Cache([string]$dir, [string]$needle, [int]$to = 40) {
    $deadline = (Get-Date).AddSeconds($to)
    while ((Get-Date) -lt $deadline) {
        if ((Cache $dir).Contains($needle)) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}
function Start-Student([string]$dir, [string]$pairing, [string]$name, [int]$seconds = 150) {
    [System.IO.Directory]::CreateDirectory($dir) | Out-Null
    # LabGuard.Settings.exe 是 **GUI 子系统（WinExe）**：`&` 调它**不会等**（错题本 #200），
    # 于是 --init 与 --pair 会并发跑、互相覆盖配置（心跳间隔就丢了）。这里必须 Wait。
    Start-Process -FilePath $setExe -ArgumentList @('--init','LabTest2026','--import',$preset,'--data-dir',$dir) `
        -WindowStyle Hidden -Wait -PassThru | Out-Null
    Start-Process -FilePath $setExe -ArgumentList @('--pair',$pairing,'--data-dir',$dir) `
        -WindowStyle Hidden -Wait -PassThru | Out-Null
    return Start-Process -FilePath $svcExe -WindowStyle Hidden -PassThru `
        -ArgumentList @('--console','--seconds',"$seconds",'--data-dir',$dir,'--machine-name',$name) `
        -RedirectStandardOutput (Join-Path $dir 'client-out.log') -RedirectStandardError (Join-Path $dir 'client-err.log')
}

Say '=== ③ 按房配对：A202 两台 + A203 一台（各听各的）==='
$procs = @()
$procs += Start-Student (Join-Path $root 'a1') $pairA 'A202-PC-11'
$procs += Start-Student (Join-Path $root 'a2') $pairA 'A202-PC-12'
$procs += Start-Student (Join-Path $root 'b1') $pairB 'A203-PC-101'
$okA1 = Wait-Cache (Join-Path $root 'a1') '"StartDelaySeconds":8201'
$okA2 = Wait-Cache (Join-Path $root 'a2') '"StartDelaySeconds":8201'
$okB1 = Wait-Cache (Join-Path $root 'b1') '"StartDelaySeconds":8302'
Check 'S2 A202 的两台学生只拿到 A202 的策略（标记 8201）' ($okA1 -and $okA2)
Check 'S2 A203 的那台学生只拿到 A203 的策略（标记 8302）' $okB1
Check 'S2 交叉污染检查：A202 学生里没有 8302、A203 学生里没有 8201' `
    ((-not (Cache (Join-Path $root 'a1')).Contains('8302')) -and (-not (Cache (Join-Path $root 'b1')).Contains('8201')))

Say '=== ④ 装错配对会怎样（模拟技术员把 A202 的配对装到了 A203 的机器上）==='
$procs += Start-Student (Join-Path $root 'c1') $pairA 'A203-PC-150'
$okC1 = Wait-Cache (Join-Path $root 'c1') '"StartDelaySeconds":8201'
Check 'S3 配错的那台**确实会被另一间管**（它 pin 的是 A202 的公钥 → 拿到 8201）——这不是 bug，是"谁配的谁管"' $okC1
Check 'S3 它 pin 的是 A202 公钥、地址也是 A202 → 用它的状态文件取证（见 S5 第一条）' $okC1

Say '=== ⑤ 跨房控制为什么在数学上做不到（这台 pin 的是 A203 公钥，但地址被换成 A202）==='
$c2 = Join-Path $root 'c2'
[System.IO.Directory]::CreateDirectory($c2) | Out-Null
Start-Process -FilePath $setExe -ArgumentList @('--init','LabTest2026','--import',$preset,'--data-dir',$c2) `
    -WindowStyle Hidden -Wait -PassThru | Out-Null
Start-Process -FilePath $setExe -ArgumentList @('--pair',$pairB,'--data-dir',$c2) `
    -WindowStyle Hidden -Wait -PassThru | Out-Null
# 只改地址（保留 A203 的公钥 pin）：等价于"有人把 A202 教师机的地址塞给了这台机器"
$pjson = Join-Path $c2 'server-pairing.json'
$doc = Get-Content -LiteralPath $pjson -Raw -Encoding UTF8 | ConvertFrom-Json
$doc.addresses = @('127.0.0.1')
$doc.port = $PortA
[System.IO.File]::WriteAllText($pjson, ($doc | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
$procs += Start-Process -FilePath $svcExe -WindowStyle Hidden -PassThru `
    -ArgumentList @('--console','--seconds','150','--data-dir',$c2,'--machine-name','A203-PC-151') `
    -RedirectStandardOutput (Join-Path $c2 'client-out.log') -RedirectStandardError (Join-Path $c2 'client-err.log')
Start-Sleep -Seconds 16
$c2cache = Cache $c2
$c2state = State $c2
Check 'S4 它**拒收** A202 的策略（信封用它 pin 的 A203 公钥验签不通过）→ 缓存里没有 8201' `
    (-not $c2cache.Contains('8201'))
Check 'S4 它也没有转到 A202 的策略上（appliedSeq 保持 0 / 状态里带拒收原因）' `
    ($null -eq $c2state -or $c2state.appliedSeq -eq 0 -or -not [string]::IsNullOrEmpty($c2state.lastError)) `
    $(if ($c2state) { 'appliedSeq=' + $c2state.appliedSeq + ' lastError=' + $c2state.lastError } else { '（无状态文件）' })

Say '=== ⑥ 取证：每台机器"听谁的"由**它自己的状态文件**说话（不依赖教师端退出清单，脚本可重复）==='
# 说明：教师端"谁看得到谁"（设备清单只含本房设备、越界标 ⚠跨机房）由 scripts\testbed-clients.ps1 的
# S9/S11 断言（那边有完整的清单取证）。这里只用**学生端自己的状态**取证，避免依赖进程退出时序。
$sa1 = State (Join-Path $root 'a1'); $sb1 = State (Join-Path $root 'b1')
Check 'S5 A202 学生的同步地址指向 A202 教师机（127.0.0.1:8232）' `
    ($null -ne $sa1 -and $sa1.address -eq ('127.0.0.1:' + $PortA)) $(if ($sa1) { $sa1.address } else { '（无状态）' })
Check 'S5 A203 学生的同步地址指向 A203 教师机（127.0.0.1:8233）——不串' `
    ($null -ne $sb1 -and $sb1.address -eq ('127.0.0.1:' + $PortB)) $(if ($sb1) { $sb1.address } else { '（无状态）' })

$sc1 = State (Join-Path $root 'c1')
Check 'S5 配错的那台：地址指向 A202（8232）= 它被 A202 管着 → 装错**看得见**（地址+缓存标记都是另一间）' `
    ($null -ne $sc1 -and $sc1.address -eq ('127.0.0.1:' + $PortA)) $(if ($sc1) { $sc1.address } else { '（无状态）' })

# 本机房信息（越界体检的配置源）：用 --room-info 取证，证明段已生效
$infoOut = Join-Path $root 'roominfo-A.txt'
Start-Process -FilePath $srvExe -WindowStyle Hidden -Wait -RedirectStandardOutput $infoOut `
    -ArgumentList @('--data-dir', $dirA, '--room-info') | Out-Null
$infoText = Get-Content -LiteralPath $infoOut -Raw -Encoding UTF8
Check 'S5 本机房信息已生效：A202 + 期望段 10.46.254.11-99（设备清单会据此标 ⚠跨机房）' `
    (($infoText -match 'A202') -and ($infoText -match '10\.46\.254\.11-99')) `
    ((($infoText -split "`n") | Where-Object { $_ -match '本机房' }) -join ' ')

foreach ($p in $procs) { try { if ($p -and -not $p.HasExited) { $p | Stop-Process -Force } } catch { } }
while ((Get-Date) -lt $srvDeadline) { Start-Sleep -Seconds 5 }
try { if (-not $srvA.HasExited) { $srvA | Stop-Process -Force } } catch { }
try { if (-not $srvB.HasExited) { $srvB | Stop-Process -Force } } catch { }

Say '=== ⑦ 收尾：真机状态复核 ==='
$hklmAfter = Test-Path 'HKLM:\SOFTWARE\LabGuard'
$usbAfter = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\usbstor' -Name Start -ErrorAction SilentlyContinue).Start
Check 'S6 HKLM\SOFTWARE\LabGuard 与开始前一致' ($hklmBefore -eq $hklmAfter)
Check 'S6 usbstor Start 未被改动' ($usbAfter -eq $usbBefore)

Say ''
Say ('两机房验收：' + $pass + ' 通过 / ' + $fail + ' 失败')
Get-Process LabGuardServer, LabGuard.Service -ErrorAction SilentlyContinue |
    ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch { } }
if (-not $KeepRoot -and $fail -eq 0) { try { [System.IO.Directory]::Delete($root, $true) } catch { } }
else { Say ('（目录已保留：' + $root + '）') }
exit $(if ($fail -eq 0) { 0 } else { 1 })
