using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LabGuardServer.Core.Api;
using LabGuardServer.Core.Catalog;
using LabGuardServer.Core.Audit;
using LabGuardServer.Core.Envelope;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Policy;
using LabGuardServer.Core.Util;

namespace LabGuardServer
{
    /// <summary>
    /// 无界面（headless）命令行模式：给自动化测试、无人值守部署与批量验收用。
    ///
    /// 为什么需要：托盘 UI 里"设密码 → 保存策略 → 导出配对文件"全靠人点，
    /// 结果是端到端验收（执行计划 LG-007）与 100/200 客户端压力测试（LG-011）根本没法自动化。
    /// 这里把同样的动作暴露成命令，**不新增任何协议能力**，只换一个人机接口。
    ///
    /// 用法：
    /// <code>
    /// LabGuardServer.exe --headless --data-dir &lt;目录&gt; [--port 8210]
    ///                    [--password &lt;口令&gt;] [--policy-default | --set-policy &lt;策略.json&gt;]
    ///                    [--export-pairing &lt;输出.json&gt;] [--seconds &lt;N&gt;]
    ///                    [--add-group &lt;组名&gt;] [--assign &lt;组名&gt;=&lt;deviceId 或机器名&gt;]
    ///                    [--group-policy &lt;组名&gt;=&lt;策略.json&gt;]
    ///                    [--pause-default | --resume-default] [--bind-all] [--pairing-address &lt;地址&gt;]
    /// </code>
    /// `--export-pairing` 只导出配对文件后退出（不起 HTTP）；给了 `--seconds` 则跑够秒数自动退出（测试用）。
    /// </summary>
    internal static class ServerCli
    {
        public static bool WantsCli(string[] args)
        {
            // 注意：**任何**只该由无界面模式处理的开关都必须列在这里。漏一个的后果不是"参数被忽略"，
            // 而是整个进程掉进 GUI 分支：起托盘 + 起 HTTP(默认 8210) 并**永久阻塞**——
            // 2026-10-13 测试台首跑就踩了这个坑（--add-group 等分组开关没列进来，
            // 每次调分组命令都留下一个挂在 8210 的僵尸服务端，脚本等到超时）。
            // 新增 CLI 开关时，这里与 Run() 里的解析必须同步。
            return Has(args, "--headless") || Has(args, "--export-pairing") || Has(args, "--set-policy")
                || Has(args, "--policy-default") || Has(args, "--screenshot")
                || Has(args, "--add-group") || Has(args, "--assign") || Has(args, "--group-policy")
                || Has(args, "--pause-default") || Has(args, "--resume-default")
                || Has(args, "--temp-preset") || Has(args, "--temp-start")
                || Has(args, "--temp-end") || Has(args, "--temp-status")
                || Has(args, "--audit-tail") || Has(args, "--audit-verify")
                || Has(args, "--audit-export") || Has(args, "--export-devices")
                || Has(args, "--room-set") || Has(args, "--expect-ips") || Has(args, "--room-info");
        }

        public static int Run(string[] args, string dataDir)
        {
            // WinExe（或被 Start-Process 隐藏窗口启动）时可能根本没有控制台句柄：
            // 此时设置 OutputEncoding 会抛 IOException（真机踩过）。没有控制台时改用显式 UTF-8 的写出器，
            // 否则中文会按本机 ANSI（GBK）落进重定向文件，自动化脚本按 UTF-8 读就成了乱码。
            TextWriter output;
            try { Console.OutputEncoding = Encoding.UTF8; output = Console.Out; }
            catch { output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true }; }
            Log.InitFile(dataDir);
            Log.MinLevel = Log.Level.Info;
            Directory.CreateDirectory(dataDir);

            output.WriteLine("LabGuard Server（无界面模式） 数据目录：" + dataDir);

