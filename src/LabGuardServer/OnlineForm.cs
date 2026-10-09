using System;
using System.Windows.Forms;
using LabGuardServer.Core.Api;

namespace LabGuardServer
{
    /// <summary>在线学生机列表：心跳聚合视图，3 秒刷新。</summary>
    internal sealed class OnlineForm : Form
    {
        private readonly ServerTrayContext _app;
        private readonly ListView _list = new ListView();
        private readonly Timer _timer = new Timer();

        public OnlineForm(ServerTrayContext app)
        {
            _app = app;
            Text = "LabGuard Server · 在线学生机";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(640, 420);
            MinimumSize = new System.Drawing.Size(520, 300);

            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.Columns.Add("机器名", 160);
            _list.Columns.Add("IP", 110);
            _list.Columns.Add("客户端版本", 90);
            _list.Columns.Add("策略 seq", 70);
            _list.Columns.Add("暂停", 50);
            _list.Columns.Add("最近心跳", 110);
            Controls.Add(_list);

            _timer.Interval = 3000;
            _timer.Tick += (s, e) => RefreshList();
            _timer.Start();
            RefreshList();
        }

        private void RefreshList()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (CheckinRecord r in _app.Api.Online.Snapshot())
            {
                var item = new ListViewItem(r.Machine);
                item.SubItems.Add(r.Ip ?? "?");
                item.SubItems.Add(r.Version ?? "?");
                item.SubItems.Add(r.Seq.ToString());
                item.SubItems.Add(r.Paused ? "是" : "");
                item.SubItems.Add(r.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss"));
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            Text = "LabGuard Server · 在线学生机（" + _list.Items.Count + " 台）";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            base.OnFormClosed(e);
        }
    }
}
