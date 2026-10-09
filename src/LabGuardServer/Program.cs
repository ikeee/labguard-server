using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using LabGuardServer.Core.Logging;

namespace LabGuardServer
{
    internal static class Program
    {
        /// <summary>数据目录：正式 %ProgramData%\LabGuardServer；--data-dir 覆盖（开发/测试用）。</summary>
        public static string DataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LabGuardServer");

        [STAThread]
        private static void Main(string[] args)
        {
            // ① 崩溃兜底：未处理异常要留日志 + 弹说明（与客户端 labguard 同款纪律）
            Application.ThreadException += (s, e) =>
            {
                Log.Error("UI 未处理异常", e.Exception);
                MessageBox.Show("程序遇到未处理的错误，详见日志。\n" + e.Exception.Message,
                    "LabGuard Server", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.Error("未处理异常（域级）", e.ExceptionObject as Exception);
            };

            // ② WinForms 初始化必须在第一个窗体创建之前（客户端 v0.07 修过的命门，这里从第一天就放对位置）
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // --data-dir <目录>：测试/便携模式
            int di = Array.IndexOf(args ?? new string[0], "--data-dir");
            if (di >= 0 && args.Length > di + 1) DataDir = args[di + 1];

            // 单实例
            bool createdNew;
            using (var mutex = new Mutex(true, "LabGuardServer_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("LabGuard 教师机服务端已经在运行了（右下角托盘）。",
                        "LabGuard Server", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try
                {
                    Log.InitFile(DataDir);
                    Directory.CreateDirectory(DataDir);
                    Application.Run(new ServerTrayContext());
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }
}
