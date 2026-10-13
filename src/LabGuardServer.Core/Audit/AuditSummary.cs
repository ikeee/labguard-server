using System.Collections.Generic;
using System.Linq;
using System.Text;
using LabGuardServer.Core.Catalog;
using LabGuardServer.Core.Policy;

namespace LabGuardServer.Core.Audit
{
    /// <summary>
    /// 把"这次到底改了什么"变成**一句人看得懂、又不含敏感值**的审计摘要（执行计划 §G1：
    /// "不记录密码、私钥或敏感策略值"）。
    ///
    /// 口径：**只写开关的中文名与"开/关/（空）"这类可读值 + 字段路径**，绝不把整份策略 JSON 塞进日志。
    /// 理由有两条：① 口令散列之类就在策略 JSON 里，整份塞进去等于把机密抄进日志；
    /// ② 老师排障要的是"谁把 U 盘放开了"，不是一坨 JSON。
    /// </summary>
    public static class AuditSummary
    {
        /// <summary>策略发布的摘要：改了 N 项（列前若干项）。</summary>
        public static string PolicyChanges(Dictionary<string, object> oldPolicy,
            Dictionary<string, object> newPolicy, IEnumerable<CatalogField> catalogFields)
        {
            if (catalogFields == null)
                return "策略已更新（本机没有开关目录快照，无法列出改动项）";
            List<PolicyChange> changes = PolicyDiff.Compare(oldPolicy, newPolicy, catalogFields);
            if (changes.Count == 0) return "策略已重新下发（内容与上一版一致）";
            var sb = new StringBuilder();
            sb.Append("改动 ").Append(changes.Count).Append(" 项：");
            foreach (PolicyChange c in changes.Take(6))
            {
                sb.Append(c.Label).Append('（').Append(c.Path).Append("）")
                  .Append(' ').Append(c.OldText).Append('→').Append(c.NewText).Append("；");
            }
            if (changes.Count > 6) sb.Append("…还有 ").Append(changes.Count - 6).Append(" 项");
            if (changes.Any(c => c.Dangerous)) sb.Append("【含危险项】");
            return sb.ToString();
        }
    }
}
