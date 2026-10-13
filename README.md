# LabGuard Server

**LabGuard 教师机服务端** —— 在教师机上统一编辑机房管控策略、一键下发给全部学生机，学生机收到即生效。

**状态：✅ v0.10.0（教师机服务端 + 学生机客户端双仓，已可部署）**

- 配套客户端（学生机端）：**[ikeee/labguard](https://github.com/ikeee/labguard)**（v0.07 为单机版最终版，v0.10 起支持本服务端）
- **部署指南：[docs/deployment-guide.md](docs/deployment-guide.md)**（教师机 3 分钟 / 学生机 1 分钟，含无人值守与排障）
- 许可：MIT（见 [LICENSE](LICENSE)）· 更新记录：[CHANGELOG.md](CHANGELOG.md) · 通信协议：[docs/protocol-v1.md](docs/protocol-v1.md)
- 开发文档：[开发基线](docs/development-baseline.md) · [协议实现对照审计](docs/protocol-conformance.md) · [接手开发执行计划](docs/development-plan.md)
- 端到端测试台（**一台机器跑 1 个真教师端 + N 个真学生端沙箱**）：[docs/e2e-testbed.md](docs/e2e-testbed.md)
- **课堂助教手册**（一节 40 分钟的课，老师只需动 5 次手）：[docs/classroom-playbook.md](docs/classroom-playbook.md)

## 是什么

LabGuard 是面向中小学计算机机房的**学生机管控程序**（客户端开源仓库见上）。本仓库是它的**教师机服务端**：

- 把学生机上的设置面板搬到教师机：老师在一处改策略，全部学生机 **≤10 秒**内生效（默认 5 秒轮询）
- 每次下发的策略带 **RSA 签名 + 单调序列号**：学生伪造服务器、篡改策略、重放旧策略在数学上被封死
- 服务器不在网时，学生机自动回退到最后一份生效策略继续单机管控（机房不失控）
- **不配对、不开服务器管控 = 纯单机版**：服务端是增强，不是前置条件

## 技术形态

- Windows / .NET Framework 4.8 / C#（与客户端同栈，教师机**零运行时依赖**，系统自带即跑）
- `HttpListener` 自托管 HTTP（默认 `http://+:8210/`），机房局域网内短轮询通信
- WinForms 托盘 + 设置面板；策略面板控件由客户端导出的 `catalog.json` **动态渲染**（与客户端 15 组 114 项配置永远同步，不手写不漂移）

## 里程碑

| 版本 | 内容 |
|---|---|
| v0.10 | 服务端最小可用：策略编辑 + 签名下发 + 在线学生机列表；客户端（labguard v0.10）接收即生效 |
| v0.11+ | 候选：一键全员暂停/恢复、上课/下课场景、审计报表导出 |

## 构建与自检（开发）

新签出的仓库，一条命令完成 Release 构建 + 自检（不需要装 Visual Studio）：

```bat
build.cmd
```

等价于 `powershell -File build.ps1`（可加 `-Clean` / `-NoSelfTest` / `-Package`）。
产物落在 `dist\labguard-server-v<版本>\`（`bin/`、`obj/`、`dist/` 均不进 Git）。
退出码：`0` 成功 / `1` 构建失败 / `2` 自检失败 / `3` 打包未实现 / `4` 前置条件不满足。

前置：.NET SDK 8（`net48` 目标由 `Microsoft.NETFramework.ReferenceAssemblies` 提供，
无需 VS Build Tools）；CI 见 `.github/workflows/ci.yml`。

### 无界面模式（自动化验收 / 无人值守）

```bat
LabGuardServer.exe --headless --data-dir D:\lg-test --port 8210 ^
    --password <管理口令> --policy-default ^
    --export-pairing D:\lg-test\pairing.json --seconds 60
```

`--policy-default`（用开关目录默认策略）或 `--set-policy <策略.json>` 保存一次策略；
`--export-pairing <文件>` 导出配对文件（给了它且没给 `--headless` 就只导出后退出）；
`--seconds N` 跑够 N 秒自动退出（测试用），省略则一直跑到 Ctrl+C。
省略 `--bind-all` 时只监听 127.0.0.1（不需要 URLACL）。

分组（v0.11，同样是"只改磁盘、运行中的服务端热拾取"）：

```bat
LabGuardServer.exe --data-dir D:\lg-test --add-group 机房A --assign 机房A=LG-xxxx ^
    --group-policy 机房A=D:\lg-test\group-a.json
LabGuardServer.exe --data-dir D:\lg-test --pause-default
LabGuardServer.exe --data-dir D:\lg-test --resume-default
```

`--assign 组名=<deviceId 或以 LG- 开头则按 ID、否则按机器名>`；`--group-policy 组名=<策略.json>`
立刻签发该组信封并把全局 seq 推进一步。跑完（或 Ctrl+C 退出）会打印**设备清单**：
`机器名 dev=设备ID v版本 seq=已下发 applied=已应用 status=ok|error state=状态 group=所属组`。

## 端到端测试台（改客户端/服务端之后必跑）

```powershell
powershell -File scripts\verify-sandbox.ps1       # 安全闸：证明沙箱不碰真机（约 25 秒）
powershell -File scripts\testbed-clients.ps1      # 21 项场景：真教师端 + 3 个真学生端沙箱（约 4 分钟）
```

细节与场景矩阵见 [docs/e2e-testbed.md](docs/e2e-testbed.md)。**先跑安全闸**——沙箱没有真正隔离时，
场景里的"学生端"会按真策略改掉开发机（2026-10-13 就是这么踩的）。

### 两间机房在同一网段（互不干扰的验收）

```powershell
powershell -File scripts\two-room-isolation.ps1     # 15 项断言：两个教师端 + 五个沙箱
```

结论：**默认就隔离**（学生端只连配对文件里写死的那一个教师机，且把它的公钥钉在本机 → 别的教师机发策略一律验签失败）。
部署要点与 3 个真会串房的坑（配对文件串用 / 教师机镜像克隆 / 同网段 IP 冲突）见
[docs/deployment-guide.md §8](docs/deployment-guide.md)。教师端侧还可以：

```bat
LabGuardServer.exe --data-dir %ProgramData%\LabGuardServer --room-set A202 --expect-ips 10.46.254.11-99
LabGuardServer.exe --data-dir %ProgramData%\LabGuardServer --room-info
LabGuardServer.exe --data-dir %ProgramData%\LabGuardServer --audit-tail 50 --audit-verify
LabGuardServer.exe --data-dir %ProgramData%\LabGuardServer --export-devices D:\A202-设备.csv
```

不在期望 IP 段内的设备会在「设备清单」里标 `⚠跨机房`（装配错当场可见）。

### 临时课堂状态（v0.12：上课/严管/考试一键切，到点自动回基础策略）

```bat
LabGuardServer.exe --data-dir %ProgramData%\LabGuardServer --temp-status
LabGuardServer.exe --data-dir %ProgramData%\LabGuardServer --temp-preset 课堂严管
LabGuardServer.exe --data-dir %ProgramData%\LabGuardServer --temp-start D:\严管.json --temp-name 课堂严管 --temp-minutes 45
LabGuardServer.exe --data-dir %ProgramData%\LabGuardServer --temp-end
```

预设表 `temp-presets.json` 在教师机数据目录里（**老师可直接编辑**）；`--temp-minutes 0` = 不自动到期。
有效策略口径 `= (分组策略 或 默认策略) ⊕ 临时覆盖`，解析优先级 **临时 > 分组 > 默认**；
到期/结束后服务端把基础策略**重签一个新 seq** 下发（否则学生机按防回滚拒收、永久卡住）。
老师怎么用、每个"学生招数"对应哪个开关、以及做不到的清单，见 [docs/classroom-playbook.md](docs/classroom-playbook.md)。

协议端到端冒烟（会自己起服务端、用**独立实现**核对 15 项契约、检查完自动收尾）：

```bat
powershell -File scripts\e2e-headless.ps1 -Port 8211 -Seconds 20
```

## 部署

在**教师机**运行 `LabGuardServer-Setup-v0.10.0.exe`（一键完成：装文件 → 配 URLACL → 加防火墙规则 → 登录自启 → 启动并自检），
然后在策略面板设置口令、保存并下发、导出「配对文件」；学生机用客户端安装包安装后导入配对文件即可。
**完整步骤、无人值守命令与排障表见 [docs/deployment-guide.md](docs/deployment-guide.md)。**
