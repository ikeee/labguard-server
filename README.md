# LabGuard Server

**LabGuard 教师机服务端** —— 在教师机上统一编辑机房管控策略、一键下发给全部学生机，学生机收到即生效。

**状态：🚧 建设中（v0.10 开发阶段，尚未发布可用版本）**

- 配套客户端（学生机端）：**[ikeee/labguard](https://github.com/ikeee/labguard)**（v0.07 为单机版最终版，v0.10 起支持本服务端）
- 许可：MIT（见 [LICENSE](LICENSE)）· 更新记录：[CHANGELOG.md](CHANGELOG.md) · 通信协议：[docs/protocol-v1.md](docs/protocol-v1.md)

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

协议端到端冒烟（会自己起服务端、用**独立实现**核对 15 项契约、检查完自动收尾）：

```bat
powershell -File scripts\e2e-headless.ps1 -Port 8211 -Seconds 20
```

## 部署（v0.10 发布后）

在**教师机**（非学生机）运行 `LabGuardServer-Setup-v0.10.exe`，安装向导会：生成签名密钥对 → 导出「配对文件」→ U 盘/共享拷到学生机执行客户端安装脚本的 `-Pair` 参数完成配对。详见届时发布的部署指南。
