using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace LabGuardServer.Installer
{
    /// <summary>
    /// 教师机服务端安装引擎（执行计划 LG-014 / §E1）。
    ///
    /// 三件别人容易漏、漏了就"服务端看着在跑但学生机连不上"的事，这里必须做对：
    /// ① **URLACL**：`http://+:8210/` 默认只有管理员能监听；非管理员/服务身份启动会被拒，
    ///    而我们的服务端会静默回退到 localhost —— 结果就是"教师机上一切正常，学生机全部连不上"。
    /// ② **防火墙入站规则**：局域网学生机要能连 8210。
    /// ③ **登录自启**：老师不会记得每次开机点一次。
    ///
    /// 卸载时要把这三样**还原干净**（尤其 URLACL 与防火墙规则），并询问是否保留密钥/策略数据目录。
    /// </summary>
    internal static class InstallEngine
    {
        public const int DefaultPort = 8210;
        public const string RegistryRoot = @"SOFTWARE\LabGuardServer";
        public const string FirewallRuleName = "LabGuard Server (教师机管控)";
        private const string StartupShortcutName = "LabGuard Server.lnk";

        public static string DefaultInstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LabGuard Server"); }
        }

        public static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LabGuardServer"); }
        }

        /// <summary>内嵌的服务端文件（逻辑名 → 落盘相对路径）。</summary>
        private static readonly string[] PayloadFiles =
        {
            "payload/LabGuardServer.exe",
            "payload/LabGuardServer.exe.config",
            "payload/LabGuardServer.Core.dll",
            "payload/assets/catalog.json",
        };

        // ================================================================ 安装

        public static void Install(string dir, int port, Action<string> log)
        {
            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            log("LabGuard Server 安装程序 v" + version);
            log("目标目录：" + dir + "   端口：" + port);

            CheckWindowsAndDotNet(log);

            if (port < 1 || port > 65535) throw new InvalidOperationException("端口不合法：" + port);
            if (IsPortOccupied(port)) log("提示：端口 " + port + " 当前已被占用（可能是旧版服务端在跑，安装会先停掉它）。");

            log("① 停止正在运行的服务端（要替换文件）");
            StopRunning();

            log("② 安装文件到 " + dir);
            Directory.CreateDirectory(dir);
            foreach (string name in PayloadFiles)
            {
                string target = Path.Combine(dir, name.Substring("payload/".Length).Replace('/', '\\'));
                string parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (s == null) throw new InvalidOperationException("内嵌资源缺失：" + name + "（安装包不完整，请重新生成）");
                    using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        s.CopyTo(fs);
                }
                log("   " + target + "  " + new FileInfo(target).Length + " B");
            }

            log("③ 配置 HTTP URL ACL（非管理员/服务身份也能监听 " + port + "）");
            UrlAclDelete(port, log);
            UrlAclAdd(port, log);

            log("④ 添加入站防火墙规则（局域网学生机要连这个端口）");
            FirewallDelete(log);
            FirewallAdd(port, log);

            log("⑤ 创建快捷方式与登录自启");
            CreateShortcuts(dir, log);

            log("⑥ 登记安装信息（供卸载/诊断用）");
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(RegistryRoot))
            {
                if (k != null)
                {
                    k.SetValue("Installed", 1, RegistryValueKind.DWord);
                    k.SetValue("InstallDir", dir, RegistryValueKind.String);
                    k.SetValue("Port", port, RegistryValueKind.DWord);
                    k.SetValue("Version", version, RegistryValueKind.String);
                }
            }

            log("⑦ 启动服务端并自检监听");
            StartService(dir, log);
            string probe = Probe(port, 12);
            log("   " + probe);
            if (probe.IndexOf("OK", StringComparison.Ordinal) < 0)
                log("   ⚠ 监听自检没通过：请检查端口占用/URLACL，或看 " + Path.Combine(DataDir, "logs"));

            log("");
            log("安装完成。下一步：");
            log("  1) 托盘图标右键 → 策略面板（首次会让你设置管理口令）→ 保存并下发；");
            log("  2) 右键 → 导出配对文件，把 pairing.json 拷到学生机；");
            log("  3) 学生机执行：powershell -File install.ps1 -Pair <配对文件>");
        }

        // ================================================================ 卸载

        public static void Uninstall(bool purgeData, Action<string> log)
        {
            string dir = null;
            int port = DefaultPort;
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(RegistryRoot))
            {
                if (k != null)
                {
                    dir = k.GetValue("InstallDir") as string;
                    port = Convert.ToInt32(k.GetValue("Port", DefaultPort));
                }
            }
            if (string.IsNullOrEmpty(dir)) dir = DefaultInstallDir;

            log("① 停止服务端进程");
            StopRunning();

            log("② 删除 URL ACL 与防火墙规则（还原系统）");
            UrlAclDelete(port, log);
            FirewallDelete(log);

            log("③ 删除快捷方式");
            RemoveShortcuts(log);

            log("④ 删除程序目录 " + dir);
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                    log("   已删除");
                }
                else log("   （不存在，跳过）");
            }
            catch (Exception ex) { log("   ⚠ 删除失败：" + ex.Message + "（可能有进程还占着文件，重启后再删）"); }

            log("⑤ 清理安装登记");
            try { Registry.LocalMachine.DeleteSubKeyTree(RegistryRoot, false); log("   已删除"); }
            catch (Exception ex) { log("   ⚠ " + ex.Message); }

            if (purgeData)
            {
                log("⑥ 删除数据目录（密钥 / 策略 / 配对记录）：" + DataDir);
                try
                {
                    if (Directory.Exists(DataDir)) { Directory.Delete(DataDir, true); log("   已删除"); }
                }
                catch (Exception ex) { log("   ⚠ 删除失败：" + ex.Message); }
            }
            else
            {
                log("⑥ 保留数据目录（密钥 / 策略 / 配对记录）：" + DataDir);
                log("   换机重装可继续用同一把签名密钥（学生机不用重新配对）；要彻底清干净请加 --purge");
            }
            log("");
            log("卸载完成。");
        }

        // ================================================================ 诊断 / 配对导出

        public static string Probe(int port, int timeoutSeconds = 5)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/version");
                req.Timeout = timeoutSeconds * 1000;
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string body = sr.ReadToEnd();
                    return "OK：/api/version → " + body;
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null && (int)resp.StatusCode == 503)
                    return "OK：服务端已监听（尚未保存策略，/api/version 返回 503 属正常）";
                return "FAIL：连不上 127.0.0.1:" + port + "（" + ex.Message + "）";
            }
            catch (Exception ex)
            {
                return "FAIL：" + ex.Message;
            }
        }

        /// <summary>调服务端的 headless CLI 导出配对文件（不重复实现配对格式，单一真相源在服务端本体）。</summary>
        public static string ExportPairing(string dir, string outFile)
        {
            string exe = Path.Combine(dir, "LabGuardServer.exe");
            if (!File.Exists(exe)) return "找不到服务端程序：" + exe;
            string args = "--data-dir \"" + DataDir + "\" --export-pairing \"" + outFile + "\"";
            string output = RunCapture(exe, args);
            return File.Exists(outFile)
                ? ("已导出：" + outFile + Environment.NewLine + output)
                : ("导出失败：" + Environment.NewLine + output);
        }

        // ================================================================ 具体动作

        private static void CheckWindowsAndDotNet(Action<string> log)
        {
            Version v = Environment.OSVersion.Version;
            log("   系统：" + Environment.OSVersion.VersionString + (v.Major < 6 ? "  ⚠ 需要 Windows 7 及以上" : ""));
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
            {
                int release = k == null ? 0 : Convert.ToInt32(k.GetValue("Release", 0));
                if (release < 528040)
                    throw new InvalidOperationException(".NET Framework 4.8 未安装（检测到 Release=" + release +
                        "）。请先装 .NET Framework 4.8 再运行本安装程序。");
                log("   .NET Framework 4.8：已安装（Release=" + release + "）");
            }
        }

        private static bool IsPortOccupied(int port)
        {
            try
            {
                var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                foreach (IPEndPoint ep in listeners) if (ep.Port == port) return true;
            }
            catch { }
            return false;
        }

        private static void StopRunning()
        {
            foreach (Process p in Process.GetProcessesByName("LabGuardServer"))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
        }

        private static void UrlAclAdd(int port, Action<string> log)
        {
            string url = "http://+:" + port + "/";
            string output = RunCapture("netsh.exe", "http add urlacl url=" + url + " user=Everyone");
            log("   " + (output.IndexOf("successfully", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         output.IndexOf("成功", StringComparison.Ordinal) >= 0 ? "已添加：" : "输出：") +
                output.Trim().Replace(Environment.NewLine, " "));
        }

        private static void UrlAclDelete(int port, Action<string> log)
        {
            string url = "http://+:" + port + "/";
            string output = RunCapture("netsh.exe", "http delete urlacl url=" + url);
            if (output.IndexOf("成功", StringComparison.Ordinal) >= 0 ||
                output.IndexOf("successfully", StringComparison.OrdinalIgnoreCase) >= 0)
                log("   已删除旧的 URLACL：" + url);
        }

        private static void FirewallAdd(int port, Action<string> log)
        {
            string output = RunCapture("netsh.exe",
                "advfirewall firewall add rule name=\"" + FirewallRuleName + "\" dir=in action=allow protocol=TCP localport=" + port);
            log("   " + (output.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0 ? "已添加入站规则（TCP " + port + "）"
                : "输出：" + output.Trim().Replace(Environment.NewLine, " ")));
        }

        private static void FirewallDelete(Action<string> log)
        {
            string output = RunCapture("netsh.exe", "advfirewall firewall delete rule name=\"" + FirewallRuleName + "\"");
            if (output.IndexOf("删除", StringComparison.Ordinal) >= 0 ||
                output.IndexOf("Deleted", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("No rules match", StringComparison.OrdinalIgnoreCase) < 0)
                log("   已清理旧防火墙规则（若有）");
        }

        private static void CreateShortcuts(string dir, Action<string> log)
        {
            try
            {
                string exe = Path.Combine(dir, "LabGuardServer.exe");
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);

                string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                    "LabGuard Server.lnk");
                dynamic lnk = shell.CreateShortcut(desktop);
                lnk.TargetPath = exe;
                lnk.WorkingDirectory = dir;
                lnk.Description = "LabGuard 教师机服务端（机房策略统一下发）";
                lnk.Save();
                log("   桌面快捷方式：" + desktop);

                // 登录自启：所有用户共用启动目录（老师开机不用记得点）
                string startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                    StartupShortcutName);
                dynamic auto = shell.CreateShortcut(startup);
                auto.TargetPath = exe;
                auto.WorkingDirectory = dir;
                auto.Description = "LabGuard 教师机服务端（登录自动启动）";
                auto.Save();
                log("   登录自启：" + startup);
            }
            catch (Exception ex)
            {
                log("   ⚠ 创建快捷方式失败：" + ex.Message + "（不影响服务端本身，可手动建）");
            }
        }

        private static void RemoveShortcuts(Action<string> log)
        {
            string[] paths =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "LabGuard Server.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupShortcutName),
            };
            foreach (string p in paths)
            {
                try { if (File.Exists(p)) { File.Delete(p); log("   已删除 " + p); } }
                catch (Exception ex) { log("   ⚠ 删除 " + p + " 失败：" + ex.Message); }
            }
        }

        private static void StartService(string dir, Action<string> log)
        {
            try
            {
                string exe = Path.Combine(dir, "LabGuardServer.exe");
                var psi = new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true };
                Process.Start(psi);
                log("   已启动：" + exe);
                for (int i = 0; i < 10; i++)
                {
                    System.Threading.Thread.Sleep(500);
                    if (Process.GetProcessesByName("LabGuardServer").Length > 0) break;
                }
            }
            catch (Exception ex)
            {
                log("   ⚠ 启动失败：" + ex.Message + "（可手动双击 " + Path.Combine(dir, "LabGuardServer.exe") + "）");
            }
        }

        /// <summary>跑一个外部命令并抓取其输出（netsh 输出会按本机代码页返回，这里按默认编码读）。</summary>
        private static string RunCapture(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return output;
                }
            }
            catch (Exception ex)
            {
                return "（执行 " + exe + " 失败：" + ex.Message + "）";
            }
        }

        public static string Status()
        {
            var sb = new StringBuilder();
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(RegistryRoot))
            {
                sb.AppendLine(k == null ? "未安装（注册表无 " + RegistryRoot + "）"
                    : "已安装：目录=" + k.GetValue("InstallDir") + "  端口=" + k.GetValue("Port") +
                      "  版本=" + k.GetValue("Version"));
            }
            sb.AppendLine("数据目录：" + DataDir + (Directory.Exists(DataDir) ? "（存在）" : "（不存在）"));
            sb.AppendLine("进程：" + Process.GetProcessesByName("LabGuardServer").Length + " 个");
            int port = DefaultPort;
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(RegistryRoot))
                if (k != null) port = Convert.ToInt32(k.GetValue("Port", DefaultPort));
            sb.AppendLine(Probe(port));
            return sb.ToString();
        }
    }
}
