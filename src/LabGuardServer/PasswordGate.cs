using System;
using System.IO;
using System.Windows.Forms;
using LabGuardServer.Core.Envelope;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Policy;
using LabGuardServer.Core.Security;

namespace LabGuardServer
{
    /// <summary>密码门禁纯函数：标题统一「LabGuard Server · 输入密码（做什么）」——绝不与策略面板标题撞脸
    /// （客户端 v0.07 修过的"设置页面没有显示"误诊教训，服务端从第一天就遵守）。</summary>
    internal static class PasswordGate
    {
        public static string Title(string purpose) => "LabGuard Server · 输入密码（" + purpose + "）";
        public static string PolicyPanelTitle => "LabGuard Server · 策略面板";

        /// <summary>取已保存口令散列；没有 = 首次运行（要引导设置密码）。</summary>
        public static string StoredHash(string dataDir)
        {
            string path = Path.Combine(dataDir, "server-password.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        public static void SaveHash(string dataDir, string password)
        {
            File.WriteAllText(Path.Combine(dataDir, "server-password.txt"), PasswordHasher.Create(password));
        }
    }

    /// <summary>输密码 / 首次设置密码的小对话框（TopMost，谁弹都在最上层）。</summary>
    internal sealed class PasswordDialog : Form
    {
        private readonly TextBox _box = new TextBox();
        private readonly Button _ok = new Button();
        private bool _confirmed;

        /// <param name="purpose">标题括号里的用途说明。</param>
        /// <param name="confirmMode">true = 首次设置（输两遍）。</param>
        public PasswordDialog(string purpose, bool confirmMode)
        {
            Text = PasswordGate.Title(purpose);
            TopMost = true;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new System.Drawing.Size(360, confirmMode ? 150 : 110);

            var label = new Label { Text = confirmMode ? "第一次使用：请设置管理密码（6 位以上字母/数字）" : "请输入管理密码：",
                AutoSize = true, Left = 12, Top = 12 };
            _box.SetBounds(12, 36, 336, 23);
            _box.UseSystemPasswordChar = true;

            var label2 = new Label { Text = "再输一遍确认：", AutoSize = true, Left = 12, Top = 66, Visible = confirmMode };
            _box2.SetBounds(12, 88, 336, 23);
            _box2.UseSystemPasswordChar = true;
            _box2.Visible = confirmMode;

            _ok.Text = "确定";
            _ok.SetBounds(196, confirmMode ? 116 : 76, 75, 25);
            _ok.DialogResult = DialogResult.None;
            _ok.Click += (s, e) => OnOk(confirmMode);
            var cancel = new Button { Text = "取消" };
            cancel.SetBounds(275, confirmMode ? 116 : 76, 75, 25);
            cancel.DialogResult = DialogResult.Cancel;

            AcceptButton = _ok;
            CancelButton = cancel;
            Controls.AddRange(new Control[] { label, _box, label2, _box2, _ok, cancel });
        }

        private readonly TextBox _box2 = new TextBox();

        public string Password => _confirmed ? _box.Text : null;

        private void OnOk(bool confirmMode)
        {
            string error = PasswordHasher.Validate(_box.Text);
            if (error != null) { MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (confirmMode && _box.Text != _box2.Text)
            {
                MessageBox.Show(this, "两次输入不一致，请重新输入。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _confirmed = true;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
