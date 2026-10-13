using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LabGuardServer.Core.Api;
using LabGuardServer.Core.Policy;

namespace LabGuardServer
{
    /// <summary>
    /// 设备分组（v0.11 最小集，执行计划 §F2）：建组 / 改名 / 删组 / 把设备放进组（一台设备只属于一个主组）。
    ///
    /// 设计取舍：这里**只做成员管理**，"向某个组下发策略"放在策略面板（顶部「下发目标」）——
    /// 避免"在分组窗口里改策略"这种职责混淆。默认组不在这里出现：它就是策略面板上的「全部设备（默认策略）」。
    /// </summary>
    internal sealed class GroupsForm : Form
    {
        private readonly ServerTrayContext _app;
        private readonly ListBox _groups = new ListBox();
        private readonly ListView _members = new ListView();
        private readonly Label _hint = new Label { Dock = DockStyle.Top, Height = 44, ForeColor = Color.DimGray, Padding = new Padding(10, 6, 10, 0) };

        public GroupsForm(ServerTrayContext app)
        {
            _app = app;
            Icon = AppIcon.Get();
            Text = "LabGuard Server · 设备分组";
            Font = new Font("微软雅黑", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 520);
            MinimumSize = new Size(760, 420);

            var left = new Panel { Dock = DockStyle.Left, Width = 300, BackColor = Color.White };
            var leftTitle = new Label
            {
                Dock = DockStyle.Top, Height = 28, Text = "  分组（默认组不在此）", TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("微软雅黑", 9.5F, FontStyle.Bold),
            };
            _groups.Dock = DockStyle.Fill;
            _groups.IntegralHeight = false;
            _groups.BorderStyle = BorderStyle.None;
            _groups.ItemHeight = 26;
            _groups.SelectedIndexChanged += (s, e) => RefreshMembers();
            left.Controls.Add(_groups);
            left.Controls.Add(leftTitle);
            _groups.BringToFront();

            var bars = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
            var bNew = new Button { Text = "新建组", Width = 84, Height = 28 };
            bNew.Click += (s, e) => NewGroup();
            var bRename = new Button { Text = "改名", Width = 70, Height = 28 };
            bRename.Click += (s, e) => RenameGroup();
            var bDelete = new Button { Text = "删除组", Width = 84, Height = 28 };
            bDelete.Click += (s, e) => DeleteGroup();
            bars.Controls.AddRange(new Control[] { bNew, bRename, bDelete });
            left.Controls.Add(bars);
            bars.BringToFront();

            var right = new Panel { Dock = DockStyle.Fill };
            var rightTitle = new Label
            {
                Dock = DockStyle.Top, Height = 28, Text = "  组成员（勾选=放进本组；取消勾选=移回默认策略）",
                TextAlign = ContentAlignment.MiddleLeft, Font = new Font("微软雅黑", 9.5F, FontStyle.Bold),
            };
            var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
            var bSave = new Button { Text = "保存成员", Width = 100, Height = 28 };
            bSave.Click += (s, e) => SaveMembers();
            var bReload = new Button { Text = "刷新设备列表", Width = 110, Height = 28 };
            bReload.Click += (s, e) => RefreshMembers();
            btns.Controls.AddRange(new Control[] { bSave, bReload });

            _members.Dock = DockStyle.Fill;
            _members.View = View.Details;
            _members.CheckBoxes = true;
            _members.FullRowSelect = true;
            _members.Columns.Add("机器名", 160);
            _members.Columns.Add("设备 ID", 140);
            _members.Columns.Add("IP", 110);
            _members.Columns.Add("状态", 90);
            _members.Columns.Add("当前分组", 140);

            right.Controls.Add(_members);
            right.Controls.Add(btns);
            right.Controls.Add(rightTitle);
            right.Controls.Add(_hint);
            _members.BringToFront();
            btns.BringToFront();
            rightTitle.BringToFront();
            _hint.BringToFront();

            Controls.Add(right);
            Controls.Add(left);

            RefreshGroups();
            RefreshMembers();
        }

        private void RefreshGroups()
        {
            string keep = CurrentGroupId();
            _groups.BeginUpdate();
            _groups.Items.Clear();
            foreach (DeviceGroup g in _app.Groups.Groups)
            {
                int members = g.DeviceIds.Count + g.Machines.Count;
                ulong seq = g.Seq;
                _groups.Items.Add(new GroupItem(g.Id, g.Name + "    (" + members + " 台 · seq " + seq + ")"));
            }
            _groups.EndUpdate();
            if (keep != null)
                for (int i = 0; i < _groups.Items.Count; i++)
                    if (((GroupItem)_groups.Items[i]).Id == keep) { _groups.SelectedIndex = i; break; }
            if (_groups.SelectedIndex < 0 && _groups.Items.Count > 0) _groups.SelectedIndex = 0;
        }

        private string CurrentGroupId()
        {
            var it = _groups.SelectedItem as GroupItem;
            return it?.Id;
        }

        private sealed class GroupItem
        {
            public readonly string Id;
            private readonly string _text;
            public GroupItem(string id, string text) { Id = id; _text = text; }
            public override string ToString() { return _text; }
        }

