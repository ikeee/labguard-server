using System;
using System.Security.Cryptography;

namespace LabGuardServer.Core.Security
{
    /// <summary>
    /// 口令散列：PBKDF2-SHA256——与客户端 LabGuard.Core.Config.PasswordHasher 完全同一算法与存储格式，
    /// （将来老师把学生机密码也设成同一个，两边可无缝互认；服务端独立使用也不依赖客户端仓库。）
    /// </summary>
    public static class PasswordHasher
    {
        private const int Iterations = 100000;
        private const int SaltSize = 16;
        private const int HashSize = 32;

        public static string Create(string password)
        {
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("密码不能为空", nameof(password));
            byte[] salt = new byte[SaltSize];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            byte[] hash = Pbkdf2(password, salt, Iterations);
            return string.Join("$", new[] { "pbkdf2-sha256", Iterations.ToString(), Convert.ToBase64String(salt), Convert.ToBase64String(hash) });
        }

        public static bool Verify(string password, string stored)
        {
            if (string.IsNullOrEmpty(stored) || password == null) return false;
            string[] parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
            if (!int.TryParse(parts[1], out int iterations) || iterations <= 0) return false;
            byte[] salt, expected;
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                expected = Convert.FromBase64String(parts[3]);
            }
            catch { return false; }
            byte[] actual = Pbkdf2(password, salt, iterations);
            if (actual.Length != expected.Length) return false;
            int diff = 0;
            for (int i = 0; i < actual.Length; i++) diff |= actual[i] ^ expected[i];
            return diff == 0;
        }

        /// <summary>口令强度检查（与客户端同口径）。</summary>
        public static string Validate(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 6) return "密码应该是6位或以上的字母或数字，请重新输入！";
            foreach (char c in password)
            {
                if (!char.IsLetterOrDigit(c)) return "密码只能是字母或数字，请重新输入！";
            }
            string[] weak = { "123456", "000000", "111111", "12345678", "888888", "666666", "abc123", "1234567890", "password" };
            foreach (string w in weak)
            {
                if (string.Equals(w, password, StringComparison.OrdinalIgnoreCase)) return "密码太简单，请重新输入！";
            }
            return null;
        }

        private static byte[] Pbkdf2(string password, byte[] salt, int iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations))
            {
                return kdf.GetBytes(HashSize);
            }
        }
    }
}
