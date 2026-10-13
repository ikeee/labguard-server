#Requires -Version 5.1
<#
  沙箱隔离验真（**安全闸**，执行计划 LG-007 的前置检查）

  为什么单独有这一个脚本（2026-10-13 真机事故）：
    测试台要在**一台机器**上跑 N 个"学生机"。做法是给客户端加 `--data-dir <目录>` 沙箱模式。
    首跑时那个开关并没有真正生效，沙箱客户端回落到真机注册表镜像里的旧配置，于是
    按真策略改了**真机**：U 盘被禁（usbstor Start=4）、Chrome/Edge/Firefox 下载策略被写、
    启动菜单被改、HKLM\SOFTWARE\LabGuard 被建出来还带 Deny ACE（管理员都删不掉）。

  所以：**任何**改动客户端之后，先跑这个脚本（20 秒级），确认"沙箱不碰真机"，
  再去跑完整场景（testbed-clients.ps1）。

  它做什么：
    ① 记录真机关键状态（LabGuard 注册表、浏览器策略、usbstor、启动菜单、真实安装目录、hosts）；
    ② 用 `--data-dir` 起一台真实客户端（配对 + 初始化 + 服务跑 N 秒，连真教师端）；
    ③ 断言沙箱自己产出了 device-id.txt / server-state.json（含 address、linkState）；
    ④ 重新记录真机状态，**逐字节比对**：不一致 = 隔离失败 = 退出码 1。

  用法（在 labguard-server 仓库根目录）：
      powershell -File scripts\verify-sandbox.ps1
      powershell -File scripts\verify-sandbox.ps1 -Seconds 30 -KeepRoot
  退出码：0 = 隔离通过；1 = 有断言失败。
#>
[CmdletBinding()]
param(
    [string]$ClientRepo,
    [int]$Port = 8241,
    [int]$Seconds = 20,
    [switch]$SkipBuild,
    [switch]$KeepRoot
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $ClientRepo) { $ClientRepo = Join-Path (Split-Path -Parent $repo) '机房助手复刻' }
$root = Join-Path $env:TEMP ('lg-sandboxcheck-' + (Get-Date -Format 'MMdd-HHmmss'))
$pass = 0; $fail = 0

function Say([string]$t) { Write-Host $t }
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + "  " + $detail) -ForegroundColor Red }
}

function Get-RegDump([string]$path) {
    $out = New-Object System.Collections.ArrayList
    if (-not (Test-Path $path)) { [void]$out.Add("(missing)"); return $out }
    try {
        $keys = @(Get-ChildItem -Path $path -Recurse -ErrorAction SilentlyContinue) + @(Get-Item -Path $path -ErrorAction SilentlyContinue)
        foreach ($k in ($keys | Where-Object { $_ })) {
            $props = Get-ItemProperty -Path $k.PSPath -ErrorAction SilentlyContinue
            if ($props) {
                foreach ($v in $props.PSObject.Properties) {
                    if ($v.Name -like 'PS*') { continue }
                    [void]$out.Add(($k.PSPath -replace '^Microsoft\.PowerShell\.Core\\Registry::', '') + '|' + $v.Name + '=' + $v.Value)
                }
            }
        }
    } catch { [void]$out.Add("(error:" + $_.Exception.Message + ")") }
    return ($out | Sort-Object)
}

