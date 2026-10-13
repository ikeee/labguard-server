using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LabGuardServer.Core.Policy
{
    /// <summary>
    /// 策略**深合并**（临时课堂状态层的唯一合并规则，执行计划 §F3）。
    ///
    /// 规则（确定、可测、有文档——只此三条，不许有例外）：
    ///   1. **对象 与 对象 → 递归合并**（键逐个合并，未出现的键保持 base 原值）；
    ///   2. **其余一律整键替换**：数组整体替换（不是追加）、字符串/数字/布尔整体替换；
    ///   3. **patch 里出现的键才会变**——patch 没有的键，base 一个都不动（这就是"覆盖"而不是"重写"）。
    ///
    /// 为什么不做数组追加：策略里的数组（进程黑名单、DNS 列表、监控路径…）都是"一整套口径"，
    /// 追加会让老师无法通过临时覆盖**减少**条目（比如考试模式临时只放行 3 个进程），
    /// 而且合并结果依赖 base 的既有内容、无法预测。整键替换符合"临时覆盖 = 换一套值"的直觉。
    /// </summary>
    public static class PolicyMerge
    {
        /// <summary>base ⊕ patch（都不修改入参；patch 为空/为 null 时返回 base 的等价副本）。</summary>
        public static Dictionary<string, object> Merge(Dictionary<string, object> basePolicy, Dictionary<string, object> patch)
        {
            var result = basePolicy == null
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                : CloneObject(basePolicy);
            if (patch == null) return result;
            foreach (KeyValuePair<string, object> kv in patch) ApplyInto(result, kv.Key, kv.Value);
            return result;
        }

        private static void ApplyInto(Dictionary<string, object> target, string key, object value)
        {
            if (key == null) return;
            var patchObj = value as Dictionary<string, object>;
            if (patchObj != null && target.TryGetValue(key, out object cur) && cur is Dictionary<string, object> curObj)
            {
                foreach (KeyValuePair<string, object> kv in patchObj) ApplyInto(curObj, kv.Key, kv.Value);
                return;
            }
            target[key] = CloneValue(value);   // 规则 2/3：整键替换
        }

        // ------------------------------------------------------------------ 深拷贝（避免 merge 结果与入参共享子对象）

        private static Dictionary<string, object> CloneObject(Dictionary<string, object> src)
        {
            var copy = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> kv in src) copy[kv.Key] = CloneValue(kv.Value);
            return copy;
        }

        private static object CloneValue(object v)
        {
            var d = v as Dictionary<string, object>;
            if (d != null) return CloneObject(d);
            if (v is object[] arr)
            {
                var copy = new object[arr.Length];
                for (int i = 0; i < arr.Length; i++) copy[i] = CloneValue(arr[i]);
                return copy;
            }
            if (v is ArrayList al)
            {
                var copy = new ArrayList(al.Count);
                foreach (object o in al) copy.Add(CloneValue(o));
                return copy;
            }
            return v;
        }

        // ------------------------------------------------------------------ 规范化摘要（自检断言 / 差异预览用）

        /// <summary>
        /// 结构化摘要：**键有序、类型带标记**，所以"键顺序不同但内容相同"的两份策略摘要完全一致。
        /// 用途：断言合并的确定性与幂等性；给界面做"变了几项"的比对底稿。
        /// </summary>
        public static string Dump(object node)
        {
            var sb = new StringBuilder();
            DumpInto(sb, node);
            return sb.ToString();
        }

        private static void DumpInto(StringBuilder sb, object node)
        {
            if (node == null) { sb.Append("null"); return; }
            var d = node as Dictionary<string, object>;
            if (d != null)
            {
                sb.Append('{');
                var keys = new List<string>(d.Keys);
                keys.Sort(StringComparer.Ordinal);
                bool first = true;
                foreach (string k in keys)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Escape(k)).Append(':');
                    DumpInto(sb, d[k]);
                }
                sb.Append('}');
                return;
            }
            if (node is object[] arr) { DumpArray(sb, arr); return; }
            if (node is ArrayList al) { DumpArray(sb, al.ToArray()); return; }
            if (node is string s) { sb.Append(Escape(s)); return; }
            if (node is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (node is IConvertible conv && !(node is DateTime))
            {
                // 数字统一成不变文化（避免 1 与 1.0 在不同机器上格式不同）
                sb.Append(Convert.ToDouble(conv, CultureInfo.InvariantCulture)
                    .ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            sb.Append(Escape(Convert.ToString(node, CultureInfo.InvariantCulture)));
        }

        private static void DumpArray(StringBuilder sb, object[] arr)
        {
            sb.Append('[');
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(',');
                DumpInto(sb, arr[i]);
            }
            sb.Append(']');
        }

        private static string Escape(string s)
        {
            return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
