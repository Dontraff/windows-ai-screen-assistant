using System.Drawing;
using System.Windows.Forms;
using ScreenAssistant.Configuration;

namespace ScreenAssistant.Tray;

internal sealed class AppTrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public AppTrayIcon(Action showWindow, Action exitApp)
    {
        Icon? icon = null;
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
                icon = Icon.ExtractAssociatedIcon(path);
        }
        catch (Exception ex)
        {
            LocalFiles.Log("tray-icon", ex);
        }

        _icon = new NotifyIcon
        {
            Text = "AI Screen Assistant",
            Icon = icon ?? SystemIcons.Application,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => showWindow();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть окно", null, (_, _) => showWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => exitApp());
        _icon.ContextMenuStrip = menu;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
