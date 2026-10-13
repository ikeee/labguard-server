using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LabGuardServer.Core.Audit;
using LabGuardServer.Core.Logging;

namespace LabGuardServer
{
    /// <summary>
    /// 审计日志查看器（v0.14 / LG-107）：**只读**——这里不提供任何"编辑/删除"入口，
    /// 因为这正是审计的意义（执行计划 §G1："应尽量防止普通 UI 操作直接篡改"）。
    /// 时间一律按本机时区显示（库里存的是 UTC）。
    /// </summary>
    internal sealed class AuditForm : Form
    {
        private readonly AuditLog _audit;
        private readonly ListView _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true,
            Font = new Font("微软雅黑", 9F),
        };
        private readonly Label _status = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray,
        };
        private readonly TextBox _filter = new TextBox { Width = 180, BorderStyle = BorderStyle.FixedSingle };

        public AuditForm(AuditLog audit)
        {
            _audit = audit;
            Icon = AppIcon.Get();
            Text = "LabGuard 教师端 · 审计日志（只读；时间按本机时区）";
            Font = new Font("微软雅黑", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1080, 620);
            MinimumSize = new Size(820, 460);
            BackColor = Color.FromArgb(248, 249, 251);

            _list.Columns.Add("序号", 60);
            _list.Columns.Add("本机时间", 140);
            _list.Columns.Add("动作", 130);
            _list.Columns.Add("对象", 130);
            _list.Columns.Add("结果", 70);
            _list.Columns.Add("策略 seq", 90);
            _list.Columns.Add("摘要", 470);

            var top = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.White };
            top.Controls.Add(new Label { Text = "筛选：", AutoSize = true, Left = 10, Top = 14 });
            _filter.SetBounds(58, 11, 200, 24);
            _filter.TextChanged += (s, e) => Reload();
            top.Controls.Add(_filter);
            var refresh = new Button { Text = "刷新", Left = 270, Top = 10, Width = 70, Height = 26, FlatStyle = FlatStyle.Flat };
            refresh.Click += (s, e) => Reload();
            top.Controls.Add(refresh);
            var verify = new Button { Text = "校验完整性", Left = 348, Top = 10, Width = 100, Height = 26, FlatStyle = FlatStyle.Flat };
            verify.Click += (s, e) =>
            {
                AuditVerifyResult r = _audit.Verify();
                MessageBox.Show(this,
                    (r.Ok ? "✅ 链完整：" : "❌ 链有问题：") + r.Reason + Environment.NewLine + Environment.NewLine +
                    "说明：每条记录带 HMAC 哈希链（密钥由服务端私钥派生、不落在日志里），" +
                    "删一条 / 改一个字段 / 调换顺序都会在这里被认出来。" +
                    (r.KeyAvailable ? "" : Environment.NewLine + "⚠ 当前链密钥不可用，已降级为纯哈希链（仍能看出改动，但防不住有心重算）。"),
                    Text, MessageBoxButtons.OK, r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            };
            top.Controls.Add(verify);
            var export = new Button { Text = "导出 CSV…", Left = 456, Top = 10, Width = 100, Height = 26, FlatStyle = FlatStyle.Flat };
            export.Click += (s, e) => ExportCsv();
            top.Controls.Add(export);
            top.Controls.Add(new Label
            {
                Text = "审计只记录管理动作与摘要，不记录口令/私钥/策略全文；导出 CSV 已做公式注入防护",
                AutoSize = true, Left = 570, Top = 15, ForeColor = Color.FromArgb(150, 150, 150),
            });

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 26, BackColor = Color.White };
            bottom.Controls.Add(_status);

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(top);
            Reload();
        }

        private void Reload()
        {
            List<AuditEntry> rows = _audit.Tail(2000);
            string key = (_filter.Text ?? "").Trim();
            _list.BeginUpdate();
            _list.Items.Clear();
            int shown = 0;
            foreach (AuditEntry e in rows)
            {
                string summary = e.Summary ?? "";
                if (key.Length > 0 &&
                    (e.Action ?? "").IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (e.TargetId ?? "").IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0 &&
                    summary.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var it = new ListViewItem(new[]
                {
                    e.Seq.ToString(),
                    e.LocalTime,
                    e.Action ?? "",
                    (string.IsNullOrEmpty(e.TargetType) ? "" : e.TargetType + "/") + (e.TargetId ?? ""),
                    (e.ErrorCode ?? "").Length > 0 ? e.Result + "(" + e.ErrorCode + ")" : (e.Result ?? ""),
                    e.NewPolicySeq == 0 ? "" : (e.PrevPolicySeq + " → " + e.NewPolicySeq),
                    summary,
                });
                if (string.Equals(e.Result, "failed", StringComparison.OrdinalIgnoreCase)) it.ForeColor = Color.FromArgb(196, 43, 28);
                _list.Items.Add(it);
                shown++;
            }
            _list.EndUpdate();
            AuditVerifyResult vr = _audit.Verify();
            _status.Text = "  共 " + rows.Count + " 条（显示 " + shown + " 条）    |    链校验：" +
                           (vr.Ok ? "通过 ✅" : "失败 ❌ " + vr.Reason) + "    |    存储：" + _audit.CurrentFile;
        }

        private void ExportCsv()
        {
            using (var dlg = new SaveFileDialog
            {
                Title = "导出审计记录（CSV，UTF-8 带 BOM，Excel 可直接打开）",
                Filter = "CSV 文件|*.csv",
                FileName = "labguard-audit-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv",
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    List<AuditEntry> all = _audit.ReadAll();
                    AuditCsv.Export(all, dlg.FileName);
                    _audit.Write(AuditAction.AuditExport, "export", System.IO.Path.GetFileName(dlg.FileName), 0, 0,
                        "ok", null, "从界面导出审计记录 " + all.Count + " 条（CSV，含公式注入防护）");
                    MessageBox.Show(this, "已导出 " + all.Count + " 条 → " + dlg.FileName, Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Reload();
                }
                catch (Exception ex)
                {
                    Log.Error("导出审计 CSV 失败", ex);
                    MessageBox.Show(this, "导出失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
