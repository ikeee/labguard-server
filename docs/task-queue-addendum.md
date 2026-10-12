# 任务队列增补（对 `docs/development-plan.md` 的补充，不改动计划正文）

> `docs/development-plan.md` 是用户交付的计划原件（逐字节同源，不作修改）。
> 本文件只记录**经用户确认的增补任务**，供后续会话对照。增补日期：2026-10-12。

## LG-016 客户端服务端模式接线（M2c）

**背景**：计划的任务队列（LG-001…LG-015）以服务端仓库为主线，
没有单列"客户端侧把服务器策略真正应用起来"这件事；但 §E2 发布闸门与 LG-007 E2E 主链路
都要求它可用。用户已确认**单列该任务**（2026-10-12）。

**现状（客户端仓库 `ikeee/labguard`，分支 `feat/server-v0.10`）**：

| 已具备 | 位置 | 自检 |
|---|---|---|
| 信封解析/验签/防回滚 | `src/LabGuard.Core/Server/PolicyEnvelope.cs` | [10]① |
| 配对文件导入 + 公钥 pin（ACL） | `src/LabGuard.Core/Server/ServerPairing.cs` | [10]② |
| 开关目录快照导出（服务端面板数据源） | `--export-catalog` / `CatalogExport.cs` | [10]③ |
| 轮询 / 应用 / 缓存 / 回退 / 暂停优先级 | `ServerClient.cs` / `ServerPolicyCache.cs` / `ServerPolicyApplier.cs` | [11] 23 条 |
| 服务器配置分组（5 项，开关 114 → 119） | `SettingsCatalog` 分组 15 | [7] 覆盖检查 |

**未做（本任务范围）**：

1. **服务侧持有 `ServerClient`**：`GuardService` 以 SYSTEM 身份运行（能写 `%ProgramData%` 与 HKLM），
   由它轮询与落盘。
2. **"怎么重新生效"**：客户端当前**没有任何热重载机制**——设置保存走
   `SettingsForm.RestartEngineSide()`（`net stop/sc start` + 杀并重新拉起小助手），
   服务 `OnStart` 只读一次配置。因此应用策略后必须由服务**重启自己的引擎**
   （`_engine.Stop()` + `StartEngine()`）并**重新拉起小助手**（复用 `EnsureAgent` / 既有杀进程+启动逻辑）。
3. **服务器暂停独立标记**：不得与老师的 `paused.flag`（`WatchdogGuard.IsPaused()`）共用，
   否则"老师恢复"会把服务器暂停一起清掉（或反之）。
4. **部署与界面**：`install.ps1 -Pair <配对文件>`、设置面板导入配对、
   服务器在线时本地面板只读提示"当前由服务器管控"、托盘显示状态（在线 / 回退 / 最近同步 / 当前 seq）。

**依赖与阻塞**：依赖 LG-003（已完成，协议口径已冻结）。
它阻塞 **LG-007 E2E 主链路**（没有可跑的学生机端就没有端到端）与 **LG-014 安装/升级/卸载**（配对流程）。

**验收（对齐计划 §C/LG-007）**：两台机器（VM 即可）完成
`服务端启动 → 导出配对文件 → 客户端配对 → check-in → 教师端改策略 → 客户端拉取/验签/应用 →
客户端回报已应用 seq → 教师端可见` 全链路，且服务端离线时客户端继续沿用最后可信策略。
