using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LabGuardServer.Core.Envelope;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Util;

namespace LabGuardServer.Core.Policy
{
    /// <summary>
    /// **临时课堂状态层**的编排器（执行计划 §F3 / LG-105 + LG-106）。
    ///
    /// 一句话规则：**有效策略 = 基础策略 + 临时覆盖**，临时覆盖在**发布时**就合成好、照旧签名 + 排 seq 下发；
    /// 结束后自动把基础策略**重签一个新 seq** 发下去，于是学生机自然地回到基础策略。
    ///
    /// 为什么"结束"要重签（而不是直接把临时状态删掉完事）：
    ///   学生机的防回滚规则是"seq 必须严格大于已应用值"。临时状态的信封 seq 一定大于基础信封，
    ///   所以只删临时状态 → 服务端又只能发**旧 seq 的基础信封** → 学生机会正确地拒收 → **永久卡在临时策略上**。
    ///   所以结束 = 把基础策略同一份内容、换一个新 seq 再签一遍（内容不变，只是"我又发了一次"）。
    ///
    /// 解析优先级（三条，确定可测）：**临时状态 &gt; 设备分组 &gt; 默认策略**。
    ///
    /// 覆盖范围口径：临时状态覆盖"**开始那一刻存在的目标**"（默认组 + 当时的所有分组）。
    /// 临时状态期间新建的分组不在覆盖内（它拿自己的分组策略）——避免"新建组突然继承一个别人组的合成结果"。
    /// </summary>
    public sealed class TempStateManager
    {
        private readonly string _dataDir;
        private readonly RsaKeyStore _keys;
        private readonly PolicyStore _store;
        private readonly GroupStore _groups;
        private readonly object _sync = new object();
        private FileSystemWatcher _watcher;
        /// <summary>上次看到的 temp-state.json **内容签名**（不用时间戳：原子替换后时间戳不一定变新，实测会漏掉改动）。</summary>
        private string _diskSig;

        /// <summary>当前进行中的临时状态（null = 无）。**只由本类改**。</summary>
        public TempState Current { get; private set; }

        /// <summary>审计日志（可选）：临时状态的开/关/到期都留痕（执行计划 §G1 要求）。</summary>
        public Audit.AuditLog Auditor { get; set; }

        /// <summary>最近一次结束（界面/日志用）：名字、原因、时间。</summary>
        public string LastEndedName { get; private set; }
        public string LastEndedReason { get; private set; }
        public DateTime LastEndedAtUtc { get; private set; }

        public TempStateManager(string dataDir, RsaKeyStore keys, PolicyStore store, GroupStore groups)
        {
            _dataDir = dataDir;
            _keys = keys;
            _store = store;
            _groups = groups;
        }

        /// <summary>是否有临时覆盖在生效（= API 该用临时信封）。</summary>
        public bool Active { get { return Current != null; } }

        public string StatusText(DateTime nowUtc)
        {
            if (Current == null) return "无临时状态（当前 = 基础策略）";
            return "临时状态【" + Current.Name + "】进行中（" + Current.RemainingText(nowUtc) +
                   "，seq 至 " + Current.MaxSeq + "）";
        }

        // ------------------------------------------------------------------ 启动

        /// <summary>
        /// 启动时装载。**过期的一律丢弃并重发基础策略**（执行计划 §F3 明文要求：
        /// "服务端重启后不得意外永久保留已过期临时状态"）——否则老师昨晚开的考试模式会跟着服务端一起"活"到今天。
        /// </summary>
        public void Load(DateTime nowUtc)
        {
            TempState t = TempState.Load(_dataDir);
            if (t == null) return;
            if (t.IsExpiredAt(nowUtc))
            {
                Log.Warn("启动时发现**已过期**的临时状态【" + t.Name + "】（到期 " +
                         t.ExpiresAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                         "），已丢弃并重发基础策略");
                TempState.DeleteFile(_dataDir);
                RepublishBase(t, "启动时丢弃过期临时状态");
                LastEndedName = t.Name;
                LastEndedReason = "服务端重启时发现已过期";
                LastEndedAtUtc = nowUtc;
                Auditor?.Write(Audit.AuditAction.TempExpire, "classroom", t.Name, t.MaxSeq, _store.CurrentSeq,
                    "ok", null, "服务端重启时发现临时状态已过期 → 丢弃并重发基础策略（" + t.Name + "）", "系统");
                return;
            }
            if (!Validate(t, "启动装载"))
            {
                TempState.DeleteFile(_dataDir);
                return;
            }
            Current = t;
            Log.Info("已恢复进行中的临时状态【" + t.Name + "】（" + t.RemainingText(nowUtc) + "）");
            MarkDiskSeen();
        }

