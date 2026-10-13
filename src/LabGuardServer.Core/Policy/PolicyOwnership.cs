using System;
using System.Collections.Generic;
using System.Linq;

namespace LabGuardServer.Core.Policy
{
    /// <summary>策略分组的归属层（谁执行 / 谁决策）。</summary>
    public enum PolicyTier
    {
        /// <summary>课堂管控：老师在教师机上按课/按班调的开关，下发给学生机执行（老师的主战场）。</summary>
        Classroom = 1,
        /// <summary>学生端自保与环境：**由学生机自己执行**的保护（电子教室保护 / 进程防拆 / 注册表加固 / 机位标识）。
        /// 装机时定一次、之后基本不动；老师一般不需要碰，改之前要知道后果。</summary>
        SelfProtect = 2,
        /// <summary>教师端自身：不下发给学生机（学生机上会保留自己的值），因此**不在策略面板里出现**。</summary>
        ServerLocal = 3,
    }

    /// <summary>
    /// 职责归属表（2026-10-12 定稿）：把客户端导出的每个分组归到某一层，并说明"谁在干活"。
    ///
    /// 为什么需要它：服务端**不执行任何管控动作**（只签发/下发策略 + 看在线状态），
    /// 全部 Guard 都跑在学生机上。但面板把 122 个开关平铺时，老师分不清
    /// "这节课我要调的上网/USB" 和 "学生机自己的自保与环境适配"——归属写清楚，界面才讲道理。
    ///
    /// 判定口径（举一反三的依据）：
    ///   · 影响**学生在机器上能做什么** → 课堂管控（老师决策，学生机执行）；
    ///   · 保护**管控程序自己活着 / 适配本机环境** → 学生端自保（学生机自己执行，老师一般不动）；
    ///   · 描述**本机与教师机的连接**（地址/轮询/心跳） → 教师端自身（学生机本机配置，不下发）。
    /// </summary>
    public static class PolicyOwnership
    {
        /// <summary>分组 → 归属层。键用分组名的**前缀数字**匹配（分组名形如 "2、网络与防火墙"）。</summary>
        private static readonly Dictionary<string, PolicyTier> ByPrefix = new Dictionary<string, PolicyTier>(StringComparer.Ordinal)
        {
            // ---- 课堂管控（老师常改）
            { "0、", PolicyTier.Classroom },    // 总体（总开关 / 开机延迟 / 口令等）
            { "2、", PolicyTier.Classroom },    // 网络与防火墙
            { "3、", PolicyTier.Classroom },    // 集中 DNS
            { "4、", PolicyTier.Classroom },    // 违规软件拦截
            { "5、", PolicyTier.Classroom },    // 新文件 / 下载监控
            { "6、", PolicyTier.Classroom },    // USB 存储设备
            { "7、", PolicyTier.Classroom },    // hosts 黑名单
            { "8、", PolicyTier.Classroom },    // 浏览器管控
            { "9、", PolicyTier.Classroom },    // 任务栏与系统工具
            { "10、", PolicyTier.Classroom },   // 安全模式与启动菜单
            { "16、", PolicyTier.Classroom },   // 防会话逃逸（换用户/新会话）—— 影响"学生能做什么"，老师决策

            // ---- 学生端自保与环境（学生机自己执行；装机定一次）
            { "1、", PolicyTier.SelfProtect },  // 电子教室保护（保住极域/红蜘蛛/锐捷/噢易不被学生脱离）
            { "11、", PolicyTier.SelfProtect }, // 机位标识（本机显示机位号，不改系统壁纸）
            { "12、", PolicyTier.SelfProtect }, // 注册表权限加固（守住 HKLM\SOFTWARE\LabGuard）
            { "13、", PolicyTier.SelfProtect }, // 互相守护与自我保护
            { "14、", PolicyTier.SelfProtect }, // 进程防拆加固（进程加锁/秒级复活/兜底任务）

            // ---- 教师端自身（不下发，面板里不出现）
            { "15、", PolicyTier.ServerLocal }, // 服务器统一管控（地址/轮询/心跳 = 学生机本机配置）
        };

        public static PolicyTier Of(string section)
        {
            if (string.IsNullOrEmpty(section)) return PolicyTier.Classroom;
            foreach (var kv in ByPrefix)
                if (section.StartsWith(kv.Key, StringComparison.Ordinal)) return kv.Value;
            return PolicyTier.Classroom;      // 未知分组按老师可控处理（宁可显示，不要默默丢掉）
        }

        /// <summary>是否在策略面板里显示（教师端自身的分组不显示：学生机不接收、改了也没用）。</summary>
        public static bool VisibleInPanel(string section)
        {
            return Of(section) != PolicyTier.ServerLocal;
        }

        public static string TierTitle(PolicyTier tier)
        {
            switch (tier)
            {
                case PolicyTier.SelfProtect: return "学生端自保与环境（学生机自己执行 · 装机定一次）";
                case PolicyTier.ServerLocal: return "教师端自身（不下发给学生机）";
                default: return "课堂管控（老师常改 · 下发给学生机执行）";
            }
        }

        public static string TierHint(PolicyTier tier)
        {
            switch (tier)
            {
                case PolicyTier.SelfProtect:
                    return "这一层是**学生机自己执行**的保护与环境适配：电子教室保护（保住极域/红蜘蛛/锐捷/噢易不被学生脱离）、"
                         + "进程防拆加固（小助手杀不掉、被杀秒级复活）、注册表加固、机位标识。装机时定一次即可；"
                         + "关掉它们等于放弃学生机的自保能力，请确认场合。";
                case PolicyTier.ServerLocal:
                    return "这些是**学生机本机**与教师机的连接配置（服务器地址/轮询间隔/心跳），由学生机自己保存，"
                         + "服务端不下发这些值，因此策略面板里不显示——配对用学生机的 install.ps1 -Pair 或 --pair。";
                default:
                    return "这一层是老师按课/按班调的管控：上网与 DNS、违规软件、下载与 USB、浏览器、任务栏与系统工具等。"
                         + "改完点「保存并下发」，学生机默认 5 秒内生效。";
            }
        }

        /// <summary>按归属层筛出分组（保持目录里的原始顺序；只返回面板该显示的）。</summary>
        public static List<string> SectionsOf(IEnumerable<string> allSections, PolicyTier tier)
        {
            return (allSections ?? Enumerable.Empty<string>())
                .Where(s => Of(s) == tier && VisibleInPanel(s))
                .ToList();
        }

        /// <summary>自检用：表里每个前缀都必须能匹配到目录里某个分组（防止客户端改分组名后归属静默失效）。</summary>
        public static List<string> UnmatchedPrefixes(IEnumerable<string> allSections)
        {
            List<string> sections = (allSections ?? Enumerable.Empty<string>()).ToList();
            return ByPrefix.Keys.Where(prefix => !sections.Any(s => s.StartsWith(prefix, StringComparison.Ordinal))).ToList();
        }

        /// <summary>自检用：目录里有没有**没被归属表覆盖**的分组。</summary>
        public static List<string> UnclassifiedSections(IEnumerable<string> allSections)
        {
            return (allSections ?? Enumerable.Empty<string>())
                .Where(s => !ByPrefix.Keys.Any(prefix => s.StartsWith(prefix, StringComparison.Ordinal)))
                .ToList();
        }
    }
}
