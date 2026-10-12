using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LabGuardServer.Core.Catalog;
using LabGuardServer.Core.Util;

namespace LabGuardServer.Core.Policy
{
    /// <summary>一条策略改动（教师端"发布前差异预览"用）。</summary>
    public sealed class PolicyChange
    {
        public string Section;
        public string Label;
        public string Path;
        public bool Dangerous;
        public string OldText;
        public string NewText;

        public override string ToString()
        {
            return (string.IsNullOrEmpty(Section) ? "" : "[" + Section + "] ") + Label + "：" +
                   OldText + " → " + NewText;
        }
    }

    /// <summary>
    /// 策略差异比较（发布前让老师看清"这次改了什么"，避免手滑下发）。
    /// 只比**目录里存在的开关**（不比对隐藏字段），数组/布尔/字符串各有可读格式化。
    /// </summary>
    public static class PolicyDiff
    {
        public static List<PolicyChange> Compare(Dictionary<string, object> oldPolicy,
            Dictionary<string, object> newPolicy, IEnumerable<CatalogField> fields)
        {
            var changes = new List<PolicyChange>();
            if (fields == null) return changes;
            foreach (CatalogField f in fields)
            {
                object o = PathAccess.Get(oldPolicy, f.Path);
                object n = PathAccess.Get(newPolicy, f.Path);
                if (PolicySearch.SameValue(o, n)) continue;
                changes.Add(new PolicyChange
                {
                    Section = f.Section,
                    Label = f.Label,
                    Path = f.Path,
                    Dangerous = f.Dangerous,
                    OldText = Format(o),
                    NewText = Format(n),
                });
            }
            return changes;
        }

        /// <summary>值的可读文本（老师看得懂的形式，不是 JSON）。</summary>
        public static string Format(object v)
        {
            if (v == null) return "（空）";
            if (v is bool b) return b ? "开" : "关";
            if (v is string s) return string.IsNullOrWhiteSpace(s) ? "（空）" : s.Trim();
            if (v is System.Collections.IEnumerable en && !(v is string))
            {
                List<string> items = en.Cast<object>().Select(x => x == null ? "" : x.ToString().Trim())
                    .Where(x => x.Length > 0).ToList();
                if (items.Count == 0) return "（空）";
                if (items.Count <= 4) return string.Join("、", items);
                return string.Join("、", items.Take(3)) + "…（共 " + items.Count + " 项）";
            }
            if (v is IConvertible)
            {
                try { return Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture); }
                catch { }
            }
            return v.ToString();
        }
    }
}
