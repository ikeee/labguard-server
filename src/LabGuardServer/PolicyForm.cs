using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LabGuardServer.Core.Api;
using LabGuardServer.Core.Catalog;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Policy;
using LabGuardServer.Core.Util;

namespace LabGuardServer
{
    /// <summary>
    /// 策略面板（v0.10 重设计版）：为"老师快速找到对应策略"服务——
    ///   · **搜索框**：跨分组按关键词找（分组名/标签/提示/路径都能命中），命中结果平铺并标注所属分组；
    ///   · **左栏分组**：每组后面直接写 "23 项 · 改 2"，一眼看出哪里动过；
    ///   · **卡片式开关**：标签加粗、提示小字灰、危险项红标、改过的项左侧彩色条 + 该开关"恢复默认"；
    ///   · **发布前差异预览**：保存前列出"这次改了什么（旧 → 新）"确认，防手滑；
    ///   · **底栏状态**：当前 seq / 下发时间 / 全员暂停 / 在线学生机数 / 监听地址 / 开关目录版本。
    /// 面板不要求密码（教师机由老师管制、改策略是高频操作，2026-10-12 产品决定）。
    /// 控件全部按 catalog.json 动态生成——客户端加开关，这里自动出现，不手写。
    /// </summary>
    internal sealed class PolicyForm : Form
    {
        private const int EM_SETCUEBANNER = 0x1501;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static readonly Color StripNormal = Color.FromArgb(232, 234, 238);
        private static readonly Color StripChanged = Color.FromArgb(0, 120, 215);
        private static readonly Color StripDanger = Color.FromArgb(196, 43, 28);
        private const int CardWidth = 700;

        private readonly PolicyStore _store;
        private readonly CatalogDoc _catalog;
        private readonly ApiServer _api;

        private readonly TextBox _search = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        private readonly CheckBox _onlyChanged = new CheckBox { Text = "只看已改动", AutoSize = true };
        private readonly ListBox _sections = new ListBox();
        private readonly Panel _body = new Panel { AutoScroll = true, Dock = DockStyle.Fill, BackColor = Color.FromArgb(248, 249, 251) };
        private readonly Label _status = new Label { Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label _changedLabel = new Label { AutoSize = true };
        private readonly Timer _poll = new Timer { Interval = 3000 };

        private Dictionary<string, object> _policy;
        private Dictionary<string, object> _published;
        private readonly List<Binding> _bindings = new List<Binding>();

        private sealed class Binding
        {
            public CatalogField Field;
            public Control Input;
            public Panel Strip;
            public LinkLabel Reset;
        }

        public PolicyForm(PolicyStore store, CatalogDoc catalog, ApiServer api = null)
        {
            _store = store;
            _catalog = catalog;
            _api = api;

            Icon = AppIcon.Get();
            Text = "LabGuard 教师端 · 策略面板（改完点「保存并下发」，学生机 5 秒内生效）";
            Font = new Font("微软雅黑", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 680);
            MinimumSize = new Size(820, 540);
            BackColor = Color.FromArgb(248, 249, 251);

            _policy = _store.CurrentOrDefaultPolicy(_catalog);
            _published = _store.CurrentOrDefaultPolicy(_catalog);

            // ---------------- 顶部工具条
            var top = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Color.White, Padding = new Padding(10, 10, 10, 8) };
            top.Controls.Add(new Label { Text = "搜索开关：", AutoSize = true, Left = 12, Top = 17 });
            _search.SetBounds(92, 13, 240, 24);
            try { SendMessage(_search.Handle, EM_SETCUEBANNER, (IntPtr)1, "例如：USB、DNS、任务管理器、遮罩"); } catch { }
            _search.TextChanged += (s, e) => RebuildBody();
            top.Controls.Add(_search);
            _onlyChanged.SetBounds(346, 16, 100, 22);
            _onlyChanged.CheckedChanged += (s, e) => RebuildBody();
            top.Controls.Add(_onlyChanged);

            Button save = MakeButton("保存并下发", 118, Color.FromArgb(0, 120, 215), Color.White);
            save.SetBounds(452, 8, 118, 32);
            save.Click += (s, e) => SavePolicy();
            Button diff = MakeButton("看差异", 80, Color.White, Color.FromArgb(60, 60, 60));
            diff.SetBounds(578, 9, 80, 30);
            diff.Click += (s, e) => { FlushBindings(); ShowDiff(true); };
            Button pause = MakeButton("全员暂停", 92, Color.White, Color.FromArgb(60, 60, 60));
            pause.SetBounds(666, 9, 92, 30);
            pause.Click += (s, e) => PauseAll(true);
            Button resume = MakeButton("全员恢复", 92, Color.White, Color.FromArgb(60, 60, 60));
            resume.SetBounds(766, 9, 92, 30);
            resume.Click += (s, e) => PauseAll(false);
            _changedLabel.SetBounds(870, 18, 120, 20);
            top.Controls.AddRange(new Control[] { save, diff, pause, resume, _changedLabel });

            // ---------------- 左栏：分组（带计数）
            var left = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            left.Controls.Add(new Label
            {
                Dock = DockStyle.Top, Height = 30, Text = "  策略分组（括号里是项数 / 已改动数）",
                TextAlign = ContentAlignment.MiddleLeft, Font = new Font("微软雅黑", 9.5F, FontStyle.Bold), BackColor = Color.White,
            });
            _sections.Dock = DockStyle.Fill;
            _sections.IntegralHeight = false;
            _sections.BorderStyle = BorderStyle.None;
            _sections.Font = new Font("微软雅黑", 9F);
            _sections.ItemHeight = 26;
            _sections.SelectedIndexChanged += (s, e) => { FlushBindings(); RebuildBody(); };
            left.Controls.Add(_sections);
            _sections.BringToFront();

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill, SplitterDistance = 300, FixedPanel = FixedPanel.Panel1, BorderStyle = BorderStyle.FixedSingle,
            };
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(_body);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = Color.White };
            bottom.Controls.Add(_status);

