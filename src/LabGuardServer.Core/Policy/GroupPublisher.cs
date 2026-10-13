using System;
using System.Collections.Generic;
using System.Globalization;
using LabGuardServer.Core.Envelope;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Util;

namespace LabGuardServer.Core.Policy
{
    /// <summary>
    /// 给某个组签发新策略信封。
    ///
    /// **关键约束：全局序号单调**（`seq` 在所有组之间共享一个计数器）——学生机的防回滚规则是
    /// "seq 必须严格大于已应用值"；若每组各自从 1 开始，
    /// 设备调组（或换组下发）就会拿到"更低 seq"的策略而被**正确拒绝**，表现为"调了组却不生效"。
    /// 因此每次发布（无论哪个组）都取 `max(全局水位, 本组 seq) + 1`，并把水位推进到该值。
    /// </summary>
    public static class GroupPublisher
    {
        /// <summary>发布某组策略，返回新的 seq。水位读写由调用方接进 PolicyStore 的全局水位。</summary>
        public static ulong Publish(RsaKeyStore keys, DeviceGroup group, Dictionary<string, object> policy, bool paused,
            Func<ulong> watermarkReader, Action<ulong> watermarkWriter)
        {
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            if (group == null) throw new ArgumentNullException(nameof(group));
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            ulong watermark = watermarkReader != null ? watermarkReader() : 0;
            ulong seq = Math.Max(watermark, group.Seq) + 1;
            string json = Json.Stringify(policy);
            string envelope = PolicyEnvelope.Build(seq, DateTime.UtcNow, paused, json, keys.Rsa);
            group.Envelope = envelope;
            group.Seq = seq;
            group.Paused = paused;
            group.IssuedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            if (watermarkWriter != null) watermarkWriter(seq);
            Log.Info("已下发到组【" + group.Name + "】：seq=" + seq + (paused ? "（暂停）" : "") +
                     "，成员 " + (group.DeviceIds.Count + group.Machines.Count) + " 个");
            return seq;
        }

        /// <summary>从某组信封里读出它的版本信息（界面显示"该组当前 seq"）。</summary>
        public static PolicyVersion VersionOf(RsaKeyStore keys, DeviceGroup group)
        {
            if (group == null || string.IsNullOrEmpty(group.Envelope) || keys == null) return null;
            EnvelopeData d = PolicyEnvelope.Parse(group.Envelope, keys.Rsa);
            if (!d.Verified) return null;
            return new PolicyVersion
            {
                Seq = d.Seq,
                Paused = d.Paused,
                IssuedAtUtc = d.IssuedAtUtc,
            };
        }
    }
}
