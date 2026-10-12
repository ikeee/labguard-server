using System.Drawing;
using System.IO;

namespace LabGuardServer.Installer
{
    /// <summary>
    /// 安装程序自己的图标（作者头像 + 黑底，与教师端一致）。
    /// 安装器是独立 exe：ApplicationIcon（Directory.Build.targets）管**文件图标**与任务栏，
    /// 窗体左上角要显式设 —— 不设就是 .NET 默认那个图标（真机上看着"不像我们程序"）。
    /// </summary>
    internal static class InstallerAppIcon
    {
        private static Icon _cached;

        public static Icon Get()
        {
            if (_cached != null) return _cached;
            try
            {
                using (Stream s = typeof(InstallerAppIcon).Assembly
                    .GetManifestResourceStream("LabGuardServer.Installer.payload.app.ico"))
                {
                    if (s != null) { _cached = new Icon(s); return _cached; }
                }
            }
            catch { }
            // 退路：从自己 exe 的关联图标取（ApplicationIcon 编译进去的那个）
            try { _cached = Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath); } catch { }
            if (_cached == null) _cached = SystemIcons.Shield;
            return _cached;
        }
    }
}
