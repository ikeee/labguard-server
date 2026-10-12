using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using LabGuardServer.Core.Api;

namespace LabGuardServer
{
    /// <summary>
    /// 设备视图（v0.10.4）：在线学生机 + **每台设备的策略应用状态**。
    ///
    /// v0.10.3 之前只能看到"谁在心跳、最后上报 seq 是多少"，看不出"这台到底应用成功没有"。
    /// 现在按协议 §3 的设备状态模型分档显示：
    /// 已应用 / 待应用 / 应用失败 / 版本不一致 / 疑似克隆（重复ID）/ 仅在线（旧客户端）/ 离线。
    /// 状态计算是纯函数（<see cref="DeviceModel"/>），面板只是展示——同一口径也能写进日志。
    /// </summary>
    internal sealed class OnlineForm : Form
    {
        private readonly ServerTrayContext _app;
        private readonly ListView _list = new ListView();
        private readonly Timer _timer = new Timer();
        private readonly Label _summary = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Color.DimGray };

        private static readonly string ServerVersion =
            Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public OnlineForm(ServerTrayContext app)
        {
            _app = app;
            Icon = AppIcon.Get();
            Text = "LabGuard Server · 设备与策略状态";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(980, 460);
            MinimumSize = new System.Drawing.Size(820, 320);
            Font = new Font("微软雅黑", 9F);

            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = false;
            _list.Columns.Add("机器名", 150);
            _list.Columns.Add("设备 ID", 120);
            _list.Columns.Add("IP", 110);
            _list.Columns.Add("客户端", 80);
            _list.Columns.Add("目标 seq", 70);
            _list.Columns.Add("已应用", 60);
            _list.Columns.Add("状态", 130);
            _list.Columns.Add("最近心跳", 90);

            Controls.Add(_list);
            Controls.Add(_summary);

            _timer.Interval = 3000;
            _timer.Tick += (s, e) => RefreshList();
            _timer.Start();
            RefreshList();
        }

        private void RefreshList()
        {
            ulong serverSeq = _app.Store.CurrentSeq;
            DateTime now = DateTime.UtcNow;
            CheckinRecord[] devices = _app.Api.Online.Snapshot();

            int applied = 0, pending = 0, error = 0, other = 0;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (CheckinRecord r in devices)
            {
                DeviceStatus st = DeviceModel.Compute(r, serverSeq, ServerVersion, now, _app.Api.Online.OfflineAfter);
                switch (st)
                {
                    case DeviceStatus.Applied: applied++; break;
                    case DeviceStatus.Pending: pending++; break;
                    case DeviceStatus.Error: error++; break;
                    default: other++; break;
                }
                var item = new ListViewItem(r.Machine);
                item.SubItems.Add(string.IsNullOrEmpty(r.DeviceId) ? "（旧客户端）" : Short(r.DeviceId));
                item.SubItems.Add(r.Ip ?? "?");
                item.SubItems.Add(r.Version ?? "?");
                item.SubItems.Add(serverSeq.ToString());
                item.SubItems.Add(r.AppliedSeq.ToString());
                string status = DeviceModel.Describe(st);
                if (st == DeviceStatus.Error && !string.IsNullOrEmpty(r.ErrorCode)) status += "：" + r.ErrorCode;
                if (r.SeenMachineNames > 1) status += "（见过 " + r.SeenMachineNames + " 个机器名）";
                item.SubItems.Add(status);
                item.SubItems.Add(r.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss"));
                item.ForeColor = StatusColor(st);
                _list.Items.Add(item);
            }
            _list.EndUpdate();

            _summary.Text = "  当前策略 seq=" + serverSeq + "（服务端 v" + ServerVersion + "）    |    " +
                            "在线 " + devices.Length + " 台：已应用 " + applied + " / 待应用 " + pending +
                            " / 应用失败 " + error + " / 其他 " + other + "    |    已登记设备 " + _app.Api.Online.Count + " 台";
            Text = "LabGuard Server · 设备与策略状态（在线 " + devices.Length + "）";
        }

        private static Color StatusColor(DeviceStatus st)
        {
            switch (st)
            {
                case DeviceStatus.Applied: return Color.FromArgb(20, 110, 40);
                case DeviceStatus.Pending: return Color.FromArgb(150, 90, 0);
                case DeviceStatus.Error: return Color.FromArgb(170, 30, 20);
                case DeviceStatus.CloneSuspect: return Color.FromArgb(140, 40, 140);
                case DeviceStatus.VersionSkew: return Color.FromArgb(90, 90, 120);
                default: return Color.FromArgb(40, 44, 50);
            }
        }

        private static string Short(string deviceId)
        {
            return deviceId.Length <= 12 ? deviceId : deviceId.Substring(0, 12) + "…";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            base.OnFormClosed(e);
        }
    }
}