            Controls.Add(split);
            Controls.Add(bottom);
            Controls.Add(top);

            RebuildSectionList();
            if (_sections.Items.Count > 0) _sections.SelectedIndex = 0;
            RebuildBody();
            RefreshStatus();
            _poll.Tick += (s, e) => RefreshStatus();
            _poll.Start();
        }

        private static Button MakeButton(string text, int width, Color back, Color fore)
        {
            var b = new Button { Text = text, Width = width, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore };
            b.FlatAppearance.BorderColor = Color.FromArgb(200, 205, 212);
            return b;
        }

        // ---------------------------------------------------------------- 左栏

        private void RebuildSectionList()
        {
            string keep = CurrentSection();
            _sections.BeginUpdate();
            _sections.Items.Clear();
            foreach (string s in _catalog.Sections())
            {
                List<CatalogField> fields = _catalog.Fields.Where(f => f.Section == s).ToList();
                int changed = PolicySearch.CountChanged(fields, _policy, _published);
                _sections.Items.Add(s + "   (" + fields.Count + " 项" + (changed > 0 ? " · 改 " + changed : "") + ")");
            }
            _sections.EndUpdate();
            if (keep != null)
            {
                for (int i = 0; i < _sections.Items.Count; i++)
                    if (((string)_sections.Items[i]).StartsWith(keep, StringComparison.Ordinal)) { _sections.SelectedIndex = i; break; }
            }
        }

        private string CurrentSection()
        {
            string item = _sections.SelectedItem as string;
            if (string.IsNullOrEmpty(item)) return null;
            int idx = item.IndexOf("   (", StringComparison.Ordinal);
            return idx > 0 ? item.Substring(0, idx) : item;
        }

        // ---------------------------------------------------------------- 右栏

        private void RebuildBody()
        {
            _body.SuspendLayout();
            _body.Controls.Clear();
            _bindings.Clear();

            List<CatalogField> show;
            bool flat;
            if (!string.IsNullOrWhiteSpace(_search.Text))
            {
                show = PolicySearch.Filter(_catalog.Fields, _search.Text);
                flat = true;
            }
            else if (_onlyChanged.Checked)
            {
                show = _catalog.Fields.Where(f => !PolicySearch.SameValue(
                    PathAccess.Get(_policy, f.Path), PathAccess.Get(_published, f.Path))).ToList();
                flat = true;
            }
            else
            {
                string section = CurrentSection();
                show = _catalog.Fields.Where(f => f.Section == section).ToList();
                flat = false;
            }

            if (show.Count == 0)
            {
                _body.Controls.Add(new Label
                {
                    Text = "没有匹配的开关。换个关键词试试（例如：USB、DNS、浏览器、遮罩）。",
                    AutoSize = true, ForeColor = Color.Gray, Location = new Point(16, 18),
                });
                _body.ResumeLayout();
                UpdateChangedUi(false);
                return;
            }

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10),
            };
            foreach (CatalogField f in show) flow.Controls.Add(BuildCard(f, flat));
            _body.Controls.Add(flow);
            _body.ResumeLayout();
            UpdateChangedUi(false);
        }

        private Control BuildCard(CatalogField f, bool showSectionTag)
        {
            var card = new Panel
            {
                Width = CardWidth, BackColor = Color.White, Margin = new Padding(0, 0, 0, 8),
                Padding = new Padding(12, 8, 12, 8), BorderStyle = BorderStyle.FixedSingle,
            };
            var strip = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = StripNormal };
            card.Controls.Add(strip);

            string title = (f.Dangerous ? "⚠ " : "") + f.Label;
            if (showSectionTag) title = "「" + f.Section + "」 " + title;
            var label = new Label
            {
                Text = title, AutoSize = true, Location = new Point(18, 10),
                Font = new Font("微软雅黑", 9.5F, FontStyle.Bold),
                ForeColor = f.Dangerous ? Color.FromArgb(160, 30, 20) : Color.FromArgb(30, 34, 40),
            };
            card.Controls.Add(label);

            var reset = new LinkLabel { Text = "恢复默认", AutoSize = true, LinkColor = Color.FromArgb(0, 110, 190) };
            reset.Location = new Point(CardWidth - 92, 11);
            reset.LinkClicked += (s, e) => { PathAccess.Set(_policy, f.Path, f.Default); RebuildBody(); RebuildSectionList(); };
            card.Controls.Add(reset);

            Control input = BuildInput(f);
            card.Controls.Add(input);

            int bottom = input.Bottom;
            if (!string.IsNullOrEmpty(f.Hint))
            {
                var hint = new Label
                {
                    Text = f.Hint, AutoSize = true, MaximumSize = new Size(CardWidth - 60, 0), ForeColor = Color.Gray,
                    Location = new Point(18, input.Bottom + 6),
                };
                card.Controls.Add(hint);
                bottom = hint.Bottom;
            }
            card.Height = bottom + 18;

            _bindings.Add(new Binding { Field = f, Input = input, Strip = strip, Reset = reset });
            return card;
        }

        private Control BuildInput(CatalogField f)
        {
            object current = PathAccess.Get(_policy, f.Path);
            Control input;
            switch (f.Kind)
            {
                case CatalogFieldKind.Bool:
                    var chk = new CheckBox { AutoSize = true, Checked = current is bool b && b, Text = "启用" };
                    chk.CheckedChanged += (s, e) => OnValueChanged();
                    input = chk;
                    break;
                case CatalogFieldKind.Choice:
                    var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
                    combo.Items.AddRange(f.Choices);
                    int idx = Array.IndexOf(f.Values, current as string);
                    combo.SelectedIndex = idx >= 0 ? idx : 0;
                    combo.SelectedIndexChanged += (s, e) => OnValueChanged();
                    input = combo;
                    break;
                case CatalogFieldKind.Number:
                    decimal cur = 0;
                    try { cur = Convert.ToDecimal(current ?? 0); } catch { }
                    if (cur < f.Min) cur = f.Min;
                    if (cur > f.Max) cur = f.Max;
                    var num = new NumericUpDown
                    {
                        Width = 140, Minimum = f.Min, Maximum = f.Max > f.Min ? f.Max : f.Min + 1, Value = cur,
                    };
                    num.ValueChanged += (s, e) => OnValueChanged();
                    input = num;
                    break;
                case CatalogFieldKind.List:
                    var list = new TextBox
                    {
                        Multiline = true, Width = CardWidth - 60, Height = 74, ScrollBars = ScrollBars.Vertical,
                        BorderStyle = BorderStyle.FixedSingle, Text = ListToText(current),
                    };
                    list.TextChanged += (s, e) => OnValueChanged();
                    input = list;
                    break;
                default:
                    var tb = new TextBox { Width = CardWidth - 60, Text = current as string ?? "", BorderStyle = BorderStyle.FixedSingle };
                    tb.TextChanged += (s, e) => OnValueChanged();
                    input = tb;
                    break;
            }
            input.Location = new Point(18, 34);
            return input;
        }

        private static string ListToText(object current)
        {
            if (current is System.Collections.IEnumerable en && !(current is string))
                return string.Join(Environment.NewLine, en.Cast<object>().Select(x => x as string ?? ""));
            return "";
        }

        private void OnValueChanged()
        {
            FlushBindings();
            UpdateChangedUi(true);
        }

        private void UpdateChangedUi(bool refreshSections)
        {
            int changed = 0;
            foreach (Binding b in _bindings)
            {
                bool isChanged = !PolicySearch.SameValue(PathAccess.Get(_policy, b.Field.Path),
                    PathAccess.Get(_published, b.Field.Path));
                if (isChanged) changed++;
                b.Strip.BackColor = isChanged ? StripChanged : (b.Field.Dangerous ? StripDanger : StripNormal);
            }
            _changedLabel.Text = changed == 0 ? "未改动" : ("已改动 " + changed + " 项");
            _changedLabel.ForeColor = changed == 0 ? Color.DimGray : Color.FromArgb(0, 90, 170);
            if (refreshSections) RebuildSectionList();
        }

        private void FlushBindings()
        {
            foreach (Binding b in _bindings)
            {
                object value;
                switch (b.Field.Kind)
                {
                    case CatalogFieldKind.Bool: value = ((CheckBox)b.Input).Checked; break;
                    case CatalogFieldKind.Choice:
                        var combo = (ComboBox)b.Input;
                        value = b.Field.Values[combo.SelectedIndex >= 0 ? combo.SelectedIndex : 0];
                        break;
                    case CatalogFieldKind.Number:
                        decimal d = ((NumericUpDown)b.Input).Value;
                        value = d == decimal.Truncate(d) ? (object)(int)d : (object)d;
                        break;
                    case CatalogFieldKind.List:
                        string[] lines = ((TextBox)b.Input).Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        value = lines.Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
                        break;
                    default: value = ((TextBox)b.Input).Text; break;
                }
                if (!PathAccess.Set(_policy, b.Field.Path, value)) Log.Warn("策略路径写入失败：" + b.Field.Path);
            }
        }

        // ---------------------------------------------------------------- 状态 / 保存

        private void RefreshStatus()
        {
            PolicyVersion v = _store.Version();
            string state = v == null
                ? "尚未保存过策略（学生机连上会收到 503，按单机模式继续）"
                : "当前 seq=" + v.Seq + (v.Paused ? " · 全员暂停中" : " · 管控中") +
                  " · 下发于 " + v.IssuedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
            string online = _api != null ? (_api.Online.Snapshot().Length + " 台") : "—";
            _status.Text = "  " + state + "    |    在线学生机 " + online + "    |    监听 " + (_api?.BoundPrefix ?? "—") +
                           "    |    开关目录：客户端 v" + _catalog.ClientVersion + "（" + _catalog.Fields.Count + " 项）";
        }

        private void SavePolicy()
        {
            FlushBindings();
            if (!ShowDiff(false)) return;      // 差异预览：老师确认后才下发
            try
            {
                bool paused = _store.Version()?.Paused ?? false;
                PolicyVersion v = _store.Save(_policy, paused, _catalog.DefaultPolicyJson);
                _published = Json.ParseObject(Json.Stringify(_policy));
                RebuildSectionList();
                RebuildBody();
                RefreshStatus();
                Log.Info("策略已保存下发 seq=" + v.Seq);
                MessageBox.Show(this, "已保存并下发（seq=" + v.Seq + "）。\n学生机默认 5 秒内生效（≤10 秒到位）。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>差异预览。previewOnly=true 只给看不问确认；返回 true = 可以继续下发。</summary>
        private bool ShowDiff(bool previewOnly)
        {
            List<PolicyChange> changes = PolicyDiff.Compare(_published, _policy, _catalog.Fields);
            if (changes.Count == 0)
            {
                if (previewOnly)
                    MessageBox.Show(this, "当前面板与已发布策略完全一致（没有未下发的改动）。", Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            string head = previewOnly ? ("与已发布策略的差异（共 " + changes.Count + " 项）：")
                                      : ("即将下发的改动（共 " + changes.Count + " 项）：");
            string bodyText = string.Join(Environment.NewLine,
                changes.Take(12).Select(c => "· " + c.Label + "：" + c.OldText + " → " + c.NewText));
            if (changes.Count > 12) bodyText += Environment.NewLine + "…还有 " + (changes.Count - 12) + " 项";
            if (changes.Any(c => c.Dangerous))
                bodyText += Environment.NewLine + Environment.NewLine + "⚠ 其中包含【危险】项，请再确认一次。";
            if (previewOnly)
            {
                MessageBox.Show(this, head + Environment.NewLine + Environment.NewLine + bodyText, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            return MessageBox.Show(this, head + Environment.NewLine + Environment.NewLine + bodyText +
                                   Environment.NewLine + Environment.NewLine + "确定下发给全部学生机？", Text,
                                   MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
        }

        private void PauseAll(bool paused)
        {
            if (MessageBox.Show(this,
                    paused ? "全员暂停：所有学生机停止管控策略（进程守护照旧）。\n确定？"
                           : "全员恢复：所有学生机回到服务器策略管控。\n确定？",
                    Text, MessageBoxButtons.OKCancel, paused ? MessageBoxIcon.Warning : MessageBoxIcon.Question)
                != DialogResult.OK) return;
            try
            {
                FlushBindings();
                _store.Save(_policy, paused, _catalog.DefaultPolicyJson);
                _published = Json.ParseObject(Json.Stringify(_policy));
                RebuildSectionList();
                RefreshStatus();
                Log.Info((paused ? "全员暂停" : "全员恢复") + " 已下发 seq=" + _store.CurrentSeq);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "操作失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------------------------------------------------------------- 截图（文档 / 人工核对）

        /// <summary>把每个分组 + 搜索示例 + "只看已改动"渲染成 PNG（不改策略、不落盘）。</summary>
        public List<string> RenderScreenshots(string outDir)
        {
            var saved = new List<string>();
            Directory.CreateDirectory(outDir);
            Size savedMin = MinimumSize, savedSize = ClientSize;
            MinimumSize = Size.Empty;
            _poll.Stop();
            try
            {
                UiCapture.ShowOffScreen(this);
                List<string> sections = _catalog.Sections();
                for (int i = 0; i < sections.Count; i++)
                {
                    _sections.SelectedIndex = i;
                    Application.DoEvents();
                    string p = CapturePage(outDir, string.Format("policy-{0:00}-{1}.png", i + 1, SafeName(sections[i])));
                    if (p != null) saved.Add(p);
                }
                _search.Text = "USB";
                Application.DoEvents();
                string s1 = CapturePage(outDir, "policy-search-usb.png");
                if (s1 != null) saved.Add(s1);
                _search.Text = "";
                _onlyChanged.Checked = true;
                Application.DoEvents();
                string s2 = CapturePage(outDir, "policy-only-changed.png");
                if (s2 != null) saved.Add(s2);
                _onlyChanged.Checked = false;
            }
            finally
            {
                MinimumSize = savedMin;
                ClientSize = savedSize;
                Hide();
            }
            return saved;
        }

        private string CapturePage(string outDir, string fileName)
        {
            FlowLayoutPanel flow = _body.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
            int contentHeight = flow != null ? Math.Min(flow.PreferredSize.Height + 30, 2000) : 420;
            ClientSize = new Size(1000, contentHeight + 52 + 28 + 10);
            PerformLayout();
            Application.DoEvents();
            return UiCapture.Save(this, Path.Combine(outDir, fileName));
        }

        private static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace("、", "_").Replace(" ", "");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _poll.Stop(); } catch { }
            base.OnFormClosed(e);
        }
    }
}
