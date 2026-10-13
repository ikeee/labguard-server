using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Util;

namespace LabGuardServer.Core.Policy
{
    /// <summary>一个设备组（v0.11 分组的最小模型：组名 + 成员 + 该组自己的策略信封）。</summary>
    public sealed class DeviceGroup
    {
        public string Id;                                  // g-xxxxxxxx
        public string Name;                                // 老师起的名字（如"高一(3)班机房"）
        public List<string> DeviceIds = new List<string>();   // 精确匹配 deviceId（新客户端）
        public List<string> Machines = new List<string>();    // 机器名兜底（旧客户端/临时指定）
        public string Envelope;                            // 该组当前策略信封原文（null = 还没保存过 → 回落默认策略）
        public ulong Seq;                                  // 该组信封的 seq（**全局单调**，见 GroupPublisher）
        public string IssuedAtUtc;
        public bool Paused;
    }

    /// <summary>
    /// 设备组仓库（**覆盖层设计**）：默认组不入库 —— 它仍然是 <see cref="PolicyStore"/> 里那份
    /// `policy.envelope`。本仓库只存"非默认组"，解析时：命中某组 → 用该组信封；否则 → 默认策略。
    ///
    /// 这样 v0.10 的行为与测试原样保留（没建任何组时人人拿默认策略），分组只是"给一部分设备换一份策略"。
    /// 持久化：数据目录 `groups.json`（带 schemaVersion，执行计划 §14）。
    /// </summary>
    public sealed class GroupStore
    {
        public const string DefaultGroupName = "全部设备（默认策略）";
        private const string FileName = "groups.json";

        /// <summary>数据目录（自检注入临时目录用）。</summary>
        public string DataDir { get; set; }
        public string GroupsPath { get { return Path.Combine(DataDir, FileName); } }

        public List<DeviceGroup> Groups { get; private set; } = new List<DeviceGroup>();

        public GroupStore(string dataDir)
        {
            DataDir = dataDir;
        }

        public void Load()
        {
            Groups = new List<DeviceGroup>();
            try
            {
                if (!File.Exists(GroupsPath)) return;
                var doc = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }
                    .Deserialize<Dictionary<string, object>>(File.ReadAllText(GroupsPath, Encoding.UTF8));
                if (doc == null) return;
                int schema = doc.TryGetValue("schemaVersion", out object sv) && sv is IConvertible ? Convert.ToInt32(sv) : 0;
                if (schema != 1) { Log.Warn("分组文件 schemaVersion 不认识（" + schema + "），按空分组处理"); return; }
                if (!(doc.TryGetValue("groups", out object groupsObj) && groupsObj is System.Collections.IEnumerable en)) return;
                foreach (object o in en)
                {
                    var m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    var g = new DeviceGroup
                    {
                        Id = Str(m, "id"),
                        Name = Str(m, "name"),
                        Envelope = Str(m, "envelope"),
                        IssuedAtUtc = Str(m, "issuedAtUtc"),
                        Paused = Bool(m, "paused"),
                        Seq = Ulong(m, "seq"),
                    };
                    if (string.IsNullOrEmpty(g.Id) || string.IsNullOrEmpty(g.Name)) continue;
                    g.DeviceIds = StrList(m, "deviceIds");
                    g.Machines = StrList(m, "machines");
                    Groups.Add(g);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取分组文件失败（按空分组处理）：" + ex.Message);
                Groups = new List<DeviceGroup>();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var doc = new Dictionary<string, object>
                {
                    { "schemaVersion", 1 },
                    { "groups", Groups.Select(g => new Dictionary<string, object>
                        {
                            { "id", g.Id }, { "name", g.Name },
                            { "deviceIds", g.DeviceIds }, { "machines", g.Machines },
                            { "envelope", g.Envelope }, { "seq", g.Seq },
                            { "issuedAtUtc", g.IssuedAtUtc }, { "paused", g.Paused },
                        }).ToList() },
                };
                AtomicFile.WriteAllText(GroupsPath, new JavaScriptSerializer().Serialize(doc));
            }
            catch (Exception ex)
            {
                Log.Error("保存分组文件失败", ex);
            }
        }