            RsaKeyStore keys;
            try
            {
                keys = RsaKeyStore.LoadOrCreate(dataDir);
            }
            catch (Exception ex)
            {
                output.WriteLine("失败：签名密钥不可用（" + ex.Message + "）。若密钥文件损坏，请人工确认后处理，不要自动重建。");
                return 4;
            }

            output.WriteLine("公钥指纹：" + keys.Fingerprint);

            // 审计日志（执行计划 §G1 / LG-107）：链密钥由服务端私钥派生（不落盘），先轮转再记事件
            var audit = new AuditLog(dataDir, AuditLog.DeriveKey(keys));
            audit.Rotate();
            if (keys.WasCreated)
                audit.Write(AuditAction.KeyCreate, "key", "server-key", 0, 0, "ok", null,
                            "首次部署：生成了新的签名密钥（公钥指纹 " + keys.Fingerprint + "）");

            string catalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "catalog.json");
            CatalogDoc catalog = null;
            try { catalog = CatalogDoc.Load(catalogPath); }
            catch (Exception ex) { output.WriteLine("提示：开关目录快照不可用（" + ex.Message + "）——不能用 --policy-default"); }

            var groups = new GroupStore(dataDir);      // v0.11 分组（脚本化：--add-group/--assign/--group-policy）
            groups.Load();
            groups.StartWatching();                    // 别人（GUI/另一个脚本进程）改的分组也要拾取
            var room = RoomConfig.Load(dataDir);       // v0.14 本机房信息（教室名 + 期望 IP 段，用来查越界设备）
            var store = new PolicyStore(dataDir, keys);
            var pinned = new PinnedStore(dataDir);     // 常驻（固化锁）：CLI 与 GUI 共用同一份 pinned.json
            store.Pinned = pinned;
            try { store.Load(); store.StartWatching(); }
            catch (Exception ex)
            {
                output.WriteLine("失败：策略信封加载被拒（" + ex.Message + "）。请人工检查 " +
                                  Path.Combine(dataDir, "policy.envelope"));
                return 5;
            }
            output.WriteLine("当前策略：seq=" + store.CurrentSeq +
                              (store.CurrentEnvelope == null ? "（尚未配置）" : "") + "  水位=" + store.Watermark);

            // 临时课堂状态层（v0.12）：启动即装载；**已过期的直接丢弃并重发基础策略**（不允许"睡一觉还在考试模式"）
            var temp = new TempStateManager(dataDir, keys, store, groups, pinned);
            temp.Auditor = audit;
            temp.Load(DateTime.UtcNow);
            temp.StartWatching();      // 别的进程（GUI/另一个脚本）开的临时状态也要拾取

            // 界面截图（文档 / 人工核对）：把策略面板的每个分组 + 搜索示例渲染成 PNG，不改变任何策略
            string shotDir = Value(args, "--screenshot");
            if (!string.IsNullOrEmpty(shotDir) && catalog != null)
            {
                try
                {
                    System.Windows.Forms.Application.EnableVisualStyles();
                    System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                }
                catch { }
                using (var form = new PolicyForm(store, catalog))
                {
                    var files = form.RenderScreenshots(shotDir);
                    foreach (string f in files) output.WriteLine(f);
                    output.WriteLine("共 " + files.Count + " 张，目录：" + shotDir);
                }
                keys.Dispose();
                return 0;
            }

