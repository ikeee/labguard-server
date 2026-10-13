#Requires -Version 5.1
<#
  端到端测试台：**一个真实教师端 + N 个真实学生端沙箱**（执行计划 LG-007 / LG-011 的可重复场景）

  设计意图（为什么不用"假客户端"）：
    · 教师端用真实服务端二进制（headless CLI，临时数据目录）—— 与正式部署同一份程序、同一份协议；
    · 学生端用**真实客户端进程**的沙箱模式（`--data-dir <目录>` 只改本进程配置目录）——
      不装服务、不写 HKLM、不碰真实 `%ProgramData%\LabGuard`，因此一台机器就能并行跑多台"学生机"；
    · 每个沙箱有自己的 `device-id.txt` → 天然具备多设备身份，这正是**分组**测试所需要的；
    · `--machine-name` 让一台物理机冒充多台机器名（顺带覆盖"旧客户端只报机器名"的兜底路径）；
    · 全部断言都读**文件**（`server-state.json` / `server-policy.cache` / `groups.json`）+ 教师端退出时的
      设备清单，不依赖界面、可无人值守。

  ⚠ 先跑 scripts\verify-sandbox.ps1（本脚本默认自动先跑一遍）：
    2026-10-13 首跑时"沙箱"并没有真正隔离，沙箱客户端按**真机**旧配置改了真机
    （U 盘禁用、浏览器策略、启动菜单、HKLM\SOFTWARE\LabGuard）。隔离验真是这个测试台的**安全闸**。

  用法：
      powershell -File scripts\testbed-clients.ps1                        # 3 台学生端，默认端口 8230
      powershell -File scripts\testbed-clients.ps1 -Clients 5 -KeepRoot   # 5 台，保留目录供排查
      powershell -File scripts\testbed-clients.ps1 -SkipIsolationGate     # 已知隔离 OK 时跳过（省 25 秒）
  退出码：0 = 全部场景通过；1 = 有场景失败。
#>
[CmdletBinding()]
param(
    [int]$Clients = 3,
    [int]$Port = 8230,
    [string]$ClientRepo,
    [switch]$SkipBuild,
    [switch]$SkipIsolationGate,
    [switch]$KeepRoot
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot                                   # labguard-server
if (-not $ClientRepo) { $ClientRepo = Join-Path (Split-Path -Parent $repo) '机房助手复刻' }  # 本地默认（中文目录名）
$clientRepo = $ClientRepo
$root = Join-Path $env:TEMP ('lg-testbed-' + (Get-Date -Format 'MMdd-HHmmss'))
$pass = 0; $fail = 0; $scenarios = New-Object System.Collections.ArrayList

function Say([string]$t) { Write-Host $t }
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + "  " + $detail) -ForegroundColor Red }
    [void]$script:scenarios.Add([pscustomobject]@{ name = $name; pass = $ok; detail = $detail })
}

# ---------------------------------------------------------------- 构建
if (-not $SkipBuild) {
    Say '=== 构建两仓（Release）==='
    & dotnet build (Join-Path $repo 'LabGuardServer.sln') -c Release --nologo -v minimal | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { Say '服务端构建失败'; exit 1 }
    & dotnet build (Join-Path $clientRepo 'LabGuard.sln') -c Release --nologo -v minimal | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { Say '客户端构建失败'; exit 1 }
}

$srvExe = Join-Path $repo 'src\LabGuardServer\bin\Release\net48\LabGuardServer.exe'
$svcExe = Join-Path $clientRepo 'src\LabGuard.Service\bin\Release\net48\LabGuard.Service.exe'
$setExe = Join-Path $clientRepo 'src\LabGuard.Settings\bin\Release\net48\LabGuard.Settings.exe'
foreach ($e in @($srvExe, $svcExe, $setExe)) {
    if (-not (Test-Path -LiteralPath $e)) { Say ('缺少可执行文件：' + $e); exit 1 }
}
# 服务端要的开关目录快照（assets\catalog.json）
$srvAssets = Join-Path $repo 'src\LabGuardServer\bin\Release\net48\assets'
[System.IO.Directory]::CreateDirectory($srvAssets) | Out-Null
Copy-Item (Join-Path $repo 'assets\catalog.json') (Join-Path $srvAssets 'catalog.json') -Force

# 真机"不被污染"的基准：本套场景跑完前后必须一致
$hklmBefore = Test-Path 'HKLM:\SOFTWARE\LabGuard'
$usbBefore = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\usbstor' -Name Start -ErrorAction SilentlyContinue).Start

