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
    /// </code>
    /// `--export-pairing` 只导出配对文件后退出（不起 HTTP）；给了 `--seconds` 则跑够秒数自动退出（测试用）。
    /// </summary>
    internal static class ServerCli
    {
        public static bool WantsCli(string[] args)
        {
            return Has(args, "--headless") || Has(args, "--export-pairing") || Has(args, "--set-policy")
                || Has(args, "--policy-default");
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

            // 口令
            string password = Value(args, "--password");
            if (!string.IsNullOrEmpty(password))
            {
                if (PasswordGate.StoredHash(dataDir) == null)
                {
                    string weak = Core.Security.PasswordHasher.Validate(password);
                    if (weak != null) { output.WriteLine("失败：口令不合规——" + weak); return 6; }
                    PasswordGate.SaveHash(dataDir, password);
                    output.WriteLine("已设置管理口令。");
                }
                else output.WriteLine("提示：本机已有管理口令，本次 --password 未生效（改口令请在图形界面里改）。");
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

            // 配对文件导出（不需要起 HTTP）
            string pairingOut = Value(args, "--export-pairing");
            int port = ParsePort(args, ApiServer.DefaultPort);
            if (!string.IsNullOrEmpty(pairingOut))
            {
                PairingFile.PairingDoc doc = PairingFile.Build(keys.PublicXml, keys.Fingerprint, port);
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

            output.WriteLine("在线学生机（最近 " + (int)online.OfflineAfter.TotalSeconds + " 秒内有心跳）：");
            CheckinRecord[] snapshot = online.Snapshot();
            if (snapshot.Length == 0) output.WriteLine("  （无）");
            foreach (CheckinRecord r in snapshot)
                output.WriteLine("  " + r.Machine + "  " + r.Ip + "  v" + r.Version + "  seq=" + r.Seq +
                                  (r.Paused ? "  已暂停" : ""));
            api.Stop();
            keys.Dispose();
            return 0;
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
