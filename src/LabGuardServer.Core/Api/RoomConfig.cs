using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Util;

namespace LabGuardServer.Core.Api
{
    /// <summary>
    /// 本机房信息（v0.14）：教室名 + 期望的 IP 段。存数据目录的 <c>room.json</c>。
    ///
    /// 用途只有一个：**让"学生装错配对/String 错 IP"当场可见**（两间机房同一网段时最怕这个）。
    /// 不配也能用（不做越界判定），配了就多一道体检。
    /// </summary>
    public sealed class RoomConfig
    {
        public const string FileName = "room.json";
        public const int SchemaVersion = 1;

        /// <summary>教室名 / 机房编号（如 "A202"）。空 = 没设。</summary>
        public string Room { get; set; } = "";

        /// <summary>期望的 IP 段原文（如 "10.46.254.11-99"）。</summary>
        public string ExpectIps { get; set; } = "";

        /// <summary>解析出来的段（不落盘，Load 时重算）。</summary>
        public List<IpRange> Ranges { get; private set; } = new List<IpRange>();

        /// <summary>写法不合法、被忽略的项（界面/命令行提示用）。</summary>
        public List<string> InvalidSpecs { get; private set; } = new List<string>();

        public static string PathOf(string dataDir) { return Path.Combine(dataDir, FileName); }

        public void RefreshRanges()
        {
            List<string> invalid;
            Ranges = RoomScope.Parse(ExpectIps, out invalid);
            InvalidSpecs = invalid;
        }

        public bool IsOutOfScope(string ip)
        {
            return !RoomScope.Contains(Ranges, ip);
        }

        public static RoomConfig Load(string dataDir)
        {
            var cfg = new RoomConfig();
            try
            {
                string path = PathOf(dataDir);
                if (File.Exists(path))
                {
                    var doc = new JavaScriptSerializer { MaxJsonLength = 256 * 1024 }
                        .Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                    if (doc != null)
                    {
                        cfg.Room = Str(doc, "room");
                        cfg.ExpectIps = Str(doc, "expectIps");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取本机房信息失败（按未设置处理）：" + ex.Message);
            }
            cfg.RefreshRanges();
            return cfg;
        }

        public void Save(string dataDir)
        {
            var doc = new Dictionary<string, object>
            {
                { "schemaVersion", SchemaVersion },
                { "room", Room ?? "" },
                { "expectIps", ExpectIps ?? "" },
                { "updatedUtc", DateTime.UtcNow.ToString("o") },
            };
            Directory.CreateDirectory(dataDir);
            AtomicFile.WriteAllText(PathOf(dataDir), Json.Stringify(doc));
            RefreshRanges();
        }

        /// <summary>一行摘要（托盘/命令行都显示这个）。</summary>
        public string Describe()
        {
            string name = string.IsNullOrWhiteSpace(Room) ? "（未设置教室名）" : Room;
            string scope = RoomScope.Describe(Ranges);
            string bad = InvalidSpecs.Count > 0 ? "；⚠ 写法不认识的项：" + string.Join("、", InvalidSpecs) : "";
            return "本机房：" + name + "    期望 IP 段：" + scope + bad;
        }

        private static string Str(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) ? v as string : null;
        }
    }
}