# ---------------------------------------------------------------- ① 安全闸：沙箱隔离验真
if (-not $SkipIsolationGate) {
    Say '=== ① 安全闸：先跑沙箱隔离验真（不通过就不往下跑）==='
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-sandbox.ps1') `
        -ClientRepo $clientRepo -SkipBuild -Seconds 10 | Select-Object -Last 4
    if ($LASTEXITCODE -ne 0) {
        Say '隔离验真未通过：沙箱会改真机，**禁止**继续跑场景。先修隔离（看 verify-sandbox.ps1 的差异输出）。'
        exit 1
    }
}

[System.IO.Directory]::CreateDirectory($root) | Out-Null
$srvDir = Join-Path $root 'server'
[System.IO.Directory]::CreateDirectory($srvDir) | Out-Null
$clientDirs = @()
1..$Clients | ForEach-Object {
    $d = Join-Path $root ('client{0}' -f $_)
    [System.IO.Directory]::CreateDirectory($d) | Out-Null
    $clientDirs += $d
}
Say ('测试台目录：' + $root)

# 无害策略：所有管控关闭（测试台只验"下发-验签-应用-上报"链路，不验 Guard 行为；Guard 有自己的 245 条自检）
$harmless = '{"Version":1,"PasswordHash":"","Enabled":false,"InputHardLock":"Off","ConfirmDelaySeconds":0,' +
            '"StartDelaySeconds":120,"ResumeAfterMinutes":0,' +
            '"Classroom":{"Enabled":false},"Network":{"Enabled":false,"DetectDisconnected":false,"EnforceDns":false,' +
            '"ForceFirewallOff":false,"DisconnectMask":false,"LockOnViolation":false,"ShutdownOnViolation":false,' +
            '"DnsServers":[],"WatchedInterfaces":[],"ExcludedInterfaces":[],"UnlockGestureSequence":"U,U,D,D,L,R,L,R"},' +
            '"ProcessBlock":{"Enabled":false},"FileCreation":{"Enabled":false,"WatchPaths":[],"Action":"Delete"},' +
            '"Usb":{"Enabled":false,"AllowStorage":true},"Hosts":{"Enabled":false,"Guard":false,"ProtectFileAcl":false},' +
            '"Browser":{"Enabled":false},"Shell":{"Enabled":false},"SafeMode":{"Enabled":false},' +
            '"Site":{"ShowMachineNumber":true},"RegistryAcl":{"Enabled":false},"Watchdog":{"Enabled":false},' +
            '"AntiTamper":{"Enabled":false,"KeepAliveWhenPaused":true},' +
            '"Server":{"Enabled":true,"Address":"","PollSeconds":2,"CheckinSeconds":10,"FailuresBeforeFallback":3}}'
