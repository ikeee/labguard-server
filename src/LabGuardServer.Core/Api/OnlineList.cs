using System;
using System.Collections.Concurrent;
using System.Linq;

namespace LabGuardServer.Core.Api
{
    /// <summary>一次学生机心跳。</summary>
    public sealed class CheckinRecord
    {
        public string Machine;
        public string Version;
        public ulong Seq;
        public bool Paused;
        public string Ip;
        public DateTime LastSeenUtc;
    }

    /// <summary>在线学生机列表：checkin 聚合，超时（默认 60s）判离线。快照按机器名排序。</summary>
    public sealed class OnlineList
    {
        public TimeSpan OfflineAfter { get; set; } = TimeSpan.FromSeconds(60);
        private readonly ConcurrentDictionary<string, CheckinRecord> _machines =
            new ConcurrentDictionary<string, CheckinRecord>(StringComparer.OrdinalIgnoreCase);

        public void Record(string machine, string version, ulong seq, bool paused, string ip)
        {
            if (string.IsNullOrEmpty(machine)) machine = "(unknown)";
            if (machine.Length > 64) machine = machine.Substring(0, 64);
            _machines[machine] = new CheckinRecord
            {
                Machine = machine, Version = version, Seq = seq, Paused = paused,
                Ip = ip, LastSeenUtc = DateTime.UtcNow,
            };
        }

        public CheckinRecord[] Snapshot()
        {
            DateTime now = DateTime.UtcNow;
            return _machines.Values
                .Where(r => now - r.LastSeenUtc <= OfflineAfter)
                .OrderBy(r => r.Machine, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
}
