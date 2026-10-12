using System;
using System.Drawing;
using System.Windows.Forms;

namespace LabGuardServer.Installer
{
    /// <summary>
    /// 极简图形界面：装/卸/导出配对/看状态，四件事一键完成（不做花哨向导——计划 §E2 要求"每步可重试、错误给建议"，
    /// 用"一条条日志 + 明确的下一步提示"满足，比多页向导更不容易卡住老师）。
    /// </summary>
    internal sealed class InstallerForm : Form
    {
        private readonly TextBox _dir = new TextBox();
        private readonly NumericUpDown _port = new NumericUpDown();
        private readonly TextBox _log = new TextBox();

        public InstallerForm()
        {
            Icon = InstallerAppIcon.Get();
            Text = "LabGuard 教师机服务端 安装程序";
            Font = new Font("微软雅黑", 9F);
            ClientSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(680, 420);

            var l1 = new Label { Text = "安装目录：", Left = 14, Top = 18, Width = 70 };
            _dir.SetBounds(88, 14, 470, 23);
            _dir.Text = InstallEngine.DefaultInstallDir;
            var l2 = new Label { Text = "端口：", Left = 570, Top = 18, Width = 40 };
            _port.SetBounds(612, 14, 70, 23);
            _port.Minimum = 1; _port.Maximum = 65535; _port.Value = InstallEngine.DefaultPort;

            var row = new FlowLayoutPanel { Left = 14, Top = 50, Width = 732, Height = 40 };
            var bInstall = new Button { Text = "① 安装教师端", Width = 150, Height = 34 };
            bInstall.Click += (s, e) => Run("安装", () => InstallEngine.Install(_dir.Text.Trim(), (int)_port.Value, Append));
            var bExport = new Button { Text = "② 导出配对文件…", Width = 150, Height = 34 };
            bExport.Click += (s, e) => ExportPairing();
            var bStatus = new Button { Text = "查看状态", Width = 100, Height = 34 };
            bStatus.Click += (s, e) => Run("状态", () => Append(InstallEngine.Status()));
            var bUninstall = new Button { Text = "卸载", Width = 90, Height = 34 };
            bUninstall.Click += (s, e) => Uninstall();
            row.Controls.AddRange(new Control[] { bInstall, bExport, bStatus, bUninstall });

            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.ReadOnly = true;
            _log.Dock = DockStyle.Bottom;
            _log.Height = 400;
            _log.BackColor = Color.FromArgb(250, 250, 250);
            _log.Font = new Font("Consolas", 9F);

            Controls.AddRange(new Control[] { l1, _dir, l2, _port, row, _log });
            Append("教师机服务端安装程序。");
            Append("安装会把服务端装到上面的目录，并自动配置：HTTP URL ACL、入站防火墙规则、登录自启。");
            Append("装好后：托盘右键 → 策略面板（设管理口令 + 保存并下发）→ 导出配对文件 → 拷到学生机用 install.ps1 -Pair 导入。");
            Append("日志文件：" + Program.LogPath);
        }

        private void Append(string line)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Append), line); return; }
            _log.AppendText(line + Environment.NewLine);
        }

        private void Run(string title, Action action)
        {
            Enabled = false;
            Append("");
            Append("===== " + title + " =====");
            try { action(); }
            catch (Exception ex) { Append("失败：" + ex.Message); MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { Enabled = true; }
        }

        private void ExportPairing()
        {
            using (var dlg = new SaveFileDialog
            {
                Title = "导出配对文件（拷到学生机，用 install.ps1 -Pair 导入）",
                Filter = "配对文件|*.json",
                FileName = "labguard-pairing.json",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Run("导出配对文件", () => { Append(InstallEngine.ExportPairing(_dir.Text.Trim(), dlg.FileName)); });
            }
        }

        private void Uninstall()
        {
            DialogResult keep = MessageBox.Show(this,
                "卸载后是否**同时删除**密钥与策略数据？\n\n" +
                "选择「否」= 保留数据（换机重装可继续用同一把签名密钥，学生机不用重新配对）\n" +
                "选择「是」= 彻底清空（下次安装会生成新密钥，所有学生机要重新配对）",
                "卸载 LabGuard 教师端服务端", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (keep == DialogResult.Cancel) return;
            bool purge = keep == DialogResult.Yes;
            if (MessageBox.Show(this, "确认卸载？", "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Run("卸载", () => InstallEngine.Uninstall(purge, Append));
        }
    }
}