$policyA = $harmless -replace '"StartDelaySeconds":120', '"StartDelaySeconds":1111'
$policyB = $harmless -replace '"StartDelaySeconds":120', '"StartDelaySeconds":2222'
$harmlessPath = Join-Path $root 'harmless.json'
$policyAPath = Join-Path $root 'group-a.json'
$policyBPath = Join-Path $root 'group-b.json'
[System.IO.File]::WriteAllText($harmlessPath, $harmless, (New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText($policyAPath, $policyA, (New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText($policyBPath, $policyB, (New-Object System.Text.UTF8Encoding($false)))

# 沙箱本机预设：把"连接教师机"的节奏调快，测试才好断言。
# 注意 Server 段属于**本机连接配置**（策略下发不会覆盖它），所以只能在 --init 时用 --import 灌进去：
# 默认 CheckinSeconds=60，一个 70 秒的观察窗里每台机器最多心跳一次，教师端设备清单根本填不满。
$preset = '{"Server":{"Enabled":true,"Address":"","PollSeconds":2,"CheckinSeconds":5,"FailuresBeforeFallback":3}}'
$presetPath = Join-Path $root 'sandbox-preset.json'
[System.IO.File]::WriteAllText($presetPath, $preset, (New-Object System.Text.UTF8Encoding($false)))

# ---------------------------------------------------------------- ② 起教师端
Say '=== ② 起教师端（headless，默认策略 = 无害策略）==='
$srvLog = Join-Path $root 'server-out.log'
$srvProc = Start-Process -FilePath $srvExe -WindowStyle Hidden -PassThru `
    -ArgumentList @('--headless', '--data-dir', $srvDir, '--port', "$Port",
                    '--set-policy', $harmlessPath, '--export-pairing', (Join-Path $root 'pairing.json'),
                    '--pairing-address', '127.0.0.1', '--seconds', '260') `
    -RedirectStandardOutput $srvLog -RedirectStandardError (Join-Path $root 'server-err.log')
$pairing = Join-Path $root 'pairing.json'
$ready = $false
for ($i = 0; $i -lt 40 -and -not $ready; $i++) { Start-Sleep -Milliseconds 500; $ready = Test-Path -LiteralPath $pairing }
Check 'S0 教师端就绪（策略已签发 + 配对文件已导出）' $ready

# ---------------------------------------------------------------- ③ 学生端沙箱：初始化 + 配对 + 启动
function Cached-Policy([string]$dir) {
    $p = Join-Path $dir 'server-policy.cache'
    if (Test-Path -LiteralPath $p) { return (Get-Content -LiteralPath $p -Raw -Encoding UTF8) }
    return ''
}
function Read-State([string]$dir) {
    $p = Join-Path $dir 'server-state.json'
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    try { return (Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null }
}
function Read-DeviceId([string]$dir) {
    $p = Join-Path $dir 'device-id.txt'
    if (Test-Path -LiteralPath $p) { return (Get-Content -LiteralPath $p -Raw -Encoding UTF8).Trim() }
    return $null
}
function Wait-State([string]$dir, [scriptblock]$cond, [int]$timeoutSec = 45) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $st = Read-State $dir
        if ($st -and (& $cond $st)) { return $st }
        Start-Sleep -Milliseconds 700
    }
    return (Read-State $dir)
}
function Wait-Cached([string]$dir, [string]$needle, [int]$timeoutSec = 45) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ((Cached-Policy $dir).Contains($needle)) { return $true }
        Start-Sleep -Milliseconds 700
    }
    return (Cached-Policy $dir).Contains($needle)
}

Say '=== ③ 学生端沙箱：初始化 + 配对 + 启动（真实客户端进程）==='
$procs = @()
$i = 0
foreach ($d in $clientDirs) {
    $i++
    $machineName = 'LAB-PC-{0}' -f $i
    # --init 必须先于 --pair：客户端的设计是"没设过密码 = 安装未完成 → 服务不启用管控"（有意行为）
    & $setExe --init 'LabTest2026' --import $presetPath --data-dir $d | Out-Null
    & $setExe --pair $pairing --data-dir $d | Out-Null
    $p = Start-Process -FilePath $svcExe -WindowStyle Hidden -PassThru `
        -ArgumentList @('--console', '--seconds', '450', '--data-dir', $d, '--machine-name', $machineName) `
        -RedirectStandardOutput (Join-Path $d 'client-out.log') -RedirectStandardError (Join-Path $d 'client-err.log')
    $procs += $p
}
Start-Sleep -Seconds 8
$deviceIds = $clientDirs | ForEach-Object { Read-DeviceId $_ }
Check ('S1 每台沙箱生成独立设备身份（' + ($deviceIds -join ', ') + '）') `
    ((($deviceIds | Where-Object { $_ -like 'LG-*' }).Count -eq $Clients) -and
     (($deviceIds | Select-Object -Unique).Count -eq $Clients))

# ---------------------------------------------------------------- S2 默认策略下发
$s1 = @()
foreach ($d in $clientDirs) { $s1 += Wait-State $d { param($st) $st.appliedSeq -ge 1 -and $st.linkState -eq 'Polling' } 45 }
Check 'S2 全部学生端在 45s 内应用默认策略（appliedSeq ≥ 1 且 Polling）' `
    (($s1 | Where-Object { $_ -and $_.appliedSeq -ge 1 -and $_.linkState -eq 'Polling' }).Count -eq $Clients) `
    (($s1 | ForEach-Object { 'seq=' + $_.appliedSeq + '/' + $_.linkState }) -join ' ')
Check 'S2 默认策略落进每台沙箱的缓存（可离线沿用）' `
    (($clientDirs | Where-Object { (Cached-Policy $_) -like 'LGSRV1|*' }).Count -eq $Clients)

# ---------------------------------------------------------------- S3 分组下发（只影响该组）
Say '=== 分组：机房A 只放第 1 台，下发 A 策略（StartDelaySeconds=1111）==='
& $srvExe --data-dir $srvDir --add-group '机房A' --assign ('机房A=' + $deviceIds[0]) `
    --group-policy ('机房A=' + $policyAPath) | Out-Null
$gotA = Wait-Cached $clientDirs[0] '"StartDelaySeconds":1111' 45
Check 'S3 组 A 的成员拿到 A 策略（缓存里出现 StartDelaySeconds=1111）' $gotA
$others = @()
foreach ($d in $clientDirs[1..($Clients - 1)]) { $others += (Cached-Policy $d) }
Check 'S3 非本组设备**不受影响**（缓存里仍是默认策略 120）' `
    (($others | Where-Object { $_.Contains('"StartDelaySeconds":120') -and -not $_.Contains('"StartDelaySeconds":1111') }).Count -eq ($Clients - 1))

# ---------------------------------------------------------------- S4 调组（全局 seq 单调）
Say '=== 调组：把第 1 台改放进机房B，下发 B 策略（StartDelaySeconds=2222）==='
& $srvExe --data-dir $srvDir --add-group '机房B' --assign ('机房B=' + $deviceIds[0]) `
    --group-policy ('机房B=' + $policyBPath) | Out-Null
$gotB = Wait-Cached $clientDirs[0] '"StartDelaySeconds":2222' 45
Check 'S4 调组后按新组策略生效（2222 覆盖 1111）' $gotB
$groupsJson = Join-Path $srvDir 'groups.json'
$g = Get-Content -LiteralPath $groupsJson -Raw -Encoding UTF8 | ConvertFrom-Json
$seqA = ($g.groups | Where-Object { $_.name -eq '机房A' }).seq
$seqB = ($g.groups | Where-Object { $_.name -eq '机房B' }).seq
Check 'S4 全局 seq 单调（B 组 seq 大于 A 组 → 调组不会被客户端当回滚拒绝）' `
    ($seqB -gt $seqA) ("A=$seqA B=$seqB")

# ---------------------------------------------------------------- S5 按机器名入组（旧客户端兜底路径）
Say '=== 按机器名入组（只报机器名、不报设备ID 的机器也要能进组）==='
& $srvExe --data-dir $srvDir --add-group '机房C' --assign '机房C=LAB-PC-3' `
    --group-policy ('机房C=' + $policyAPath) | Out-Null
$gotC = Wait-Cached $clientDirs[2] '"StartDelaySeconds":1111' 45
Check 'S5 用机器名也能把设备分到组（缓存里出现该组策略）' $gotC

# ---------------------------------------------------------------- S6 全员暂停 / 恢复
Say '=== 全员暂停 → 恢复（默认策略）==='
& $srvExe --data-dir $srvDir --pause-default | Out-Null
$pausedOk = $false
$dl = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $dl -and -not $pausedOk) {
    $pausedOk = ((Cached-Policy $clientDirs[1]) -match '^LGSRV1\|\d+\|[^|]+\|1\|')
    if (-not $pausedOk) { Start-Sleep -Milliseconds 700 }
}
Check 'S6 全员暂停信封（paused=1）下达到非组设备' $pausedOk
& $srvExe --data-dir $srvDir --resume-default | Out-Null
$resumedOk = $false
$dl = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $dl -and -not $resumedOk) {
    $resumedOk = ((Cached-Policy $clientDirs[1]) -match '^LGSRV1\|\d+\|[^|]+\|0\|')
    if (-not $resumedOk) { Start-Sleep -Milliseconds 700 }
}
Check 'S6 全员恢复信封（paused=0）' $resumedOk
Check 'S6 发布过分组策略后仍能加载/改写默认策略（全局水位与默认组水位分离的回归点）' `
    ($pausedOk -and $resumedOk)

# ================================================================ 课堂演练：模拟一节 40 分钟的课
# 口径（重要，别夸大）：这里的"学生捣蛋"是**按真实事件写成台词**，本演练验证的是
#   「老师 1 次动作 → 全班 N 台在 X 秒内换成正确的开关组合（含分组隔离）」这条链路与耗时；
#   真正的"游戏进程被拦、Win+Tab 被屏蔽"由**学生机上的 Guard**执行（沙箱是干跑模式，只发策略不动系统）。
Say ''
Say '=== 课堂演练：一节 40 分钟的课（压缩成几分钟跑；学生台词是模拟的，链路与耗时是真的）==='
$classLedger = New-Object System.Collections.ArrayList

function Start-ClassPreset([string]$name) {
    & $srvExe --data-dir $srvDir --temp-preset $name | Out-Null
}
function Wait-AllCached([string]$needle, [int]$timeoutSec = 20) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $all = $true
        foreach ($d in $clientDirs) { if (-not (Cached-Policy $d).Contains($needle)) { $all = $false; break } }
        if ($all) { $sw.Stop(); return $sw.Elapsed.TotalSeconds }
        Start-Sleep -Milliseconds 200
    }
    $sw.Stop()
    return -1
}
function Ledger([string]$wn, [string]$ev, [string]$act, [int]$mac, [string]$took) {
    [void]$classLedger.Add([pscustomobject]@{ when = $wn; event = $ev; action = $act; machines = $mac; took = $took })
}

