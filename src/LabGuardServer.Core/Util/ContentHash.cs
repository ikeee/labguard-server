using System.Security.Cryptography;
using System.Text;

namespace LabGuardServer.Core.Util
{
    /// <summary>
    /// 小文件的**内容签名**：热拾取（"磁盘上的文件变了没有"）用它替代文件时间戳。
    ///
    /// 为什么不能用时间戳（2026-10-13 实测踩坑）：运行中的服务端原来靠
    /// <c>File.GetLastWriteTimeUtc</c> 判断"外部改过分组/临时状态文件没有"，
    /// 而**原子写是"临时文件 → 替换"**，替换后拿到的"最后写入时间"并不总是稳定地变新
    /// （取决于 Replace/Move 的元数据语义与时间戳粒度）。后果是：
    /// 老师连着切两次课堂状态，第二次**被服务端当成"没变"**，
    /// 于是全班拿的还是上一次的策略 —— 从现象看像是"点了没反应"。
    /// 内容签名没有这个不确定性：内容变了一定认得出，没变一定不重载。
    /// </summary>
    public static class ContentHash
    {
        public static string Of(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder(64);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
