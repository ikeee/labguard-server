# LabGuard Server 开发基线

> 产出任务：**LG-001 仓库基线审计**（《LabGuard Server 接手开发执行计划》§4 A1）。
> 审计时点：2026-10-12。服务端基线 `main` @ `3e2d6bf`，客户端基线 `feat/server-v0.10` @ `280336a`。
> 本文只描述**现状**（代码实测），不含设想；与计划的差距集中列在第 7 节。

## 1. 项目列表与职责

| 项目 | 类型 | 职责 | 依赖 |
|---|---|---|---|
| `src/LabGuardServer.Core` | net48 类库 | 协议与安全内核：信封、密钥、策略仓库、HTTP API、在线列表、目录契约、口令散列、JSON 工具、日志 | 仅 BCL |
| `src/LabGuardServer` | net48 WinExe | 教师机托盘与 UI：启动装配、策略面板（按 catalog 动态渲染）、在线学生机、配对文件导出、密码门禁 | Core |
| `src/LabGuardServer.SelfTest` | net48 Exe | 自检：**64 条断言 / 7 组**（信封、密钥库、目录契约、策略仓库、API 逻辑、回环端口冒烟、口令散列） | Core |

外部依赖：**只有** `Microsoft.NETFramework.ReferenceAssemblies 1.0.3`（`PrivateAssets=all`，仅编译期）
+ 系统程序集（System.Management / System.ServiceProcess / System.Security / System.Drawing /
System.Windows.Forms / System.Web.Extensions）。
→ 符合计划 §2.2「不引入需要单独安装的运行时」；教师机零运行时依赖。

## 2. 主要类与依赖关系

```
Program ──► ServerTrayContext ──► ApiServer ──► PolicyStore ──► RsaKeyStore
                 │                   │              │            └── PolicyEnvelope
                 │                   └── OnlineList │
                 ├── CatalogDoc ◄── assets/catalog.json（客户端 --export-catalog 产出）
                 ├── PolicyForm / OnlineForm / PasswordGate+PasswordDialog / PairingFile
                 └── Log
```

- **可单测的纯逻辑**：`ApiServer.GetVersion / GetPolicy / Checkin`（static，不依赖端口）、
  `PolicyEnvelope.Parse/Build/IsNewer`、`CatalogDoc.ValidateAgainstDefaultPolicy`、`PasswordHasher.*`。
  自检依赖这一分层，不需要真开端口即可断言（另有 [6] 组做回环冒烟）。
- **双仓契约面**（改动必须双仓同步 + 测试）：
  `PolicyEnvelope`（LGSRV1 信封，客户端同源实现）、`docs/protocol-v1.md`、
  `CatalogDoc`（`labguard-catalog/1`，由客户端 `LabGuard.Settings.exe --export-catalog` 生成）、
  `PasswordHasher`（PBKDF2-SHA256，与客户端同算法同格式）。
- 注册表：**服务端不使用注册表**（客户端才用 `HKLM\SOFTWARE\LabGuard`）。

## 3. 服务端生命周期（启动 → 关闭）

`Program.Main`（`src/LabGuardServer/Program.cs`）：

1. 崩溃兜底：`Application.ThreadException` + `AppDomain.UnhandledException` → 写日志 + 弹说明。
2. WinForms 初始化（**必须在第一个窗体之前**；客户端 v0.07 踩过的坑，这里从第一天放对位置）。
3. `--data-dir <目录>` 覆盖数据目录（默认 `%ProgramData%\LabGuardServer`，测试/便携用）。
4. 单实例 Mutex `LabGuardServer_SingleInstance`。
5. `Log.InitFile(DataDir)` → `Application.Run(new ServerTrayContext())`。

`ServerTrayContext` 装配顺序与失败语义：