# 老师的"一键预设"= 机房自己的 temp-presets.json（托盘菜单里就是这么列出来的）
$presets = @'
{"schemaVersion":1,"presets":[
 {"name":"上课模式","description":"正常上课：锁 U 盘存储、放开浏览器（查资料）","durationMinutes":45,
  "patch":{"StartDelaySeconds":4101,"Usb":{"Enabled":true,"AllowStorage":false},"Browser":{"Enabled":false},"ProcessBlock":{"Enabled":false}}},
 {"name":"课堂严管","description":"有人打外网游戏：启用违规软件拦截 + 集中 DNS + 收浏览器小游戏","durationMinutes":45,
  "patch":{"StartDelaySeconds":4202,"ProcessBlock":{"Enabled":true,"BlockGames":true,"BlockVirtualDesktop":true},
           "Browser":{"Enabled":true,"BlockDownloads":true,"BlockChromeDino":true,"BlockEdgeSurf":true},
           "Network":{"EnforceDns":true,"BlockDoh":true},"Usb":{"Enabled":true,"AllowStorage":false}}},
 {"name":"课堂严管+桌面防逃","description":"有人多开桌面逃管制：再屏蔽 Win 键/任务视图/任务管理器","durationMinutes":45,
  "patch":{"StartDelaySeconds":4303,"ProcessBlock":{"Enabled":true,"BlockGames":true,"BlockVirtualDesktop":true,"BlockTaskManager":true,"BlockCommandPrompt":true},
           "Shell":{"Enabled":true,"HideTaskView":true,"BlockWindowsKey":true,"DisableTaskManager":true,"DisableCmd":true},
           "Session":{"Enabled":true,"BlockFastUserSwitch":true},
           "Browser":{"Enabled":true,"BlockDownloads":true,"BlockChromeDino":true,"BlockEdgeSurf":true},
           "Network":{"EnforceDns":true,"BlockDoh":true},"Usb":{"Enabled":true,"AllowStorage":false}}},
 {"name":"全班暂停（讲评）","description":"老师要讲评，全班先停下来","durationMinutes":10,"paused":true,"patch":{}}
]}
'@
[System.IO.File]::WriteAllText((Join-Path $srvDir 'temp-presets.json'), $presets, (New-Object System.Text.UTF8Encoding($false)))

