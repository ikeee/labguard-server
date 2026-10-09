using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LabGuardServer.Core.Envelope
{
    /// <summary>一次解析的结果。Verify 为 false 时其余字段不可信。</summary>
    public sealed class EnvelopeData
    {
        public ulong Seq;
        public DateTime IssuedAtUtc;
        public bool Paused;
        public string PolicyJson;
        public string SignatureBase64;
        public bool Verified;
    }

    /// <summary>
    /// LGSRV1 策略信封：LGSRV1|seq|issuedAt|paused|policyJson|signature
    /// 签名 = RSA-SHA256(PKCS#1 v1.5)，覆盖前五段拼接的 UTF-8 字节（docs/protocol-v1.md 是唯一真相源）。
    /// 解析约定：前 4 个 '|' 划定头部；最后一个 '|' 分隔签名（base64 不含 '|'），
    /// policyJson 里允许出现任意 '|'（List 字段一行一个的提示文案就含竖线转义，不能 naive Split）。
    /// </summary>
    public static class PolicyEnvelope
    {
        public const string Magic = "LGSRV1";

        /// <summary>组装并对给定 RSA 私钥签名。</summary>
        public static string Build(ulong seq, DateTime issuedAtUtc, bool paused, string policyJson, RSA rsa)
        {
            if (policyJson == null) throw new ArgumentNullException(nameof(policyJson));
            string payload = SignedPayload(seq, issuedAtUtc, paused, policyJson);
            byte[] sig = Sign(rsa, payload);
            return payload + "|" + Convert.ToBase64String(sig);
        }

        public static string SignedPayload(ulong seq, DateTime issuedAtUtc, bool paused, string policyJson)
        {
            return Magic + "|" + seq.ToString(CultureInfo.InvariantCulture) + "|" +
                   issuedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "|" +
                   (paused ? "1" : "0") + "|" + policyJson;
        }

        /// <summary>解析并验签。任何结构性问题 / 验签失败都返回 Verified=false（不抛异常，调用方决定告警口径）。</summary>
        public static EnvelopeData Parse(string envelope, RSA rsa)
        {
            var d = new EnvelopeData { Verified = false };
            if (string.IsNullOrEmpty(envelope) || rsa == null) return d;

            // 前 4 个 '|'：魔数/seq/issuedAt/paused
            int p1 = envelope.IndexOf('|');
            if (p1 < 0 || envelope.IndexOf(Magic + "|", StringComparison.Ordinal) != 0) return d;
            int p2 = envelope.IndexOf('|', p1 + 1);
            if (p2 < 0) return d;
            int p3 = envelope.IndexOf('|', p2 + 1);
            if (p3 < 0) return d;
            int p4 = envelope.IndexOf('|', p3 + 1);
            if (p4 < 0) return d;
            // 最后一个 '|' 分隔签名
            int pSig = envelope.LastIndexOf('|');
            if (pSig <= p4) return d;

            string magic = envelope.Substring(0, p1);
            if (magic != Magic) return d;
            if (!ulong.TryParse(envelope.Substring(p1 + 1, p2 - p1 - 1), NumberStyles.None, CultureInfo.InvariantCulture, out d.Seq)) return d;
            if (!DateTime.TryParse(envelope.Substring(p2 + 1, p3 - p2 - 1), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out d.IssuedAtUtc)) return d;
            string pausedField = envelope.Substring(p3 + 1, p4 - p3 - 1);
            if (pausedField != "0" && pausedField != "1") return d;
            d.Paused = pausedField == "1";
            d.PolicyJson = envelope.Substring(p4 + 1, pSig - p4 - 1);
            d.SignatureBase64 = envelope.Substring(pSig + 1);

            byte[] sig;
            try { sig = Convert.FromBase64String(d.SignatureBase64); }
            catch { return d; }

            string payload = envelope.Substring(0, pSig);
            try
            {
                d.Verified = Verify(rsa, payload, sig);
            }
            catch
            {
                d.Verified = false;
            }
            return d;
        }

        /// <summary>客户端防回滚规则（纯函数，客户端仓库同款语义）：新信封必须 seq 严格更大。</summary>
        public static bool IsNewer(ulong candidateSeq, ulong appliedSeq)
        {
            return candidateSeq > appliedSeq;
        }

        internal static byte[] Sign(RSA rsa, string payload)
        {
            return rsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        internal static bool Verify(RSA rsa, string payload, byte[] signature)
        {
            return rsa.VerifyData(Encoding.UTF8.GetBytes(payload), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
    }
}
