using System;
using System.IO;
using System.Text;

namespace LabGuardServer.Core.Util
{
    /// <summary>
    /// 安全写入（执行计划 §B4）：**同目录临时文件 → 刷盘 → 原子替换**，
    /// 绝不用 `File.WriteAllText` 直接覆盖唯一可信副本（断电/崩溃会留下截断文件）。
    ///
    /// 之后读者只会看到"旧内容"或"新内容"，永远不会看到半截内容。
    /// </summary>
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string text)
        {
            WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text ?? ""));
        }

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);            // 刷到磁盘，不只是刷到 OS 缓存
            }

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, null, true);   // 原子替换（同卷）
                    return;
                }
                catch (Exception)
                {
                    // 某些文件系统/权限组合不支持 Replace：退回"删除 + 改名"（窗口极小，且有刷盘保证）
                    try { File.Delete(path); } catch { }
                }
            }
            File.Move(tmp, path);
        }
    }
}
