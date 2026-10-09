using System;
using System.IO;

namespace LabGuardServer.Core.Logging
{
    /// <summary>极简日志：控制台 + 可选文件（dataDir/logs/server-yyyyMMdd.log）。自检/干跑不会初始化文件端。</summary>
    public static class Log
    {
        public enum Level { Debug = 0, Info = 1, Warn = 2, Error = 3 }

        public static Level MinLevel = Level.Info;
        private static string _dir;

        public static void InitFile(string dataDir)
        {
            _dir = Path.Combine(dataDir, "logs");
            try { Directory.CreateDirectory(_dir); } catch { _dir = null; }
        }

        public static void Info(string msg) => Write(Level.Info, msg);
        public static void Warn(string msg) => Write(Level.Warn, msg);
        public static void Error(string msg, Exception ex = null) => Write(Level.Error, ex == null ? msg : msg + " :: " + ex);

        private static void Write(Level level, string msg)
        {
            if (level < MinLevel) return;
            string line = DateTime.Now.ToString("HH:mm:ss") + " [" + level + "] " + msg;
            Console.WriteLine(line);
            if (_dir == null) return;
            try
            {
                File.AppendAllText(Path.Combine(_dir, "server-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                    line + Environment.NewLine);
            }
            catch { }
        }
    }
}