        /// <summary>
        /// 盯住 <c>temp-state.json</c>：**外部（CLI/脚本）开的临时状态也要能被运行中的服务端拾取**。
        ///
        /// 与分组/策略完全同一个设计理由（2026-10-13 测试台踩过两次）：内存状态只在启动时读一次的话，
        /// 外部进程改了磁盘就等于没改——磁盘上临时状态已经生效、HTTP 还在发基础策略，
        /// 表现为"命令说开始了，学生机毫无变化"。这里只**采用**别人算好的信封（签名与 seq 都由写入方算好），
        /// 不重新签发——避免两个进程各签一套、把 seq 推高两轮。
        /// </summary>
        public void StartWatching()
        {
            if (_watcher != null) return;
            try
            {
                Directory.CreateDirectory(_dataDir);
                _watcher = new FileSystemWatcher(_dataDir, TempState.FileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += (s, e) => TryReloadFromDisk();
                _watcher.Created += (s, e) => TryReloadFromDisk();
                _watcher.Renamed += (s, e) => TryReloadFromDisk();
                _watcher.Deleted += (s, e) => TryReloadFromDisk();
                Log.Info("已盯住临时状态文件（外部开的临时状态会被自动拾取）：" + TempState.PathOf(_dataDir));
            }
            catch (Exception ex)
            {
                Log.Warn("启动临时状态文件监视失败（只能靠重启拾取外部改动）：" + ex.Message);
                _watcher = null;
            }
        }

        /// <summary>尝试从磁盘拾取临时状态的变化（外部开/结束）。返回 true = 内存里的状态确实变了。</summary>
        public bool TryReloadFromDisk()
        {
            lock (_sync)
            {
                try
                {
                    string path = TempState.PathOf(_dataDir);
                    if (!File.Exists(path))
                    {
                        if (_diskSig == null) return false;                     // 从没见过文件：什么都没变
                        // 原子替换存在极短的"文件消失"窗口：重试几次再判定，别把"正在重写"当成"被删除"
                        bool reappeared = false;
                        for (int i = 0; i < 4 && !reappeared; i++)
                        {
                            System.Threading.Thread.Sleep(60);
                            reappeared = File.Exists(path);
                        }
                        if (reappeared) { /* 落下去走正常的内容比较 */ }
                        else
                        {
                            if (Current == null) { _diskSig = null; return false; }
                            Log.Info("临时状态文件已不存在 → 外部结束了临时状态【" + Current.Name + "】");
                            Current = null;
                            _diskSig = null;
                            return true;
                        }
                    }
                    string text = ReadDiskText();
                    if (text == null) return false;
                    string sig = ContentHash.Of(text);
                    if (sig == _diskSig) return false;
                    TempState t = TempState.Parse(text);
                    _diskSig = sig;
                    if (t == null) return false;
                    if (!Validate(t, "外部拾取"))
                    {
                        TempState.DeleteFile(_dataDir);
                        _diskSig = null;
                        Current = null;
                        return true;
                    }
                    bool changed = Current == null || Current.Id != t.Id;
                    Current = t;
                    if (changed)
                        Log.Info("已拾取外部开始的临时状态【" + t.Name + "】（" + t.RemainingText(DateTime.UtcNow) + "）");
                    return changed;
                }
                catch (Exception ex)
                {
                    Log.Warn("拾取临时状态失败（保留现行状态）：" + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>读临时状态文件（带退避重试：写入方可能还没关闭文件）。</summary>
        private string ReadDiskText()
        {
            string path = TempState.PathOf(_dataDir);
            for (int attempt = 0; attempt < 25; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                        return sr.ReadToEnd();
                }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                catch (IOException) { System.Threading.Thread.Sleep(80); }
            }
            return null;
        }

        /// <summary>记录"我现在看到的文件内容"，免得自己刚写完又被自己当成外部改动重载一遍。</summary>
        private void MarkDiskSeen()
        {
            try
            {
                string path = TempState.PathOf(_dataDir);
                _diskSig = File.Exists(path) ? ContentHash.Of(ReadDiskText() ?? "") : null;
            }
            catch { _diskSig = null; }
        }

        // ------------------------------------------------------------------ 开始

        /// <summary>
        /// 开始（或替换）临时状态：把 patch 合成到每个目标的**基础策略**上，逐目标签发新信封。
        /// <paramref name="expiresAtUtc"/> 为 null = 不自动到期（要人工结束）。
        /// </summary>
        public TempState Start(string name, string patchJson, bool paused, DateTime? expiresAtUtc, string startedBy, DateTime nowUtc)
        {
            if (_store.CurrentEnvelope == null)
                throw new InvalidOperationException("还没有保存过基础策略——先在策略面板保存一次，再开临时状态");
            Dictionary<string, object> patch = string.IsNullOrWhiteSpace(patchJson)
                ? new Dictionary<string, object>()
                : (Json.ParseObject(patchJson) ?? new Dictionary<string, object>());

            var t = new TempState
            {
                Id = "t-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = string.IsNullOrWhiteSpace(name) ? "临时状态" : name.Trim(),
                PatchJson = Json.Stringify(patch),
                Paused = paused,
                StartedAtUtc = nowUtc,
                ExpiresAtUtc = expiresAtUtc,
                StartedBy = startedBy ?? "",
            };
            Materialize(t);
            Current = t;
            t.Save(_dataDir);
            MarkDiskSeen();
            Log.Info("已开始临时状态【" + t.Name + "】（" + t.RemainingText(nowUtc) + "，覆盖 " +
                     (1 + t.GroupEnvelopes.Count) + " 个目标，seq 至 " + t.MaxSeq + "）");
            Auditor?.Write(Audit.AuditAction.TempStart, "classroom", t.Name, _store.CurrentSeq, t.MaxSeq,
                "ok", null, "临时课堂状态开始：" + t.Name + "（" + t.RemainingText(nowUtc) +
                "，覆盖 " + (1 + t.GroupEnvelopes.Count) + " 个目标）",
                string.IsNullOrEmpty(startedBy) ? null : startedBy);
            return t;
        }

        // ------------------------------------------------------------------ 结束 / 到期

        /// <summary>人工结束：把被覆盖的目标改回基础策略（新 seq），并删掉临时状态文件。</summary>
        public bool End(string reason, DateTime nowUtc)
        {
            return End(reason, nowUtc, Audit.AuditAction.TempEnd);
        }

        /// <summary>同 <see cref="End(string, DateTime)"/>，但可以指定审计动作名（人工结束 / 到期 / 重启丢弃）。</summary>
        public bool End(string reason, DateTime nowUtc, string auditAction)
        {
            if (Current == null) return false;
            TempState t = Current;
            Current = null;
            TempState.DeleteFile(_dataDir);
            _diskSig = null;
            RepublishBase(t, reason);
            LastEndedName = t.Name;
            LastEndedReason = reason;
            LastEndedAtUtc = nowUtc;
            Auditor?.Write(auditAction, "classroom", t.Name, t.MaxSeq, _store.CurrentSeq, "ok", null,
                "临时课堂状态结束（" + reason + "）→ 已把被覆盖的目标改回基础策略", "系统");
            return true;
        }

        /// <summary>到点就结束。返回 true = 本次真的结束了（调用方据此刷新界面/记日志）。</summary>
        public bool ExpireIfDue(DateTime nowUtc)
        {
            if (Current == null || !Current.IsExpiredAt(nowUtc)) return false;
            Log.Info("临时状态【" + Current.Name + "】已到期 → 自动恢复基础策略");
            return End("到期自动恢复", nowUtc, Audit.AuditAction.TempExpire);
        }

        // ------------------------------------------------------------------ 基础策略变了 → 重算临时信封

        /// <summary>
        /// 在临时状态进行中**又发布了基础/分组策略**时调用：用新的基础策略重新合成临时信封（新 seq）。
        /// 不重算的话，临时信封会停留在旧基础上——表现为"老师在面板改了东西，临时状态里的那一项没跟着变"。
        /// </summary>
        public bool RefreshIfActive(string reason)
        {
            if (Current == null) return false;
            Materialize(Current);
            Current.Save(_dataDir);
            MarkDiskSeen();
            Log.Info("基础策略已变更，已按新基础重算临时状态【" + Current.Name + "】的信封（seq 至 " +
                     Current.MaxSeq + "）：" + reason);
            return true;
        }

        // ------------------------------------------------------------------ 给 API 用

        /// <summary>该设备所属组的临时信封（组为空 = 默认组那一份）；无临时状态 → null（API 回落基础策略）。</summary>
        public string EnvelopeFor(DeviceGroup g)
        {
            return Current?.EnvelopeFor(g?.Id);
        }

        /// <summary>该目标当前"有效信封"的 seq（0 = 没有）。设备状态计算用。</summary>
        public ulong EffectiveSeqFor(DeviceGroup g)
        {
            string env = EnvelopeFor(g);
            if (string.IsNullOrEmpty(env)) return 0;
            return PolicyEnvelope.ParseHeader(env).Seq;
        }

        // ------------------------------------------------------------------ 内部

        /// <summary>
        /// 把 patch 合到"默认组 + 当前所有分组"的基础策略上，逐目标签发新信封并写进 tempState。
        /// 每个目标一个 seq（全局单调，永不重复——学生机按"严格大于已应用"判新旧）。
        /// </summary>
        private void Materialize(TempState t)
        {
            Dictionary<string, object> patch = string.IsNullOrEmpty(t.PatchJson)
                ? new Dictionary<string, object>()
                : (Json.ParseObject(t.PatchJson) ?? new Dictionary<string, object>());

            EnvelopeData baseEnv = PolicyEnvelope.Parse(_store.CurrentEnvelope, _keys.Rsa);
            if (!baseEnv.Verified) throw new InvalidDataException("基础策略信封验签失败，拒绝据此生成临时状态");

            t.DefaultEnvelope = BuildMerged(baseEnv.PolicyJson, patch, baseEnv.Paused || t.Paused, t);
            t.GroupEnvelopes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DeviceGroup g in _groups.Groups)
            {
                if (string.IsNullOrEmpty(g.Envelope)) continue;      // 还没下发过策略的组：不覆盖（它本来就在拿默认策略）
                EnvelopeData ge = PolicyEnvelope.Parse(g.Envelope, _keys.Rsa);
                if (!ge.Verified)
                {
                    Log.Warn("分组【" + g.Name + "】的信封验签失败，临时状态**跳过**该组（绝不基于不可信内容合成策略）");
                    continue;
                }
                t.GroupEnvelopes[g.Id] = BuildMerged(ge.PolicyJson, patch, ge.Paused || t.Paused, t);
            }
        }

        private string BuildMerged(string basePolicyJson, Dictionary<string, object> patch, bool paused, TempState t)
        {
            Dictionary<string, object> merged = PolicyMerge.Merge(Json.ParseObject(basePolicyJson), patch);
            ulong seq = NextSeq();
            if (seq > t.MaxSeq) t.MaxSeq = seq;
            return PolicyEnvelope.Build(seq, DateTime.UtcNow, paused, Json.Stringify(merged), _keys.Rsa);
        }

        /// <summary>取全局下一个 seq（先与磁盘对齐，多进程/多目标都不撞号）。</summary>
        private ulong NextSeq()
        {
            ulong seq = _store.RefreshWatermark() + 1;
            _store.MarkPublished(seq);
            return seq;
        }

        /// <summary>
        /// 临时状态里的**每个信封都必须验签通过**——否则拒绝采用并删掉这个文件。
        ///
        /// 为什么必须有这条：<c>temp-state.json</c> 是磁盘文件，能写它的人（最多到本机管理员）就能
        /// 塞一份"内容随便写、签名是假的"信封进来；而 API 会把它当"有效策略"发出去。
        /// 客户端会验签拒绝（不会真的执行坏策略），但老师看到的是"策略下发成功、学生机没变化"的假象。
        /// 所以口径与策略/分组一致：**先验签，再采用**。
        /// </summary>
        private bool Validate(TempState t, string phase)
        {
            try
            {
                if (string.IsNullOrEmpty(t.DefaultEnvelope)) return false;
                if (!PolicyEnvelope.Parse(t.DefaultEnvelope, _keys.Rsa).Verified)
                {
                    Log.Error("临时状态（" + phase + "）的默认信封验签失败，拒绝采用：" + t.Name);
                    return false;
                }
                foreach (KeyValuePair<string, string> kv in t.GroupEnvelopes ?? new Dictionary<string, string>())
                {
                    if (!PolicyEnvelope.Parse(kv.Value, _keys.Rsa).Verified)
                    {
                        Log.Error("临时状态（" + phase + "）里分组 " + kv.Key + " 的信封验签失败，拒绝采用：" + t.Name);
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("临时状态（" + phase + "）校验异常，拒绝采用", ex);
                return false;
            }
        }

        /// <summary>
        /// 把"被临时状态覆盖过的那几个目标"改回基础策略——**内容不变、换新 seq 重签**（见类注释）。
        /// 没被覆盖过的目标一个都不动（避免无谓地让全班学生机重启引擎）。
        /// </summary>
        private void RepublishBase(TempState t, string reason)
        {
            try
            {
                if (!string.IsNullOrEmpty(t.DefaultEnvelope) && _store.CurrentEnvelope != null)
                {
                    EnvelopeData d = PolicyEnvelope.Parse(_store.CurrentEnvelope, _keys.Rsa);
                    if (d.Verified)
                    {
                        PolicyVersion v = _store.Save(Json.ParseObject(d.PolicyJson), d.Paused, null);
                        Log.Info("已把默认组改回基础策略：seq=" + v.Seq + "（" + reason + "）");
                    }
                }
                bool groupsDirty = false;
                foreach (KeyValuePair<string, string> kv in t.GroupEnvelopes ?? new Dictionary<string, string>())
                {
                    DeviceGroup g = _groups.Get(kv.Key);
                    if (g == null) continue;                       // 组已被删：设备已回默认策略，无需处理
                    if (string.IsNullOrEmpty(g.Envelope)) continue;
                    EnvelopeData ge = PolicyEnvelope.Parse(g.Envelope, _keys.Rsa);
                    if (!ge.Verified) { Log.Warn("分组【" + g.Name + "】基础信封验签失败，跳过恢复"); continue; }
                    ulong seq = GroupPublisher.Publish(_keys, g, Json.ParseObject(ge.PolicyJson), ge.Paused,
                        () => _store.RefreshWatermark(), s => _store.MarkPublished(s));
                    groupsDirty = true;
                    Log.Info("已把分组【" + g.Name + "】改回基础策略：seq=" + seq + "（" + reason + "）");
                }
                if (groupsDirty) _groups.Save();
            }
            catch (Exception ex)
            {
                Log.Error("恢复基础策略失败（临时状态文件已删除；请检查后重新下发一次策略）", ex);
            }
        }

    }
}
