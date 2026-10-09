using System;
using System.IO;
using System.Windows.Forms;
using LabGuardServer.Core.Api;
using LabGuardServer.Core.Catalog;
using LabGuardServer.Core.Envelope;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Policy;

namespace LabGuardServer
{
    /// <summary>托盘应用主体：装配密钥/目录/策略/API，托盘菜单统一入口（策略面板有密码门禁）。</summary>
    internal sealed class ServerTrayContext : ApplicationContext
    {
        private RsaKeyStore _keys;
        private PolicyStore _store;
        private ApiServer _api;
        private CatalogDoc _catalog;
        private NotifyIcon _tray;

        public ServerTrayContext()
        {
            try
            {
                _keys = RsaKeyStore.LoadOrCreate(Program.DataDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show("签名密钥加载失败，服务端无法启动：\n" + ex.Message,
                    "LabGuard Server", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ExitThread();
                return;
            }

            try
            {
                _catalog = CatalogDoc.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "catalog.json"));
            }
            catch (Exception ex)
            {
                MessageBox.Show("开关目录快照加载失败（assets\\catalog.json）：\n" + ex.Message +
                    "\n\n请从客户端仓库重新导出（LabGuard.Settings.exe --export-catalog）。",
                    "LabGuard Server", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ExitThread();
                return;
            }

            _store = new PolicyStore(Program.DataDir, _keys);
            try
            {
                _store.Load();
            }
            catch (Exception ex)
            {
                Log.Error("策略信封加载失败", ex);
                MessageBox.Show("策略信封验签失败，已拒绝下发（疑似被篡改）：\n" + ex.Message +
                    "\n\n修复方法：删除 " + Path.Combine(Program.DataDir, "policy.envelope") + " 后重新保存一次策略。",
                    "LabGuard Server", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            _api = new ApiServer(_store, new OnlineList());
            if (!_api.Start(ApiServer.DefaultPort))
            {
                MessageBox.Show("HTTP 监听启动失败（端口 " + ApiServer.DefaultPort + " 被占用或无权限）。\n" +
                    "学生机将连不上本服务端。请关闭占用端口的程序后重启。",
                    "LabGuard Server", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                Log.Info("API 监听：" + _api.BoundPrefix);
            }

            BuildTray();
        }

        public PolicyStore Store => _store;
        public CatalogDoc Catalog => _catalog;
        public ApiServer Api => _api;

        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("策略面板（需密码）", null, (s, e) => OpenPolicyForm());
            menu.Items.Add("在线学生机", null, (s, e) => OpenOnlineForm());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("导出配对文件…", null, (s, e) => ExportPairing());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) =>
            {
                if (MessageBox.Show("退出教师机服务端？学生机将回退单机模式继续管控。",
                        "LabGuard Server", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                    ExitThread();
            });

            _tray = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Shield,
                Text = "LabGuard Server（教师机服务端）",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.DoubleClick += (s, e) => OpenPolicyForm();
        }

        private PolicyForm _policyForm;
        private void OpenPolicyForm()
        {
            // 密码门禁：首次运行先设置密码；之后每次打开都要验
            string hash = PasswordGate.StoredHash(Program.DataDir);
            if (hash == null)
            {
                using (var set = new PasswordDialog("第一次使用，设置管理密码", true))
                {
                    if (set.ShowDialog() != DialogResult.OK) return;
                    PasswordGate.SaveHash(Program.DataDir, set.Password);
                }
            }
            else
            {
                using (var ask = new PasswordDialog("打开策略面板", false))
                {
                    if (ask.ShowDialog() != DialogResult.OK) return;
                    if (!Core.Security.PasswordHasher.Verify(ask.Password, hash))
                    {
                        MessageBox.Show("密码不正确。", PasswordGate.Title("打开策略面板"),
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
            }

            if (_policyForm == null || _policyForm.IsDisposed)
            {
                _policyForm = new PolicyForm(this);
                _policyForm.Show();
            }
            else
            {
                _policyForm.Activate();
            }
        }

        private OnlineForm _onlineForm;
        private void OpenOnlineForm()
        {
            if (_onlineForm == null || _onlineForm.IsDisposed)
            {
                _onlineForm = new OnlineForm(this);
                _onlineForm.Show();
            }
            else
            {
                _onlineForm.Activate();
            }
        }

        private void ExportPairing()
        {
            using (var dlg = new SaveFileDialog
            {
                Title = "导出配对文件（拷到学生机执行 install.ps1 -Pair）",
                Filter = "配对文件|*.json",
                FileName = "labguard-pairing.json",
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                try
                {
                    var doc = PairingFile.Build(_keys.PublicXml, _keys.Fingerprint,
                        _api?.BoundPrefix != null ? ApiServer.DefaultPort : ApiServer.DefaultPort);
                    PairingFile.Export(doc, dlg.FileName);
                    MessageBox.Show("已导出配对文件：\n" + dlg.FileName +
                        "\n\n公钥指纹（可人工核对）：" + _keys.Fingerprint,
                        "LabGuard Server", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("导出失败：" + ex.Message, "LabGuard Server",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        protected override void ExitThreadCore()
        {
            _api?.Stop();
            _keys?.Dispose();
            if (_tray != null) _tray.Visible = false;
            base.ExitThreadCore();
        }
    }
}
