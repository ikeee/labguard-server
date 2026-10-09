using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LabGuardServer.Core.Catalog;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Policy;
using LabGuardServer.Core.Util;

namespace LabGuardServer
{
    /// <summary>
    /// 策略面板：控件全部由 catalog.json 动态渲染（不手写任何一项——客户端加配置项，这里自动出现）。
    /// 左侧分组列表，右侧当前分组字段；底部 保存并下发 / 恢复默认 / 全员暂停 / 全员恢复。
    /// </summary>
    internal sealed class PolicyForm : Form
    {
        private readonly ServerTrayContext _app;
        private readonly ListBox _sections = new ListBox();
        private readonly Panel _body = new Panel { AutoScroll = true, Dock = DockStyle.Fill };
        private readonly Label _status = new Label();

        private Dictionary<string, object> _policy;
        private readonly List<FieldControlBinding> _bindings = new List<FieldControlBinding>();

        private sealed class FieldControlBinding
        {
            public CatalogField Field;
            public Control Control;
        }

        public PolicyForm(ServerTrayContext app)
        {
            _app = app;
            Text = PasswordGate.PolicyPanelTitle;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(880, 640);
            MinimumSize = new Size(720, 480);

            // 读当前策略作为底稿（未保存过 → 目录默认值）
            _policy = app.Store.CurrentOrDefaultPolicy(app.Catalog);

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 220 };
            _sections.Dock = DockStyle.Fill;
            _sections.IntegralHeight = false;
            _sections.Font = new Font(_sections.Font, FontStyle.Bold);
            foreach (string s in app.Catalog.Sections()) _sections.Items.Add(s);
            _sections.SelectedIndexChanged += (s, e) => { FlushBindings(); BuildSection(); };
            split.Panel1.Controls.Add(_sections);

            var right = new Panel { Dock = DockStyle.Fill };
            _body.Parent = right;
            _body.Dock = DockStyle.Fill;
            _body.Padding = new Padding(12);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 76 };
            _status.Dock = DockStyle.Top;
            _status.Height = 24;
            _status.ForeColor = Color.DimGray;

            var save = new Button { Text = "保存并下发", Width = 110, Height = 32, Left = 12, Top = 40 };
            save.BackColor = Color.FromArgb(0, 120, 215);
            save.ForeColor = Color.White;
            save.FlatStyle = FlatStyle.Flat;
            save.Click += (s, e) => SavePolicy(false);

