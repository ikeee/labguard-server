using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LabGuardServer.Core.Api
{
    /// <summary>一个 IPv4 段（闭区间）。</summary>
    public sealed class IpRange
    {
        public uint Start;
        public uint End;
        public string Source;      // 老师写的原文（显示用）

        public bool Contains(uint ip) { return ip >= Start && ip <= End; }
        public override string ToString() { return Source; }
    }

    /// <summary>
    /// **本机房 IP 段**（v0.14）：用来回答"这台设备是不是我这间房的"。
    ///
    /// 为什么教师端需要它：两间机房在同一网段时，最怕的是**装错配对文件**（或 IP 配错）——
    /// 学生会跑到另一间房的教师机下面，而两边"看起来都正常"。
    /// 教师端知道自己的地址段之后，就能把"不属于本房"的设备标出来，让错误当场可见。
    ///
    /// **注意它只是"体检"，不是"拦截"**：真正的隔离靠配对时的公钥 pin
    /// （学生端只认自己 pin 的那把公钥，别的教师机发策略一律验签失败）。
    ///
    /// 支持的写法（逗号分隔多段）：
    ///   <c>10.46.254.11-99</c>（只写末段，最常用）
    ///   <c>10.46.254.11-10.46.254.99</c>
    ///   <c>10.46.254.10</c>（单个地址）
    /// </summary>
    public static class RoomScope
    {
        /// <summary>解析地址段；写法不对的项**返回空串**到 <paramref name="invalid"/>（不抛异常，老师写错不该让服务端起不来）。</summary>
        public static List<IpRange> Parse(string spec, out List<string> invalid)
        {
            var list = new List<IpRange>();
            invalid = new List<string>();
            if (string.IsNullOrWhiteSpace(spec)) return list;
            foreach (string raw in spec.Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = raw.Trim();
                if (s.Length == 0) continue;
                int dash = s.IndexOf('-');
                try
                {
                    if (dash < 0)
                    {
                        uint ip = ToUint(s);
                        list.Add(new IpRange { Start = ip, End = ip, Source = s });
                        continue;
                    }
                    string left = s.Substring(0, dash).Trim();
                    string right = s.Substring(dash + 1).Trim();
                    uint a = ToUint(left);
                    // "10.46.254.11-99"：右边只有一段数字 → 沿用左边前三段（老师最常这么写）
                    uint b = right.IndexOf('.') < 0 ? Combine(left, right) : ToUint(right);
                    if (b < a) { uint t = a; a = b; b = t; }         // 写反了也认（a-b 与 b-a 等价）
                    list.Add(new IpRange { Start = a, End = b, Source = s });
                }
                catch
                {
                    invalid.Add(s);
                }
            }
            return list;
        }

        /// <summary>IP 是否落在任一段内。段为空 ⇒ 恒 true（没配期望段 = 不做越界判定，别误报）。</summary>
        public static bool Contains(IEnumerable<IpRange> ranges, string ip)
        {
            List<IpRange> list = ranges?.ToList() ?? new List<IpRange>();
            if (list.Count == 0) return true;
            uint v;
            if (!TryToUint(ip, out v)) return false;      // 认不出的地址（含 IPv6）→ 算越界，值得老师看一眼
            return list.Any(r => r.Contains(v));
        }

        /// <summary>本机是否在期望段内（教师机自检用；多网卡只要有一张在段内就算在）。</summary>
        public static bool SelfInScope(IEnumerable<IpRange> ranges, IEnumerable<string> localIps)
        {
            List<IpRange> list = ranges?.ToList() ?? new List<IpRange>();
            if (list.Count == 0) return true;
            return (localIps ?? Enumerable.Empty<string>()).Any(ip => Contains(list, ip));
        }

        public static string Describe(IEnumerable<IpRange> ranges)
        {
            List<IpRange> list = ranges?.ToList() ?? new List<IpRange>();
            if (list.Count == 0) return "（未设置：不做越界判定）";
            var sb = new StringBuilder();
            foreach (IpRange r in list)
            {
                if (sb.Length > 0) sb.Append("，");
                sb.Append(r.Source);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ IPv4 文本 ↔ uint

        public static uint ToUint(string ip)
        {
            uint v;
            if (!TryToUint(ip, out v))
                throw new FormatException("不是合法 IPv4：" + ip);
            return v;
        }

        public static bool TryToUint(string ip, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(ip)) return false;
            string[] parts = ip.Trim().Split('.');
            if (parts.Length != 4) return false;
            uint acc = 0;
            foreach (string p in parts)
            {
                int n;
                if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return false;
                if (n < 0 || n > 255) return false;
                acc = (acc << 8) | (uint)n;
            }
            value = acc;
            return true;
        }

        /// <summary>把 "10.46.254.11" 的前三段 + "99" 拼成 "10.46.254.99"。</summary>
        private static uint Combine(string reference, string lastOctet)
        {
            string[] parts = reference.Split('.');
            if (parts.Length != 4) throw new FormatException("需要 4 段 IPv4：" + reference);
            int n;
            if (!int.TryParse(lastOctet, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 0 || n > 255)
                throw new FormatException("末段不是 0-255：" + lastOctet);
            return ToUint(parts[0] + "." + parts[1] + "." + parts[2] + "." + n);
        }
    }
}
