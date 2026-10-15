using System;
using System.Collections.Generic;
using LabGuardServer.Core.Util;

namespace LabGuardServer.Core.Policy
{
    /// <summary>
    /// **「开了但不会生效」检查**（2026-10-15 起客户端出厂口径 = 全关之后新增）。
    ///
    /// 客户端每个模块都先判"自己那一组的总闸"（<c>Xxx.Enabled</c>）再干活。出厂全关之后，
    /// 老师在面板上很容易只点开一个子开关（例如「禁快速切换用户」）而忘了打开它那组的总闸
    /// （「防会话逃逸」）—— 结果是**面板上看着开了、学生机上什么也没发生**。
    /// 这条检查把这个坑在"下发确认"里直接说出来，而不是让老师到教室里去发现。
    ///
    /// 只报"这次改动里**新打开**的、且所属组总闸是关的"那些项（不是把所有 true 都报一遍）：
    /// 修饰性开关（如「允许 USB 存储」）在某组关着时本来就无所谓，全报只会变噪音。
    /// </summary>
    public static class PolicyGate
    {
        /// <summary>
        /// 组总闸表（分组键 → 总闸路径）。只列"总闸真的一票否决"的分组。
        ///
        /// **故意不含 Hosts**：客户端的 hosts 模块是 `Hosts.Enabled || Hosts.ProtectFileAcl` 的"或"语义
        /// （见客户端 HostsGuard），单独打开「hosts 只读加固」是**有效**的 —— 写进表里会把有效配置误报成不生效。
        /// **不含 Site（机位标识）**：那一组没有总闸，两个子项各自独立生效。
        /// </summary>
        private static readonly Dictionary<string, string> Gates =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "Classroom", "Classroom.Enabled" },
                { "Network", "Network.Enabled" },
                { "ProcessBlock", "ProcessBlock.Enabled" },
                { "FileCreation", "FileCreation.Enabled" },
                { "Usb", "Usb.Enabled" },
                { "Browser", "Browser.Enabled" },
                { "Shell", "Shell.Enabled" },
                { "Session", "Session.Enabled" },
                { "SafeMode", "SafeMode.Enabled" },
                { "RegistryAcl", "RegistryAcl.Enabled" },
                { "Watchdog", "Watchdog.Enabled" },
                { "AntiTamper", "AntiTamper.Enabled" },
            };

        public static bool IsGroupGate(string path)
        {
            return !string.IsNullOrEmpty(path) && path.EndsWith(".Enabled", StringComparison.Ordinal);
        }

        /// <summary>返回给老师看的提示行（可能为空 = 这次改动没有"开了不生效"的项）。</summary>
        public static List<string> Warnings(List<PolicyChange> changes, Dictionary<string, object> policy)
        {
            var warn = new List<string>();
            if (policy == null) return warn;

            // ① 总闸关了：整台机器不受管（单项全开也没用）
            object total = PathAccess.Get(policy, "Enabled");
            if (total is bool b && !b)
                warn.Add("⚠ 总闸「启用监控（总开关）」是关的 —— 下面所有策略都不会生效。");

            // ② 这次新打开的项，所属组总闸是关的
            if (changes != null)
            {
                foreach (PolicyChange c in changes)
                {
                    if (c == null || c.NewText != "开") continue;       // 只看"这次打开"
                    if (IsGroupGate(c.Path)) continue;                  // 打开总闸本身不算
                    int dot = (c.Path ?? "").IndexOf('.');
                    if (dot <= 0) continue;
                    string group = c.Path.Substring(0, dot);
                    string gate;
                    if (!Gates.TryGetValue(group, out gate)) continue;   // 没有总闸的分组（Hosts/Site）跳过
                    object gateVal = PathAccess.Get(policy, gate);
                    bool gateOn = gateVal is bool gv && gv;
                    if (!gateOn)
                        warn.Add("⚠「" + c.Label + "」这次打开了，但它所属分组的总闸（" + gate +
                                 "）是关的 → 学生机上不会生效。");
                }
            }
            return warn;
        }
    }
}
