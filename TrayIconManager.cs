using System.Reflection;
using System.Windows.Forms;

namespace YAOLlm
{
    public class TrayIconManager
    {
        private readonly NotifyIcon _trayIcon;

        public TrayIconManager(Action onDoubleClick)
        {
            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "YAOLlm";
            _trayIcon.Visible = true;
            _trayIcon.ContextMenuStrip = new ContextMenuStrip();
            _trayIcon.ContextMenuStrip.Items.Add("Quit", null, (s, e) => Application.Exit());
            _trayIcon.DoubleClick += (s, e) => onDoubleClick();

            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("YAOLlm.icon.ico");
                _trayIcon.Icon = stream != null ? new Icon(stream) : SystemIcons.Application;
            }
            catch
            {
                _trayIcon.Icon = SystemIcons.Application;
            }
        }

        public void Dispose()
        {
            _trayIcon.Dispose();
        }
    }
}