function Get-DirDump([string]$path, [string]$skipName) {
    $out = New-Object System.Collections.ArrayList
    if (-not (Test-Path -LiteralPath $path)) { [void]$out.Add("(missing)"); return $out }
    try {
        foreach ($f in (Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue)) {
            if ($skipName -and $f.FullName -like ('*\' + $skipName + '\*')) { continue }
            [void]$out.Add(($f.FullName.Substring($path.Length) + '|' + $f.Length))
        }
    } catch { [void]$out.Add("(error:" + $_.Exception.Message + ")") }
    return ($out | Sort-Object)
}

function Get-BcdValue() {
    try {
        $t = (& bcdedit /enum '{bootmgr}' 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0) { return '(unavailable)' }
        return (($t -split "`r?`n" | Where-Object { $_ -match 'displaybootmenu' }) -join ';')
    } catch { return '(error)' }
}

# ---------------------------------------------------------------- 真机状态快照
function Get-MachineState {
    $lines = New-Object System.Collections.ArrayList
    foreach ($p in @(
        'HKLM:\SOFTWARE\LabGuard',
        'HKCU:\SOFTWARE\LabGuard',
        'HKLM:\SOFTWARE\Policies\Google',
        'HKLM:\SOFTWARE\Policies\Microsoft\Edge',
        'HKLM:\SOFTWARE\Policies\Mozilla',
        'HKLM:\SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices',
        'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System',
        'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LabGuard'
    )) {
        foreach ($l in (Get-RegDump $p)) { [void]$lines.Add('REG ' + $p + ' ' + $l) }
    }
    foreach ($svc in @('usbstor', 'LabGuardSvc')) {
        $v = (Get-ItemProperty ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $svc) -Name Start -ErrorAction SilentlyContinue).Start
        [void]$lines.Add('SVC ' + $svc + ' Start=' + $v)
    }
    [void]$lines.Add('BCD displaybootmenu=' + (Get-BcdValue))
    [void]$lines.Add('HOSTS ' + (Get-FileHash -LiteralPath "$env:SystemRoot\System32\drivers\etc\hosts" -Algorithm SHA256).Hash)
    foreach ($l in (Get-DirDump (Join-Path $env:ProgramData 'LabGuard') 'logs')) { [void]$lines.Add('FS ' + $l) }
    return $lines
}

# ---------------------------------------------------------------- 构建
$srvExe = Join-Path $repo 'src\LabGuardServer\bin\Release\net48\LabGuardServer.exe'
$svcExe = Join-Path $ClientRepo 'src\LabGuard.Service\bin\Release\net48\LabGuard.Service.exe'
$setExe = Join-Path $ClientRepo 'src\LabGuard.Settings\bin\Release\net48\LabGuard.Settings.exe'
if (-not $SkipBuild) {
    Say '=== 构建两仓（Release）==='
    & dotnet build (Join-Path $repo 'LabGuardServer.sln') -c Release --nologo -v minimal | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { Say '服务端构建失败'; exit 1 }
    & dotnet build (Join-Path $ClientRepo 'LabGuard.sln') -c Release --nologo -v minimal | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { Say '客户端构建失败'; exit 1 }
}
foreach ($e in @($srvExe, $svcExe, $setExe)) {
    if (-not (Test-Path -LiteralPath $e)) { Say ('缺少可执行文件：' + $e); exit 1 }
}

# 服务端要的开关目录快照（assets\catalog.json）
$srvAssets = Join-Path $repo 'src\LabGuardServer\bin\Release\net48\assets'
[System.IO.Directory]::CreateDirectory($srvAssets) | Out-Null
Copy-Item (Join-Path $repo 'assets\catalog.json') (Join-Path $srvAssets 'catalog.json') -Force

[System.IO.Directory]::CreateDirectory($root) | Out-Null
$srvDir = Join-Path $root 'server'
$sandbox = Join-Path $root 'student-sandbox'
[System.IO.Directory]::CreateDirectory($srvDir) | Out-Null
[System.IO.Directory]::CreateDirectory($sandbox) | Out-Null

# 无害策略：所有管控关闭（本检查只验"隔离 + 链路"，Guard 行为有自己的 245 条自检）
$harmless = '{"Version":1,"PasswordHash":"","Enabled":false,"InputHardLock":"Off","ConfirmDelaySeconds":0,' +
            '"StartDelaySeconds":120,"ResumeAfterMinutes":0,"Classroom":{"Enabled":false},' +
            '"Network":{"Enabled":false,"DnsServers":[],"WatchedInterfaces":[],"ExcludedInterfaces":[]},' +
            '"ProcessBlock":{"Enabled":false},"FileCreation":{"Enabled":false,"WatchPaths":[]},' +
            '"Usb":{"Enabled":false,"AllowStorage":true},"Hosts":{"Enabled":false,"Guard":false,"ProtectFileAcl":false},' +
            '"Browser":{"Enabled":false},"Shell":{"Enabled":false},"SafeMode":{"Enabled":false},' +
            '"Site":{"ShowMachineNumber":true},"RegistryAcl":{"Enabled":false},"Watchdog":{"Enabled":false},' +
            '"AntiTamper":{"Enabled":false,"KeepAliveWhenPaused":true},' +
            '"Server":{"Enabled":true,"Address":"","PollSeconds":2,"CheckinSeconds":10,"FailuresBeforeFallback":3}}'
$hp = Join-Path $root 'harmless.json'
[System.IO.File]::WriteAllText($hp, $harmless, (New-Object System.Text.UTF8Encoding($false)))

Say ('测试目录：' + $root)
Say '=== ① 快照真机状态 ==='
$before = Get-MachineState
Say ('  记录 ' + $before.Count + ' 条')

$srvProc = $null
try {
    Say '=== ② 起教师端（headless，临时数据目录）==='
    $pairing = Join-Path $root 'pairing.json'
    $srvProc = Start-Process -FilePath $srvExe -WindowStyle Hidden -PassThru `
        -ArgumentList @('--headless', '--data-dir', $srvDir, '--port', "$Port", '--set-policy', $hp,
                        '--export-pairing', $pairing, '--pairing-address', '127.0.0.1', '--seconds', "$($Seconds + 40)") `
        -RedirectStandardOutput (Join-Path $root 'server.out') -RedirectStandardError (Join-Path $root 'server.err')
    $ok = $false
    for ($i = 0; $i -lt 40 -and -not $ok; $i++) { Start-Sleep -Milliseconds 500; $ok = Test-Path -LiteralPath $pairing }
    Check 'S 教师端就绪（已签发策略 + 导出配对文件）' $ok

    Say '=== ③ 学生端沙箱：初始化 + 配对 + 跑服务 ==='
    # --init 必须先于 --pair：客户端"没设过密码 = 未安装完成 → 服务不启用管控"（这是有意的产品行为）
    & $setExe --init 'LabTest2026' --data-dir $sandbox | Out-Null
    Check 'S 沙箱初始化（--init 写 config.json）' (Test-Path -LiteralPath (Join-Path $sandbox 'config.json'))
    & $setExe --pair $pairing --data-dir $sandbox | Out-Null
    Check 'S 沙箱已配对（pin 教师机公钥 + 打开服务器管控）' (Test-Path -LiteralPath (Join-Path $sandbox 'server-pairing.json'))

    $svcProc = Start-Process -FilePath $svcExe -WindowStyle Hidden -PassThru `
        -ArgumentList @('--console', '--seconds', "$Seconds", '--data-dir', $sandbox, '--machine-name', 'LAB-SANDBOX-1') `
        -RedirectStandardOutput (Join-Path $sandbox 'client.out') -RedirectStandardError (Join-Path $sandbox 'client.err')
    $svcProc.WaitForExit(($Seconds + 60) * 1000) | Out-Null
    if (-not $svcProc.HasExited) { $svcProc | Stop-Process -Force }

    $devId = ''
    $devIdPath = Join-Path $sandbox 'device-id.txt'
    if (Test-Path -LiteralPath $devIdPath) { $devId = (Get-Content -LiteralPath $devIdPath -Raw -Encoding UTF8).Trim() }
    Check ('S 沙箱有自己的设备身份（' + $devId + '）') ($devId -like 'LG-*')

    $state = $null
    $statePath = Join-Path $sandbox 'server-state.json'
    if (Test-Path -LiteralPath $statePath) {
        try { $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
    }
    Check 'S 沙箱状态文件里有教师机地址与链路状态（address / linkState）' `
        ($null -ne $state -and -not [string]::IsNullOrEmpty($state.address) -and -not [string]::IsNullOrEmpty($state.linkState)) `
        $(if ($state) { 'address=' + $state.address + ' linkState=' + $state.linkState } else { '没有 server-state.json' })
    Check 'S 沙箱已应用过教师端策略（appliedSeq ≥ 1）' ($null -ne $state -and $state.appliedSeq -ge 1) `
        $(if ($state) { 'appliedSeq=' + $state.appliedSeq } else { '' })
} finally {
    Say '=== ④ 收尾：停所有进程 ==='
    Get-Process LabGuardServer, LabGuard.Service, LabGuard.Agent -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    if ($srvProc -and -not $srvProc.HasExited) { $srvProc | Stop-Process -Force -ErrorAction SilentlyContinue }
}

Say '=== ⑤ 再快照真机状态并逐条比对（这是本脚本存在的主要理由）==='
$after = Get-MachineState
$diff = Compare-Object -ReferenceObject $before -DifferenceObject $after
Check '隔离：真机状态**完全没变**（注册表/浏览器策略/usbstor/启动菜单/安装目录/hosts）' ($null -eq $diff) `
    ('差异 ' + (@($diff).Count) + ' 条')
if ($diff) {
    Say '  --- 真机被改动的条目（前 20 条）---'
    $diff | Select-Object -First 20 | ForEach-Object { Say ('    ' + $_.SideIndicator + ' ' + $_.InputObject) }
}

Say ''
Say ('隔离验真结果：' + $pass + ' 通过 / ' + $fail + ' 失败')
if ($KeepRoot -or $fail -gt 0) { Say ('（目录已保留：' + $root + '）') }
else { try { [System.IO.Directory]::Delete($root, $true) } catch { } }
exit $(if ($fail -eq 0) { 0 } else { 1 })
