using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LabGuardServer.Core.Envelope
{
    /// <summary>
    /// 教师机签名密钥对：RSA 2048。私钥以 XML 形式 DPAPI(LocalMachine) 加密落盘，
    /// 只允许 Administrators/SYSTEM 访问（目录权限加固）。数据目录可注入——自检用临时目录，正式用 ProgramData。
    /// </summary>
    public sealed class RsaKeyStore : IDisposable
    {
        private const string KeyFileName = "server-key.bin";

        public string DataDir { get; }
        private string KeyPath => Path.Combine(DataDir, KeyFileName);
        public RSA Rsa { get; private set; }

        /// <summary>指纹：公钥 XML 的 SHA256 前 16 字节 hex。配对文件里给学生机人工核对用。</summary>
        public string Fingerprint { get; private set; }

        private RsaKeyStore(string dataDir, RSA rsa)
        {
            DataDir = dataDir;
            Rsa = rsa;
            Fingerprint = ComputeFingerprint(rsa);
        }

        /// <summary>加载已有密钥；不存在则生成新的并立即落盘。</summary>
        public static RsaKeyStore LoadOrCreate(string dataDir)
        {
            Directory.CreateDirectory(dataDir);
            string path = Path.Combine(dataDir, KeyFileName);
            RSA rsa;
            if (File.Exists(path))
            {
                byte[] blob = File.ReadAllBytes(path);
                byte[] xmlBytes;
                try
                {
                    xmlBytes = ProtectedData.Unprotect(blob, null, DataProtectionScope.LocalMachine);
                }
                catch
                {
                    throw new InvalidDataException("签名密钥解密失败（DPAPI），密钥文件可能被替换或跨机拷贝");
                }
                rsa = new RSACryptoServiceProvider();
                rsa.FromXmlString(Encoding.UTF8.GetString(xmlBytes));
            }
            else
            {
                // 错题本：net48 上 RSA.Create() 设 KeySize=2048 无效（仍是 1024 位），
                // 必须 new RSACryptoServiceProvider(2048) 显式指定位长
                rsa = new RSACryptoServiceProvider(2048);
                byte[] xmlBytes = Encoding.UTF8.GetBytes(rsa.ToXmlString(true));
                File.WriteAllBytes(path, ProtectedData.Protect(xmlBytes, null, DataProtectionScope.LocalMachine));
                TryHardenFile(path);
            }
            if (rsa.KeySize < 2048)
                throw new InvalidDataException("签名密钥低于 2048 位（实际 " + rsa.KeySize + "），拒绝使用");
            return new RsaKeyStore(dataDir, rsa);
        }

        /// <summary>公钥 XML（不含私钥）——配对文件导出用。</summary>
        public string PublicXml => Rsa.ToXmlString(false);

        public static string ComputeFingerprint(RSA rsa)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(rsa.ToXmlString(false)));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static void TryHardenFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                var sec = info.GetAccessControl();
                sec.SetAccessRuleProtection(true, false);
                var admin = new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(admin,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(system,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
                info.SetAccessControl(sec);
            }
            catch
            {
                // 权限加固失败不致命（可能非管理员运行）；密钥本身仍有 DPAPI 加密
            }
        }

        public void Dispose()
        {
            Rsa?.Dispose();
        }
    }
}
