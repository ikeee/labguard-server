using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LabGuardServer.Core.Envelope;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Util;

namespace LabGuardServer.Core.Policy
{
    /// <summary>当前策略版本信息（/api/version 的数据源）。</summary>
    public sealed class PolicyVersion
    {
        public ulong Seq;
        public bool Paused;
        public string Sha256Hex;
        public DateTime IssuedAtUtc;
    }

    /// <summary>
    /// 策略仓库：保存 = seq+1 → 组信封（签名）→ 落盘；加载 = 读信封并用**自己的公钥**验签
    /// （服务端磁盘上的信封被篡改也要检出）。落盘文件本身就是发给学生的那一份，读出来即可下发。
    /// </summary>
    public sealed class PolicyStore
    {
        private const string PolicyFileName = "policy.envelope";
        private const string WatermarkFileName = "seq.highwater";

        public string DataDir { get; }
        private string PolicyPath => Path.Combine(DataDir, PolicyFileName);
        private string WatermarkPath => Path.Combine(DataDir, WatermarkFileName);
        private readonly RsaKeyStore _keys;

        /// <summary>
        /// 已发布过的**最大 seq**（水位）。它是防"重启后 seq 回退"的关键：
        /// 磁盘信封被换成一份**签名合法的旧信封**时，光靠验签是查不出来的，
        /// 必须拿水位比对（执行计划 §B2 不变量 8）。
        /// </summary>
        public ulong Watermark { get; private set; }

        public PolicyStore(string dataDir, RsaKeyStore keys)
        {
            DataDir = dataDir;
            _keys = keys;
            Directory.CreateDirectory(dataDir);
        }

        /// <summary>当前生效信封原文（未配置过策略 = null，API 应答 503）。</summary>
        public string CurrentEnvelope { get; private set; }

        public ulong CurrentSeq { get; private set; }

        /// <summary>
        /// 从磁盘加载信封。先解析验签、成功才提交（失败时**保留内存中的现行信封**——
        /// resign 场景磁盘被篡改不会把服务打回 503）。
        /// minSeq：防回滚下限——磁盘信封 seq 低于它则拒绝（签名合法的旧信封也拦）。
        /// </summary>
        public void Load(ulong? minSeq = null)
        {
            Watermark = LoadWatermark();     // 先读水位：它比磁盘信封更可信（信封可被人换成旧的一份）
            if (!File.Exists(PolicyPath))
            {
                CurrentEnvelope = null;
                CurrentSeq = 0;
                return;
            }
            string raw = ReadDiskEnvelope();
            if (raw == null) throw new InvalidDataException("策略信封读不到（文件缺失或持续被占用）：" + PolicyPath);
            EnvelopeData d = PolicyEnvelope.Parse(raw, _keys.Rsa);
            if (!d.Verified)
                throw new InvalidDataException("策略信封验签失败（policy.envelope 疑似被篡改）——拒绝加载，请人工检查 " + PolicyPath);
            ulong floor = Math.Max(minSeq ?? 0, Watermark);
            if (floor > 0 && d.Seq < floor)
                throw new InvalidDataException("拒绝回滚：磁盘信封 seq=" + d.Seq + " 低于已发布水位/下限 " + floor +
                    "（签名虽合法，但它是旧版本信封——放行会让 seq 回退）");
            CurrentEnvelope = raw;
            CurrentSeq = d.Seq;
            SaveWatermark(Math.Max(Watermark, d.Seq));
        }

        /// <summary>
        /// 保存并下发：策略字典 + 暂停标志 → seq+1 → 签名 → 落盘。
        /// policyJson 用与客户端一致的 JavaScriptSerializer 序列化。
        /// </summary>
        public PolicyVersion Save(Dictionary<string, object> policy, bool paused, string defaultPolicyJsonForReset)
        {
            string json;
            if (policy != null)
            {
                json = Json.Stringify(policy);
            }
            else
            {
                // policy == null：按目录默认值重置（第一次保存 / 「恢复默认」按钮）
                if (string.IsNullOrEmpty(defaultPolicyJsonForReset))
                    throw new ArgumentNullException(nameof(defaultPolicyJsonForReset));
                json = defaultPolicyJsonForReset;
            }
            ulong seq = Math.Max(CurrentSeq, Watermark) + 1;   // 永不重复使用已发布过的 seq
            string envelope = PolicyEnvelope.Build(seq, DateTime.UtcNow, paused, json, _keys.Rsa);
            AtomicFile.WriteAllText(PolicyPath, envelope);
            CurrentEnvelope = envelope;
            CurrentSeq = seq;
            SaveWatermark(seq);
            return Version();
        }

