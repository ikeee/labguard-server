using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LabGuardServer.Core.Audit
{
    /// <summary>
    /// CSV 输出（执行计划 §G2）：**明确编码（UTF-8 带 BOM，Excel 直接认中文）+ 正确转义 +
    /// 防电子表格公式注入**。三条都不是"讲究"，是踩过的坑：
    ///   · 不带 BOM 的 UTF-8，中文在 Excel 里是乱码（用户会以为数据坏了）；
    ///   · 字段里带逗号/引号/换行不转义，整张表就错列；
    ///   · 以 <c>= + - @</c>（以及 Tab/回车）开头的字段会被 Excel 当公式执行 ——
    ///     机器名/说明文字是"外部可控"的（学生能改机器名），这就是一个**注入面**：
    ///     `=cmd|' /C calc'!A0` 这类载荷在老师双击 CSV 时执行。按 OWASP 的口径，
    ///     在这些值前面加一个单引号让它变成纯文本。
    /// </summary>
    public static class Csv
    {
        /// <summary>转义单个字段：先做公式注入防护，再做引号/逗号/换行的标准转义。</summary>
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string v = s;
            // 公式注入防护：这些字符开头会被 Excel/LibreOffice 当公式
            char c0 = v[0];
            if (c0 == '=' || c0 == '+' || c0 == '-' || c0 == '@' || c0 == '\t' || c0 == '\r')
                v = "'" + v;
            if (v.IndexOf('"') >= 0 || v.IndexOf(',') >= 0 || v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0)
                v = "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        /// <summary>一行（字段自动转义，CRLF 结尾）。</summary>
        public static string Line(params object[] fields)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < (fields?.Length ?? 0); i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Escape(fields[i] == null ? "" : Convert.ToString(fields[i],
                    System.Globalization.CultureInfo.InvariantCulture)));
            }
            sb.Append("\r\n");
            return sb.ToString();
        }

        /// <summary>写文件：UTF-8 **带 BOM**（Excel 认中文的关键）+ CRLF + 原子写。</summary>
        public static void WriteFile(string path, IEnumerable<string> lines)
        {
            var sb = new StringBuilder();
            if (lines != null) foreach (string l in lines) sb.Append(l);
            var utf8Bom = new UTF8Encoding(true);
            Util.AtomicFile.WriteAllBytes(path, utf8Bom.GetPreamble().Length > 0
                ? Concat(utf8Bom.GetPreamble(), utf8Bom.GetBytes(sb.ToString()))
                : utf8Bom.GetBytes(sb.ToString()));
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }
    }
}
