using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using LabGuardServer.Core.Envelope;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Util;

namespace LabGuardServer.Core.Audit
{
    /// <summary>审计动作名（执行计划 §G1 要求的"至少审计"这些动作）。</summary>
    public static class AuditAction
    {
        public const string ServerStart = "server.start";
        public const string ServerStop = "server.stop";
        public const string PanelOpen = "panel.open";          // 策略面板被打开（本产品是免密面板 → 这是最接近"管理界面登录"的事件）
        public const string PolicyPublish = "policy.publish";
        public const string PolicyPause = "policy.pause";
        public const string PolicyResume = "policy.resume";
        public const string TempStart = "temp.start";
        public const string TempEnd = "temp.end";
        public const string TempExpire = "temp.expire";        // 到期自动恢复（不经人手，必须留下痕迹）
        public const string PairingExport = "pairing.export";
        public const string KeyCreate = "key.create";
        public const string KeyUnavailable = "key.unavailable";
        public const string DeviceExport = "device.export";
        public const string AuditExport = "audit.export";
        public const string RoomSet = "room.set";           // 设置本机房信息（教室名 / 期望 IP 段）
    }

    /// <summary>一条审计记录（字段与执行计划 §G1 给的表一一对应）。</summary>
    public sealed class AuditEntry
    {
        public long Seq;
        public DateTime AtUtc;
        public string Action;
        public string Actor;
        public string TargetType;
        public string TargetId;
        public ulong PrevPolicySeq;
        public ulong NewPolicySeq;
        public string Result;          // "ok" / "failed"
        public string ErrorCode;
        public string Summary;
        public string PrevHash;
        public string Hash;