        /// <summary>全员暂停/恢复：沿用当前策略 JSON，只翻转 paused（seq+1）。没保存过策略时抛异常。</summary>
        public PolicyVersion SetPaused(bool paused)
        {
            if (CurrentEnvelope == null)
                throw new InvalidOperationException("还没有保存过策略，无从暂停——先在策略面板保存一次");
            EnvelopeData d = PolicyEnvelope.Parse(CurrentEnvelope, _keys.Rsa);
            if (!d.Verified) throw new InvalidDataException("当前信封验签失败，拒绝改写");
            return Save(Json.ParseObject(d.PolicyJson), paused, null);
        }

        public PolicyVersion Version()
        {
            if (CurrentEnvelope == null) return null;
            EnvelopeData d = PolicyEnvelope.Parse(CurrentEnvelope, _keys.Rsa);
            using (var sha = SHA256.Create())
            {
                return new PolicyVersion
                {
                    Seq = d.Seq,
                    Paused = d.Paused,
                    IssuedAtUtc = d.IssuedAtUtc,
                    Sha256Hex = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(CurrentEnvelope))).Replace("-", "").ToLowerInvariant(),
                };
            }
        }

        /// <summary>
        /// 把"已发布过的最大 seq"推进到给定值。用于**分组发布**：组的信封由 GroupPublisher 直接签发，
        /// 不走本类的 Save，但必须把全局水位一起抬高——否则默认组下次保存可能生成一个
        /// 比学生机已应用值更小的 seq（被正确拒绝，表现为"改了策略却没生效"）。
        /// </summary>
        public void MarkPublished(ulong seq)
        {
            if (seq <= CurrentSeq) { SaveWatermark(seq); return; }
            CurrentSeq = seq;
            SaveWatermark(seq);
        }
        /// <summary>当前策略字典（编辑面板的底稿）。没保存过 → 目录默认值。</summary>
        public Dictionary<string, object> CurrentOrDefaultPolicy(Catalog.CatalogDoc catalog)
        {
            if (CurrentEnvelope != null)
            {
                EnvelopeData d = PolicyEnvelope.Parse(CurrentEnvelope, _keys.Rsa);
                if (d.Verified) return Json.ParseObject(d.PolicyJson);
            }
            return Json.ParseObject(catalog.DefaultPolicyJson);
        }

        // ------------------------------------------------------------------ seq 水位

        // ------------------------------------------------------------------ 磁盘策略热拾取

        private FileSystemWatcher _watcher;

        /// <summary>
        /// 盯住 <c>policy.envelope</c>：**外部保存的策略也能被运行中的服务端拾取**。
        ///
        /// 为什么需要：服务端的内存策略只在启动时读一次；而策略可能被另一个进程写下——
        /// 无人值守脚本用 CLI 保存、或以后做成 Windows 服务时由管理端保存。
        /// 没有它，就会出现"磁盘上是新策略、/api/policy 还在发旧信封"的静默不一致。
        ///
        /// 安全口径与启动加载完全一致：**先验签、再比 seq 下限（水位/现行），通过才提交**；
        /// 任何失败都保留现行信封并记日志（可被人放回旧信封诱发的回退，在这里也被拦住）。
        /// </summary>
        public void StartWatching()
        {
            if (_watcher != null) return;
            try
            {
                Directory.CreateDirectory(DataDir);
                _watcher = new FileSystemWatcher(DataDir, PolicyFileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += (s, e) => TryReloadFromDisk();
                _watcher.Created += (s, e) => TryReloadFromDisk();
                _watcher.Renamed += (s, e) => TryReloadFromDisk();
                Log.Info("已盯住策略文件（外部保存的策略会被自动拾取）：" + PolicyPath);
            }
            catch (Exception ex)
            {
                Log.Warn("启动策略文件监视失败（只能靠重启拾取外部改动）：" + ex.Message);
                _watcher = null;
            }
        }

        public void StopWatching()
        {
            try { if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } } catch { }
            _watcher = null;
        }

        /// <summary>尝试从磁盘拾取更新的策略。返回 true = 确实换了现行的信封。</summary>
        public bool TryReloadFromDisk()
        {
            try
            {
                if (!File.Exists(PolicyPath)) return false;
                string disk = ReadDiskEnvelope();
                if (disk == null) return false;
                if (disk == CurrentEnvelope) return false;
                EnvelopeData d = PolicyEnvelope.Parse(disk, _keys.Rsa);
                if (!d.Verified)
                {
                    Log.Error("磁盘策略信封验签失败，拒绝加载（保留现行策略）：" + PolicyPath);
                    return false;
                }
                ulong floor = Math.Max(Watermark, CurrentSeq);
                if (d.Seq < floor)
                {
                    Log.Error("磁盘策略 seq=" + d.Seq + " 低于现行/水位 " + floor +
                              "，拒绝加载（防回退，保留现行策略）");
                    return false;
                }
                CurrentEnvelope = disk;
                CurrentSeq = d.Seq;
                SaveWatermark(Math.Max(Watermark, d.Seq));
                Log.Info("已拾取磁盘上的新策略：seq=" + d.Seq);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("拾取磁盘策略失败（保留现行策略）", ex);
                return false;
            }
        }

        /// <summary>
        /// 读磁盘信封，**带退避重试**：Windows 下 FileSystemWatcher 的事件常常在写入者还没关闭文件时就到，
        /// 直接读会抛 IOException（真机踩过：外部保存 seq=2 后永远重载不上）。
        /// 读到即返回；文件不存在返回 null；2 秒仍读不到则放弃（等下一次事件）。
        /// </summary>
        private string ReadDiskEnvelope()
        {
            for (int attempt = 0; attempt < 25; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(PolicyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                        return sr.ReadToEnd();
                }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                catch (IOException) { System.Threading.Thread.Sleep(80); }
            }
            Log.Warn("策略文件在约 2 秒内一直不可读（可能正被写入），本次不重载");
            return null;
        }

        /// <summary>
        /// 读水位（<c>seq.highwater</c>，格式 <c>{"schemaVersion":1,"seq":N}</c>）。
        /// 缺失 / 损坏 / schemaVersion 不认识 → 按 0 处理并告警（不抛异常：降级为"无回退保护"，
        /// 也不影响本次启动——但下次 Save 一定基于内存里的 CurrentSeq，不会倒着走）。
        /// </summary>
        private ulong LoadWatermark()
        {
            try
            {
                if (!File.Exists(WatermarkPath)) return 0;
                Dictionary<string, object> doc = Json.ParseObject(File.ReadAllText(WatermarkPath, Encoding.UTF8));
                if (doc == null) throw new InvalidDataException("水位文件不是 JSON 对象");
                int schema = doc.TryGetValue("schemaVersion", out object s) && s is IConvertible
                    ? Convert.ToInt32(s) : 0;
                if (schema != 1) throw new InvalidDataException("水位文件 schemaVersion 不认识：" + schema);
                if (!doc.TryGetValue("seq", out object v) || v == null)
                    throw new InvalidDataException("水位文件缺少 seq");
                return Convert.ToUInt64(v);
            }
            catch (Exception ex)
            {
                Log.Warn("seq 水位文件不可用，按 0 处理（重启防回退保护降级）：" + ex.Message);
                return 0;
            }
        }

        private void SaveWatermark(ulong seq)
        {
            if (seq <= Watermark) return;
            try
            {
                AtomicFile.WriteAllText(WatermarkPath,
                    "{\"schemaVersion\":1,\"seq\":" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
                Watermark = seq;
            }
            catch (Exception ex)
            {
                Log.Warn("写 seq 水位失败（重启防回退保护降级）：" + ex.Message);
            }
        }
    }
}
