using System;
using System.Collections.Generic;
using System.IO;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Util;

namespace LabGuardServer.Core.Policy
{
    /// <summary>
    /// **常驻（固化锁）**：老师在策略面板里勾了「常驻」的项，值被锁在这里 ——
    /// **临时课堂状态、分组策略、下课/到点回滚都不能动它**，只有两件事能改：
    ///   ① 老师在**默认策略（全部设备）**里改这一项并保存下发（显式改动 → 锁值就地更新）；
    ///   ② 老师取消勾选「常驻」（或点「恢复默认」，那会清空全部锁定）。
    ///
    /// 为什么做成"服务端一把锁"而不是跟着每份策略走：老师的心智模型是
    /// "这一项我锁死了，别管系统自动怎么切"。跟着策略走的话，设备一调组（换一份基础策略）
    /// 锁就换了一层含义，正好违背他要的"分组切换也不许动它"。
    ///
    /// 客户端**零改动**：锁值在服务端合成时盖回策略 JSON，学生机收到的还是普通策略（照旧验签）。
    /// </summary>
    public sealed class PinnedStore
    {
        private const string FileName = "pinned.json";
        private const int FormatVersion = 1;

        private readonly string _path;
        private readonly object _lock = new object();
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.Ordinal);

        public PinnedStore(string dataDir)
        {
            Directory.CreateDirectory(dataDir);
            _path = Path.Combine(dataDir, FileName);
            Load();
        }

        /// <summary>pinned.json 的完整路径（**注意别叫 Path**：那会盖住 System.IO.Path，Path.Combine 就编译不过了）。</summary>
        public string FilePath { get { return _path; } }
        public int Count { get { lock (_lock) return _values.Count; } }

        /// <summary>已锁定的键（点路径，如 "Usb.Enabled"）。副本，调用方改不了内部状态。</summary>
        public List<string> Keys { get { lock (_lock) return new List<string>(_values.Keys); } }

        public bool IsPinned(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (_lock) return _values.ContainsKey(path);
        }

        public bool TryGet(string path, out object value)
        {
            lock (_lock) return _values.TryGetValue(path ?? "", out value);
        }

        public void Pin(string path, object value)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (_lock) _values[path] = Clone(value);
            Save();
        }

        public bool Unpin(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            bool removed;
            lock (_lock) removed = _values.Remove(path);
            if (removed) Save();
            return removed;
        }

        public int Clear()
        {
            int n;
            lock (_lock) { n = _values.Count; _values.Clear(); }
            if (n > 0) Save();
            return n;
        }

        /// <summary>
        /// 把锁值盖回策略（**不改入参**，返回新字典）：只盖"这份策略里真的有这个路径"的项
        /// （合成结果与完整策略都满足；万一某项在这份策略里不存在，就当它不适用，绝不凭空造一棵子树）。
        /// </summary>
        public Dictionary<string, object> ApplyTo(Dictionary<string, object> policy)
        {
            if (policy == null) return null;
            Dictionary<string, object> copy = Json.ParseObject(Json.Stringify(policy));
            lock (_lock)
            {
                foreach (KeyValuePair<string, object> kv in _values)
                {
                    if (PathAccess.Get(copy, kv.Key) == null) continue;
                    PathAccess.Set(copy, kv.Key, Clone(kv.Value));
                }
            }
            return copy;
        }

        /// <summary>
        /// 【老师显式改动】用这份（默认策略的）策略更新锁值：锁着的项如果值变了，就更新锁值。
        /// 这是"唯一能让锁值变化"的两条路之一（另一条是取消勾选）。返回被更新的路径（用于提示/日志）。
        /// </summary>
        public List<string> UpdateFromExplicit(Dictionary<string, object> policy)
        {
            var changed = new List<string>();
            if (policy == null) return changed;
            lock (_lock)
            {
                foreach (string path in new List<string>(_values.Keys))
                {
                    object cur = PathAccess.Get(policy, path);
                    if (cur == null) continue;
                    if (PolicyMerge.Dump(cur) == PolicyMerge.Dump(_values[path])) continue;
                    _values[path] = Clone(cur);
                    changed.Add(path);
                }
            }
            if (changed.Count > 0) Save();
            return changed;
        }

        // ---------------------------------------------------------------- 落盘

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                Dictionary<string, object> root = Json.ParseObject(File.ReadAllText(_path, System.Text.Encoding.UTF8));
                object values = root != null && root.ContainsKey("values") ? root["values"] : null;
                var dict = values as Dictionary<string, object>;
                if (dict == null) return;
                lock (_lock)
                {
                    _values.Clear();
                    foreach (KeyValuePair<string, object> kv in dict) _values[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                // 读坏了**不能静默**：宁可让老师知道"常驻列表读不出来"，也不要假装没有锁定（那样会静默放开锁）
                Log.Error("常驻列表（" + FileName + "）读取失败，本次按" + "\u201c未锁定任何项\u201d处理：" + ex.Message);
            }
        }

        private void Save()
        {
            try
            {
                var root = new Dictionary<string, object>
                {
                    { "version", FormatVersion },
                };
                lock (_lock) root["values"] = Clone(_values);
                AtomicFile.WriteAllText(_path, Json.Stringify(root));
            }
            catch (Exception ex)
            {
                Log.Error("常驻列表保存失败（本次锁定只在内存里生效）：" + ex.Message);
            }
        }

        private static object Clone(object value)
        {
            if (value == null) return null;
            if (value is Dictionary<string, object> || value is List<object> || value is object[] || value is System.Collections.ArrayList)
                return Json.ParseObject("{\"v\":" + Json.Stringify(value) + "}")["v"];
            return value;
        }
    }
}