# ---- ① 上课铃：老师点一下「上课模式」
Say '  [00:00] 上课铃。老师：点 1 下「上课模式」（托盘 → 临时课堂状态 → 上课模式）'
Start-ClassPreset '上课模式'
$took = Wait-AllCached '"StartDelaySeconds":4101'
Check ('课堂① 一键「上课模式」→ 全班 ' + $Clients + ' 台在 ' + [math]::Round($took, 1) + ' 秒内换到上课策略') ($took -ge 0)
Ledger '00:00' '上课铃' '点 1 下：上课模式' $Clients ([math]::Round($took, 1).ToString() + 's')

# ---- ② 有人打外网游戏
Say '  [00:08] 学生A：开浏览器打在线小游戏；学生B：插 U 盘想拷游戏进来'
Say '           老师：还是 1 下 —— 换「课堂严管」（拦小游戏 + 集中 DNS + 收浏览器）'
$sw = [Diagnostics.Stopwatch]::StartNew()
Start-ClassPreset '课堂严管'
$took = Wait-AllCached '"StartDelaySeconds":4202'
$okGames = $true
foreach ($d in $clientDirs) {
    $c = Cached-Policy $d
    if (-not $c.Contains('"BlockGames":true') -or -not $c.Contains('"BlockDoh":true')) { $okGames = $false }
}
Check ('课堂② 「课堂严管」全班生效（' + [math]::Round($took, 1) + 's），且信封里确实带上了 BlockGames / BlockDoh') `
    (($took -ge 0) -and $okGames)
Ledger '00:08' '打外网游戏 + 插 U 盘' '点 1 下：课堂严管' $Clients ([math]::Round($took, 1).ToString() + 's')

# ---- ③ 有人多开桌面想逃管制
Say '  [00:15] 学生C：按 Win+Tab 开虚拟桌面，把游戏窗口藏到第二个桌面'
Say '           学生D：Ctrl+Alt+Del 想「切换用户」，换个账号继续玩'
Say '           老师：还是 1 下 —— 换「课堂严管+桌面防逃」（屏蔽 Win 键/任务视图 + 拦虚拟桌面软件 + 禁换用户）'
Start-ClassPreset '课堂严管+桌面防逃'
$took = Wait-AllCached '"StartDelaySeconds":4303'
$okEscape = $true
foreach ($d in $clientDirs) {
    $c = Cached-Policy $d
    if (-not $c.Contains('"BlockWindowsKey":true') -or -not $c.Contains('"HideTaskView":true') -or
        -not $c.Contains('"BlockVirtualDesktop":true') -or -not $c.Contains('"BlockFastUserSwitch":true')) { $okEscape = $false }
}
Check ('课堂③ 桌面/会话防逃开关全班到位（' + [math]::Round($took, 1) + 's）：BlockWindowsKey + HideTaskView + BlockVirtualDesktop + BlockFastUserSwitch') `
    (($took -ge 0) -and $okEscape)
