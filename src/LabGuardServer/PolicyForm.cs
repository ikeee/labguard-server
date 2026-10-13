using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LabGuardServer.Core.Api;
using LabGuardServer.Core.Catalog;
using LabGuardServer.Core.Envelope;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Policy;
using LabGuardServer.Core.Util;

namespace LabGuardServer
{
    /// <summary>
    /// 策略面板（v0.10.3 按"职责归属"重写）：
    ///
    /// **归属口径**（详见 Core/Policy/PolicyOwnership.cs）——服务端不执行任何管控动作，
    /// 它只签发/下发策略 + 看在线状态；13 个 Guard 全部跑在学生机上。面板按"谁执行/谁决策"分三层：
    ///   ① 课堂管控（老师常改，下发给学生机执行）
    ///   ② 学生端自保与环境（**学生机自己执行**：电子教室保护 / 进程防拆加固 / 注册表加固 / 机位标识…）
    ///   ③ 教师端自身（学生机本机配置，**不下发**，面板里不出现）
    ///
    /// 交互：左栏三层树（层顶说明"谁在干活"）→ 组 → 开关卡片；顶部搜索跨全部组；
    /// 保存前差异预览；右上下角常驻"这次改动/在线数/seq"。
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
        private readonly GroupStore _groups;                       // v0.11 分组（可为空 → 只编辑默认策略）
        private readonly Func<DeviceGroup, Dictionary<string, object>, bool, ulong> _publishGroup;
        private readonly Action _openGroupsUi;

