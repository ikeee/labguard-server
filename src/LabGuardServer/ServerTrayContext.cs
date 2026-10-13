using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using LabGuardServer.Core.Api;
using LabGuardServer.Core.Catalog;
using LabGuardServer.Core.Envelope;
using LabGuardServer.Core.Logging;
using LabGuardServer.Core.Policy;
using LabGuardServer.Core.Util;

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
        private GroupStore _groups;
        private TempStateManager _temp;
        private System.Windows.Forms.Timer _tempWatch;
        private ToolStripMenuItem _tempMenu;
        private List<TempPreset> _presets = new List<TempPreset>();

        /// <summary>设备分组仓库（v0.11）。</summary>
        public GroupStore Groups { get { return _groups; } }

        /// <summary>临时课堂状态层（v0.12）。</summary>
        public TempStateManager Temp { get { return _temp; } }

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
            _groups = new GroupStore(Program.DataDir);
            _groups.Load();
            _groups.StartWatching();   // 外部（CLI/脚本/无人值守）改的分组也要能被拾取
            try
            {
                _store.Load();
                _store.StartWatching();   // 外部（CLI/脚本/以后的管理端）保存的策略也要能被拾取
            }
            catch (Exception ex)
            {
                Log.Error("策略信封加载失败", ex);
                MessageBox.Show("策略信封验签失败，已拒绝下发（疑似被篡改）：\n" + ex.Message +
                    "\n\n修复方法：删除 " + Path.Combine(Program.DataDir, "policy.envelope") + " 后重新保存一次策略。",
                    "LabGuard Server", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            _api = new ApiServer(_store, new OnlineList());
            _api.Groups = _groups;      // v0.11：/api/version 与 /api/policy 按设备身份解析分组

            // v0.12 临时课堂状态层：启动即装载（已过期的会被丢弃并把基础策略重发一次）
            _temp = new TempStateManager(Program.DataDir, _keys, _store, _groups);
            _temp.Load(DateTime.UtcNow);
            _temp.StartWatching();
            _api.Temp = _temp;
            _presets = TempPresets.Load(Program.DataDir);
            _tempWatch = new System.Windows.Forms.Timer { Interval = 5000 };
            _tempWatch.Tick += (s, e) => CheckTempExpiry();
            _tempWatch.Start();

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

        /// <summary>到期检查（每 5 秒）：到点就把基础策略重发一遍（新 seq），学生机自然回到基础策略。</summary>
        private void CheckTempExpiry()
        {
            try
            {
                // 外部（CLI/脚本）刚开了或结束了临时状态 → 界面跟着变，不要让老师看着过期信息做决定
                if (_temp.TryReloadFromDisk())
                {
                    RebuildTempMenu();
                    Balloon("临时课堂状态", _temp.StatusText(DateTime.UtcNow));
                }
                string endedName = _temp.Current?.Name;
                if (_temp.ExpireIfDue(DateTime.UtcNow))
                {
                    RebuildTempMenu();
                    Balloon("临时课堂状态已结束", "【" + endedName + "】已到时间，已自动恢复基础策略（新 seq 已下发）。");
                }
            }
            catch (Exception ex)
            {
                Log.Error("临时状态到期检查失败", ex);
            }
        }

        private void Balloon(string title, string text)
        {
            try
            {
                if (_tray == null) return;
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.ShowBalloonTip(5000);
            }
            catch { }
        }

        /// <summary>开一次临时课堂状态（托盘菜单与策略面板共用）。</summary>
        internal bool StartTempState(string name, string patchJson, bool paused, int minutes, out string message)
        {
            try
            {
                DateTime? expires = minutes > 0 ? (DateTime?)DateTime.UtcNow.AddMinutes(minutes) : null;
                TempState t = _temp.Start(name, patchJson, paused, expires, "面板", DateTime.UtcNow);
                message = "已开始临时状态【" + t.Name + "】：" + t.RemainingText(DateTime.UtcNow) +
                          "，覆盖 " + (1 + t.GroupEnvelopes.Count) + " 个目标（学生机 5 秒内生效）";
                Log.Info(message);
                RebuildTempMenu();
                return true;
            }
            catch (Exception ex)
            {
                message = "开不了临时状态：" + ex.Message;
                Log.Error("开始临时状态失败", ex);
                return false;
            }
        }

        internal bool EndTempState(out string message)
        {
            try
            {
                if (!_temp.End("人工结束", DateTime.UtcNow)) { message = "当前没有进行中的临时状态。"; return false; }
                message = "已结束临时状态【" + _temp.LastEndedName + "】，目标已改回基础策略（新 seq 已下发）。";
                Log.Info(message);
                RebuildTempMenu();
                return true;
            }
            catch (Exception ex)
            {
                message = "结束临时状态失败：" + ex.Message;
                Log.Error("结束临时状态失败", ex);
                return false;
            }
        }

        public PolicyStore Store => _store;
        public CatalogDoc Catalog => _catalog;
        public ApiServer Api => _api;

        private GroupsForm _groupsForm;
        internal void OpenGroupsForm()
        {
            if (_groupsForm == null || _groupsForm.IsDisposed)
            {
                _groupsForm = new GroupsForm(this);
                _groupsForm.Show();
            }
            else _groupsForm.Activate();
        }

        /// <summary>给某个组签发并落盘策略（策略面板的「下发目标」用它）。</summary>
        internal ulong PublishToGroup(DeviceGroup group, System.Collections.Generic.Dictionary<string, object> policy, bool paused)
        {
            ulong seq = GroupPublisher.Publish(_keys, group, policy, paused,
                () => _store.RefreshWatermark(), s => _store.MarkPublished(s));
            _groups.Save();
            // 分组策略变了 → 若正在临时状态中，按新基础重算临时信封（否则临时层停在旧基础上）
            _temp?.RefreshIfActive("分组策略更新：" + group.Name);
            return seq;
        }
        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("策略面板（需密码）", null, (s, e) => OpenPolicyForm());
            menu.Items.Add("在线学生机", null, (s, e) => OpenOnlineForm());
            menu.Items.Add("设备分组…", null, (s, e) => OpenGroupsForm());
            _tempMenu = new ToolStripMenuItem("临时课堂状态");
            menu.Items.Add(_tempMenu);
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
                Icon = AppIcon.Get(),      // 右下角托盘图标 = 作者头像 + 黑底（不是系统盾牌）
                Text = "LabGuard Server（教师机服务端）",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.DoubleClick += (s, e) => OpenPolicyForm();
            RebuildTempMenu();
        }

        /// <summary>
        /// 重建「临时课堂状态」子菜单：
        ///   第一行 = 当前状态（有没有临时覆盖、还剩多久）；
        ///   中间 = 预设一键应用（改 temp-presets.json 就能改这一列，老师按自己机房定义）；
        ///   最后 = 结束（只有存在临时覆盖时才可点）。
        /// 老师切一次常用场景 = 3 次点击以内（托盘 → 本项 → 预设）。
        /// </summary>
        internal void RebuildTempMenu()
        {
            if (_tempMenu == null) return;
            _tempMenu.DropDownItems.Clear();

            var status = new ToolStripMenuItem(_temp.StatusText(DateTime.UtcNow)) { Enabled = false };
            _tempMenu.DropDownItems.Add(status);
            _tempMenu.DropDownItems.Add(new ToolStripSeparator());

            if (_presets.Count == 0)
                _tempMenu.DropDownItems.Add(new ToolStripMenuItem("（没有预设：看 temp-presets.json）") { Enabled = false });
            foreach (TempPreset p in _presets)
            {
                string label = p.Name + (p.DurationMinutes > 0 ? "（" + p.DurationMinutes + " 分钟）" : "（不自动到期）");
                var item = new ToolStripMenuItem(label) { ToolTipText = p.Description ?? "" };
                TempPreset captured = p;
                item.Click += (s, e) =>
                {
                    string msg;
                    if (StartTempState(captured.Name, Json.Stringify(captured.Patch), captured.Paused,
                                       captured.DurationMinutes, out msg))
                        Balloon("已开始临时课堂状态", msg);
                    else MessageBox.Show(msg, "LabGuard Server", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                };
                _tempMenu.DropDownItems.Add(item);
            }

            _tempMenu.DropDownItems.Add(new ToolStripSeparator());
            var reload = new ToolStripMenuItem("重新读取预设（temp-presets.json）");
            reload.Click += (s, e) => { _presets = TempPresets.Load(Program.DataDir); RebuildTempMenu(); };
            _tempMenu.DropDownItems.Add(reload);

            var end = new ToolStripMenuItem("结束临时状态（回到基础策略）") { Enabled = _temp.Active };
            end.Click += (s, e) =>
            {
                string msg;
                EndTempState(out msg);
                Balloon("临时课堂状态", msg);
            };
            _tempMenu.DropDownItems.Add(end);

            if (_tray != null)
                _tray.Text = "LabGuard Server（教师机服务端）" +
                             (_temp.Active ? "\n临时：" + _temp.Current.Name + "（" + _temp.Current.RemainingText(DateTime.UtcNow) + "）" : "");
        }

        private PolicyForm _policyForm;
        private void OpenPolicyForm()
        {
            // 策略面板**不要求密码**（2026-10-12 产品决定）：教师机由老师自己管制，且改策略是高频操作，
            // 每次输密码纯属添堵。安全性由"物理与账号上只有老师能碰这台机器"承担；
            // 残留风险（学生碰到教师机就能改策略）与替代做法见 docs/deployment-guide.md「安全边界」。
            if (_policyForm == null || _policyForm.IsDisposed)
            {
                _policyForm = new PolicyForm(_store, _catalog, _api, _groups, PublishToGroup, OpenGroupsForm,
                                             _temp, StartTempState);
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
