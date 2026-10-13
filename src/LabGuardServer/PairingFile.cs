using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using LabGuardServer.Core.Catalog;
using LabGuardServer.Core.Util;

namespace LabGuardServer
{
    /// <summary>配对文件导出：学生机 install.ps1 -Pair <文件> 的输入。含公钥与指纹（人工核对用）。</summary>
    internal static class PairingFile
    {
        public sealed class PairingDoc
        {
            public string format;
            public string serverHost;
            public string[] addresses;
            public int port;
            public string publicKeyXml;
            public string fingerprint;
            public string generatedAt;
        }

        /// <summary>
        /// 生成配对文件。<paramref name="addressOverride"/> 非空时**只用这一个地址**（不列本机各网卡）。
        ///
        /// 用途：单机端到端测试台。学生端只会连 `addresses[0]`，而本机 IPv4 列表里**不含回环**，
        /// 于是"服务端只监听 127.0.0.1"时学生端永远连不上（必须先绑 `+`，而绑 `+` 要管理员/URLACL）。
        /// 测试台用 `--pairing-address 127.0.0.1` 就能免提权跑通；正式部署仍用各网卡地址。
        /// </summary>
        public static PairingDoc Build(string publicKeyXml, string fingerprint, int port, string addressOverride = null)
        {
            return new PairingDoc
            {
                format = "labguard-pairing/1",
                serverHost = Dns.GetHostName(),
                addresses = string.IsNullOrWhiteSpace(addressOverride)
                    ? LocalIpv4Addresses()
                    : new[] { addressOverride.Trim() },
                port = port,
                publicKeyXml = publicKeyXml,
                fingerprint = fingerprint,
                generatedAt = DateTime.UtcNow.ToString("o"),
            };
        }

        public static void Export(PairingDoc doc, string path)
        {
            File.WriteAllText(path, Json.Stringify(doc), new System.Text.UTF8Encoding(false));
        }

        /// <summary>本机 IPv4（机房网卡），排除回环与自动私有地址。</summary>
        public static string[] LocalIpv4Addresses()
        {
            try
            {
                return Dns.GetHostAddresses(Dns.GetHostName())
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    .Select(a => a.ToString())
                    .ToArray();
            }
            catch
            {
                return new string[0];
            }
        }
    }
}