            // 策略
            if (Has(args, "--policy-default"))
            {
                if (catalog == null) { output.WriteLine("失败：没有 catalog.json，无法用默认策略。"); return 6; }
                ulong prevSeq = store.Version()?.Seq ?? 0;
                Dictionary<string, object> oldPolicy = store.CurrentOrDefaultPolicy(catalog);
                Dictionary<string, object> newPolicy = Json.ParseObject(catalog.DefaultPolicyJson);
                PolicyVersion v = store.Save(null, false, catalog.DefaultPolicyJson);
                output.WriteLine("已保存默认策略：seq=" + v.Seq);
                audit.Write(AuditAction.PolicyPublish, "policy", "default", prevSeq, v.Seq, "ok", null,
                    "下发默认策略（恢复开关目录默认值）：" + AuditSummary.PolicyChanges(oldPolicy, newPolicy, catalog.Fields));
            }
            string setPolicy = Value(args, "--set-policy");
            if (!string.IsNullOrEmpty(setPolicy))
            {
                if (!File.Exists(setPolicy)) { output.WriteLine("失败：策略文件不存在：" + setPolicy); return 6; }
                Dictionary<string, object> policy;
                try { policy = Json.ParseObject(File.ReadAllText(setPolicy, Encoding.UTF8)); }
                catch (Exception ex) { output.WriteLine("失败：策略文件不是合法 JSON（" + ex.Message + "）"); return 6; }
                if (policy == null || policy.Count == 0) { output.WriteLine("失败：策略文件是空对象。"); return 6; }
                ulong prevSeq = store.Version()?.Seq ?? 0;
                Dictionary<string, object> oldPolicy = store.CurrentOrDefaultPolicy(catalog);
                PolicyVersion v = store.Save(policy, false, null);
                output.WriteLine("已保存策略：" + setPolicy + " → seq=" + v.Seq);
                audit.Write(AuditAction.PolicyPublish, "policy", "default", prevSeq, v.Seq, "ok", null,
                    "下发默认策略（来自 " + Path.GetFileName(setPolicy) + "）：" +
                    AuditSummary.PolicyChanges(oldPolicy, policy, catalog?.Fields));
            }

            // ---- 分组命令（v0.11，供测试台/无人值守脚本化）
            bool groupsDirty = false;
            if (Has(args, "--pause-default") || Has(args, "--resume-default"))
            {
                if (store.CurrentEnvelope == null) { output.WriteLine("失败：默认策略还没保存过，无从暂停"); return 6; }
                bool paused = Has(args, "--pause-default");
                PolicyVersion pv = store.SetPaused(paused);
                output.WriteLine((paused ? "已全员暂停" : "已全员恢复") + "（默认策略）：seq=" + pv.Seq);
                audit.Write(paused ? AuditAction.PolicyPause : AuditAction.PolicyResume,
                    "policy", "default", pv.Seq, pv.Seq, "ok", null,
                    paused ? "全员暂停（学生机停止管控策略，进程守护照旧）" : "全员恢复（学生机回到服务器策略管控）");
            }
            foreach (string name in Values(args, "--add-group"))
            {
                if (groups.Groups.Any(g => g.Name == name)) { output.WriteLine("分组已存在：" + name); continue; }
                DeviceGroup ng = groups.Add(name);
                output.WriteLine("已新建分组：" + ng.Name + "（" + ng.Id + "）");
                groupsDirty = true;
            }
            foreach (string pair in Values(args, "--assign"))
            {
                string[] kv = SplitPair(pair);
                if (kv == null) { output.WriteLine("失败：--assign 需要 组名=设备ID或机器名"); return 6; }
                DeviceGroup g = FindOrCreateGroup(groups, kv[0], output);
                string member = kv[1];
                if (member.StartsWith("LG-", StringComparison.OrdinalIgnoreCase)) groups.Assign(g.Id, member, null);
                else groups.Assign(g.Id, null, member);
                output.WriteLine("已把 " + member + " 放进组【" + g.Name + "】");
                groupsDirty = true;
            }
            foreach (string pair in Values(args, "--group-policy"))
            {
                string[] kv = SplitPair(pair);
                if (kv == null) { output.WriteLine("失败：--group-policy 需要 组名=策略.json"); return 6; }
                DeviceGroup g = FindOrCreateGroup(groups, kv[0], output);
                if (!File.Exists(kv[1])) { output.WriteLine("失败：策略文件不存在：" + kv[1]); return 6; }
                Dictionary<string, object> gp;
                try { gp = Json.ParseObject(File.ReadAllText(kv[1], Encoding.UTF8)); }
                catch (Exception ex) { output.WriteLine("失败：策略文件不是合法 JSON（" + ex.Message + "）"); return 6; }
                ulong prevSeq = g.Seq;
                Dictionary<string, object> oldGroupPolicy = null;
                if (!string.IsNullOrEmpty(g.Envelope))
                {
                    EnvelopeData oldEnv = PolicyEnvelope.Parse(g.Envelope, keys.Rsa);
                    if (oldEnv.Verified) oldGroupPolicy = Json.ParseObject(oldEnv.PolicyJson);
                }
                // 水位读数用 RefreshWatermark()：先跟磁盘对齐再分配（多进程同时发布也不会撞号）
                ulong seq = GroupPublisher.Publish(keys, g, gp, false, () => store.RefreshWatermark(), s => store.MarkPublished(s), pinned);
                output.WriteLine("已下发到组【" + g.Name + "】：seq=" + seq + "（成员 " + (g.DeviceIds.Count + g.Machines.Count) + "）");
                groupsDirty = true;
                audit.Write(AuditAction.PolicyPublish, "group", g.Name, prevSeq, seq, "ok", null,
                    "下发分组策略（来自 " + Path.GetFileName(kv[1]) + "，成员 " + (g.DeviceIds.Count + g.Machines.Count) + " 台）：" +
                    AuditSummary.PolicyChanges(oldGroupPolicy, gp, catalog?.Fields));
            }
            // 只在**真的改过**分组时才落盘：每次 CLI 都无脑 Save 会平白制造"文件被重写"的窗口，
            // 而运行中的服务端正在盯着这个文件（v0.12 实测：撞上原子替换的瞬间会误判"分组被删"）
            if (groupsDirty && groups.Groups.Count > 0)
            {
                groups.Save();
                output.WriteLine("分组一览（" + groups.Groups.Count + " 组）：");
                foreach (DeviceGroup g in groups.Groups)
                    output.WriteLine("  【" + g.Name + "】" + g.Id + "  成员 " + (g.DeviceIds.Count + g.Machines.Count) +
                                     "  seq " + g.Seq + (string.IsNullOrEmpty(g.Envelope) ? "（尚未下发策略）" : ""));
            }