# 学生机上真的走到执行层了吗？（沙箱是干跑：日志里应是 [dry] SET-REG；"禁快速切换用户"写的是 HKLM 机器级位置）
$escapeDry = $false
foreach ($d in $clientDirs) {
    $lg = Join-Path $d 'client-out.log'
    if (Test-Path -LiteralPath $lg) {
        if ((Get-Content -LiteralPath $lg -Raw -Encoding UTF8).Contains('Policies\System\HideFastUserSwitching = 1')) { $escapeDry = $true }
    }
}
Check '课堂③ 学生机日志里出现 [dry] 写「禁快速切换用户」（证明开关真的走到了执行层）' $escapeDry
Ledger '00:15' '多开桌面逃管制' '点 1 下：课堂严管+桌面防逃' $Clients ([math]::Round($took, 1).ToString() + 's')

# ---- ④ 个别人继续破坏电脑：只处理他，不牵连全班（分组 + 临时层自动重算）
Say '  [00:22] 学生C 继续折腾：改注册表、开任务管理器、cmd 里删文件（就这 1 台）'
Say '           老师：还是 1 下 —— 把这一台划进「整改组」并只给它下发更严的组策略（其余人继续上课）'
$remedy = '{"StartDelaySeconds":4404,"Usb":{"Enabled":true,"AllowStorage":false},' +
          '"Browser":{"Enabled":true,"BlockDownloads":true},' +
          '"ProcessBlock":{"Enabled":true,"BlockTaskManager":true,"BlockCommandPrompt":true,"BlockRegedit":true},' +
          '"Shell":{"Enabled":true,"DisableTaskManager":true,"DisableCmd":true,"DisableRegistryTools":true}}'
$remedyPath = Join-Path $root 'class-remedy.json'
[System.IO.File]::WriteAllText($remedyPath, $remedy, (New-Object System.Text.UTF8Encoding($false)))
$badOne = $deviceIds[$Clients - 1]                      # 最后那台 = 捣蛋的那台
& $srvExe --data-dir $srvDir --add-group '整改组' --assign ('整改组=' + $badOne) `
    --group-policy ('整改组=' + $remedyPath) | Out-Null
$sw2 = [Diagnostics.Stopwatch]::StartNew()
$okRemedy = $false
$deadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $deadline) {
    $c = Cached-Policy $clientDirs[$Clients - 1]
    # 那台机 = 自己的更严基础（DisableRegistryTools）⊕ 全班临时覆盖（4303）——两条同时在
    if ($c.Contains('"DisableRegistryTools":true') -and $c.Contains('"StartDelaySeconds":4303')) { $okRemedy = $true; break }
    Start-Sleep -Milliseconds 200
}
$sw2.Stop()
Check ('课堂④ 只对捣蛋那 1 台加严：它同时拿到「整改组策略 ⊕ 全班临时覆盖」（' +
       [math]::Round($sw2.Elapsed.TotalSeconds, 1) + 's），其余机器不受影响') $okRemedy
$othersClean = $true
foreach ($d in $clientDirs[0..($Clients - 2)]) {
    if ((Cached-Policy $d).Contains('"DisableRegistryTools":true')) { $othersClean = $false }
}
Check '课堂④ 其他同学**完全不受影响**（分组隔离）' $othersClean
Ledger '00:22' '个别人破坏电脑' '点 1 下：划进整改组 + 只发那 1 台' 1 ([math]::Round($sw2.Elapsed.TotalSeconds, 1).ToString() + 's')

# ---- ⑤ 下课：结束临时状态 → 全班自动回基础策略
Say '  [00:40] 下课。老师：1 下「结束临时状态」→ 全班回到基础策略（不用逐项改回来）'
& $srvExe --data-dir $srvDir --temp-end | Out-Null
$sw3 = [Diagnostics.Stopwatch]::StartNew()
$backBase = $false
$deadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $deadline) {
    $all = $true
    foreach ($d in $clientDirs) { if ((Cached-Policy $d).Contains('"StartDelaySeconds":4303')) { $all = $false } }
    if ($all) { $backBase = $true; break }
    Start-Sleep -Milliseconds 200
}
$sw3.Stop()
Check ('课堂⑤ 一下课全班自动脱离课堂状态（' + [math]::Round($sw3.Elapsed.TotalSeconds, 1) + 's，无需老师逐项改回）') $backBase
Check '课堂⑤ 捣蛋那台的「整改组」严管策略仍然保留（分组基础没有被课堂状态覆盖掉）' `
    ((Cached-Policy $clientDirs[$Clients - 1]).Contains('"DisableRegistryTools":true'))
