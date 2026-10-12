using System;
using System.Collections.Concurrent;
using System.Linq;

namespace LabGuardServer.Core.Api
{
    /// <summary>
    /// 一次学生机心跳（v0.10.4：加入稳定设备身份与策略应用结果）。
    /// 所有字段都是**固定 schema + 长度上限**的短文本，属于协议允许的"客户端状态数据"。
    /// </summary>
    public sealed class CheckinRecord
    {
        /// <summary>机器名（旧客户端唯一可用的身份；新客户端仍上报，用于诊断"改名/克隆"）。</summary>
        public string Machine;
        /// <summary>稳定设备 ID（`LG-` + 16 hex）；旧客户端为空 → 用机器名兜底。</summary>
        public string DeviceId;
        public string Version;
        /// <summary>已应用策略序号（历史字段名；v0.10.4 起语义固定为"已应用"）。</summary>
        public ulong Seq;
        /// <summary>v0.10.4 显式字段：已成功应用的策略序号（缺省时取 Seq）。</summary>
        public ulong AppliedSeq;
        /// <summary>客户端是否**真的上报过**序号（旧客户端可能什么都不报 → 只能判"在线"，不能判"待应用"）。</summary>
        public bool HasAppliedSeq;
        public bool Paused;
        /// <summary>应用结果：`ok` / `error` / 空（旧客户端 = 未上报）。</summary>
        public string Status;
        public string ErrorCode;
        public string Ip;
        public DateTime LastSeenUtc;
        /// <summary>该 deviceId 至今见过几种机器名（&gt;1 → 疑似克隆镜像重复身份）。</summary>
        public int SeenMachineNames = 1;

        /// <summary>面板/日志里显示的身份（旧客户端显示机器名）。</summary>
        public string Identity { get { return string.IsNullOrEmpty(DeviceId) ? Machine : DeviceId; } }
    }

    /// <summary>
    /// 在线学生机列表：按**设备身份**聚合心跳，超时（默认 60s）判离线，快照按机器名排序。
    ///
    /// 身份优先级：`deviceId` &gt; 机器名（旧客户端）。同一 deviceId 来自多个机器名时累计
    /// <see cref="CheckinRecord.SeenMachineNames"/>，供"克隆镜像"诊断（协议 §3 设备状态模型）。
    /// </summary>
    public sealed class OnlineList
    {
        public TimeSpan OfflineAfter { get; set; } = TimeSpan.FromSeconds(60);

        private readonly ConcurrentDictionary<string, CheckinRecord> _devices =
            new ConcurrentDictionary<string, CheckinRecord>(StringComparer.OrdinalIgnoreCase);

        /// <summary>deviceId → 见过的机器名集合（名数有上限，防异常客户端把内存撑大）。</summary>
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _namesSeen =
            new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.OrdinalIgnoreCase);

        private const int MaxTrackedNames = 8;

        public void Record(string machine, string version, ulong appliedSeq, bool hasAppliedSeq, bool paused, string ip,
            string deviceId = null, string status = null, string errorCode = null)
        {
            if (string.IsNullOrEmpty(machine)) machine = "(unknown)";
            if (machine.Length > 64) machine = machine.Substring(0, 64);
            if (deviceId != null && deviceId.Length > 64) deviceId = deviceId.Substring(0, 64);
            if (status != null && status.Length > 16) status = status.Substring(0, 16);
            if (errorCode != null && errorCode.Length > 64) errorCode = errorCode.Substring(0, 64);

            string key = string.IsNullOrEmpty(deviceId) ? machine : deviceId;
            int seenNames = 1;
            if (!string.IsNullOrEmpty(deviceId))
            {
                ConcurrentDictionary<string, byte> names = _namesSeen.GetOrAdd(deviceId,
                    _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
                names[machine] = 1;
                if (names.Count > MaxTrackedNames)
                    _namesSeen[deviceId] = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase) { [machine] = 1 };
                seenNames = Math.Max(1, names.Count);
            }

            _devices[key] = new CheckinRecord
            {
                Machine = machine,
                DeviceId = deviceId,
                Version = version,
                Seq = appliedSeq,
                AppliedSeq = appliedSeq,
                HasAppliedSeq = hasAppliedSeq,
                Paused = paused,
                Status = status,
                ErrorCode = errorCode,
                Ip = ip,
                LastSeenUtc = DateTime.UtcNow,
                SeenMachineNames = seenNames,
            };
        }

        /// <summary>最近有心跳的设备（默认 60s 内），按机器名排序。</summary>
        public CheckinRecord[] Snapshot()
        {
            DateTime now = DateTime.UtcNow;
            return _devices.Values
                .Where(r => now - r.LastSeenUtc <= OfflineAfter)
                .OrderBy(r => r.Machine, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>设备总数（含离线）——教师端"已登记设备"用。</summary>
        public int Count { get { return _devices.Count; } }
    }
}