| 步骤 | 失败时的行为 |
|---|---|
| `RsaKeyStore.LoadOrCreate` | 弹错误框 → **退出**（无签名能力的服务端不应运行） |
| `CatalogDoc.Load(程序目录\assets\catalog.json)` | 弹错误框（提示用客户端 `--export-catalog` 重出）→ **退出** |
| `PolicyStore.Load()` | 记日志 + 弹警告（"疑似被篡改，拒绝下发"）→ **继续运行，但策略为空** |
| `ApiServer.Start(8210)` | 弹错误框（端口占用/无权限）→ 继续运行（**无 HTTP 服务**） |
| 托盘构建 | 菜单：策略面板（需密码）/ 在线学生机 / 导出配对文件 / 退出 |

关闭：`ExitThreadCore` → `ApiServer.Stop()` → `RsaKeyStore.Dispose()` → 托盘隐藏。
**没有 Windows 服务形态**：当前是登录后常驻的托盘进程（见 R-2）。

## 4. 策略从编辑到客户端生效的数据流

```
教师机
  策略面板（密码门禁）→ PolicyForm 收集 → PolicyStore.Save(policy, paused, defaultJson)
      → seq = CurrentSeq + 1
      → PolicyEnvelope.Build(seq, utcNow, paused, policyJson, 私钥)   // RSA-2048-SHA256/PKCS#1
      → File.WriteAllText(policy.envelope)                          // 落盘内容 == 下发内容
      → CurrentEnvelope 更新（内存）

学生机（客户端仓库 feat/server-v0.10，M2a+M2b 已完成，M2c 接线未做）
  ServerClient 后台线程轮询（默认 5s，3s 超时，代际号作废在途结果）
      → GET /api/version {seq, sha256, paused}                      // seq 未前进就到此为止
      → GET /api/policy（信封原文）→ 用 pin 的本机公钥验签
      → seq 严格大于已应用值？否则拒绝（重放/回滚）
      → ServerPolicyApplier.Merge（保留本机专属字段：口令 / Server 配置 / 版本）
      → ConfigStore.Save → 引擎重启生效（M2c 接线）
      → ServerPolicyCache 落盘（重启后先吃缓存）
      → POST /api/checkin {machine, version, seq, paused}
```

服务端侧读路径：`GET /api/version` = `PolicyStore.Version()`（**每次请求做一次 RSA 验签 + SHA-256**，
见 T-5）；`GET /api/policy` = `CurrentEnvelope` 原文（`text/plain; charset=utf-8`）。

## 5. 持久化：位置、权限、恢复

数据目录：`%ProgramData%\LabGuardServer`（可由 `--data-dir` 覆盖）。

| 文件 | 内容 | 权限 | 恢复/降级语义 |
|---|---|---|---|
| `server-key.bin` | DPAPI(LocalMachine) 加密的 RSA-2048 XML（**含私钥**） | 文件 ACL 尝试加固为 Administrators/SYSTEM 完全控制（失败仅告警） | 解密失败 → 抛错 → 弹框退出（**不自动重建**：避免静默更换信任根）；`KeySize < 2048` 拒绝使用 |
| `policy.envelope` | LGSRV1 信封原文（= 下发内容） | **未加固**（T-3） | 加载先验签；失败 → 抛错、保留内存现行信封；启动时**未传 minSeq**（T-1） |
| `server-password.txt` | PBKDF2-SHA256 口令散列（`pbkdf2-sha256$迭代$盐$散列`） | **未加固**（T-3） | 无恢复逻辑；缺失 = 首次运行（引导设置） |
| `logs\server-YYYYMMDD.log` | 文本日志（`HH:mm:ss [Level] msg`） | 未加固 | **无轮转、无大小上限**（T-4） |
| `assets\catalog.json` | 开关目录契约快照（随 exe 拷贝到输出目录） | 程序目录，只读 | 缺失/格式不符 → 启动失败（提示客户端重出） |

## 6. 现有 API（实现现状，对应协议 §3）

