using System;
using System.Collections.Generic;
using System.Linq;
using LabGuardServer.Core.Catalog;

namespace LabGuardServer.Core.Policy
{
    /// <summary>
    /// 策略检索（教师端"快速找到对应策略"的核心）：按关键词在**分组名 / 标签 / 提示 / 路径**里找。
    ///
    /// 匹配规则：关键词按空白切成多个词，**每个词都必须出现**（AND）；不区分大小写。
    /// 老师敲"USB"能同时命中"6、USB存储设备"整组与别处提到 U 盘的开关；
    /// 敲"DNS 锁"这种多词也能收敛。空关键词 = 全部（等价于"不筛选"）。
    /// </summary>
    public static class PolicySearch
    {
        public static bool Matches(CatalogField f, string keyword)
        {
            if (f == null) return false;
            if (string.IsNullOrWhiteSpace(keyword)) return true;
            string haystack = string.Join("\u0001", new[]
            {
                f.Section ?? "", f.Label ?? "", f.Hint ?? "", f.Path ?? "",
            });
            foreach (string token in keyword.Split(new[] { ' ', '\t', '　' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }

        public static List<CatalogField> Filter(IEnumerable<CatalogField> fields, string keyword)
        {
            if (fields == null) return new List<CatalogField>();
            return fields.Where(f => Matches(f, keyword)).ToList();
        }

        /// <summary>某分组在"已改动/未改动"下的计数（左栏显示 "网络与防火墙 (23 项 · 改 2)"）。</summary>
        public static int CountChanged(IEnumerable<CatalogField> fields, Dictionary<string, object> policy,
            Dictionary<string, object> baseline)
        {
            int n = 0;
            foreach (CatalogField f in fields)
            {
                object a = Util.PathAccess.Get(policy, f.Path);
                object b = Util.PathAccess.Get(baseline, f.Path);
                if (!SameValue(a, b)) n++;
            }
            return n;
        }

        /// <summary>值比较（数组按元素比，字符串忽略大小写，数字按数值比）。</summary>
        public static bool SameValue(object a, object b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a is System.Collections.IEnumerable ea && !(a is string) &&
                b is System.Collections.IEnumerable eb && !(b is string))
            {
                List<string> la = ea.Cast<object>().Select(x => x == null ? "" : x.ToString()).ToList();
                List<string> lb = eb.Cast<object>().Select(x => x == null ? "" : x.ToString()).ToList();
                return la.Count == lb.Count && !la.Where((t, i) =>
                    !string.Equals(t, lb[i], StringComparison.OrdinalIgnoreCase)).Any();
            }
            if (a is IConvertible && b is IConvertible && !(a is string) && !(b is string))
            {
                try { return Convert.ToDecimal(a) == Convert.ToDecimal(b); } catch { }
            }
            return string.Equals(a.ToString().Trim(), b.ToString().Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
