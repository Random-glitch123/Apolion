using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Timers;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using CefSharp.WinForms;

namespace Apolion
{
    /// <summary>
    /// Safety net for escaped CEF windows.
    ///
    /// Every JavaScript popup is cancelled in <see cref="ApolionLifeSpanHandler"/>
    /// and re-created as an Apolion tab. If a window ever slips past that handler
    /// (a DevTools-owned popup, CEF/Chrome UI code, a renderer race, ...) it becomes
    /// an unmanaged top-level <c>Chrome_WidgetWin_*</c> window. CEF titles such a
    /// window "Chromium" only until its page provides a title, so matching on the
    /// title misses most escapes.
    ///
    /// This guard polls the process' top-level windows and closes anything that is
    /// not one of the browser windows Apolion is deliberately hosting. It never
    /// touches windows that have an owner (menus, tooltips, selects, Chromium's
    /// download bubble), our WPF windows, live tab browsers (even while they are
    /// briefly re-parented during a tab move), or the DevTools window.
    ///
    /// IMPORTANT: Uses a background <see cref="System.Timers.Timer"/> rather than a
    /// <see cref="DispatcherTimer"/> so sweeps keep running while the UI thread is
    /// blocked by a modal dialog (e.g. a Save File dialog). The guard never depends
    /// on the WPF dispatcher being able to pump.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class StrayWindowGuard
    {
        private const uint GW_OWNER = 4;
        private const uint WM_CLOSE = 0x0010;

        // Sweep every 1 second (was 5s).
        private const int SweepIntervalMs = 1000;

        // Titles CEF/Chrome assigns to download UI. These only ever match unowned
        // top-level windows; Chromium's in-browser download bubble is an owned popup
        // and is therefore skipped by this guard. The bubble is suppressed at the
        // source: the DownloadBubble feature is disabled via
        // --disable-features=DownloadBubble (App.xaml.cs), and
        // download_bubble.partial_view_enabled=false is set as belt-and-suspenders.
        // This list is a fallback for a download window that genuinely escaped.
        private static readonly string[] DownloadTitles = {
            "download", "downloads", "recent download", "download history", "download shelf", "download bubble"
        };

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private static readonly HashSet<long> RequestedClose = new();
        private static readonly HashSet<long> KnownHandles = new();
        private static readonly StringBuilder ClassNameBuffer = new(128);
        private static readonly StringBuilder TitleBuffer = new(256);

        private static System.Timers.Timer? _timer;
        private static uint _processId;

        public static void Start(Dispatcher dispatcher)
        {
            if (_timer != null) return;

            _processId = (uint)Environment.ProcessId;

            // Background timer: never blocks on the UI thread, fires even while a
            // modal dialog holds the dispatcher. AutoReset keeps it running.
            _timer = new System.Timers.Timer(SweepIntervalMs) { AutoReset = true };
            _timer.Elapsed += (_, _) => Sweep();
            _timer.Start();

            AppLogger.Info($"Stray CEF window guard started (interval={SweepIntervalMs}ms, background timer)");
        }

        public static void Stop()
        {
            if (_timer == null) return;

            try { _timer.Stop(); _timer.Dispose(); } catch { }
            _timer = null;
            RequestedClose.Clear();
            KnownHandles.Clear();
            AppLogger.Info("Stray CEF window guard stopped");
        }

        private static void Sweep()
        {
            try
            {
                CollectKnownHandles();
                EnumWindows(OnWindow, IntPtr.Zero);
                PruneClosedHandles();
            }
            catch (Exception ex)
            {
                AppLogger.Exception("StrayWindowGuard.Sweep", ex);
            }
        }

        /// <summary>
        /// Collects every HWND that belongs to an Apolion window or a live tab
        /// browser. A tab browser is normally a child window, but tab moves detach
        /// and re-parent it, so it can briefly appear as a top-level window. Those
        /// must never be mistaken for an escaped popup.
        /// </summary>
        private static void CollectKnownHandles()
        {
            KnownHandles.Clear();

            var app = System.Windows.Application.Current;
            if (app == null) return;

            // Application.Windows is owned by the WPF dispatcher; the guard's
            // background timer cannot touch it directly. Marshal to the UI thread.
            // If the dispatcher is blocked (modal dialog), fall back to the
            // previously cached KnownHandles rather than crashing the sweep.
            List<System.Windows.Window>? windows;
            try
            {
                windows = (List<System.Windows.Window>?)app.Dispatcher.Invoke(() =>
                {
                    try { return app.Windows.OfType<System.Windows.Window>().ToList(); }
                    catch { return null; }
                }, TimeSpan.FromMilliseconds(100));
            }
            catch
            {
                // Dispatcher blocked or unavailable; keep stale KnownHandles so
                // we never falsely close a legitimate window on this sweep.
                return;
            }

            if (windows == null) return;

            foreach (System.Windows.Window window in windows)
            {
                if (window is not MainWindow mainWindow) continue;

                try
                {
                    IntPtr windowHandle = new WindowInteropHelper(mainWindow).Handle;
                    if (windowHandle != IntPtr.Zero)
                    {
                        KnownHandles.Add(windowHandle.ToInt64());
                    }

                    var layer = mainWindow.BrowserLayerGrid;
                    if (layer == null) continue;

                    foreach (var host in layer.Children.OfType<WindowsFormsHost>())
                    {
                        if (host.Child is not ChromiumWebBrowser browser || browser.IsDisposed)
                        {
                            continue;
                        }

                        try
                        {
                            if (browser.IsHandleCreated)
                            {
                                KnownHandles.Add(browser.Handle.ToInt64());
                            }

                            IntPtr browserHwnd = browser.BrowserHwnd;
                            if (browserHwnd != IntPtr.Zero)
                            {
                                KnownHandles.Add(browserHwnd.ToInt64());
                            }
                        }
                        catch
                        {
                            // The browser may be tearing down; skip it for this sweep.
                        }
                    }
                }
                catch
                {
                    // A window may be closing; skip it for this sweep.
                }
            }
        }

        private static bool IsDownloadTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            var lower = title.ToLowerInvariant();
            foreach (var keyword in DownloadTitles)
            {
                if (lower.Contains(keyword)) return true;
            }
            return false;
        }