        /// <summary>
        /// 设备 → 组解析：**deviceId 精确匹配优先，其次机器名精确匹配，都不中 → null（= 用默认策略）**。
        /// 同时命中多个组时按"先 deviceId 后机器名、组顺序靠前优先"取第一个（口径确定、可测）。
        /// </summary>
        public DeviceGroup Resolve(string deviceId, string machine)
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                DeviceGroup byId = Groups.FirstOrDefault(g =>
                    g.DeviceIds.Any(d => string.Equals(d, deviceId, StringComparison.OrdinalIgnoreCase)));
                if (byId != null) return byId;
            }
            if (!string.IsNullOrEmpty(machine))
            {
                DeviceGroup byName = Groups.FirstOrDefault(g =>
                    g.Machines.Any(d => string.Equals(d, machine, StringComparison.OrdinalIgnoreCase)));
                if (byName != null) return byName;
            }
            return null;
        }

        public DeviceGroup Get(string id)
        {
            return string.IsNullOrEmpty(id) ? null : Groups.FirstOrDefault(g => g.Id == id);
        }

        public DeviceGroup Add(string name)
        {
            var g = new DeviceGroup { Id = "g-" + Guid.NewGuid().ToString("N").Substring(0, 8), Name = name };
            Groups.Add(g);
            return g;
        }

        public bool Rename(string id, string name)
        {
            DeviceGroup g = Get(id);
            if (g == null || string.IsNullOrWhiteSpace(name)) return false;
            g.Name = name.Trim();
            return true;
        }

        public bool Remove(string id)
        {
            DeviceGroup g = Get(id);
            if (g == null) return false;
            Groups.Remove(g);
            return true;
        }

        /// <summary>把设备放进某个组（先从其它组移除，保证"一台设备只属于一个主组"）。</summary>
        public void Assign(string groupId, string deviceId, string machine)
        {
            RemoveMember(deviceId, machine);
            DeviceGroup g = Get(groupId);
            if (g == null) return;
            if (!string.IsNullOrEmpty(deviceId) && !g.DeviceIds.Any(d => string.Equals(d, deviceId, StringComparison.OrdinalIgnoreCase)))
                g.DeviceIds.Add(deviceId);
            if (!string.IsNullOrEmpty(machine) && !g.Machines.Any(d => string.Equals(d, machine, StringComparison.OrdinalIgnoreCase)))
                g.Machines.Add(machine);
        }

        /// <summary>把设备移出所有组（回到默认策略）。</summary>
        public void RemoveMember(string deviceId, string machine)
        {
            foreach (DeviceGroup g in Groups)
            {
                if (!string.IsNullOrEmpty(deviceId)) g.DeviceIds.RemoveAll(d => string.Equals(d, deviceId, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(machine)) g.Machines.RemoveAll(d => string.Equals(d, machine, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>该设备当前属于哪个组（界面显示用：组名 or 默认）。</summary>
        public string GroupNameOf(string deviceId, string machine)
        {
            DeviceGroup g = Resolve(deviceId, machine);
            return g == null ? DefaultGroupName : g.Name;
        }

        // ---- JSON 取值小工具（JavaScriptSerializer：数组→ArrayList，对象→Dictionary，数字→int/decimal）

        private static string Str(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) ? v as string : null;
        }

        private static bool Bool(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) && v is bool b && b;
        }

        private static ulong Ulong(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) && v is IConvertible ? Convert.ToUInt64(v) : 0;
        }

        private static List<string> StrList(Dictionary<string, object> d, string k)
        {
            var list = new List<string>();
            if (d.TryGetValue(k, out object v) && v is System.Collections.IEnumerable en && !(v is string))
                foreach (object o in en) { var s = o as string; if (!string.IsNullOrEmpty(s)) list.Add(s); }
            return list;
        }
    }
}
