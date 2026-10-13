using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Util;

namespace LabGuardServer.Core.Policy
{
    /// <summary>
    /// **临时课堂状态**（执行计划 §F3）：「基础策略 + 临时覆盖」里的那一层临时覆盖。
    ///
    /// 语义：老师在某个时刻把一批开关临时改掉（或全员暂停），并（可选）给一个到期时间；
    /// 到期/手动结束后**自动回到基础策略**——不需要老师把每个选项改回来。
    ///
    /// 落地方式（关键设计）：临时状态在**发布时**就被合成进"有效策略"，然后照旧签名 + 排 seq 下发。
    /// 所以：客户端**零改动、零新字段**（协议不用扩展），学生机仍然只看 seq 变没变、验签通不通；
    /// 服务端的解析优先级 = **临时状态 &gt; 分组 &gt; 默认**，三条都确定、可测。
    ///
    /// 这个类只负责"一个临时状态的记录 + 落盘"，编排（发布/重算/到期）在 <see cref="TempStateManager"/>。
    /// </summary>
    public sealed class TempState
    {
        public const string FileName = "temp-state.json";
        private const int SchemaVersion = 1;

        public string Id;                                  // t-xxxxxxxx
        public string Name;                                // 老师看到的名字（"考试模式"）
        public string PatchJson;                           // 覆盖内容（部分策略 JSON 原文）
        public bool Paused;                                // 临时状态自带的全员暂停
        public DateTime StartedAtUtc;
        public DateTime? ExpiresAtUtc;                     // null = 不自动到期（需要人工结束）
        public string StartedBy;                           // "预设/面板/CLI"（日志与审计用）
        public ulong MaxSeq;                               // 本次临时状态发布过的最大 seq（界面显示）

        /// <summary>未命中任何分组的设备（= 默认组）的临时信封原文。</summary>
        public string DefaultEnvelope;

        /// <summary>分组 ID → 该组的临时信封原文（键是 `g-xxxxxxxx`）。</summary>
        public Dictionary<string, string> GroupEnvelopes = new Dictionary<string, string>(StringComparer.Ordinal);

        public static string PathOf(string dataDir)
        {
            return System.IO.Path.Combine(dataDir, FileName);
        }

        public bool IsExpiredAt(DateTime nowUtc)
        {
            return ExpiresAtUtc.HasValue && nowUtc >= ExpiresAtUtc.Value;
        }

        /// <summary>临时信封：groupId 为空 → 默认组那一份；没有 → null（调用方回落基础策略）。</summary>
        public string EnvelopeFor(string groupId)
        {
            if (string.IsNullOrEmpty(groupId)) return DefaultEnvelope;
            if (GroupEnvelopes != null && GroupEnvelopes.TryGetValue(groupId, out string env)) return env;
            return null;
        }

        /// <summary>"还剩 12 分 30 秒" / "不自动到期"。给界面与日志用。</summary>
        public string RemainingText(DateTime nowUtc)
        {
            if (!ExpiresAtUtc.HasValue) return "不自动到期";
            TimeSpan left = ExpiresAtUtc.Value - nowUtc;
            if (left <= TimeSpan.Zero) return "已到期";
            if (left.TotalHours >= 1) return "剩 " + (int)left.TotalHours + " 小时 " + left.Minutes + " 分";
            if (left.TotalMinutes >= 1) return "剩 " + (int)left.TotalMinutes + " 分 " + left.Seconds + " 秒";
            return "剩 " + left.Seconds + " 秒";
        }

        // ------------------------------------------------------------------ 落盘

        public void Save(string dataDir)
        {
            var doc = new Dictionary<string, object>
            {
                { "schemaVersion", SchemaVersion },
                { "id", Id },
                { "name", Name },
                { "patch", PatchJson ?? "{}" },
                { "paused", Paused },
                { "startedAtUtc", StartedAtUtc.ToString("o", CultureInfo.InvariantCulture) },
                { "expiresAtUtc", ExpiresAtUtc.HasValue ? ExpiresAtUtc.Value.ToString("o", CultureInfo.InvariantCulture) : "" },
                { "startedBy", StartedBy ?? "" },
                { "defaultEnvelope", DefaultEnvelope ?? "" },
                { "groupEnvelopes", GroupEnvelopes ?? new Dictionary<string, string>() },
                { "maxSeq", MaxSeq },
            };
            AtomicFile.WriteAllText(PathOf(dataDir), Json.Stringify(doc));
        }

        /// <summary>读临时状态；不存在 / 版本不认识 / 解析失败 → null（并告警，绝不带病继续）。</summary>
        public static TempState Load(string dataDir)
        {
            string path = PathOf(dataDir);
            try
            {
                if (!File.Exists(path)) return null;
                var doc = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }
                    .Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                if (doc == null) { Log.Warn("临时状态文件不是 JSON 对象，已忽略：" + path); return null; }
                int schema = doc.TryGetValue("schemaVersion", out object sv) && sv is IConvertible ? Convert.ToInt32(sv) : 0;
                if (schema != SchemaVersion)
                {
                    Log.Warn("临时状态文件 schemaVersion 不认识（" + schema + "），已忽略（不会当成生效状态）");
                    return null;
                }
                var t = new TempState
                {
                    Id = Str(doc, "id"),
                    Name = Str(doc, "name"),
                    PatchJson = Str(doc, "patch"),
                    Paused = Bool(doc, "paused"),
                    StartedBy = Str(doc, "startedBy"),
                    DefaultEnvelope = Str(doc, "defaultEnvelope"),
                    StartedAtUtc = Time(doc, "startedAtUtc") ?? DateTime.UtcNow,
                    ExpiresAtUtc = Time(doc, "expiresAtUtc"),
                    MaxSeq = doc.TryGetValue("maxSeq", out object ms) && ms is IConvertible ? Convert.ToUInt64(ms) : 0,
                };
                if (doc.TryGetValue("groupEnvelopes", out object ge) && ge is Dictionary<string, object> gd)
                {
                    foreach (KeyValuePair<string, object> kv in gd)
                    {
                        string v = kv.Value as string;
                        if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(v)) t.GroupEnvelopes[kv.Key] = v;
                    }
                }
                if (string.IsNullOrEmpty(t.Name) || string.IsNullOrEmpty(t.DefaultEnvelope))
                {
                    Log.Warn("临时状态文件缺字段（name/默认信封），已忽略");
                    return null;
                }
                return t;
            }
            catch (Exception ex)
            {
                Log.Warn("读取临时状态失败（按『无临时状态』处理）：" + ex.Message);
                return null;
            }
        }

        /// <summary>删除临时状态文件（结束/到期/丢弃时调用）。文件不存在也算成功。</summary>
        public static void DeleteFile(string dataDir)
        {
            try
            {
                string path = PathOf(dataDir);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Warn("删除临时状态文件失败（下次启动会再试）：" + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 小工具

        private static string Str(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) ? v as string : null;
        }

        private static bool Bool(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) && v is bool b && b;
        }

        private static DateTime? Time(Dictionary<string, object> d, string k)
        {
            string raw = Str(d, k);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t))
                return t.ToUniversalTime();
            Log.Warn("临时状态里的时间字段解析失败（" + k + "=" + raw + "），按空处理");
            return null;
        }
    }
}
