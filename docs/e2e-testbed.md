# 端到端测试台：一台机器上的"1 教师端 + N 学生端"

> 适用：教师端仓库 `ikeee/labguard-server`（脚本在 `scripts\`），配套客户端仓库 `ikeee/labguard`（中文目录 `机房助手复刻`）。
> 相关：执行计划 LG-007（端到端验收）/ LG-011（压力测试）、`docs/protocol-v1.md`、`docs/deployment-guide.md`。

## 1. 要解决什么

在真正铺到机房之前，我们要能在**一台开发机**上把整条链跑通并且可重复：

* 教师端：保存策略 → 签发 `LGSRV1` 信封 → 按设备身份/分组解析 → 下发 → 收心跳 → 汇总设备状态；
* 学生端：配对 → 轮询 → 验签 → 应用 → 落缓存 → 上报 `appliedSeq`/`status` → 断网回退 → 恢复；
* 分组：加组、调组、按机器名兜底入组，且**不影响其它设备**；
* 全过程**不许碰这台真机**（它是老师的开发机，不是测试机）。

## 2. 设计：为什么是"真进程 + 沙箱"，不是假客户端

| 做法 | 优点 | 结论 |
|---|---|---|
| 写一套假的 HTTP 客户端打教师端 API | 跑得快 | ✗ 只能验服务端，验不到客户端的验签/缓存/回退/应用 |
| 用虚拟机/容器起多台学生机 | 最真实 | ✗ 太重，日常改一行代码要等几分钟 |
| **真客户端进程 + `--data-dir` 沙箱 + `--machine-name` 冒充** | 真代码、真协议、秒级启动、单机可并行 | ✓ 采用 |

三个关键机关：

1. **`--data-dir <目录>`（便携/沙箱模式）**：把配置目录整体改到临时目录。
   配置、缓存、`device-id.txt`、状态文件、暂停旗标、日志全在沙箱里；
   **不读不写真机注册表**（含 `CfgData` 镜像与签名密钥 `CfgKey`，后者改落沙箱的 `cfgkey.bin`）；
   **强制干跑且改不掉**（`Log.ForceDryRunOn()`，因为 `GuardEngine.Start` 会把 `Log.DryRun` 掀掉）；
   服务侧跳过自身 DACL 加固、跳过小助手守护（`schtasks`）、跳过秒级复活线程。
   只有命令行能触发——服务（SCM）与兜底任务（计划任务）的命令行里都不含这个参数，学生无法用它逃逸管控。
2. **`--machine-name <名字>`**：一台物理机冒充多台机器名；查询串与心跳体两处都吃这个覆盖值。
3. **每台沙箱一个数据目录** → 各自独立的 `device-id.txt` → 天然多设备身份，正好用来验分组与克隆检测。

## 3. 两个脚本

### 3.1 `scripts\verify-sandbox.ps1` —— 隔离安全闸（先跑这个）

```
powershell -File scripts\verify-sandbox.ps1              # 约 25 秒
powershell -File scripts\verify-sandbox.ps1 -KeepRoot    # 保留临时目录供排查
```

它做的事：记录真机关键状态 → 起一台 headless 教师端 → 起一台沙箱学生端（配对 + `--init` + 服务跑 N 秒）
→ 断言沙箱自己产出了 `device-id.txt` 与 `server-state.json`（含 `address` / `linkState` / `appliedSeq ≥ 1`）
→ **重新记录真机状态并逐条比对**。

被比对的真机面（任何一条变了就 FAIL 并打印差异）：

| 类别 | 具体项 |
|---|---|
| LabGuard 注册表 | `HKLM\SOFTWARE\LabGuard`、`HKCU\SOFTWARE\LabGuard`、卸载项（含全部子键/值） |
| 浏览器策略 | `HKLM\SOFTWARE\Policies\{Google, Microsoft\Edge, Mozilla}`、`RemovableStorageDevices` |
| **会话策略（v0.13）** | `…\CurrentVersion\Policies\System`（禁快速切换用户）、`Policies\Microsoft\Windows NT\Terminal Services` 与 `SYSTEM\…\Control\Terminal Server`（禁远程桌面） |
| 服务 | `usbstor Start`、`LabGuardSvc Start` |
| 启动 | `bcdedit {bootmgr}` 的 `displaybootmenu` |
| 文件 | `%ProgramData%\LabGuard`（除 `logs`）的文件清单+大小、`hosts` 哈希 |

> 加开关就得同步加"被比对的真机面"：v0.13 新增的两个开关写的是**新的注册表位置**，
> 如果安全闸不跟着扩，一旦隔离破了它照样报"完全没变"——这正是 2026-10-13 事故的教训。

> **纪律：任何改动客户端之后先跑它。** 2026-10-13 的事故就是跳过这一步直接跑场景导致的。

### 3.2 `scripts\testbed-clients.ps1` —— 28 项断言的场景套件（含"课堂演练"）

```
powershell -File scripts\testbed-clients.ps1                      # 3 台学生端，端口 8230
powershell -File scripts\testbed-clients.ps1 -Clients 5 -KeepRoot # 5 台，保留目录
powershell -File scripts\testbed-clients.ps1 -SkipIsolationGate   # 已经跑过安全闸时省 25 秒
```

默认先自动跑一遍隔离验真（不通过就直接停，不往下跑场景）。退出码 0 = 全通；报告落
`%TEMP%\lg-testbed-*\testbed-report.json`（含每项断言的 pass/detail）。

## 4. 场景矩阵

| # | 场景 | 断言口径（读什么） | 覆盖的协议/机制 |
|---|---|---|---|
| S0 | 教师端就绪 | 配对文件已导出 | 密钥 + 策略签发 + `--export-pairing` |
| S1 | 多设备身份 | 每台沙箱 `device-id.txt` 互不相同且为 `LG-*` | 稳定设备身份 |
| S2 | 默认策略下发 | `server-state.json` 中 `appliedSeq ≥ 1` 且 `linkState=Polling`；`server-policy.cache` 为 `LGSRV1|…` | 轮询 / 验签 / 应用 / 缓存 |
| S3 | 分组下发只影响本组 | 组内机器缓存出现组策略；**其它机器仍是默认策略** | 分组覆盖层 + 身份解析 |
| S4 | 调组 | 新组策略覆盖旧组；`groups.json` 里两组 seq 单调递增 | 全局 seq 单调（否则调组被客户端当回滚拒绝） |
| S5 | 按机器名入组 | 只报机器名的设备也能进组 | 旧客户端兜底路径 |
| S6 | 全员暂停 / 恢复 | 缓存里信封 `paused` 位 1 → 0（`^LGSRV1\|seq\|issuedAt\|1\|`） | 暂停优先级 + **发布过分组后仍能改默认策略**（水位分离回归点） |
| S7 | 应用失败上报 | 沙箱 `lastError` 非空、`appliedSeq` 不前进；教师端清单该机 `status=error` | 应用失败 → errorCode → 教师端可见 |
| S8 | 离线回退 / 自动恢复 | 停教师端 → `linkState=FallbackSingle` 且策略不丢；教师端回来 → 自动 `Polling` | 回退与自愈，无需人工干预 |
| S9 | 克隆检测 | 复制 `device-id.txt` 的另一台机器上线上出现"疑似克隆（重复ID）"；清单含 `state=`/`group=` | 身份聚合 + 7 档设备状态 |
| S10 | 真机未被污染 | `HKLM\SOFTWARE\LabGuard` 存在性、`usbstor Start` 与套件开始前一致 | 隔离（分组/暂停这些新动作同样不许碰真机） |

### 4.1 课堂演练（模拟一节 40 分钟的课，实测数据见 [classroom-playbook.md](classroom-playbook.md)）

夹在 S6 与 S7 之间，模拟老师一节课遇到 5 个事件、每个事件只动 **1 次手**：

| # | 课堂事件 | 老师动作 | 断言 |
|---|---|---|---|
| 课堂① | 上课铃 | 1 键「上课模式」（临时状态） | 全班 3 台在时限内换到上课策略 |
| 课堂② | 打外网游戏 + 插 U 盘 | 1 键「课堂严管」 | 全班生效，且**信封里确实带上了** `BlockGames` / `BlockDoh` |
| 课堂③ | 多开桌面逃管制 | 1 键「课堂严管 + 桌面防逃」 | `BlockWindowsKey` + `HideTaskView` + `BlockVirtualDesktop` 三项到位 |
| 课堂④ | 个别人继续破坏电脑 | 1 键：划进「整改组」+ 只发那 1 台 | 那台 = **整改组策略 ⊕ 全班临时覆盖**（两条同时在）；**其余机器一个字段都没变** |
| 课堂⑤ | 下课 | 1 键「结束临时状态」 | 全班自动脱离课堂状态；整改组的严管项**仍然保留** |

最后打印"老师动作账本"（事件 / 动作 / 涉及台数 / 实测生效秒数），用来跟"逐个机位跑"做对照。

### 4.2 临时课堂状态层（v0.12）：合并规则与优先级

```
这台机的有效策略 = ( 它所属分组的策略  或  默认策略 )   ⊕   全班临时覆盖
```

* **合并规则只有三条**：对象与对象递归合并、其余（数组/标量）整键替换、补丁没提到的键一个都不动。
* **解析优先级**：临时状态 > 设备分组 > 默认策略（三层都在服务端解析，客户端零新字段、零改动）。
* **到期/结束**：服务端把基础策略**内容不变、换新 seq 重签**再发一遍——因为学生机的规则是
  "seq 必须严格大于已应用值"，只删临时状态会让它们正确地拒收旧 seq 而**永久卡在临时策略上**。
* **重启安全**：启动时发现已过期的临时状态一律丢弃并重发基础策略（"睡一觉还在考试模式"是禁止的）。
* **先验签再用**：`temp-state.json` 里任何信封验签不过 → 拒绝采用并删除该文件。

教师端退出时会打印设备清单，测试台直接断言这份清单，例如：

```
LAB-PC-1  dev=LG-d41a37640203b5cf  127.0.0.1  v0.12.0  seq=27  applied=27  status=ok     state=疑似克隆（重复ID）  group=机房B    effSeq=27
LAB-PC-2  dev=LG-6dc1fbc1ccfee123  127.0.0.1  v0.12.0  seq=25  applied=25  status=error  state=应用失败           group=默认策略  effSeq=26
LAB-PC-3  dev=LG-da943163cb442be9  127.0.0.1  v0.12.0  seq=29  applied=29  status=ok     state=已应用             group=整改组    effSeq=29
```

（`effSeq` = 该设备**实际生效那一层**信封的 seq，v0.12 起打印；上面第三行就是课堂演练里被划进整改组那台。）

## 5. 踩过的坑（别重犯）

1. **沙箱不是"加个参数"就安全**。必须把注册表镜像读写、签名密钥落点、服务 DACL、小助手守护全部堵掉；
   而且 `Log.DryRun` 会被 `GuardEngine.Start` 覆盖，普通赋值挡不住——要用"改不掉"的总闸。
   所以在做任何场景之前，先用 `verify-sandbox.ps1` **证明**隔离成立。
2. **分组命令属于 CLI，必须列进 `WantsCli`**。漏一个不是"参数被忽略"，而是整个进程掉进 GUI 分支
   （起托盘 + 占 8210）并永久阻塞，表现为测试脚本"卡住不动"。
3. **全局水位 ≠ 默认组水位**。它们的用途不同（前者保证不撞号、后者是默认信封的回滚下限），
   混用会导致"发布过分组策略之后默认策略加载被拒"。
4. **headless CLI 要把 `GroupStore` 接给 `ApiServer`**（`api.Groups = groups`），否则分组永不生效。
5. **`CheckinSeconds` 默认 60 秒**，且 `Server` 段属于本机连接配置（策略不会覆盖它）。
   要让教师端设备清单在观察窗内填满，得在 `--init` 时用 `--import` 灌一份 `CheckinSeconds=5` 的预设。
6. **先别急着停学生端**：教师端退出时才打印设备清单，停早了清单就是空的。
7. 沙箱要用 `--init <口令> --import <预设>` **再** `--pair`：客户端有意设计成"没设过密码 = 安装未完成 →
   服务不启用管控"，少了 `--init` 就永远连不上教师端。
8. **单机测试台要给 `--pairing-address 127.0.0.1`**：配对文件里默认写的是本机各网卡 IPv4（**不含回环**），
   学生端只会连 `addresses[0]`；而"只监听 127.0.0.1"时那个地址根本连不上（想连就得绑 `+`，绑 `+` 要提权/URLACL）。
   给了 `--pairing-address 127.0.0.1` 之后，测试台全流程**免提权**，CI 里也能跑。

## 6. 与其它验证手段的分工

| 工具 | 层级 | 管什么 |
|---|---|---|
| `LabGuard.SelfTest.exe`（客户端 245 条）/ `LabGuardServer.SelfTest.exe`（服务端 130 条） | 单元/契约 | 纯函数、协议字段、验签、水位、状态机 |
| `scripts\e2e-headless.ps1` | 协议端到端 | 只用 HTTP 打契约（不含客户端进程） |
| `scripts\stress-headless.ps1` | 压力 | 100/200 并发客户端的轮询/心跳 |
| **`scripts\verify-sandbox.ps1`** | **安全闸** | 沙箱是否真的不碰真机 |
| **`scripts\testbed-clients.ps1`** | **场景端到端** | 真教师端 + N 个真学生端沙箱的完整业务场景 |

## 7. 结果基线（2026-10-13）

* 隔离验真：**7 / 7 通过**（真机状态逐条一致）。
* 场景套件：**28 / 28 通过**（3 台学生端 + 1 台克隆 + 课堂演练，全套约 6 分钟）。
* 课堂演练实测：5 次老师动作 / 5 个事件，全班生效 0.6~2.2 秒（3 台沙箱，轮询间隔 2 秒）。
* 服务端自检 130 条、客户端自检 245 条全绿。
