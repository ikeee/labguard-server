using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Util;

namespace LabGuardServer.Core.Policy
{
    /// <summary>一个可一键应用的临时课堂状态预设。</summary>
    public sealed class TempPreset
    {
        public string Name;
        public string Description;
        /// <summary>默认时长（分钟）；0 = 不自动到期（要老师手动结束）。</summary>
        public int DurationMinutes;
        /// <summary>是否同时全员暂停（临时暂停：到点自动恢复管控）。</summary>
        public bool Paused;
        /// <summary>相对**基础策略**的覆盖（部分策略 JSON）。</summary>
        public Dictionary<string, object> Patch;
    }

    /// <summary>
    /// 临时课堂状态预设表（<c>temp-presets.json</c>）。
    ///
    /// **内置 3 个只是"示例起点"，不是产品语义**：每个机房的"考试模式该锁什么"都不一样，
    /// 所以这份表就是普通 JSON，老师（或以后的功能）直接改文件即可——文件不存在时自动写一份内置的，
    /// 存在的就**以文件为准**（不会被程序覆盖）。真正灵活的路子是策略面板上的「临时生效」：
    /// 老师自己改哪些项，就临时改哪些项。
    /// </summary>
    public static class TempPresets
    {
        public const string FileName = "temp-presets.json";
        private const int SchemaVersion = 1;

        public static string PathOf(string dataDir)
        {
            return System.IO.Path.Combine(dataDir, FileName);
        }

        /// <summary>内置示例（同时是"文件不存在时"自动落盘的内容）。</summary>
        public const string BuiltinJson =
            "{\"schemaVersion\":1,\"presets\":[" +
            "{\"name\":\"考试模式\",\"description\":\"临时收紧：锁 U 盘存储、收紧浏览器、启用进程管控\"," +
            "\"durationMinutes\":90,\"paused\":false," +
            "\"patch\":{\"Usb\":{\"Enabled\":true,\"AllowStorage\":false}," +
            "\"Browser\":{\"Enabled\":true},\"ProcessBlock\":{\"Enabled\":true}}}," +
            "{\"name\":\"自由练习\",\"description\":\"临时放开：允许 U 盘、关浏览器限制、关进程管控\"," +
            "\"durationMinutes\":60,\"paused\":false," +
            "\"patch\":{\"Usb\":{\"Enabled\":false,\"AllowStorage\":true}," +
            "\"Browser\":{\"Enabled\":false},\"ProcessBlock\":{\"Enabled\":false}," +
            "\"Classroom\":{\"Enabled\":false}}}," +
            "{\"name\":\"全员暂停\",\"description\":\"临时暂停管控（30 分钟后自动恢复基础策略）\"," +
            "\"durationMinutes\":30,\"paused\":true,\"patch\":{}}" +
            "]}";

        /// <summary>
        /// 读预设表：文件不存在 → 写一份内置示例并返回它；存在且合法 → 以文件为准；
        /// 存在但坏掉 → 告警并**只**用内置示例（绝不覆盖老师的文件）。
        /// </summary>
        public static List<TempPreset> Load(string dataDir)
        {
            string path = PathOf(dataDir);
            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(dataDir);
                    AtomicFile.WriteAllText(path, BuiltinJson);
                    Log.Info("已生成临时课堂状态预设示例：" + path + "（老师可直接编辑这个文件）");
                }
                else
                {
                    var parsed = Parse(File.ReadAllText(path, Encoding.UTF8));
                    if (parsed != null) return parsed;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取临时课堂状态预设失败（用内置示例兜底，不动你的文件）：" + ex.Message);
            }
            return Parse(BuiltinJson) ?? new List<TempPreset>();
        }

        /// <summary>解析预设 JSON；结构不合法返回 null（由调用方决定兜底策略）。</summary>
        public static List<TempPreset> Parse(string json)
        {
            var doc = Json.ParseObject(json);
            if (doc == null) return null;
            int schema = doc.TryGetValue("schemaVersion", out object sv) && sv is IConvertible ? Convert.ToInt32(sv) : 0;
            if (schema != SchemaVersion) { Log.Warn("预设文件 schemaVersion 不认识：" + schema); return null; }
            // 注意：JavaScriptSerializer 解析出来的数组既可能是 object[]，也可能是 ArrayList（取决于目标类型），
            // 一律按 IEnumerable 处理（字符串要排除——它也是 IEnumerable）。
            if (!doc.TryGetValue("presets", out object raw) ||
                !(raw is System.Collections.IEnumerable items) || raw is string) return null;
            var list = new List<TempPreset>();
            foreach (object o in items)
            {
                var m = o as Dictionary<string, object>;
                if (m == null) continue;
                string name = m.TryGetValue("name", out object n) ? n as string : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var p = new TempPreset
                {
                    Name = name.Trim(),
                    Description = m.TryGetValue("description", out object d) ? d as string : null,
                    DurationMinutes = m.TryGetValue("durationMinutes", out object dm) && dm is IConvertible
                        ? Math.Max(0, Convert.ToInt32(dm)) : 0,
                    Paused = m.TryGetValue("paused", out object pz) && pz is bool b && b,
                    Patch = m.TryGetValue("patch", out object pt) ? pt as Dictionary<string, object> : null,
                };
                if (p.Patch == null) p.Patch = new Dictionary<string, object>();
                list.Add(p);
            }
            return list;
        }

        /// <summary>按名字找预设（去首尾空格、忽略大小写）。</summary>
        public static TempPreset Find(List<TempPreset> presets, string name)
        {
            if (presets == null || string.IsNullOrWhiteSpace(name)) return null;
            return presets.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }
}