            // ---- 临时课堂状态命令（v0.12，与托盘菜单同一实现；测试台与无人值守都走这里）
            bool tempTouched = false;
            string presetName = Value(args, "--temp-preset");
            string patchFile = Value(args, "--temp-start");
            if (!string.IsNullOrEmpty(presetName) || !string.IsNullOrEmpty(patchFile))
            {
                string patchJson;
                string name = Value(args, "--temp-name");
                bool paused = Has(args, "--temp-paused");
                int minutes = ParseInt(Value(args, "--temp-minutes"), -1);
                int durSeconds = ParseInt(Value(args, "--temp-seconds"), -1);
                if (!string.IsNullOrEmpty(presetName))
                {
                    TempPreset p = TempPresets.Find(TempPresets.Load(dataDir), presetName);
                    if (p == null) { output.WriteLine("失败：预设不存在：" + presetName); return 6; }
                    patchJson = Json.Stringify(p.Patch);
                    paused = paused || p.Paused;
                    if (string.IsNullOrEmpty(name)) name = p.Name;
                    if (minutes < 0 && durSeconds < 0) minutes = p.DurationMinutes;   // 0 = 不自动到期
                }
                else
                {
                    if (!File.Exists(patchFile)) { output.WriteLine("失败：临时覆盖文件不存在：" + patchFile); return 6; }
                    patchJson = File.ReadAllText(patchFile, Encoding.UTF8);
                    if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(patchFile);
                }
                int totalSeconds = durSeconds >= 0 ? durSeconds : (minutes > 0 ? minutes * 60 : -1);
                DateTime? expires = totalSeconds > 0 ? (DateTime?)DateTime.UtcNow.AddSeconds(totalSeconds) : null;
                try
                {
                    TempState t = temp.Start(name, patchJson, paused, expires, "CLI", DateTime.UtcNow);
                    output.WriteLine("已开始临时状态【" + t.Name + "】：" + t.RemainingText(DateTime.UtcNow) +
                                     "，覆盖 " + (1 + t.GroupEnvelopes.Count) + " 个目标，seq 至 " + t.MaxSeq);
                }
                catch (Exception ex)
                {
                    output.WriteLine("失败：开不了临时状态（" + ex.Message + "）");
                    return 6;
                }
                tempTouched = true;
            }
            if (Has(args, "--temp-end"))
            {
                if (temp.End("CLI 结束", DateTime.UtcNow))
                    output.WriteLine("已结束临时状态【" + temp.LastEndedName + "】→ 目标已改回基础策略（新 seq 已下发）");
                else output.WriteLine("当前没有进行中的临时状态");
                tempTouched = true;
            }
            if (Has(args, "--temp-status"))
            {
                output.WriteLine("临时状态：" + temp.StatusText(DateTime.UtcNow));
                if (temp.Current != null)
                    output.WriteLine("  覆盖：默认组 + " + temp.Current.GroupEnvelopes.Count +
                                     " 个分组；补丁 " + temp.Current.PatchJson);
                tempTouched = true;
            }
            // 本进程刚发布过基础/分组策略 → 临时状态必须按新基础重算（否则临时信封停在旧基础上）
            if (!tempTouched && temp.Active && (Has(args, "--set-policy") || Has(args, "--policy-default") ||
                                                Values(args, "--group-policy").Count > 0))
                temp.RefreshIfActive("CLI 发布了新的基础/分组策略");

