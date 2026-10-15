using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LabGuardServer
{
    /// <summary>
    /// **拨钮开关**（老师的设计：拨向左 = 关，拨向右 = 开）。
    ///
    /// 为什么自绘：WinForms 没有现成的开关控件，用 CheckBox 的话 100+ 项扫一眼看不出"哪些是开的"
    /// （勾选框只有一个小勾）。自绘一个是真的省老师的时间。
    ///
    /// 键盘可达：Tab 能停在上头，**空格/回车**切换（和 CheckBox 的手感一致）；
    /// 焦点态画一圈虚框，方便纯键盘操作。
    /// </summary>
    internal sealed class ToggleSwitch : Control
    {
        private const int TrackWidth = 46;
        private const int TrackHeight = 22;

        private static readonly Color OnColor = Color.FromArgb(0, 120, 215);
        private static readonly Color OffColor = Color.FromArgb(190, 196, 204);
        private static readonly Color KnobColor = Color.White;
        private static readonly Color BorderColor = Color.FromArgb(150, 158, 168);

        private bool _checked;

        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Size = new Size(TrackWidth, TrackHeight);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                EventHandler h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Rounded(track, Height / 2))
            {
                using (var brush = new SolidBrush(_checked ? OnColor : OffColor))
                    g.FillPath(brush, path);
                using (var pen = new Pen(Focused ? Color.FromArgb(0, 90, 170) : BorderColor, Focused ? 1.6f : 1f))
                    g.DrawPath(pen, path);
            }
            int d = Height - 6;
            int x = _checked ? Width - d - 3 : 3;
            using (var brush = new SolidBrush(KnobColor))
                g.FillEllipse(brush, x, 3, d, d);
            using (var pen = new Pen(Color.FromArgb(120, 130, 140)))
                g.DrawEllipse(pen, x, 3, d, d);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left) Checked = !Checked;   // 左键点任意位置都切换（手不需要瞄准）
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Space || keyData == Keys.Enter || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                Checked = !Checked;
                e.Handled = true;
            }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        public override string ToString() { return _checked ? "开" : "关"; }
    }
}
