# 课堂助教手册：一节 40 分钟的课，老师只需要动 5 次手

> 面向：一线老师（计算机机房 / 信息技术课）
> 依据：`scripts\testbed-clients.ps1` 里的「课堂演练」段（真教师端 + 3 台真学生端沙箱，可随时复跑）
> 口径：**下面每个数字都是实测的**；凡是做不到的，第 5 节逐条写明原因，不粉饰。

## 0. 一句话结论

老师原来的做法是"**发现问题 → 起身跑一趟 → 每台机点一遍**"，一节课被打断七八次；
换成"**托盘菜单里点一下 → 全班 2 秒内换状态**"之后，同样 5 个课堂事件只需 **5 次点击**，
而且**下课不用逐项改回来**（到点自动回基础策略）。

## 1. 演练剧本与实测结果（2026-10-13 实跑）

| 时间 | 课堂事件（学生） | 老师动作（**各 1 次**） | 实测全班生效 |
|---|---|---|---|
| 00:00 | 上课铃 | 点「上课模式」 | **1.9 s** |
| 00:08 | 学生开浏览器打在线小游戏；有人插 U 盘想拷游戏进来 | 点「课堂严管」 | **1.9 s** |
| 00:15 | 学生按 Win+Tab 开虚拟桌面，把游戏藏到第二个桌面 | 点「课堂严管 + 桌面防逃」 | **2.2 s** |
| 00:22 | 同一个学生继续改注册表 / 开任务管理器 / cmd 删文件（就这 1 台） | 点「划进整改组」——**只发那 1 台**，其余人继续上课 | **0.6 s** |
| 00:40 | 下课 | 点「结束临时状态」 | **1.9 s**（自动回基础策略） |

**老师动作账本：5 次点击 / 5 个事件；同样效果"逐个机位跑"= 至少 3 台 × 5 次 = 15 次到场操作。**
（机房 50 台的话，量级是 **250 次**。）

## 2. 老师实际怎么操作（三个入口，按熟练度选）

### 入口 A：托盘菜单（推荐，1~3 击）

右下角托盘图标 →「临时课堂状态」→ 点一个预设。菜单里第一行永远显示**当前有没有临时状态、还剩多久**。

预设清单在教师机数据目录的 `temp-presets.json`（**纯 JSON，老师可以自己改**）：

```json
{"schemaVersion":1,"presets":[
  {"name":"上课模式","description":"锁 U 盘、放开浏览器","durationMinutes":45,
   "patch":{"Usb":{"Enabled":true,"AllowStorage":false},"Browser":{"Enabled":false}}},
  {"name":"课堂严管","description":"有人打外网游戏","durationMinutes":45,
   "patch":{"ProcessBlock":{"Enabled":true,"BlockGames":true},"Network":{"EnforceDns":true}}},
  {"name":"全班暂停（讲评）","description":"先停下来听我讲","durationMinutes":10,"paused":true,"patch":{}}
]}
```

* `durationMinutes` = 到点**自动回基础策略**（写 0 = 不自动到期，需要手动结束）。
* `patch` = 只写"这节课要临时改的那几项"，**没写的项一律保持基础策略不动**（深合并规则，见 `docs/e2e-testbed.md` §4）。
* 改完文件，托盘菜单里点「重新读取预设」即时生效。

### 入口 B：策略面板（想自己临时改几项时）

策略面板里改完 → 点**「临时生效…」** → 选 30/45/60/90 分钟或自己填 → 到点自动回基础策略。
面板底栏在任何时候都会用 `⚑ 临时课堂状态【…】剩 N 分` 告诉你"现在有临时覆盖"。

### 入口 C：命令行（无人值守 / 批处理 / 交给课代表一键跑）

```bat
LabGuardServer.exe --data-dir %ProgramData%\LabGuardServer --temp-preset 课堂严管
LabGuardServer.exe --data-dir %ProgramData%\LabGuardServer --temp-start D:\严管.json --temp-name 课堂严管 --temp-minutes 45
LabGuardServer.exe --data-dir %ProgramData%\LabGuardServer --temp-status
LabGuardServer.exe --data-dir %ProgramData%\LabGuardServer --temp-end
```

## 3. 学生的每个"招"，对应哪个开关（诚实映射）