Ledger '00:40' '下课' '点 1 下：结束临时状态' $Clients ([math]::Round($sw3.Elapsed.TotalSeconds, 1).ToString() + 's')

Say ''
Say '  ---- 这节课的"老师动作账本" ----'
$classLedger | ForEach-Object {
    Say ('   ' + $_.when.PadRight(6) + ' ' + $_.event.PadRight(22) + ' | ' + $_.action.PadRight(30) +
         ' | 涉及 ' + $_.machines + ' 台 | 生效 ' + $_.took)
}
Say ('   合计：' + $classLedger.Count + ' 次动作处理 ' + $classLedger.Count + ' 个课堂事件；' +
     '同等效果下"老师逐个机位跑"= 至少 ' + ($Clients * $classLedger.Count) + ' 次到场操作')

# ---------------------------------------------------------------- S7 应用失败上报
Say '=== 应用失败上报：把某台沙箱的 config.json 设为只读，再逼它应用新策略 ==='
$failDir = $clientDirs[1]
$cfgPath = Join-Path $failDir 'config.json'
$beforeFailSeq = (Read-State $failDir).appliedSeq
if (Test-Path -LiteralPath $cfgPath) { (Get-Item -LiteralPath $cfgPath -Force).IsReadOnly = $true }
& $srvExe --data-dir $srvDir --set-policy $policyBPath | Out-Null
$failed = $false
$failState = $null
$dl = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $dl) {
    $failState = Read-State $failDir
    if ($failState -and -not [string]::IsNullOrEmpty($failState.lastError)) { $failed = $true; break }
    Start-Sleep -Milliseconds 700
}
Check 'S7 应用失败被记录（lastError 非空，且已应用序号不前进）' `
    ($failed -and $failState.appliedSeq -le $beforeFailSeq) `
    ('lastError=' + $failState.lastError + ' applied=' + $failState.appliedSeq)
# 注意：**先别恢复可写**。恢复后这台机器下一次成功应用就会把"应用失败"清掉，
# 末端的教师端设备清单里就看不到 error 标注了（本套最后要断言的那一项）。留到收尾再恢复。

# ---------------------------------------------------------------- S8 离线回退 + 恢复
Say '=== 离线回退：停教师端 → 学生端应进 FallbackSingle 且沿用最后策略 ==='
if (-not $srvProc.HasExited) { $srvProc | Stop-Process -Force -ErrorAction SilentlyContinue }
Get-Process LabGuardServer -ErrorAction SilentlyContinue |
    ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch { } }
$fb = Wait-State $clientDirs[0] { param($st) $st.linkState -eq 'FallbackSingle' } 45
Check 'S8 教师端不可达 → 回退单机（FallbackSingle），已应用策略不丢' `
    ($fb -and $fb.linkState -eq 'FallbackSingle' -and $fb.appliedSeq -ge 1) `
    ('state=' + $fb.linkState + ' applied=' + $fb.appliedSeq)

Say '=== 教师端恢复 → 学生端自动回到 Polling ==='
$srvProc2 = Start-Process -FilePath $srvExe -WindowStyle Hidden -PassThru `
    -ArgumentList @('--headless', '--data-dir', $srvDir, '--port', "$Port", '--seconds', '110') `
    -RedirectStandardOutput (Join-Path $root 'server2-out.log') -RedirectStandardError (Join-Path $root 'server2-err.log')
$back = Wait-State $clientDirs[0] { param($st) $st.linkState -eq 'Polling' } 60
Check 'S8 教师端恢复 → 学生端自动回到 Polling（无需人工干预）' ($back -and $back.linkState -eq 'Polling')