| 方法与路径 | 请求 | 响应 | 状态码 |
|---|---|---|---|
| `GET` / `HEAD` `/api/version` | — | `{"seq":N,"sha256":"<信封全文 SHA-256>","paused":bool}`，`application/json` | 200 / 503（未配置策略） |
| `GET` / `HEAD` `/api/policy` | — | 信封原文，`text/plain; charset=utf-8` | 200 / 503 |
| `POST` `/api/checkin` | `{"machine":str1..256,"version":str≤32,"seq":uint,"paused":bool}` | `{"ok":true}` | 200 / 400 / 408 / 413 / 429 |
| 其它路径 | — | `not found` | 404 |

限制与防护（均有实现）：

- 请求体上限 **8192 B**：`Content-Length` **先验**即拒（413，不读流）+ 读完后二次判定。
- 请求体读取超时 **5 秒** → 408 并 `Abort()`（慢客户端不发 body）。
- 同 IP 限流 **5 次/秒**（仅 checkin）；限速表 >1024 条时清理 60 秒无活动的 IP。
- **每连接线程池**处理：单个慢客户端不阻塞其他请求。
- `HEAD` 与 `GET` 等价但不写 body（写 body 会被 HttpListener 拒绝并挂死连接）。
- checkin 字段严格类型校验：`machine` 必填；`version`/`seq` 给了但类型错 → 400（不静默吞）。
- 异常兜底：任何响应失败都 `Abort()` 连接，不留挂死连接。

**缺失**（计划要求，见 §7）：`/api/health`、`DeviceId`、`AppliedPolicySeq`、状态模型、稳定错误码（§13.2）。

## 7. 与《执行计划》的差距矩阵

| 计划任务 | 现状 | 差距 / 结论 |
|---|---|---|
| **LG-001** 基线审计 | **本次完成**（本文件） | — |
| **LG-002** 一键 Release 构建 | 本次新增 `build.ps1` / `build.cmd`（clean / restore / build / selftest / dist 产物） | 已满足"一条命令 + 失败非零退出"；`-Package` 明确返回"未实现" |
| LG-003 协议对照审计 | 协议文档已覆盖端点/状态码/限制/信封/seq/防回滚 | **尚未逐项与实现核对**；文档缺"服务重启后 seq 持久化与恢复"章节 → 下一任务 |
| LG-004 安全不变量 | §5.2 十条中：1–7、9 有测试（[1]/[4] 组，共 12+13 条） | **#8 服务重启后 seq 不回退：无测试且实现有缺口**（`Load()` 启动时不传 `minSeq`，见 T-1）；#5 同 seq 不同内容 → 需补断言；#10 私钥泄漏 → 仅有"密钥文件不含明文 XML"一条 |
| LG-005 HTTP 边界 | 已有 408/413/429/400/404/HEAD/并发/慢连接 | 缺：`Content-Length` 负数/不匹配、UTF-8 非法序列、极深 JSON/超长字符串/重复字段、未知字段、空体、非支持方法 |
| LG-006 独立模拟客户端 | M1 互测用过 Python+openssl 代理，**未入库、无固定测试向量** | 需落地为可重复的协议测试代理 |
| LG-007 E2E 主链路 | 依赖 LG-004/006，未做；客户端侧已有**真实 HTTP 回环联调**（客户端自检 [11]，真 HttpListener ↔ 真 transport） | 服务端侧 E2E 未做 |
| LG-008 故障恢复矩阵 | 部分：磁盘信封篡改（4.7）、回滚加载（4.11–4.13） | 缺：重启/强杀/IP 变更/端口占用/密钥损坏/临时文件残留/时间错误/多客户端 |
| LG-009 DeviceId | **无**。checkin 只有 `machine`（计算机名）+ IP | 计划 D1 的完整字段与克隆检测均缺 |
| LG-010 AppliedPolicySeq | 客户端**已上报** `seq`（M2b 完成）；服务端仅展示 seq/paused | 缺状态模型（Offline/Online/Pending/Applied/Error/VersionSkew）与 UI 状态列 |
| LG-011 压力测试 | 未做 | 需 50/100/200 客户端脚本与基线数据 |
| LG-012 首次运行与配对 | 有：首跑设密码 + 生成密钥 + 导出配对文件 | 缺：向导化、端口/URLACL 自检、健康检查、失败重试 |
| LG-013 诊断包 | 无 | 需 manifest + 脱敏日志 + 明确排除私钥/口令 |
| LG-014 安装/升级/卸载 | **无安装器**（README 提到的 `LabGuardServer-Setup-v0.10.exe` 不存在；无 URLACL/防火墙/自启配置） | 发布阻塞项 R-1/R-3 |
| LG-015 v0.10 发布候选 | 未达 | 发布闸门 §E5 全部未开始 |

