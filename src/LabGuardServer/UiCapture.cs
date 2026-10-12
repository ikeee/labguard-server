using System.Drawing;
using System.Windows.Forms;

namespace LabGuardServer
{
    /// <summary>
    /// 离屏渲染（文档/人工核对用）：把窗体摆到屏幕外再 DrawToBitmap，不影响用户当前桌面。
    /// 与客户端仓库 LabGuard.Core/UI/UiCapture.cs 同一思路——本项目模型没有图像输入，
    /// 界面改动必须靠"程序自己渲染成 PNG + 人来看"来验收。
    /// </summary>
    internal static class UiCapture
    {
        private static readonly Point OffScreen = new Point(-4000, -4000);

        public static void ShowOffScreen(Form form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = OffScreen;
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
        }

        /// <summary>把窗体当前内容存成 PNG；失败返回 null（不抛，截图不该影响主流程）。</summary>
        public static string Save(Form form, string path)
        {
            try
            {
                form.PerformLayout();
                Application.DoEvents();
                using (var bmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                return path;
            }
            catch
            {
                return null;
            }
        }
    }
}