            // ---- 审计与报表命令（v0.14：LG-107 / LG-108）
            string auditTail = Value(args, "--audit-tail");
            if (Has(args, "--audit-tail"))
            {
                int n = ParseInt(auditTail, 30);
                if (n <= 0) n = 30;
                var rows = audit.Tail(n);
                output.WriteLine("最近 " + rows.Count + " 条审计记录（时间按本机时区显示）：");
                if (rows.Count == 0) output.WriteLine("  （还没有记录）");
                foreach (AuditEntry e in rows)
                    output.WriteLine("  #" + e.Seq + "  " + e.LocalTime + "  " + e.Action +
                                     "  " + (string.IsNullOrEmpty(e.TargetId) ? "-" : e.TargetId) +
                                     "  " + e.Result + (string.IsNullOrEmpty(e.ErrorCode) ? "" : "(" + e.ErrorCode + ")") +
                                     "  " + (e.NewPolicySeq > 0 ? "seq " + e.PrevPolicySeq + "→" + e.NewPolicySeq + "  " : "") +
                                     e.Summary);
            }
            if (Has(args, "--audit-verify"))
            {
                AuditVerifyResult vr = audit.Verify();
                output.WriteLine("审计链校验：" + (vr.Ok ? "通过 ✅ " : "失败 ❌ ") + vr.Reason +
                                 "（" + vr.Entries + " 条" + (vr.KeyAvailable ? "" : "；链密钥不可用，已降级为纯哈希链") + "）");
                if (!vr.Ok) return 7;
            }
            string auditCsv = Value(args, "--audit-export");
            if (!string.IsNullOrEmpty(auditCsv))
            {
                var all = audit.ReadAll();
                AuditCsv.Export(all, auditCsv);
                output.WriteLine("已导出审计记录 " + all.Count + " 条 → " + auditCsv + "（UTF-8 带 BOM，可被 Excel 直接打开）");
                audit.Write(AuditAction.AuditExport, "export", Path.GetFileName(auditCsv), 0, 0, "ok", null,
                    "导出审计记录 " + all.Count + " 条（CSV，含公式注入防护）");
            }

