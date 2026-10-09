using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LabGuardServer.Core.Envelope;
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

        public string DataDir { get; }
        private string PolicyPath => Path.Combine(DataDir, PolicyFileName);
        private readonly RsaKeyStore _keys;

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
            if (!File.Exists(PolicyPath))
            {
                CurrentEnvelope = null;
                CurrentSeq = 0;
                return;
            }
            string raw = File.ReadAllText(PolicyPath, Encoding.UTF8);
            EnvelopeData d = PolicyEnvelope.Parse(raw, _keys.Rsa);
            if (!d.Verified)
                throw new InvalidDataException("策略信封验签失败（policy.envelope 疑似被篡改）——拒绝加载，请人工检查 " + PolicyPath);
            if (minSeq.HasValue && d.Seq < minSeq.Value)
                throw new InvalidDataException("拒绝回滚：磁盘信封 seq=" + d.Seq + " 低于当前 seq=" + minSeq.Value +
                    "（签名虽合法但为旧版本信封）");
            CurrentEnvelope = raw;
            CurrentSeq = d.Seq;
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
            ulong seq = CurrentSeq + 1;
            string envelope = PolicyEnvelope.Build(seq, DateTime.UtcNow, paused, json, _keys.Rsa);
            File.WriteAllText(PolicyPath, envelope, new UTF8Encoding(false));
            CurrentEnvelope = envelope;
            CurrentSeq = seq;
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
    }
}
