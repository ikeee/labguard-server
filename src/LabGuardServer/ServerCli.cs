using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LabGuardServer.Core.Api;
using LabGuardServer.Core.Catalog;
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
                || Has(args, "--pause-default") || Has(args, "--resume-default");
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

            string catalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "catalog.json");
            CatalogDoc catalog = null;
            try { catalog = CatalogDoc.Load(catalogPath); }
            catch (Exception ex) { output.WriteLine("提示：开关目录快照不可用（" + ex.Message + "）——不能用 --policy-default"); }

            var groups = new GroupStore(dataDir);      // v0.11 分组（脚本化：--add-group/--assign/--group-policy）
            groups.Load();
            groups.StartWatching();                    // 别人（GUI/另一个脚本进程）改的分组也要拾取
            var store = new PolicyStore(dataDir, keys);
            try { store.Load(); store.StartWatching(); }
            catch (Exception ex)
            {
                output.WriteLine("失败：策略信封加载被拒（" + ex.Message + "）。请人工检查 " +
                                  Path.Combine(dataDir, "policy.envelope"));
                return 5;
            }
            output.WriteLine("当前策略：seq=" + store.CurrentSeq +
                              (store.CurrentEnvelope == null ? "（尚未配置）" : "") + "  水位=" + store.Watermark);

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
                PolicyVersion v = store.Save(null, false, catalog.DefaultPolicyJson);
                output.WriteLine("已保存默认策略：seq=" + v.Seq);
            }
            string setPolicy = Value(args, "--set-policy");
            if (!string.IsNullOrEmpty(setPolicy))
            {
                if (!File.Exists(setPolicy)) { output.WriteLine("失败：策略文件不存在：" + setPolicy); return 6; }
                Dictionary<string, object> policy;
                try { policy = Json.ParseObject(File.ReadAllText(setPolicy, Encoding.UTF8)); }
                catch (Exception ex) { output.WriteLine("失败：策略文件不是合法 JSON（" + ex.Message + "）"); return 6; }
                if (policy == null || policy.Count == 0) { output.WriteLine("失败：策略文件是空对象。"); return 6; }
                PolicyVersion v = store.Save(policy, false, null);
                output.WriteLine("已保存策略：" + setPolicy + " → seq=" + v.Seq);
            }

            // ---- 分组命令（v0.11，供测试台/无人值守脚本化）
            if (Has(args, "--pause-default") || Has(args, "--resume-default"))
            {
                if (store.CurrentEnvelope == null) { output.WriteLine("失败：默认策略还没保存过，无从暂停"); return 6; }
                bool paused = Has(args, "--pause-default");
                PolicyVersion pv = store.SetPaused(paused);
                output.WriteLine((paused ? "已全员暂停" : "已全员恢复") + "（默认策略）：seq=" + pv.Seq);
            }
            foreach (string name in Values(args, "--add-group"))
            {
                if (groups.Groups.Any(g => g.Name == name)) { output.WriteLine("分组已存在：" + name); continue; }
                DeviceGroup ng = groups.Add(name);
                output.WriteLine("已新建分组：" + ng.Name + "（" + ng.Id + "）");
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
                // 水位读数用 RefreshWatermark()：先跟磁盘对齐再分配（多进程同时发布也不会撞号）
                ulong seq = GroupPublisher.Publish(keys, g, gp, false, () => store.RefreshWatermark(), s => store.MarkPublished(s));
                output.WriteLine("已下发到组【" + g.Name + "】：seq=" + seq + "（成员 " + (g.DeviceIds.Count + g.Machines.Count) + "）");
            }
            if (groups.Groups.Count > 0)
            {
                groups.Save();
                output.WriteLine("分组一览（" + groups.Groups.Count + " 组）：");
                foreach (DeviceGroup g in groups.Groups)
                    output.WriteLine("  【" + g.Name + "】" + g.Id + "  成员 " + (g.DeviceIds.Count + g.Machines.Count) +
                                     "  seq " + g.Seq + (string.IsNullOrEmpty(g.Envelope) ? "（尚未下发策略）" : ""));
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
            if (!api.Start(port, Has(args, "--bind-all") ? null : "127.0.0.1"))
            {
                output.WriteLine("失败：HTTP 监听启动失败（端口 " + port + " 被占用或无权限）。");
                keys.Dispose();
                return 5;
            }
            output.WriteLine("HTTP 已监听：" + api.BoundPrefix + "（未配置策略时 /api/version 返回 503）");

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
            CheckinRecord[] snapshot = online.Snapshot();
            if (snapshot.Length == 0) output.WriteLine("  （无）");
            string serverVer = typeof(ServerCli).Assembly.GetName().Version.ToString(3);
            foreach (CheckinRecord r in snapshot)
            {
                DeviceStatus st = DeviceModel.Compute(r, store.CurrentSeq, serverVer, DateTime.UtcNow, online.OfflineAfter);
                DeviceGroup owner = groups.Resolve(r.DeviceId, r.Machine);
                output.WriteLine("  " + r.Machine + "  dev=" + (string.IsNullOrEmpty(r.DeviceId) ? "-" : r.DeviceId) +
                                 "  " + r.Ip + "  v" + r.Version + "  seq=" + r.Seq + "  applied=" + r.AppliedSeq +
                                 "  status=" + (string.IsNullOrEmpty(r.Status) ? "-" : r.Status) +
                                 "  state=" + DeviceModel.Describe(st) +
                                 "  group=" + (owner == null ? "默认策略" : owner.Name) +
                                 (r.Paused ? "  已暂停" : ""));
            }
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
    }
}