客户端侧补充（计划 §4 A1 要求"记录配套客户端中配对、轮询、验签和应用策略的实现"）：
**M2a 契约与信任层**（信封验签 + 配对公钥 pin + `--export-catalog` 快照，开关 114 → 119）、
**M2b 轮询与应用**（ServerClient / 策略缓存 / 合并保留本机专属字段 / 暂停优先级）已完成，
客户端自检 224 条全绿（含真实 HTTP 回环联调）；**M2c 服务侧接线未做**（客户端目前没有热重载机制，
应用后需由服务重启引擎 + 重拉小助手）。

## 8. 已知技术债与发布阻塞项

### 发布阻塞项（R）

- **R-1 URLACL 静默降级**：`ApiServer.Start` 依次尝试 `http://+:8210/` → `localhost` → `127.0.0.1`，
  非管理员环境下**静默退回回环**（只写一行日志）。结果是"服务端看起来在运行，学生机连不上"。
  必须在安装器里提权 + `netsh http add urlacl` + 防火墙放行，并把实际监听前缀显示在 UI。
- **R-2 无服务形态**：托盘进程，老师注销/重启即停。
- **R-3 无安装器 / 无部署指南 / 无诊断包**（LG-012~014）。
- **R-4 客户端 M2c 未接线**：轮询与验签逻辑已就绪，但没有进程持有它 → 协议端到端仍不可用。
- **R-5 无 CI**（A3）。

### 技术债（T）

- **T-1 启动不校验 seq 下限**：`PolicyStore.Load()` 在启动路径未传 `minSeq`，
  若 `policy.envelope` 被替换为**签名合法的旧信封**，seq 会回退（违反不变量 8）。
- **T-2 落盘非原子**：`File.WriteAllText` 直接覆盖唯一可信副本，断电可能截断
  （降级语义尚可：加载失败 → 503 → 客户端沿用旧策略，但不符合 §B4 的原子写要求）。
- **T-3 数据目录与口令文件未加固 ACL**（只有密钥文件尝试加固）。
- **T-4 日志无轮转/无上限**（§13.1）。
- **T-5 `/api/version` 每次请求都做 RSA 验签 + SHA-256**：100 客户端 × 5 秒 ≈ 20 req/s，
  需在 LG-011 里实测（预期可接受，但要数据）。
- **T-6 无稳定错误码体系**（§13.2）。
- **T-7 `OnlineList` 条目只增不减**（离线只影响快照过滤，不清理内存）。
- **T-8 策略面板无搜索/折叠/差异预览**（§15 要求，LG-102/107 阶段）。

## 9. 计划合规自查（永久禁止范围 §1）

已核对全部现有代码与服务端 API：**不含**远程桌面、屏幕监控、远程键鼠、截屏、录屏、文件传输、聊天，
也无为这些能力预留的入口、开关或依赖。唯一跨机传输的数据对象是
**签名策略信封**、**配对文件**（公钥/主机/端口/指纹）与 **checkin 状态**，
三者均为固定 schema、有大小上限、字段白名单可控 —— 属于 §1.2 明确允许的例外。
客户端侧同样无上述能力（`FileCreationGuard` 只做"删除新文件"的管控，不上传）。
