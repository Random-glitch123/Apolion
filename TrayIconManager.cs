using System;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace Apolion
{
    /// <summary>
    /// Tray icon used only while warm start is enabled. It keeps Apolion
    /// reachable after the last window closes so the next launch can hand its
    /// request to the still-warm browser process.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class TrayIconManager
    {
        private static NotifyIcon? _notifyIcon;
        private static ContextMenuStrip? _menu;
        private static Icon? _icon;
        private static bool _ownsIcon;
        private static bool _backgroundTipShown;

        public static bool IsVisible => _notifyIcon?.Visible == true;

        public static void Show(Action open, Action exit)
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = true;
                return;
            }

            try
            {
                _icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);
                _ownsIcon = _icon != null;
                _icon ??= SystemIcons.Application;
            }
            catch
            {
                _icon = SystemIcons.Application;
                _ownsIcon = false;
            }

            _menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("Open Apolion");
            openItem.Click += (_, _) => open();
            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (_, _) => exit();
            _menu.Items.Add(openItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);

            _notifyIcon = new NotifyIcon
            {
                Icon = _icon,
                Text = "Apolion",
                Visible = true,
                ContextMenuStrip = _menu
            };
            _notifyIcon.DoubleClick += (_, _) => open();

            AppLogger.Info("Tray icon shown (warm start enabled)");
        }

        public static void Hide()
        {
            if (_notifyIcon == null) return;

            try { _notifyIcon.Visible = false; } catch { }
            try { _notifyIcon.Dispose(); } catch { }
            try { _menu?.Dispose(); } catch { }
            if (_ownsIcon)
            {
                try { _icon?.Dispose(); } catch { }
            }

            _notifyIcon = null;
            _menu = null;
            _icon = null;
            _ownsIcon = false;
            AppLogger.Info("Tray icon hidden");
        }

        /// <summary>
        /// Explains once per process why Apolion is still running after the
        /// last window closed.
        /// </summary>
        public static void NotifyRunningInBackground()
        {
            if (_notifyIcon == null || _backgroundTipShown) return;
            _backgroundTipShown = true;

            try
            {
                _notifyIcon.ShowBalloonTip(
                    4000,
                    "Apolion is still running",
                    "Warm start is on, so Apolion stays in the tray and the next launch opens instantly. Right-click the tray icon to exit.",
                    ToolTipIcon.Info);
            }
            catch
            {
                // Notifications may be disabled by the OS; the tray icon is enough.
            }
        }
    }
}