# ---------------------------------------------------------------- S9 克隆检测
Say '=== 克隆检测：再起一台沙箱，复制第 1 台的 device-id ==='
$cloneDir = Join-Path $root 'client-clone'
[System.IO.Directory]::CreateDirectory($cloneDir) | Out-Null
& $setExe --init 'LabTest2026' --import $presetPath --data-dir $cloneDir | Out-Null
& $setExe --pair $pairing --data-dir $cloneDir | Out-Null
Copy-Item (Join-Path $clientDirs[0] 'device-id.txt') (Join-Path $cloneDir 'device-id.txt') -Force
$cloneProc = Start-Process -FilePath $svcExe -WindowStyle Hidden -PassThru `
    -ArgumentList @('--console', '--seconds', '60', '--data-dir', $cloneDir, '--machine-name', 'LAB-CLONE-PC') `
    -RedirectStandardOutput (Join-Path $cloneDir 'client-out.log') -RedirectStandardError (Join-Path $cloneDir 'client-err.log')
Start-Sleep -Seconds 20

Say '=== 收尾：让教师端自然退出并打印设备清单（含状态/所属组）——**先别停学生端**，否则清单是空的 ==='
$srvLeft = 110 - [int]((Get-Date) - $srvProc2.StartTime).TotalSeconds
if ($srvLeft -gt 0) { Start-Sleep -Seconds ($srvLeft + 3) }
foreach ($p in ($procs + $cloneProc)) { try { if ($p -and -not $p.HasExited) { $p | Stop-Process -Force } } catch { } }
$listing = ''
if (Test-Path -LiteralPath (Join-Path $root 'server2-out.log')) {
    $listing = Get-Content -LiteralPath (Join-Path $root 'server2-out.log') -Raw -Encoding UTF8
}
Say '  --- 教师端设备清单 ---'
foreach ($line in (($listing -split "`n") | Where-Object { $_ -match 'dev=' })) { Say ('    ' + $line.TrimEnd()) }
# 现在可以恢复可写了（S7 那台机器的"应用失败"标记已经写进上面的清单）
if (Test-Path -LiteralPath $cfgPath) { (Get-Item -LiteralPath $cfgPath -Force).IsReadOnly = $false }
Check 'S9 教师端设备清单出现「疑似克隆（重复ID）」' ($listing -match '疑似克隆') `
    ((($listing -split "`n") | Where-Object { $_ -match 'dev=' } | Select-Object -First 3) -join ' | ')
Check 'S9 设备清单含状态与所属组字段' (($listing -match 'state=') -and ($listing -match 'group='))
Check 'S9 设备清单出现「应用失败」标注（S7 那台机器）' ($listing -match '应用失败') `
    ((($listing -split "`n") | Where-Object { $_ -match 'status=error' } | Select-Object -First 2) -join ' | ')
Check 'S9 设备清单出现按组归属（机房A / 机房B / 机房C 至少一个）' `
    (($listing -match 'group=机房A') -or ($listing -match 'group=机房B') -or ($listing -match 'group=机房C'))

# ---------------------------------------------------------------- ④ 收尾断言：真机没被污染
Say '=== ④ 收尾：真机状态复核（分组/暂停这些新动作也不许碰真机）==='
$hklmAfter = Test-Path 'HKLM:\SOFTWARE\LabGuard'
$usbAfter = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\usbstor' -Name Start -ErrorAction SilentlyContinue).Start
Check 'S10 HKLM\SOFTWARE\LabGuard 的存在性与套件开始前一致' ($hklmBefore -eq $hklmAfter) `
    ('before=' + $hklmBefore + ' after=' + $hklmAfter)
Check 'S10 usbstor Start 未被改动' ($usbAfter -eq $usbBefore) ('before=' + $usbBefore + ' after=' + $usbAfter)

# ---------------------------------------------------------------- 报告
$report = [pscustomobject]@{ at = (Get-Date).ToString('o'); clients = $Clients; port = $Port; root = $root
    pass = $pass; fail = $fail; scenarios = $scenarios; deviceIds = $deviceIds
    groups = (Get-Content -LiteralPath $groupsJson -Raw -Encoding UTF8 | ConvertFrom-Json) }
$reportPath = Join-Path $root 'testbed-report.json'
[System.IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
Say ''
Say ('测试台结果：' + $pass + ' 通过 / ' + $fail + ' 失败    报告：' + $reportPath)

Get-Process LabGuardServer, LabGuard.Service, LabGuard.Agent -ErrorAction SilentlyContinue |
    ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch { } }
if (-not $KeepRoot -and $fail -eq 0) { try { [System.IO.Directory]::Delete($root, $true) } catch { } }
else { Say ('（目录已保留：' + $root + '）') }
exit $(if ($fail -eq 0) { 0 } else { 1 })