        public string LocalTime { get { return AtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); } }
    }

    /// <summary>链校验结果。</summary>
    public sealed class AuditVerifyResult
    {
        public bool Ok;
        public int Entries;
        public string Reason;          // 不 Ok 时给出第一处问题
        public bool KeyAvailable;
    }

    /// <summary>
    /// **审计日志**（执行计划 §G1 / LG-107）。
    ///
    /// 落地形态：**按天一个文件** <c>audit-yyyyMMdd.jsonl</c>（UTC 日期），一行一条 JSON。
    /// 选 JSONL 不选 CSV 当存储：追加一条只动一行、解析不用猜引号、出了问题人眼也看得懂。
    ///
    /// 三条硬要求怎么满足的：
    ///   ① **不记录密码/私钥/敏感策略值**：写入方只交"摘要"（比如"改了 3 项：Usb.AllowStorage true→false…"），
    ///      绝不把整份策略 JSON 塞进来；<see cref="Sanitize"/> 再做一次兜底（长度截断 + 敏感词改写）。
    ///   ② **轮转与体积上限**：每天一个文件；总大小超过上限就删最老的（<see cref="Rotate"/>）。
    ///   ③ **尽量防篡改**：a) 文件 ACL 只给 Administrators/SYSTEM 写（普通用户改不了）；
    ///      b) 每条记录带**哈希链**（`hash = HMAC(服务端私钥派生密钥, 本条字段 + 上一条 hash)`）——
    ///      删一条、改一个字段、调换顺序都会被 <see cref="Verify"/> 认出来；
    ///      而密钥是从**服务端 RSA 私钥**派生的、不落在日志里，所以拿到日志文件也伪造不出链。
    ///   另外：**写审计失败绝不影响业务**（全部 try/catch，只告警）——
    ///   "策略发下去了但审计没写成"不能变成"策略发布失败"。
    /// </summary>
    public sealed class AuditLog
    {
        public const string FilePrefix = "audit-";
        public const string FileSuffix = ".jsonl";
        public const int DefaultMaxTotalBytes = 8 * 1024 * 1024;   // 约 8 MB：一天几千条也够用几个月
        public const int DefaultMaxFiles = 120;                     // 约 4 个月的日文件

        private readonly string _dataDir;
        private readonly byte[] _key;
        private readonly object _gate = new object();
        private string _lastHash = "";
        private long _lastSeq;

        public AuditLog(string dataDir, byte[] hmacKey)
        {
            _dataDir = dataDir;
            _key = hmacKey;
            try { LoadTailState(); } catch { _lastHash = ""; _lastSeq = 0; }
        }

        /// <summary>从服务端签名密钥派生审计链的 HMAC 密钥（不落盘、不写进日志）。</summary>
        public static byte[] DeriveKey(RsaKeyStore keys)
        {
            if (keys == null || keys.Rsa == null) return null;
            try
            {
                string priv = keys.Rsa.ToXmlString(true);
                using (var sha = SHA256.Create())
                    return sha.ComputeHash(Encoding.UTF8.GetBytes("labguard-audit-chain-v1|" + priv));
            }
            catch { return null; }
        }

        /// <summary>账本文件（按天，UTC）。</summary>
        public string FileFor(DateTime utc) { return Path.Combine(_dataDir, FilePrefix + utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + FileSuffix); }
        public string CurrentFile { get { return FileFor(DateTime.UtcNow); } }

        // ------------------------------------------------------------------ 写

        /// <summary>
        /// 追加一条。**永不抛异常**：审计写不进去只告警（否则会把"策略发布成功"误报成失败）。
        /// </summary>
        public bool Write(string action, string targetType, string targetId, ulong prevSeq, ulong newSeq,
                          string result, string errorCode, string summary, string actor = null)
        {
            try
            {
                lock (_gate)
                {
                    var e = new AuditEntry
                    {
                        Seq = _lastSeq + 1,
                        AtUtc = DateTime.UtcNow,
                        Action = Sanitize(action, 64),
                        Actor = Sanitize(string.IsNullOrWhiteSpace(actor) ? CurrentActor() : actor, 96),
                        TargetType = Sanitize(targetType, 32),
                        TargetId = Sanitize(targetId, 96),
                        PrevPolicySeq = prevSeq,
                        NewPolicySeq = newSeq,
                        Result = Sanitize(result, 16),
                        ErrorCode = Sanitize(errorCode, 64),
                        Summary = Sanitize(summary, 400),
                        PrevHash = _lastHash,
                    };
                    e.Hash = Chain(e);
                    Directory.CreateDirectory(_dataDir);
                    string line = Json.Stringify(ToDoc(e));
                    File.AppendAllText(FileFor(e.AtUtc), line + "\n", new UTF8Encoding(false));
                    Harden(FileFor(e.AtUtc));
                    _lastSeq = e.Seq;
                    _lastHash = e.Hash;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("写审计日志失败（不影响业务，但请检查磁盘/权限）：" + ex.Message);
                return false;
            }
        }

        /// <summary>从最后一条记录恢复链头（重启后继续接得上）。</summary>
        private void LoadTailState()
        {
            // 注意：net48（.NET Standard 2.0）没有 Enumerable.TakeLast，别用。
            List<AuditEntry> all = ReadAll();
            if (all.Count == 0) return;
            AuditEntry last = all[all.Count - 1];
            _lastSeq = last.Seq;
            _lastHash = last.Hash ?? "";
        }

        private string Chain(AuditEntry e)
        {
            string payload = string.Join("|", new[] {
                e.Seq.ToString(CultureInfo.InvariantCulture),
                e.AtUtc.ToString("o", CultureInfo.InvariantCulture),
                e.Action ?? "", e.Actor ?? "", e.TargetType ?? "", e.TargetId ?? "",
                e.PrevPolicySeq.ToString(CultureInfo.InvariantCulture),
                e.NewPolicySeq.ToString(CultureInfo.InvariantCulture),
                e.Result ?? "", e.ErrorCode ?? "", e.Summary ?? "", e.PrevHash ?? "",
            });
            if (_key == null || _key.Length == 0)
            {
                using (var sha = SHA256.Create())           // 密钥不可用：降级为纯哈希链（仍能看出改动）
                    return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(payload)));
            }
            using (var h = new HMACSHA256(_key))
                return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        }

        private static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        // ------------------------------------------------------------------ 读 / 校验

        /// <summary>按文件名的日期顺序读全部记录（坏行跳过并告警，绝不让一行坏数据把整个日志废掉）。</summary>
        public List<AuditEntry> ReadAll()
        {
            var list = new List<AuditEntry>();
            try
            {
                if (!Directory.Exists(_dataDir)) return list;
                foreach (string f in Files())
                {
                    foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var doc = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }
                                .Deserialize<Dictionary<string, object>>(line);
                            if (doc == null) continue;
                            list.Add(FromDoc(doc));
                        }
                        catch { Log.Warn("审计日志有一行无法解析（已跳过）：" + Path.GetFileName(f)); }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("读审计日志失败：" + ex.Message); }
            return list;
        }

        /// <summary>最近 N 条（新的在后）。</summary>
        public List<AuditEntry> Tail(int max)
        {
            List<AuditEntry> all = ReadAll();
            if (max <= 0 || all.Count <= max) return all;
            return all.Skip(all.Count - max).ToList();
        }

        /// <summary>校验哈希链：seq 连续、prevHash 接得上、每条的 hash 能重算出来。</summary>
        public AuditVerifyResult Verify()
        {
            var r = new AuditVerifyResult { Ok = true, KeyAvailable = _key != null && _key.Length > 0 };
            List<AuditEntry> all = ReadAll();
            r.Entries = all.Count;
            string prev = "";
            long expectSeq = 0;
            foreach (AuditEntry e in all)
            {
                expectSeq++;
                if (e.Seq != expectSeq)
                {
                    r.Ok = false; r.Reason = "第 " + expectSeq + " 条 seq 不连续（实际 " + e.Seq + "）——有记录被删除或插入";
                    return r;
                }
                if ((e.PrevHash ?? "") != prev)
                {
                    r.Ok = false; r.Reason = "第 " + e.Seq + " 条的 prevHash 接不上上一条——顺序被调换或被截断";
                    return r;
                }
                if (Chain2(e, prev) != (e.Hash ?? ""))
                {
                    r.Ok = false; r.Reason = "第 " + e.Seq + " 条内容被改过（hash 对不上）";
                    return r;
                }
                prev = e.Hash ?? "";
            }
            if (r.Ok) r.Reason = "链完整（" + r.Entries + " 条）";
            return r;
        }

        private string Chain2(AuditEntry e, string prevHash)
        {
            string saved = e.PrevHash;
            e.PrevHash = prevHash;
            string h = Chain(e);
            e.PrevHash = saved;
            return h;
        }

        /// <summary>Files 按日期排序（旧 → 新）。</summary>
        public List<string> Files()
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(_dataDir)) return list;
                list = Directory.GetFiles(_dataDir, FilePrefix + "*" + FileSuffix).ToList();
                list.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return list;
        }

        // ------------------------------------------------------------------ 轮转

        /// <summary>总大小/文件数超上限就删最老的。返回删掉的文件数。</summary>
        public int Rotate(int maxTotalBytes = DefaultMaxTotalBytes, int maxFiles = DefaultMaxFiles)
        {
            int removed = 0;
            try
            {
                lock (_gate)
                {
                    List<string> files = Files();
                    long total = 0;
                    foreach (string f in files) { try { total += new FileInfo(f).Length; } catch { } }
                    // 从最老的开始删，直到"文件数够少且总大小够小"
                    for (int i = 0; i < files.Count - 1; i++)
                    {
                        if (total <= maxTotalBytes && files.Count - removed <= maxFiles) break;
                        try
                        {
                            long len = new FileInfo(files[i]).Length;
                            File.Delete(files[i]);
                            total -= len; removed++;
                        }
                        catch (Exception ex) { Log.Warn("删除旧审计文件失败：" + ex.Message); break; }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("审计日志轮转失败：" + ex.Message); }
            return removed;
        }

        // ------------------------------------------------------------------ 脱敏

        /// <summary>
        /// 兜底脱敏：截断长度 + 把疑似机密（口令散列/私钥/kv 形态）改写成占位符。
        /// 写入方本来就只交摘要，这里是"万一有人以后往里塞整份 JSON"的第二道闸。
        /// </summary>
        public static string Sanitize(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string v = s;
            foreach (string bad in new[] { "pbkdf2-sha256$", "passwordhash", "<rsakeyvalue>", "privatekey", "server-key.bin", "cfgkey" })
            {
                int i = v.IndexOf(bad, StringComparison.OrdinalIgnoreCase);
                if (i >= 0) v = v.Substring(0, i) + "[已脱敏]";
            }
            v = v.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            if (v.Length > maxLen) v = v.Substring(0, maxLen) + "…";
            return v;
        }

        private static string CurrentActor()
        {
            try
            {
                string u = Environment.UserName;
                string m = Environment.MachineName;
                return (string.IsNullOrEmpty(u) ? "?" : u) + "@" + (string.IsNullOrEmpty(m) ? "?" : m);
            }
            catch { return "LocalAdmin"; }
        }

        // ------------------------------------------------------------------ JSON 往返

        private static Dictionary<string, object> ToDoc(AuditEntry e)
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "seq", e.Seq },
                { "atUtc", e.AtUtc.ToString("o", CultureInfo.InvariantCulture) },
                { "action", e.Action ?? "" },
                { "actor", e.Actor ?? "" },
                { "targetType", e.TargetType ?? "" },
                { "targetId", e.TargetId ?? "" },
                { "prevPolicySeq", e.PrevPolicySeq },
                { "newPolicySeq", e.NewPolicySeq },
                { "result", e.Result ?? "" },
                { "errorCode", e.ErrorCode ?? "" },
                { "summary", e.Summary ?? "" },
                { "prevHash", e.PrevHash ?? "" },
                { "hash", e.Hash ?? "" },
            };
        }

        private static AuditEntry FromDoc(Dictionary<string, object> d)
        {
            return new AuditEntry
            {
                Seq = Long(d, "seq"),
                AtUtc = Date(d, "atUtc"),
                Action = Str(d, "action"),
                Actor = Str(d, "actor"),
                TargetType = Str(d, "targetType"),
                TargetId = Str(d, "targetId"),
                PrevPolicySeq = Ulong(d, "prevPolicySeq"),
                NewPolicySeq = Ulong(d, "newPolicySeq"),
                Result = Str(d, "result"),
                ErrorCode = Str(d, "errorCode"),
                Summary = Str(d, "summary"),
                PrevHash = Str(d, "prevHash"),
                Hash = Str(d, "hash"),
            };
        }

        private static string Str(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) ? v as string : null;
        }

        private static long Long(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) && v is IConvertible ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
        }

        private static ulong Ulong(Dictionary<string, object> d, string k)
        {
            return d.TryGetValue(k, out object v) && v is IConvertible ? Convert.ToUInt64(v, CultureInfo.InvariantCulture) : 0;
        }

        private static DateTime Date(Dictionary<string, object> d, string k)
        {
            string raw = Str(d, k);
            if (string.IsNullOrEmpty(raw)) return DateTime.MinValue;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t))
                return t.ToUniversalTime();
            return DateTime.MinValue;
        }

        /// <summary>ACL：审计文件只给 Administrators/SYSTEM 写（普通用户改不了 = 防"普通 UI 操作直接篡改"）。</summary>
        private static void Harden(string path)
        {
            try
            {
                var info = new FileInfo(path);
                var sec = info.GetAccessControl();
                sec.SetAccessRuleProtection(true, false);
                var admin = new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(admin,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(system,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
                info.SetAccessControl(sec);
            }
            catch { /* 非管理员运行时加固失败不致命（自检/沙箱场景） */ }
        }
    }

    /// <summary>审计记录的 CSV 导出（执行计划 §G2 的"审计记录"那一项）。</summary>
    public static class AuditCsv
    {
        public static readonly string[] Header =
        {
            "序号", "UTC 时间", "本地时间", "动作", "操作者", "对象类型", "对象",
            "原策略seq", "新策略seq", "结果", "错误码", "摘要",
        };

        public static List<string> Render(IEnumerable<AuditEntry> entries)
        {
            var lines = new List<string> { Csv.Line(Header) };
            foreach (AuditEntry e in entries)
            {
                lines.Add(Csv.Line(e.Seq,
                    e.AtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    e.LocalTime,
                    e.Action, e.Actor, e.TargetType, e.TargetId,
                    e.PrevPolicySeq, e.NewPolicySeq, e.Result, e.ErrorCode, e.Summary));
            }
            return lines;
        }

        public static void Export(IEnumerable<AuditEntry> entries, string path)
        {
            Csv.WriteFile(path, Render(entries));
        }
    }
}