            var defaults = new Button { Text = "恢复默认", Width = 90, Height = 32, Left = 130, Top = 40 };
            defaults.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "把所有配置恢复成默认值？（未保存的修改会丢失）",
                        Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                _policy = Json.ParseObject(_app.Catalog.DefaultPolicyJson);
                FlushBindings();
                BuildSection();
            };

            var pause = new Button { Text = "全员暂停", Width = 90, Height = 32, Left = 230, Top = 40 };
            pause.Click += (s, e) => PauseAll(true);
            var resume = new Button { Text = "全员恢复", Width = 90, Height = 32, Left = 328, Top = 40 };
            resume.Click += (s, e) => PauseAll(false);

            bottom.Controls.AddRange(new Control[] { _status, save, defaults, pause, resume });
            right.Controls.Add(_body);
            right.Controls.Add(bottom);
            split.Panel2.Controls.Add(right);
            Controls.Add(split);

            if (_sections.Items.Count > 0) _sections.SelectedIndex = 0;
            RefreshStatus();
        }

        private void SavePolicy(bool silent)
        {
            FlushBindings();
            try
            {
                bool paused = _app.Store.Version()?.Paused ?? false;
                PolicyVersion v = _app.Store.Save(_policy, paused, _app.Catalog.DefaultPolicyJson);
                Log.Info("策略已保存下发 seq=" + v.Seq + " paused=" + v.Paused);
                RefreshStatus();
                if (!silent)
                    MessageBox.Show(this, "已保存并下发（seq=" + v.Seq + "）。\n学生机默认 5 秒内生效。",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PauseAll(bool paused)
        {
            string verb = paused ? "暂停" : "恢复";
            if (MessageBox.Show(this,
                    paused
                        ? "全员暂停：所有学生机停止管控策略（进程守护照旧）。\n确定？"
                        : "全员恢复：所有学生机回到服务器策略管控。\n确定？",
                    Text, MessageBoxButtons.OKCancel, paused ? MessageBoxIcon.Warning : MessageBoxIcon.Question)
                != DialogResult.OK) return;
            try
            {
                // 暂停/恢复也带上当前面板里未保存的修改（老师改完顺手点暂停是常见顺序）；
                // 信封的 paused 位由本操作定，策略 JSON 用面板当前值
                FlushBindings();
                _app.Store.Save(_policy, paused, _app.Catalog.DefaultPolicyJson);
                RefreshStatus();
                Log.Info((paused ? "全员暂停" : "全员恢复") + " 已下发 seq=" + _app.Store.CurrentSeq);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "操作失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RefreshStatus()
        {
            PolicyVersion v = _app.Store.Version();
            string state = v == null
                ? "尚未保存过策略（学生机连上会收到 503，按单机模式继续）"
                : "seq=" + v.Seq + (v.Paused ? " · 全员暂停中" : " · 管控中") + " · 下发时间 " + v.IssuedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
            _status.Text = "  " + state +
                "  |  监听 " + (_app.Api.BoundPrefix ?? "未启动") +
                "  |  在线学生机 " + _app.Api.Online.Snapshot().Length +
                "  |  目录快照：客户端 v" + _app.Catalog.ClientVersion + "（" + _app.Catalog.Fields.Count + " 项）";
        }

        // ---------------------------------------------------------------- 动态渲染

        private void BuildSection()
        {
            _bindings.Clear();
            _body.Controls.Clear();
            string section = _sections.SelectedItem as string;
            if (section == null) return;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            int row = 0;
            foreach (CatalogField f in _app.Catalog.Fields)
            {
                if (f.Section != section) continue;
                layout.Controls.Add(BuildField(f), 0, row++);
            }
            _body.Controls.Add(layout);
        }

        private Control BuildField(CatalogField f)
        {
            var panel = new Panel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 4, 0, 4) };
            var label = new Label
            {
                Text = (f.Dangerous ? "⚠ " : "") + f.Label,
                AutoSize = true,
                ForeColor = f.Dangerous ? Color.Firebrick : SystemColors.ControlText,
            };
            Control input;
            object current = PathAccess.Get(_policy, f.Path);

            switch (f.Kind)
            {
                case CatalogFieldKind.Bool:
                    var chk = new CheckBox { AutoSize = true, Checked = current is bool b && b };
                    input = chk;
                    break;
                case CatalogFieldKind.Choice:
                    var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
                    combo.Items.AddRange(f.Choices);
                    int idx = Array.IndexOf(f.Values, current as string);
                    combo.SelectedIndex = idx >= 0 ? idx : 0;
                    input = combo;
                    break;
                case CatalogFieldKind.Number:
                    var num = new NumericUpDown
                    {
                        Width = 120,
                        Minimum = f.Min,
                        Maximum = f.Max > f.Min ? f.Max : f.Min + 1,
                        Value = Convert.ToDecimal(current ?? 0) < f.Min ? f.Min :
                                Convert.ToDecimal(current ?? 0) > f.Max ? f.Max : Convert.ToDecimal(current ?? 0),
                    };
                    input = num;
                    break;
                case CatalogFieldKind.List:
                    var list = new TextBox
                    {
                        Multiline = true,
                        Width = 430,
                        Height = 72,
                        ScrollBars = ScrollBars.Vertical,
                        Text = current is System.Collections.IEnumerable en && !(current is string)
                            ? string.Join(Environment.NewLine, en.Cast<object>().Select(x => x as string ?? ""))
                            : "",
                    };
                    input = list;
                    break;
                default:   // Text / Path
                    input = new TextBox { Width = 430, Text = current as string ?? "" };
                    break;
            }

            input.Left = 0;
            input.Top = 22;
            label.Left = 0;
            label.Top = 0;
            panel.Controls.Add(label);
            panel.Controls.Add(input);

            if (!string.IsNullOrEmpty(f.Hint))
            {
                var hint = new Label
                {
                    Text = f.Hint,
                    AutoSize = true,
                    MaximumSize = new Size(640, 0),
                    ForeColor = Color.Gray,
                    Left = 0,
                    Top = input.Bottom + 4,
                };
                panel.Controls.Add(hint);
            }

            _bindings.Add(new FieldControlBinding { Field = f, Control = input });
            return panel;
        }

        /// <summary>把控件里的值刷回 _policy（切分组 / 保存前必调）。</summary>
        private void FlushBindings()
        {
            foreach (FieldControlBinding b in _bindings)
            {
                object value;
                switch (b.Field.Kind)
                {
                    case CatalogFieldKind.Bool:
                        value = ((CheckBox)b.Control).Checked;
                        break;
                    case CatalogFieldKind.Choice:
                        var combo = (ComboBox)b.Control;
                        value = b.Field.Values[combo.SelectedIndex >= 0 ? combo.SelectedIndex : 0];
                        break;
                    case CatalogFieldKind.Number:
                        decimal d = ((NumericUpDown)b.Control).Value;
                        value = d == decimal.Truncate(d) ? (object)(int)d : (object)d;
                        break;
                    case CatalogFieldKind.List:
                        string[] lines = ((TextBox)b.Control).Text
                            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        value = Array.ConvertAll(lines, x => x.Trim()).Length > 0
                            ? Array.ConvertAll(lines, x => x.Trim())
                            : (object)new string[0];
                        break;
                    default:
                        value = ((TextBox)b.Control).Text;
                        break;
                }
                if (!PathAccess.Set(_policy, b.Field.Path, value))
                {
                    Log.Warn("策略路径写入失败：" + b.Field.Path);
                }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
        }
    }
}