            // ---- 本机房信息（v0.14：两间机房同网段时，用来把"装错配对/IP 配错"的设备标出来）
            if (Has(args, "--room-set") || Has(args, "--expect-ips"))
            {
                string newRoom = Value(args, "--room-set");
                string newIps = Value(args, "--expect-ips");
                if (!string.IsNullOrWhiteSpace(newRoom)) room.Room = newRoom.Trim();
                if (!string.IsNullOrWhiteSpace(newIps)) room.ExpectIps = newIps.Trim();
                room.Save(dataDir);
                output.WriteLine("已保存本机房信息 → " + RoomConfig.PathOf(dataDir));
                output.WriteLine("  " + room.Describe());
                if (room.InvalidSpecs.Count > 0)
                    output.WriteLine("  ⚠ 写法不认识、已忽略的项：" + string.Join("、", room.InvalidSpecs) +
                                     "（支持 10.46.254.11-99 或 10.46.254.11-10.46.254.99，逗号分隔多段）");
                audit.Write(AuditAction.RoomSet, "room", room.Room, 0, 0, "ok", null, "设置本机房信息：" + room.Describe());
            }
            if (Has(args, "--room-info"))
            {
                output.WriteLine(room.Describe());
                string[] localIps = PairingFile.LocalIpv4Addresses();
                output.WriteLine("  本机（教师机）IPv4：" + (localIps.Length == 0 ? "（探测不到）" : string.Join("，", localIps)));
                if (room.Ranges.Count > 0)
                    output.WriteLine("  说明：期望段填的是**学生机**段；教师机地址通常不在段内（这是正常的）。" +
                                     "设备清单里越界的设备会被标 ⚠跨机房。");
            }

            // 配对文件导出（不需要起 HTTP）
            string pairingOut = Value(args, "--export-pairing");
            int port = ParsePort(args, ApiServer.DefaultPort);
            if (!string.IsNullOrEmpty(pairingOut))
            {
                // --pairing-address：测试台用（配对文件里只写 127.0.0.1，学生端才会连回环；正式部署不传）
                PairingFile.PairingDoc doc = PairingFile.Build(keys.PublicXml, keys.Fingerprint, port,
                    Value(args, "--pairing-address"));
                PairingFile.Export(doc, pairingOut);
                output.WriteLine("已导出配对文件：" + pairingOut);
                audit.Write(AuditAction.PairingExport, "pairing", Path.GetFileName(pairingOut), 0, 0, "ok", null,
                    "导出教师机配对文件（含公钥与指纹，不含私钥；指纹 " + keys.Fingerprint + "，端口 " + port + "）");
                output.WriteLine("  教师机：" + doc.serverHost + "  地址：" + string.Join(",", doc.addresses) +
                                  "  端口：" + doc.port);
                output.WriteLine("  公钥指纹（学生机上核对用）：" + doc.fingerprint);
                if (!Has(args, "--headless")) { keys.Dispose(); return 0; }
            }

            if (!Has(args, "--headless"))
            {
                keys.Dispose();
                return 0;
            }

            // headless 运行：起 HTTP，供学生机轮询
            var online = new OnlineList();
            var api = new ApiServer(store, online);
            api.Groups = groups;      // **必须接上**：不然 /api/version、/api/policy 永远回落默认策略，
                                      // 表现就是"分组命令执行成功、学生机却一直拿默认策略"（2026-10-13 测试台实测）
            api.Temp = temp;          // v0.12 临时课堂状态层（优先级：临时 > 分组 > 默认）
            if (!api.Start(port, Has(args, "--bind-all") ? null : "127.0.0.1"))
            {
                output.WriteLine("失败：HTTP 监听启动失败（端口 " + port + " 被占用或无权限）。");
                keys.Dispose();
                return 5;
            }
            output.WriteLine("HTTP 已监听：" + api.BoundPrefix + "（未配置策略时 /api/version 返回 503）");
            audit.Write(AuditAction.ServerStart, "server", "headless", 0, store.CurrentSeq, "ok", null,
                "教师机服务端启动（无界面模式，监听 " + api.BoundPrefix + "，当前策略 seq=" + store.CurrentSeq + "）");