        private static bool OnWindow(IntPtr hWnd, IntPtr lParam)
        {
            try
            {
                GetWindowThreadProcessId(hWnd, out uint processId);
                if (processId != _processId) return true;
                if (!IsWindowVisible(hWnd)) return true;

                // Owned windows are normal child UI (menus, select popups, tooltips).
                if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;

                ClassNameBuffer.Clear();
                GetClassName(hWnd, ClassNameBuffer, ClassNameBuffer.Capacity);
                string className = ClassNameBuffer.ToString();
                if (!className.StartsWith("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                long handle = hWnd.ToInt64();
                if (KnownHandles.Contains(handle)) return true;

                TitleBuffer.Clear();
                GetWindowText(hWnd, TitleBuffer, TitleBuffer.Capacity);
                string title = TitleBuffer.ToString();

                // DevTools is an intentional Apolion feature; never reap it.
                if (title.Contains("DevTools", StringComparison.OrdinalIgnoreCase)) return true;

                GetWindowRect(hWnd, out RECT rect);
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                // A zero-sized window is a hidden/plumbing window, not an escape.
                if (width <= 0 || height <= 0) return true;

                bool isDownload = IsDownloadTitle(title);

                // Download overlays are closed on the very first sweep; other
                // escaped windows need to be seen twice before we act (gives
                // transient CEF plumbing windows a chance to disappear).
                if (isDownload)
                {
                    AppLogger.Warn(
                        $"[STRAY] Closing escaped download UI hwnd=0x{handle:X} class='{className}' " +
                        $"title='{title}' size={width}x{height}");
                    PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }

                if (!RequestedClose.Add(handle)) return true;

                AppLogger.Warn(
                    $"[STRAY] Closing escaped CEF window hwnd=0x{handle:X} class='{className}' " +
                    $"title='{title}' size={width}x{height}");
                PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // A single window must never break the sweep for the others.
            }

            return true;
        }

        private static void PruneClosedHandles()
        {
            if (RequestedClose.Count == 0) return;

            RequestedClose.RemoveWhere(handle => !IsWindow(new IntPtr(handle)));
        }
    }
}