using System.IO;
using System.Windows;

namespace PingBoost;

public partial class MainWindow
{
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ToolStripItem? _trayRestoreItem;
    private System.Windows.Forms.ToolStripItem? _trayExitItem;
    private bool _trayHintShown;

    private void InitializeTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        _trayRestoreItem = menu.Items.Add(string.Empty, null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _trayExitItem = menu.Items.Add(string.Empty, null, (_, _) => Application.Current.Shutdown());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "PingBoost",
            ContextMenuStrip = menu,
            Visible = false
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left)
                RestoreFromTray();
        };
        RefreshTrayMenu();
    }

    private void RefreshTrayMenu()
    {
        var english = _settings.Language == "en";
        if (_trayRestoreItem is not null)
            _trayRestoreItem.Text = english ? "Restore" : "Развернуть";
        if (_trayExitItem is not null)
            _trayExitItem.Text = english ? "Exit" : "Выход";
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/PingBoost.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or ArgumentException)
        {
            // Fall back to the stock icon below; a missing tray picture must not stop the app.
        }
        return System.Drawing.SystemIcons.Application;
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        if (_trayIcon is null)
            return;
        _trayIcon.Visible = true;
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            var english = _settings.Language == "en";
            _trayIcon.ShowBalloonTip(3000, "PingBoost",
                english ? "Still running. Click the tray icon to restore, right-click for the menu."
                        : "Работает в фоне. Щёлкните значок в трее, чтобы развернуть; правая кнопка открывает меню.",
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null)
            _trayIcon.Visible = false;
    }

    private void ShutdownTray()
    {
        if (_trayIcon is null)
            return;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }
}