| 学生的做法 | 真正起作用的开关（学生机上执行） | 有效程度 |
|---|---|---|
| 上外网打游戏（网页小游戏 / 客户端游戏） | ① **集中 DNS**（`Network.EnforceDns` + `Network.DnsServers`，上网过滤主防线）② `ProcessBlock.BlockGames` + `ProcessBlock.ExtraKeywords`（游戏关键词表）③ `Browser.BlockChromeDino / BlockEdgeSurf`（浏览器自带小游戏）④ `Network.BlockDoh`（堵加密 DNS 绕过） | DNS 白名单最硬；进程名拦截永远滞后于游戏改名，只当补刀 |
| 插 U 盘拷游戏/带走东西 | `Usb.Enabled` + `Usb.AllowStorage=false`（`Usb.KeepHidDevices` 保住键鼠/机器人） | 硬（改注册表 usbstor 会被守卫改回） |
| 多开桌面把窗口藏起来 | ① `Shell.BlockWindowsKey`（屏蔽 Win 键，含 Win+Tab）② `Shell.HideTaskView`（隐藏任务视图那个按钮）③ `ProcessBlock.BlockVirtualDesktop`（第三方虚拟桌面软件按进程拦） | Win+Tab 这条**硬**；第三方软件按名字拦，改名可绕 |
| **Ctrl+Alt+Del → 切换用户**（换个账号接着玩） | **`Session.BlockFastUserSwitch`**（v0.13 新增，写机器级策略 `HideFastUserSwitching=1`，登录界面/开始菜单/任务管理器里的「切换用户」全没了） | **硬**（机器级，跟登录的是哪个账号无关） |
| 用**远程桌面**连本机开第二个会话 | `Session.DenyRemoteDesktop`（v0.13 新增，`fDenyTSConnections=1`） | 硬；**默认关闭**——你自己要远程连这台机排障就别开 |
| 关任务管理器/注册表/命令行来搞破坏 | `Shell.DisableTaskManager / DisableRegistryTools / DisableCmd`（组策略级）+ `ProcessBlock.BlockTaskManager / BlockRegedit / BlockCommandPrompt`（进程级）+ `RegistryAcl.Enabled`（注册表权限加固） | 硬（组策略比"杀进程"更彻底） |
| 杀服务 / 删程序目录 / 停小助手 | `AntiTamper.*`（进程加锁、秒级复活、兜底计划任务、拦杀进程工具、反复被杀告警）+ `Watchdog.*`（文件自愈、从 payload 恢复） | 硬（这是 v0.10 之前反复踩出来的防线） |
| 挂掉电子教室学生端（极域/云桌面）逃避课堂管控 | `Classroom.*`（保护电子教室关键进程/服务，被挂起强制恢复、被结束自动重拉） | 硬（`Classroom.GuardELearningParameters / GuardVoiParameters` 还能守住频道/服务器地址这类参数） |
| 重启进安全模式 | `SafeMode.*`（默认安全模式不加载管控 → 老师有后路；`BlockNetworkSafeMode` 可禁掉"带网络的安全模式"）+ `SafeMode.HideBootMenu` | 半硬：安全模式不加载是**故意的后路**，别建议关 |

## 4. 用"分组"避免一刀切

不同机房/班级的基础策略不该一样（考试机房要严、创客教室要放开串口/USB）。
**分组 + 课堂状态层是可以叠加的**，规则是：

```
这台机的有效策略 = 它所属分组的策略  ⊕  全班临时覆盖
```

所以"全班换课堂状态"和"某几台特别加严"能同时成立——演练里那台捣蛋机器最后同时拥有
*整改组*的严管项 **和** 全班临时覆盖，其余同学完全不受影响（两条断言都过了）。

> 顺序提示：**先**给那几台发组策略、**再**（或同时）开课堂状态；服务端会自动把课堂状态按新的分组基础重算。

## 5. 做不到的 & 为什么（别指望，也别乱加）

1. **"看见学生在干嘛"（屏幕监控/远程桌面/截屏录像）——永久不做。**
   这是项目的硬边界（写进开发计划的禁止范围）。老师的替代手段是
   **教师端设备视图**（谁没应用策略 / 谁上报了 error / 谁离线）+ 到机位去。看画面就请起身走两步。
2. **切换用户 / 新建会话 —— v0.13 起已堵（但只堵到这里为止）。**
   `Session.BlockFastUserSwitch` 写的是**机器级**策略，所以换谁登录都生效；
   但请看清剩下的两个口子：① 学生**仍然可以注销**（注销不能逃逸：本程序是机器级服务，
   注销后那个会话根本没人操作）；② 如果学生**知道别的账号的密码**，注销后用那个账号登录仍可行
   —— 这属于账号与制度问题（学生账号绝不该知道管理员密码）。
   而且即使他登进去：机器级策略（USB/浏览器/注册表/进程/远程桌面）一样生效，
   会话级的任务栏策略也会被程序重新拉起的本进程重新应用 —— 逃逸是**暂时的**，不是"脱管"。
   远程桌面那条路（RDP 第二会话）由 `Session.DenyRemoteDesktop` 封（默认关，见上表）。
3. **本地管理员**（学生用管理员账号登录、或用 PE 盘启动）。
   用户态程序挡不住本地管理员，这是 Windows 的物理事实。
   现有后路：安全模式不加载管控 + `cleanup-all.ps1` + 重装；学校层面把"学生账号一律非管理员"作为制度要求。
4. **进程名拦截不是万能的**：游戏/工具改名、加壳、白名单外的新软件，进程拦截会漏；
   **真正的主防线是把外网锁成"只允许白名单"的集中 DNS**，或者干脆上课时不通外网。
5. **拔网线/关交换机**：客户端有"断网全屏遮罩 + 违规锁定"，但物理层面的断网终究要老师现场处理；
   教师端设备视图会把这类机器标成**离线**，一眼能看出是哪几台。

## 6. 上课前 5 分钟的自检清单（3 条命令 / 30 秒）

```bat
:: ① 教师机：服务在跑吗、当前 seq 多少、有没有残留的临时状态
LabGuardServer.exe --data-dir %ProgramData%\LabGuardServer --temp-status

:: ② 学生机抽查 1 台（或让课代表在讲台机跑）：配对与链路是否正常
LabGuard.Settings.exe --data-dir %ProgramData%\LabGuard --server-status

:: ③ 教师机：打开「在线学生机」→ 看设备视图（未应用 / 应用失败 / 离线 各几台）
```

**"上课状态"要提前设好**：把「上课模式」的时长设成你的课时长（45 或 40 分钟），
上课铃响那一刻点一下，下课自动解除——不需要惦记着"别忘了改回来"。

## 7. 想自己验证这一切？跑演练

```powershell
powershell -File scripts\verify-sandbox.ps1     # 先过安全闸（证明沙箱不碰真机）
powershell -File scripts\testbed-clients.ps1    # 全套 28 项断言，含上面这节课的演练
```

演练打印的"老师动作账本"就是本文第 1 节那张表；任何一次改动后重跑，数字会重新报一遍。