            // 临时状态到期检查：无界面模式也要"到点自动恢复基础策略"（每 5 秒看一次）
            var tempTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (temp.ExpireIfDue(DateTime.UtcNow))
                        output.WriteLine("[临时状态] 已到期 → 已自动恢复基础策略（新 seq）");
                }
                catch (Exception ex) { Log.Warn("临时状态到期检查异常：" + ex.Message); }
            }, null, 5000, 5000);

            int seconds = 0;
            int.TryParse(Value(args, "--seconds"), out seconds);
            if (seconds > 0)
            {
                output.WriteLine("运行 " + seconds + " 秒后自动退出（测试模式）…");
                for (int i = 0; i < seconds; i++) Thread.Sleep(1000);
            }
            else
            {
                output.WriteLine("按 Ctrl+C 结束。");
                var quit = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit.Set(); };
                quit.Wait();
            }

            output.WriteLine("设备清单（最近 " + (int)online.OfflineAfter.TotalSeconds + " 秒内有心跳）：");
            if (temp.Active) output.WriteLine("  ⚠ " + temp.StatusText(DateTime.UtcNow));
            if (room.Ranges.Count > 0) output.WriteLine("  " + room.Describe());
            CheckinRecord[] snapshot = online.Snapshot();
            if (snapshot.Length == 0) output.WriteLine("  （无）");
            string serverVer = typeof(ServerCli).Assembly.GetName().Version.ToString(3);
            int outOfScope = 0;
            foreach (CheckinRecord r in snapshot)
            {
                DeviceGroup owner = groups.Resolve(r.DeviceId, r.Machine);
                // 越界检测：不属于本机房期望 IP 段的设备（多半是配对文件装错 / IP 配错）——当场可见，别让它悄悄混着
                bool outside = room.Ranges.Count > 0 && room.IsOutOfScope(r.Ip);
                if (outside) outOfScope++;
                // "服务端 seq" 要按该设备**实际生效的那一层**算：临时 > 分组 > 默认。
                // 用默认层的 seq 会把分组设备一律标成"待应用"（分组信封 seq 本来就更小），是错的。
                ulong effSeq = temp.EffectiveSeqFor(owner);
                if (effSeq == 0) effSeq = owner != null && !string.IsNullOrEmpty(owner.Envelope)
                    ? PolicyEnvelope.ParseHeader(owner.Envelope).Seq : store.CurrentSeq;
                DeviceStatus st = DeviceModel.Compute(r, effSeq, serverVer, DateTime.UtcNow, online.OfflineAfter);
                output.WriteLine("  " + r.Machine + "  dev=" + (string.IsNullOrEmpty(r.DeviceId) ? "-" : r.DeviceId) +
                                 "  " + r.Ip + "  v" + r.Version + "  seq=" + r.Seq + "  applied=" + r.AppliedSeq +
                                 "  status=" + (string.IsNullOrEmpty(r.Status) ? "-" : r.Status) +
                                 "  state=" + DeviceModel.Describe(st) +
                                 "  group=" + (owner == null ? "默认策略" : owner.Name) +
                                 "  effSeq=" + effSeq +
                                 (outside ? "  ⚠跨机房" : "") +
                                 (r.Paused ? "  已暂停" : ""));
            }
            if (outOfScope > 0)
                output.WriteLine("  越界 " + outOfScope + " 台：IP 不在本机房期望段（" + RoomScope.Describe(room.Ranges) +
                                 "）内 —— 请核对这些机器的配对文件与 IP 配置（它们可能正被**另一间**的教师机管着）。");
            tempTimer.Dispose();
            // 设备状态 CSV（§G2 的"设备状态摘要"那一项）：给老师留一份可归档的表格
            string devCsv = Value(args, "--export-devices");
            if (!string.IsNullOrEmpty(devCsv))
            {
                var lines = new List<string> { Csv.Line("机器名", "设备ID", "IP", "客户端版本", "已应用seq", "生效seq", "状态", "所属组", "机房", "越界", "最近心跳(本地)") };
                foreach (CheckinRecord r in snapshot)
                {
                    DeviceGroup owner = groups.Resolve(r.DeviceId, r.Machine);
                    ulong effSeq = temp.EffectiveSeqFor(owner);
                    if (effSeq == 0) effSeq = owner != null && !string.IsNullOrEmpty(owner.Envelope)
                        ? PolicyEnvelope.ParseHeader(owner.Envelope).Seq : store.CurrentSeq;
                    bool outside = room.Ranges.Count > 0 && room.IsOutOfScope(r.Ip);
                    lines.Add(Csv.Line(r.Machine, r.DeviceId, r.Ip, r.Version, r.AppliedSeq, effSeq,
                        DeviceModel.Describe(DeviceModel.Compute(r, effSeq, serverVer, DateTime.UtcNow, online.OfflineAfter)),
                        owner == null ? "默认策略" : owner.Name,
                        string.IsNullOrWhiteSpace(room.Room) ? "" : room.Room,
                        outside ? "是（不在本机房期望段）" : "",
                        r.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
                }
                Csv.WriteFile(devCsv, lines);
                output.WriteLine("已导出设备状态 " + snapshot.Length + " 台 → " + devCsv);
                audit.Write(AuditAction.DeviceExport, "export", Path.GetFileName(devCsv), 0, 0, "ok", null,
                    "导出设备状态摘要 " + snapshot.Length + " 台（CSV，含公式注入防护）");
            }
            audit.Write(AuditAction.ServerStop, "server", "headless", 0, store.CurrentSeq, "ok", null,
                "教师机服务端停止（无界面模式；本次共 " + online.Snapshot().Length + " 台设备在册）");
            audit.Rotate();
            api.Stop();
            keys.Dispose();
            return 0;
        }

        /// <summary>重复出现的参数（如多次 --assign）。</summary>
        private static List<string> Values(string[] args, string name)
        {
            var list = new List<string>();
            args = args ?? new string[0];
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) list.Add(args[i + 1]);
            return list;
        }

        /// <summary>"A=值" 拆两半（组名里允许有中文/括号，值里允许有等号？不允许——按第一个 '=' 拆）。</summary>
        private static string[] SplitPair(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int i = s.IndexOf('=');
            if (i <= 0 || i >= s.Length - 1) return null;
            return new[] { s.Substring(0, i).Trim(), s.Substring(i + 1).Trim() };
        }

        /// <summary>按 "组 ID 或组名" 找组；找不到就新建（脚本化时更省事）。</summary>
        private static DeviceGroup FindOrCreateGroup(GroupStore groups, string key, TextWriter output)
        {
            DeviceGroup g = groups.Groups.FirstOrDefault(x => x.Id == key || x.Name == key);
            if (g != null) return g;
            g = groups.Add(key);
            output.WriteLine("（分组不存在，已自动新建：【" + g.Name + "】）");
            return g;
        }

        private static bool Has(string[] args, string name)
        {
            return (args ?? new string[0]).Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string Value(string[] args, string name)
        {
            args = args ?? new string[0];
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static int ParsePort(string[] args, int fallback)
        {
            int port;
            string raw = Value(args, "--port");
            return int.TryParse(raw, out port) && port > 0 && port <= 65535 ? port : fallback;
        }

        /// <summary>整数参数（解析不了就用 fallback；负数/0 由调用方按语义解释）。</summary>
        private static int ParseInt(string raw, int fallback)
        {
            return int.TryParse(raw, out int v) ? v : fallback;
        }
    }
}