        private readonly TextBox _search = new TextBox { BorderStyle = BorderStyle.FixedSingle };
        private readonly CheckBox _onlyChanged = new CheckBox { Text = "只看已改动", AutoSize = true };
        private readonly TreeView _tree = new TreeView();
        private readonly Panel _body = new Panel { AutoScroll = true, Dock = DockStyle.Fill, BackColor = Color.FromArgb(248, 249, 251) };
        private readonly Label _status = new Label { Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label _changedLabel = new Label { AutoSize = true };
        private readonly ComboBox _target = new ComboBox();      // 「全部设备（默认策略）」或某个组
        private readonly Timer _poll = new Timer { Interval = 3000 };

        private Dictionary<string, object> _policy;
        private Dictionary<string, object> _published;
        private readonly List<Binding> _bindings = new List<Binding>();

        private sealed class Binding
        {
            public CatalogField Field;
            public Control Input;
            public Panel Strip;
        }

        public PolicyForm(PolicyStore store, CatalogDoc catalog, ApiServer api = null,
            GroupStore groups = null,
            Func<DeviceGroup, Dictionary<string, object>, bool, ulong> publishGroup = null,
            Action openGroupsUi = null)
        {
            _store = store;
            _catalog = catalog;
            _api = api;
            _groups = groups;
            _publishGroup = publishGroup;
            _openGroupsUi = openGroupsUi;

            Icon = AppIcon.Get();
            Text = "LabGuard 教师端 · 策略面板（只负责下发策略；改完点「保存并下发」）";
            Font = new Font("微软雅黑", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1040, 720);
            MinimumSize = new Size(880, 580);
            BackColor = Color.FromArgb(248, 249, 251);

            _policy = _store.CurrentOrDefaultPolicy(_catalog);
            _published = _store.CurrentOrDefaultPolicy(_catalog);

            // ---------------- 顶部工具条
            var top = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Color.White, Padding = new Padding(10, 10, 10, 8) };
            top.Controls.Add(new Label { Text = "下发目标：", AutoSize = true, Left = 10, Top = 17 });
            _target.SetBounds(78, 13, 190, 24);
            _target.DropDownStyle = ComboBoxStyle.DropDownList;
            _target.SelectedIndexChanged += (s, e) => { ReloadPolicyForTarget(); RebuildTree(); RebuildBody(); RefreshStatus(); };
            top.Controls.Add(_target);
            var manage = new Button { Text = "管理分组…", Width = 90, Height = 24, Left = 272, Top = 13, FlatStyle = FlatStyle.Flat };
            manage.Click += (s, e) => { _openGroupsUi?.Invoke(); ReloadTargets(); };
            top.Controls.Add(manage);
            top.Controls.Add(new Label { Text = "搜索：", AutoSize = true, Left = 372, Top = 17 });
            _search.SetBounds(414, 13, 190, 24);
            try { SendMessage(_search.Handle, EM_SETCUEBANNER, (IntPtr)1, "例如：USB、DNS、任务管理器、遮罩"); } catch { }
            _search.TextChanged += (s, e) => RebuildBody();
            top.Controls.Add(_search);
            _onlyChanged.SetBounds(614, 16, 100, 22);
            _onlyChanged.CheckedChanged += (s, e) => RebuildBody();
            top.Controls.Add(_onlyChanged);

            Button save = MakeButton("保存并下发", 118, Color.FromArgb(0, 120, 215), Color.White);
            save.SetBounds(716, 8, 118, 32);
            save.Click += (s, e) => SavePolicy();
            Button diff = MakeButton("看差异", 80, Color.White, Color.FromArgb(60, 60, 60));
            diff.SetBounds(840, 9, 70, 30);
            diff.Click += (s, e) => { FlushBindings(); ShowDiff(true); };
            Button pause = MakeButton("全员暂停", 92, Color.White, Color.FromArgb(60, 60, 60));
            pause.SetBounds(408, 9, 92, 30); pause.Top = 40;
            pause.Click += (s, e) => PauseAll(true);
            Button resume = MakeButton("全员恢复", 92, Color.White, Color.FromArgb(60, 60, 60));
            resume.SetBounds(506, 9, 92, 30); resume.Top = 40;
            resume.Click += (s, e) => PauseAll(false);
            var who = new Label
            {
                Text = "教师端只下发策略；管控动作全部由学生机执行",
                AutoSize = true, Left = 862, Top = 18, ForeColor = Color.FromArgb(120, 124, 130),
            };
            _changedLabel.SetBounds(900, 18, 120, 18);
            top.Controls.AddRange(new Control[] { save, diff, pause, resume, who, _changedLabel });

            // ---------------- 左栏：三层归属树
            var left = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            left.Controls.Add(new Label
            {
                Dock = DockStyle.Top, Height = 30, Text = "  按「谁执行」分三层", TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("微软雅黑", 9.5F, FontStyle.Bold), BackColor = Color.White,
            });
            _tree.Dock = DockStyle.Fill;
            _tree.BorderStyle = BorderStyle.None;
            _tree.Font = new Font("微软雅黑", 9F);
            _tree.ItemHeight = 24;
            _tree.ShowLines = true;
            _tree.HideSelection = false;
            _tree.AfterSelect += (s, e) => { FlushBindings(); RebuildBody(); };
            left.Controls.Add(_tree);
            _tree.BringToFront();

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill, SplitterDistance = 316, FixedPanel = FixedPanel.Panel1, BorderStyle = BorderStyle.FixedSingle,
            };
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(_body);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = Color.White };
            bottom.Controls.Add(_status);

            Controls.Add(split);
            Controls.Add(bottom);
            Controls.Add(top);

            ReloadTargets();
            ReloadPolicyForTarget();
            RebuildTree();
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

        /// <summary>面板里可见的开关（教师端自身的组不出现：学生机不收这些值，改了没用）。</summary>
        private IEnumerable<CatalogField> VisibleFields()
        {
            return _catalog.Fields.Where(f => PolicyOwnership.VisibleInPanel(f.Section));
        }

        // ---------------------------------------------------------------- 左栏（三层归属）

        private void RebuildTree()
        {
            string keepSection = SelectedSection();
            PolicyTier? keepTier = SelectedTier();
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            foreach (PolicyTier tier in new[] { PolicyTier.Classroom, PolicyTier.SelfProtect })
            {
                List<string> sections = PolicyOwnership.SectionsOf(_catalog.Sections(), tier);
                var node = new TreeNode(PolicyOwnership.TierTitle(tier) + "    (" + sections.Count + " 组)")
                {
                    Tag = tier,
                    NodeFont = new Font("微软雅黑", 9F, FontStyle.Bold),
                    ForeColor = tier == PolicyTier.SelfProtect ? Color.FromArgb(120, 60, 20) : Color.FromArgb(20, 30, 40),
                };
                foreach (string s in sections)
                {
                    List<CatalogField> fields = _catalog.Fields.Where(f => f.Section == s).ToList();
                    int changed = PolicySearch.CountChanged(fields, _policy, _published);
                    node.Nodes.Add(new TreeNode(s + "   (" + fields.Count + " 项" + (changed > 0 ? " · 改 " + changed : "") + ")")
                    {
                        Tag = s,
                    });
                }
                _tree.Nodes.Add(node);
                node.Expand();
            }
            _tree.EndUpdate();

            // 保持原选择
            if (keepSection != null)
                foreach (TreeNode t in _tree.Nodes)
                    foreach (TreeNode c in t.Nodes)
                        if (((string)c.Tag) == keepSection) { _tree.SelectedNode = c; return; }
            if (keepTier.HasValue && _tree.Nodes.Count > 0)
                foreach (TreeNode t in _tree.Nodes)
                    if ((PolicyTier)t.Tag == keepTier.Value) { _tree.SelectedNode = t; return; }
            if (_tree.Nodes.Count > 0) _tree.SelectedNode = _tree.Nodes[0];
        }

        private PolicyTier? SelectedTier()
        {
            if (_tree.SelectedNode == null) return null;
            return _tree.SelectedNode.Tag is PolicyTier t ? t : (PolicyTier?)null;
        }

        private string SelectedSection()
        {
            if (_tree.SelectedNode == null) return null;
            return _tree.SelectedNode.Tag as string;
        }

        // ---------------------------------------------------------------- 右栏

        private void RebuildBody()
        {
            _body.SuspendLayout();
            _body.Controls.Clear();
            _bindings.Clear();

            List<CatalogField> show;
            bool flat;
            PolicyTier tier = SelectedTier() ?? PolicyOwnership.Of(SelectedSection());
            string section = SelectedSection();
            if (!string.IsNullOrWhiteSpace(_search.Text))
            {
                show = PolicySearch.Filter(VisibleFields(), _search.Text);
                flat = true;
            }
            else if (_onlyChanged.Checked)
            {
                show = VisibleFields().Where(f => !PolicySearch.SameValue(
                    PathAccess.Get(_policy, f.Path), PathAccess.Get(_published, f.Path))).ToList();
                flat = true;
            }
            else if (section != null)
            {
                show = _catalog.Fields.Where(f => f.Section == section).ToList();
                flat = false;
            }
            else
            {
                show = PolicyOwnership.SectionsOf(_catalog.Sections(), tier)
                    .SelectMany(s => _catalog.Fields.Where(f => f.Section == s)).ToList();
                flat = true;
            }

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10),
            };
            flow.Controls.Add(Banner(section == null || flat ? PolicyOwnership.TierTitle(tier) : section,
                                     section == null || flat ? PolicyOwnership.TierHint(tier) : ("本组 " + show.Count + " 项，容器在「" + PolicyOwnership.TierTitle(tier) + "」")));
            if (show.Count == 0)
            {
                flow.Controls.Add(new Label
                {
                    Text = "没有匹配的开关。换个关键词试试（例如：USB、DNS、浏览器、遮罩）。",
                    AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(6, 8, 0, 0),
                });
            }
            else
            {
                foreach (CatalogField f in show) flow.Controls.Add(BuildCard(f, flat));
            }
            _body.Controls.Add(flow);
            _body.ResumeLayout();
            UpdateChangedUi(false);
        }

        private static Control Banner(string title, string hint)
        {
            var p = new Panel { Width = CardWidth, BackColor = Color.FromArgb(238, 242, 248), Margin = new Padding(0, 0, 0, 10), Padding = new Padding(12, 8, 12, 8) };
            var t = new Label
            {
                Text = title, AutoSize = true, Location = new Point(14, 8),
                Font = new Font("微软雅黑", 10F, FontStyle.Bold), ForeColor = Color.FromArgb(20, 40, 70),
            };
            var h = new Label
            {
                Text = hint, AutoSize = true, MaximumSize = new Size(CardWidth - 40, 0), ForeColor = Color.FromArgb(90, 96, 104),
                Location = new Point(14, t.Bottom + 4),
            };
            p.Controls.Add(t);
            p.Controls.Add(h);
            p.Height = h.Bottom + 12;
            return p;
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
            reset.LinkClicked += (s, e) => { PathAccess.Set(_policy, f.Path, f.Default); RebuildBody(); RebuildTree(); };
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
            _bindings.Add(new Binding { Field = f, Input = input, Strip = strip });
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

        private void UpdateChangedUi(bool refreshTree)
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
            if (refreshTree) RebuildTree();
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
            string targetTxt = "";
            DeviceGroup tg = TargetGroup();
            if (tg != null && _api != null)
            {
                int want = tg.DeviceIds.Count + tg.Machines.Count;
                int applied = 0;
                foreach (CheckinRecord r in _api.Online.Snapshot())
                {
                    DeviceGroup owner = _groups.Resolve(r.DeviceId, r.Machine);
                    if (owner != null && owner.Id == tg.Id && tg.Seq > 0 && r.AppliedSeq >= tg.Seq) applied++;
                }
                targetTxt = "    |    目标【" + tg.Name + "】" + want + " 台，已应用 " + applied + " 台";
            }
            _status.Text = "  " + state + targetTxt + "    |    在线学生机 " + online + "    |    监听 " + (_api?.BoundPrefix ?? "—") +
                           "    |    开关目录：客户端 v" + _catalog.ClientVersion + "（" + _catalog.Fields.Count + " 项，面板显示 " +
                           VisibleFields().Count() + " 项）";
        }

        private void SavePolicy()
        {
            FlushBindings();
            DeviceGroup target = TargetGroup();
            if (!ShowDiff(false)) return;
            try
            {
                if (target != null)
                {
                    if (_publishGroup == null) { MessageBox.Show(this, "本界面不支持分组下发（请从托盘打开策略面板）。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    ulong seq = _publishGroup(target, _policy, target.Paused);
                    _published = Json.ParseObject(Json.Stringify(_policy));
                    RebuildTree(); RebuildBody(); ReloadTargets(); RefreshStatus();
                    Log.Info("已下发到组【" + target.Name + "】seq=" + seq);
                    MessageBox.Show(this, "已下发到组【" + target.Name + "】（seq=" + seq + "）。\n该组设备默认 5 秒内生效，其他设备不受影响。",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                bool paused = _store.Version()?.Paused ?? false;
                PolicyVersion v = _store.Save(_policy, paused, _catalog.DefaultPolicyJson);
                _published = Json.ParseObject(Json.Stringify(_policy));
                RebuildTree();
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
                DeviceGroup target = TargetGroup();
                if (target != null && _publishGroup != null)
                {
                    ulong seq = _publishGroup(target, _policy, paused);
                    _published = Json.ParseObject(Json.Stringify(_policy));
                    RebuildTree(); RebuildBody(); ReloadTargets(); RefreshStatus();
                    Log.Info((paused ? "组暂停" : "组恢复") + "【" + target.Name + "】seq=" + seq);
                    return;
                }
                _store.Save(_policy, paused, _catalog.DefaultPolicyJson);
                _published = Json.ParseObject(Json.Stringify(_policy));
                RebuildTree();
                RefreshStatus();
                Log.Info((paused ? "全员暂停" : "全员恢复") + " 已下发 seq=" + _store.CurrentSeq);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "操作失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------------------------------------------------------------- 下发目标（v0.11 分组）

        private sealed class TargetItem
        {
            public readonly string Key;      // null = 默认策略（全部设备）
            private readonly string _text;
            public TargetItem(string key, string text) { Key = key; _text = text; }
            public override string ToString() { return _text; }
        }

        /// <summary>刷新「下发目标」：默认策略 + 各组（组名后带成员数与该组当前 seq）。</summary>
        private void ReloadTargets()
        {
            string keep = CurrentTargetKey();
            _target.BeginUpdate();
            _target.Items.Clear();
            _target.Items.Add(new TargetItem(null, "全部设备（默认策略）"));
            if (_groups != null)
                foreach (DeviceGroup g in _groups.Groups)
                    _target.Items.Add(new TargetItem(g.Id, g.Name + "（" + (g.DeviceIds.Count + g.Machines.Count) +
                                                           " 台 · seq " + g.Seq + "）"));
            _target.EndUpdate();
            int idx = 0;
            for (int i = 0; i < _target.Items.Count; i++)
                if (((TargetItem)_target.Items[i]).Key == keep) { idx = i; break; }
            _target.SelectedIndex = idx;
        }

        private string CurrentTargetKey()
        {
            var it = _target.SelectedItem as TargetItem;
            return it?.Key;
        }

        private DeviceGroup TargetGroup()
        {
            string k = CurrentTargetKey();
            return (k == null || _groups == null) ? null : _groups.Get(k);
        }

        /// <summary>切换下发目标时，把面板载入该目标当前的策略（组没下发过 → 以默认策略为底稿）。</summary>
        private void ReloadPolicyForTarget()
        {
            if (_catalog == null) return;
            DeviceGroup g = TargetGroup();
            Dictionary<string, object> basePolicy;
            if (g != null && !string.IsNullOrEmpty(g.Envelope))
            {
                EnvelopeData d = PolicyEnvelope.ParseHeader(g.Envelope);
                basePolicy = string.IsNullOrEmpty(d.PolicyJson)
                    ? _store.CurrentOrDefaultPolicy(_catalog)
                    : Json.ParseObject(d.PolicyJson);
            }
            else basePolicy = _store.CurrentOrDefaultPolicy(_catalog);
            _policy = basePolicy;
            _published = Json.ParseObject(Json.Stringify(_policy));
        }

        // ---------------------------------------------------------------- 截图（文档 / 人工核对）

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
                foreach (PolicyTier tier in new[] { PolicyTier.Classroom, PolicyTier.SelfProtect })
                {
                    _tree.SelectedNode = _tree.Nodes.OfType<TreeNode>().FirstOrDefault(n => n.Tag is PolicyTier t && t == tier);
                    Application.DoEvents();
                    string p1 = CapturePage(outDir, string.Format("policy-tier-{0}.png", (int)tier));
                    if (p1 != null) saved.Add(p1);
                }
                // 逐组
                int i = 0;
                foreach (TreeNode root in _tree.Nodes)
                    foreach (TreeNode child in root.Nodes)
                    {
                        _tree.SelectedNode = child;
                        Application.DoEvents();
                        i++;
                        string p = CapturePage(outDir, string.Format("policy-{0:00}-{1}.png", i, SafeName((string)child.Tag)));
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
            ClientSize = new Size(1040, contentHeight + 52 + 28 + 10);
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
