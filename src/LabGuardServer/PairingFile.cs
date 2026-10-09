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

        public static PairingDoc Build(string publicKeyXml, string fingerprint, int port)
        {
            return new PairingDoc
            {
                format = "labguard-pairing/1",
                serverHost = Dns.GetHostName(),
                addresses = LocalIpv4Addresses(),
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
