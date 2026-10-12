using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace LabGuardServer.Installer
{
    internal static class Program
    {
        /// <summary>安装日志（老师排查用；不含任何密钥/口令）。</summary>
        public static string LogPath
        {
            get { return Path.Combine(Path.GetTempPath(), "LabGuardServer-install.log"); }
        }

        public static void Log(string line)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine, new UTF8Encoding(false)); }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            args = args ?? new string[0];

            TextWriter output;
            try { Console.OutputEncoding = Encoding.UTF8; output = Console.Out; }
            catch { output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true }; }

            Action<string> log = line =>
            {
                output.WriteLine(line);
                Log(line);
            };

            int exit = 0;
            try
            {
                if (Has(args, "--install"))
                {
                    string dir = Value(args, "--dir") ?? InstallEngine.DefaultInstallDir;
                    int port = ParsePort(args, InstallEngine.DefaultPort);
                    InstallEngine.Install(dir, port, log);
                }
                else if (Has(args, "--uninstall"))
                {
                    InstallEngine.Uninstall(Has(args, "--purge"), log);
                }
                else if (Has(args, "--export-pairing"))
                {
                    string file = Value(args, "--export-pairing");
                    if (string.IsNullOrEmpty(file)) { log("用法：--export-pairing <输出.json> [--dir <安装目录>]"); exit = 1; }
                    else log(InstallEngine.ExportPairing(Value(args, "--dir") ?? InstallEngine.DefaultInstallDir, file));
                }
                else if (Has(args, "--status"))
                {
                    log(InstallEngine.Status());
                }
                else
                {
                    Application.Run(new InstallerForm());
                }
            }
            catch (Exception ex)
            {
                log("失败：" + ex.Message);
                Log("异常：" + ex);
                exit = 1;
            }
            Environment.ExitCode = exit;
        }

        private static bool Has(string[] args, string name)
        {
            return args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string Value(string[] args, string name)
        {
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