        private void RefreshMembers()
        {
            string groupId = CurrentGroupId();
            DeviceGroup g = groupId != null ? _app.Groups.Get(groupId) : null;
            _hint.Text = g == null
                ? "先在左边建一个组，再从下面的设备列表里勾选成员。没被任何组勾选的设备用「默认策略」。"
                : "正在编辑组【" + g.Name + "】：勾选 = 放进本组；取消勾选 = 移回默认策略。"
                  + "（同一台设备只能属于一个主组，勾选会自动把它从原组移走）";

            _members.BeginUpdate();
            _members.Items.Clear();
            CheckinRecord[] devices = _app.Api.Online.Snapshot();
            foreach (CheckinRecord r in devices)
            {
                DeviceGroup owner = _app.Groups.Resolve(r.DeviceId, r.Machine);
                string state = string.IsNullOrEmpty(r.DeviceId) ? "旧客户端" :
                               (r.Status == "error" ? "应用失败" : "在线");
                var item = new ListViewItem(r.Machine);
                item.SubItems.Add(string.IsNullOrEmpty(r.DeviceId) ? "（旧客户端）" : r.DeviceId);
                item.SubItems.Add(r.Ip ?? "?");
                item.SubItems.Add(state);
                item.SubItems.Add(owner == null ? "默认策略" : owner.Name);
                item.Tag = new string[] { r.DeviceId, r.Machine };
                item.Checked = g != null &&
                               ((!string.IsNullOrEmpty(r.DeviceId) && g.DeviceIds.Any(d => string.Equals(d, r.DeviceId, StringComparison.OrdinalIgnoreCase))) ||
                                (g.Machines.Any(m => string.Equals(m, r.Machine, StringComparison.OrdinalIgnoreCase))));
                _members.Items.Add(item);
            }
            _members.EndUpdate();
        }

        private void NewGroup()
        {
            string name = Prompt("新组的名字（例如：高一(3)班机房）", "新建分组", "机房组 " + (_app.Groups.Groups.Count + 1));
            if (string.IsNullOrWhiteSpace(name)) return;
            _app.Groups.Add(name.Trim());
            _app.Groups.Save();
            RefreshGroups();
            RefreshMembers();
        }

        private void RenameGroup()
        {
            string id = CurrentGroupId();
            if (id == null) { Warn("先在左边选中一个组。"); return; }
            DeviceGroup g = _app.Groups.Get(id);
            string name = Prompt("组的新名字", "重命名分组", g.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            _app.Groups.Rename(id, name);
            _app.Groups.Save();
            RefreshGroups();
            RefreshMembers();
        }

        private void DeleteGroup()
        {
            string id = CurrentGroupId();
            if (id == null) { Warn("先在左边选中一个组。"); return; }
            DeviceGroup g = _app.Groups.Get(id);
            if (MessageBox.Show(this, "删除组【" + g.Name + "】？\n\n组里的设备会回到「默认策略」，已经下发过的组策略信封一并丢弃。",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            _app.Groups.Remove(id);
            _app.Groups.Save();
            RefreshGroups();
            RefreshMembers();
        }

        private void SaveMembers()
        {
            string id = CurrentGroupId();
            if (id == null) { Warn("先在左边选中一个组，再勾选成员。"); return; }
            var devices = new List<string[]>();
            var checkedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem item in _members.Items)
            {
                var tags = (string[])item.Tag;
                devices.Add(tags);
                if (item.Checked)
                {
                    if (!string.IsNullOrEmpty(tags[0])) checkedKeys.Add(tags[0]);
                    checkedKeys.Add("M:" + tags[1]);
                }
            }
            foreach (string[] d in devices)
            {
                bool inGroup = (!string.IsNullOrEmpty(d[0]) && checkedKeys.Contains(d[0])) || checkedKeys.Contains("M:" + d[1]);
                if (inGroup) _app.Groups.Assign(id, d[0], d[1]);
                else _app.Groups.RemoveMember(d[0], d[1]);
            }
            _app.Groups.Save();
            RefreshGroups();
            RefreshMembers();
            MessageBox.Show(this, "已保存组成员。\n\n提示：成员只决定\"谁拿哪份策略\"；要给这个组下发策略，请在【策略面板】顶部把「下发目标」选成这个组，再点保存并下发。",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Warn(string msg) { MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }

        private static string Prompt(string label, string title, string initial)
        {
            using (var dlg = new Form())
            {
                dlg.Text = title;
                dlg.ClientSize = new Size(420, 130);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = false; dlg.MaximizeBox = false;
                var lbl = new Label { Text = label, AutoSize = true, Left = 14, Top = 16 };
                var box = new TextBox { Left = 14, Top = 40, Width = 392, Text = initial ?? "" };
                var ok = new Button { Text = "确定", Left = 240, Top = 80, Width = 80, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "取消", Left = 326, Top = 80, Width = 80, DialogResult = DialogResult.Cancel };
                dlg.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
                dlg.AcceptButton = ok; dlg.CancelButton = cancel;
                return dlg.ShowDialog() == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
