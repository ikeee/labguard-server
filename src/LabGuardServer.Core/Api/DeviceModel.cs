using System;

namespace LabGuardServer.Core.Api
{
    /// <summary>设备在教师端列表里的状态（协议 §3「设备状态模型」）。</summary>
    public enum DeviceStatus
    {
        /// <summary>有心跳但没带 appliedSeq（旧客户端）：只能证明"在线"。</summary>
        Online,
        /// <summary>已应用当前策略（appliedSeq ≥ 服务端 seq）。</summary>
        Applied,
        /// <summary>服务端已发布新策略，该机还没用上（appliedSeq &lt; 服务端 seq）。</summary>
        Pending,
        /// <summary>客户端最近一次应用失败（status=error，带 errorCode）。</summary>
        Error,
        /// <summary>客户端主次版本与服务端不一致（仍继续服务，只标注）。</summary>
        VersionSkew,
        /// <summary>同一 deviceId 来自多台机器名 → 疑似克隆镜像带来的重复身份。</summary>
        CloneSuspect,
        /// <summary>超过阈值没有心跳。</summary>
        Offline,
    }

    /// <summary>
    /// 设备状态计算：**纯函数**（便于自检断言，也让面板/日志共用同一口径）。
    ///
    /// 优先级（先出的赢）：克隆嫌疑 &gt; 应用失败 &gt; 版本错配 &gt; 已应用 / 待应用 / 仅在线。
    /// 理由：身份问题会让"这台机器到底是谁"都不可信，必须最先暴露；应用失败次之（要人处理）；
    /// 版本错配是可继续服务的降级项；最后才是正常态。
    /// </summary>
    public static class DeviceModel
    {
        public static DeviceStatus Compute(CheckinRecord r, ulong serverSeq, string serverVersion, DateTime nowUtc,
            TimeSpan offlineAfter)
        {
            if (r == null) return DeviceStatus.Offline;
            if (nowUtc - r.LastSeenUtc > offlineAfter) return DeviceStatus.Offline;
            if (r.SeenMachineNames > 1) return DeviceStatus.CloneSuspect;
            if (string.Equals(r.Status, "error", StringComparison.OrdinalIgnoreCase)) return DeviceStatus.Error;
            if (VersionSkew(r.Version, serverVersion)) return DeviceStatus.VersionSkew;
            if (!r.HasAppliedSeq) return DeviceStatus.Online;      // 旧客户端没报序号：只能证明"在线"
            if (r.AppliedSeq >= serverSeq) return DeviceStatus.Applied;
            return serverSeq > 0 ? DeviceStatus.Pending : DeviceStatus.Online;
        }

        /// <summary>主次版本不同即算错配（补丁号不同不算）；客户端没报版本 = 未知，不判错配。</summary>
        public static bool VersionSkew(string clientVersion, string serverVersion)
        {
            string a = MajorMinor(clientVersion), b = MajorMinor(serverVersion);
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string MajorMinor(string v)
        {
            if (string.IsNullOrWhiteSpace(v) || v == "?") return null;
            string[] parts = v.Trim().Split('.');
            return parts.Length >= 2 ? parts[0] + "." + parts[1] : parts[0];
        }

        public static string Describe(DeviceStatus s)
        {
            switch (s)
            {
                case DeviceStatus.Applied: return "已应用";
                case DeviceStatus.Pending: return "待应用";
                case DeviceStatus.Error: return "应用失败";
                case DeviceStatus.VersionSkew: return "版本不一致";
                case DeviceStatus.CloneSuspect: return "疑似克隆（重复ID）";
                case DeviceStatus.Offline: return "离线";
                default: return "在线";
            }
        }
    }
}
