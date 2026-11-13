using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Forms.Integration;
using CefSharp;
using CefSharp.WinForms;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using TabControl = System.Windows.Controls.TabControl;
using Panel = System.Windows.Controls.Panel;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using DragEventArgs = System.Windows.DragEventArgs;
using GiveFeedbackEventArgs = System.Windows.GiveFeedbackEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DataObject = System.Windows.DataObject;
using DragDrop = System.Windows.DragDrop;
using System.Windows.Documents;
using System.Windows.Media.Effects;

namespace Apolion
{
    /// <summary>
    /// Simple LRU cache with a maximum size limit.
    /// </summary>
    internal sealed class LruCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<LruEntry>> _map;
        private readonly LinkedList<LruEntry> _list;
        private readonly object _lock = new();

        private sealed class LruEntry
        {
            public TKey Key { get; }
            public TValue Value { get; set; }
            public LruEntry(TKey key, TValue value) { Key = key; Value = value; }
        }

        public LruCache(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _map = new Dictionary<TKey, LinkedListNode<LruEntry>>(capacity);
            _list = new LinkedList<LruEntry>();
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _list.Remove(node);
                    _list.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
                value = default!;
                return false;
            }
        }

        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _list.Remove(node);
                    _list.AddFirst(node);
                    return node.Value.Value;
                }

                var entry = new LruEntry(key, valueFactory(key));
                var newNode = _list.AddFirst(entry);
                _map[key] = newNode;

                if (_map.Count > _capacity)
                {
                    var last = _list.Last!;
                    _list.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
                return entry.Value;
            }
        }

        public void Remove(TKey key)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _list.Remove(node);
                    _map.Remove(key);
                }
            }
        }
    }

    public partial class MainWindow : Window
    {
        private WindowState _preFullscreenState = WindowState.Normal;
        private ResizeMode _preFullscreenResizeMode = ResizeMode.CanResize;
        private Rect _preFullscreenBounds;
        internal bool _isFullscreen = false;
        private string _currentTheme = "Dark";
        private bool _savedTabsCleared = false;
        private bool _suppressLanguageChange = false;

        private Point? _tabDragStartPoint;
        private Point _dragOffset;
        private TabItem? _draggedTab;
        private Window? _dragPreviewWindow;
        internal int _currentDropIndex = -1;
        private bool _isReorderingTabs = false;
        private bool _tabDraggedOutside = false;

        // Lazy CEF initialization support
        private bool _cefReady = false;
        private readonly List<(string url, bool inBackground, string? title)> _pendingTabs = new();
        private readonly object _pendingTabsLock = new();

        private class TabDragData
        {
            public MainWindow SourceWindow { get; set; } = null!;
            public TabItem SourceTab { get; set; } = null!;
            public bool DropHandled { get; set; } = false;
        }

        // ─── Pointer lock ────────────────────────────────────────────────────
        // Pointer Lock is handled natively by Chromium (Chrome runtime style,
        // configured in App.OnStartup). There is no host-side shim or raw-input
        // pump; the page's requestPointerLock()/exitPointerLock() and Esc are
        // handled entirely by the browser.

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT2 lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT2 Point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const uint GA_ROOT = 2;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT2
        {
            public int X;
            public int Y;
        }

        private const double ZoomStep = 0.5;

        private static readonly System.Net.Http.HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private DispatcherTimer? _zoomHideTimer;

        // Debounced tab width updates to avoid layout thrashing
        private DispatcherTimer? _tabWidthDebounceTimer;
        private int _lastTabCount = -1;
        private double _lastAvailableWidth = -1;

        // Favicon cache: URL -> BitmapImage (shared across all windows, LRU limited to 256 entries)
        private static readonly LruCache<string, System.Windows.Media.Imaging.BitmapImage> _faviconCache = new(256);
        // Deduplication: URL -> Task<BitmapImage> for in-flight downloads
        private static readonly ConcurrentDictionary<string, Task<System.Windows.Media.Imaging.BitmapImage?>> _pendingFaviconDownloads = new();

        // Tab sleeping: discard inactive tabs after timeout to save memory/CPU
        private TimeSpan _tabSleepTimeout = TimeSpan.FromMinutes(10);
        private readonly Dictionary<TabItem, DateTime> _tabLastActive = new();
        private DispatcherTimer? _tabSleepTimer;

        // Helper: get the active ChromiumWebBrowser from the selected tab's hosted layer
        private ChromiumWebBrowser? CurrentBrowser =>
            BrowserContainer?.SelectedItem is TabItem tab &&
            tab.Tag is WindowsFormsHost host &&
            host.Child is ChromiumWebBrowser b &&
            !b.IsDisposed
                ? b
                : null;

        internal bool IsActiveBrowser(IWebBrowser browser)
            => ReferenceEquals(CurrentBrowser, browser);

        private class LazyTabInfo
        {
            public string Url { get; set; } = "https://www.google.com";
            public TextBlock HeaderText { get; set; } = null!;
        }

        public MainWindow()
        {
            InitializeComponent();
            var handle = new WindowInteropHelper(this).EnsureHandle();
            var source = HwndSource.FromHwnd(handle);
            source?.AddHook(WndProc);

            this.PreviewKeyDown += MainWindow_PreviewKeyDown;
            this.StateChanged += Window_StateChanged;
            this.Closing += Window_Closing;

            TitleBarGrid.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) ToggleMaximize();
                else if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            };

            AllowDrop = true;
            DragOver += MainWindow_DragOver;
            Drop += MainWindow_Drop;

            BrowserContainer.AllowDrop = true;
            BrowserContainer.DragOver += BrowserContainer_DragOver;
            BrowserContainer.Drop += BrowserContainer_Drop;
            BrowserContainer.DragLeave += (s, e) =>
            {
                HideTabInsertionIndicator();
            };

            _zoomHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _zoomHideTimer.Tick += (s, e) =>
            {
                _zoomHideTimer.Stop();
                ZoomIndicatorBorder.Visibility = Visibility.Collapsed;
            };

            // Windows created after CEF is ready can create browsers immediately;
            // only a window opened while CEF is still initializing needs the
            // placeholder and the deferred-tab queue.
            _cefReady = App.IsCefInitialized;
            // Defer non-critical UI setup (theme, language list, placeholder) until after Show()
            // to reduce time-to-first-paint
            Loaded += (_, _) => DeferredInit();
        }

        private bool _deferredInitDone = false;

        /// <summary>
        /// Deferred initialization of non-critical UI elements after window is shown.
        /// This improves startup time by moving work off the critical path.
        /// </summary>
        private void DeferredInit()
        {
            if (_deferredInitDone) return;
            _deferredInitDone = true;
            _currentTheme = SettingsManager.GetTheme();
            ApplySavedTheme();

            LstLanguage.ItemsSource = LanguageManager.Languages;
            _suppressLanguageChange = true;
            LstLanguage.SelectedItem = LanguageManager.Current;
            _suppressLanguageChange = false;

            UpdateTabWidths();

            this.StateChanged += (s, e) =>
            {
                if (_isFullscreen && WindowState == WindowState.Minimized)
                {
                    SetFullscreen(false);
                }
            };

            // Tab sleep timer: check for inactive tabs every minute
            _tabSleepTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMinutes(1)
            };
            _tabSleepTimer.Tick += (_, _) => CheckTabSleep();
            _tabSleepTimer.Start();

            // Power mode awareness: adjust tab sleep timeout on battery
            PowerModeManager.RegisterCallback(onBattery =>
            {
                _tabSleepTimeout = onBattery ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(10);
                AppLogger.Debug($"Tab sleep timeout adjusted: {_tabSleepTimeout.TotalMinutes} min (onBattery={onBattery})");
            });

            if (!_cefReady)
            {
                ShowCefInitializingPlaceholder();
            }
        }

        /// <summary>
        /// Called by App when CEF initialization completes.
        /// Processes any pending tabs and, for the startup window only, restores
        /// the previous session.
        /// </summary>
        internal void OnCefInitialized(bool restoreSession)
        {
            // A window created after CEF was already ready has nothing to wait for.
            if (_cefReady) return;

            _cefReady = true;
            HideCefInitializingPlaceholder();

            // Process any tabs that were added before CEF was ready
            List<(string url, bool inBackground, string? title)> pending;
            lock (_pendingTabsLock)
            {
                pending = _pendingTabs.ToList();
                _pendingTabs.Clear();
            }

            if (pending.Count > 0)
            {
                foreach (var (url, inBackground, title) in pending)
                {
                    AddNewTabCore(url, inBackground, title);
                }
            }
            else if (restoreSession)
            {
                // No pending tabs - restore session or create default tab
                _ = RestoreSessionAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        AppLogger.Exception("RestoreSessionAsync failed", t.Exception!);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        private void ShowCefInitializingPlaceholder()
        {
            // Create a temporary placeholder tab showing "Initializing..."
            var placeholderTab = new TabItem();
            var (_, headerText, _) = CreateTabHeader(placeholderTab, "Initializing Apolion…");
            headerText.Foreground = (Brush)FindResource("SettingsTextMuted");
            placeholderTab.IsEnabled = false;
            
            // Add a simple text block as content
            var placeholderContent = new TextBlock
            {
                Text = "Initializing browser engine…\nPlease wait a moment.",
                Foreground = (Brush)FindResource("SettingsTextMuted"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                FontSize = 14,
                TextAlignment = TextAlignment.Center
            };
            
            var host = new WindowsFormsHost
            {
                Child = new System.Windows.Forms.Panel { BackColor = System.Drawing.Color.FromArgb(13, 17, 23) },
                Visibility = Visibility.Visible
            };
            Panel.SetZIndex(host, 1);
            BrowserLayerGrid.Children.Add(host);
            placeholderTab.Tag = host;

            BrowserContainer.Items.Add(placeholderTab);
            BrowserContainer.SelectedItem = placeholderTab;
        }

        private void HideCefInitializingPlaceholder()
        {
            // Remove placeholder tab and its host
            if (BrowserContainer.Items.Count > 0 && BrowserContainer.Items[0] is TabItem placeholder)
            {
                if (placeholder.Tag is WindowsFormsHost host)
                {
                    try { BrowserLayerGrid.Children.Remove(host); } catch { }
                    try { host.Dispose(); } catch { }
                }
                BrowserContainer.Items.Remove(placeholder);
            }
        }

        /// <summary>
        /// Restores previous session tabs asynchronously.
        /// Active tab loads first, background tabs are created as lazy tabs.
        /// </summary>
        private async Task RestoreSessionAsync()
        {
            await Task.Yield(); // Ensure async execution
            
            string[]? restoreUrls = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--restore-tabs" && i + 1 < args.Length)
                {
                    restoreUrls = args[i + 1].Split('|', StringSplitOptions.RemoveEmptyEntries);
                    break;
                }
            }

            var savedTabs = (restoreUrls != null && restoreUrls.Length > 0)
                ? restoreUrls
                : SettingsManager.LoadOpenTabs();

            if (savedTabs.Length == 0)
            {
                // Only add default tab if no tabs exist yet (e.g., not a transfer target)
                if (BrowserContainer.Items.Count == 0)
                {
                    AddNewTab("https://www.google.com");
                }
                return;
            }

            // Find active tab index
            int activeIndex = 0;
            for (int i = 0; i < savedTabs.Length; i++)
            {
                if (savedTabs[i].StartsWith("1:"))
                {
                    activeIndex = i;
                    break;
                }
            }

            // Load active tab FIRST (synchronously for immediate interactivity)
            var activeTabLine = savedTabs[activeIndex];
            if (activeTabLine.StartsWith("1:") || activeTabLine.StartsWith("0:"))
                activeTabLine = activeTabLine.Substring(2);

            string activeUrl = activeTabLine;
            string? activeTitle = null;
            int tabSep = activeTabLine.IndexOf('\t');
            if (tabSep >= 0)
            {
                activeUrl = activeTabLine.Substring(0, tabSep);
                activeTitle = activeTabLine.Substring(tabSep + 1);
            }

            if (!string.IsNullOrWhiteSpace(activeUrl))
            {
                AddNewTabCore(activeUrl, inBackground: false, initialTitle: activeTitle);
            }

            // Load background tabs ASYNCHRONOUSLY (as lazy tabs)
            var backgroundTabs = new List<(string url, string? title)>();
            for (int i = 0; i < savedTabs.Length; i++)
            {
                if (i == activeIndex) continue;
                
                var tabLine = savedTabs[i];
                if (tabLine.StartsWith("1:") || tabLine.StartsWith("0:"))
                    tabLine = tabLine.Substring(2);

                string cleanUrl = tabLine;
                string? savedTitle = null;
                int sep = tabLine.IndexOf('\t');
                if (sep >= 0)
                {
                    cleanUrl = tabLine.Substring(0, sep);
                    savedTitle = tabLine.Substring(sep + 1);
                }

                if (!string.IsNullOrWhiteSpace(cleanUrl))
                {
                    backgroundTabs.Add((cleanUrl, savedTitle));
                }
            }

            // Add background tabs as lazy tabs (no browser created until selected)
            foreach (var (url, title) in backgroundTabs)
            {
                AddLazyTab(url, title);
            }

            AppLogger.Info($"Restored session: 1 active + {backgroundTabs.Count} background tabs");
        }

        /// <summary>
        /// Adds a lazy tab (no browser created until selected).
        /// </summary>
        private void AddLazyTab(string url, string? initialTitle = null)
        {
            string title = initialTitle ?? "";
            if (string.IsNullOrWhiteSpace(title))
            {
                if (!string.IsNullOrWhiteSpace(url) && !url.Equals("https://www.google.com", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var uri = new Uri(url);
                        title = uri.Host;
                    }
                    catch { title = "New Tab"; }
                }
                else { title = "New Tab"; }
            }

            var tabItem = new TabItem();
            var (_, headerText, _) = CreateTabHeader(tabItem, title);
            tabItem.Tag = new LazyTabInfo { Url = url, HeaderText = headerText };
            
            BrowserContainer.Items.Add(tabItem);
            UpdateTabWidths();
        }

        // ─── Theme ────────────────────────────────────────────────────────────

        private void ApplySavedTheme()
        {
            bool isDark = _currentTheme == "Dark";
            SetTheme(isDark);
            UpdateThemeButtons();
        }

        private void SetTheme(bool isDark)
        {
            var themeDict = isDark
                ? (ResourceDictionary)Resources["DarkTheme"]
                : (ResourceDictionary)Resources["LightTheme"];
            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(themeDict);
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            AppLogger.Info($"MainWindow closing ({BrowserContainer?.Items.Count ?? 0} tab(s))");

            try
            {
                if (_savedTabsCleared)
                {
                    lock (_pendingClosedTabsLock) { _pendingClosedTabs.Clear(); }
                    SettingsManager.ClearSavedTabs();
                }
                else
                {
                    var myTabs = CollectOpenTabs();
                    bool otherWindowsVisible = Application.Current != null &&
                        Application.Current.Windows.OfType<MainWindow>().Any(w => w != this && w.IsVisible);

                    lock (_pendingClosedTabsLock)
                    {
                        _pendingClosedTabs.AddRange(myTabs);

                        if (!otherWindowsVisible)
                        {
                            // Last window closing - collect tabs from all windows
                            var finalTabs = new List<string>(_pendingClosedTabs);
                            
                            // Also collect from other windows that might have pending tabs
                            if (Application.Current != null)
                            {
                                foreach (var otherWindow in Application.Current.Windows.OfType<MainWindow>())
                                {
                                    if (otherWindow != this && otherWindow.IsVisible)
                                    {
                                        var otherTabs = otherWindow.CollectOpenTabs();
                                        finalTabs.AddRange(otherTabs);
                                    }
                                }
                            }
                            
                            _pendingClosedTabs.Clear();

                            if (finalTabs.Count == 0)
                                finalTabs.Add("1:https://www.google.com");

                            SettingsManager.SaveOpenTabs(finalTabs.ToArray());
                            AppLogger.Info($"Saved {finalTabs.Count} tab(s) for next session");
                        }
                        else
                        {
                            AppLogger.Info($"Deferred session save until the last window closes ({_pendingClosedTabs.Count} tab(s) queued)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Exception("Window_Closing saving tabs", ex);
            }

            try
            {
                bool lastMainWindow = Application.Current == null ||
                    !Application.Current.Windows.OfType<MainWindow>().Any(w => w != this && w.IsVisible);
                if (lastMainWindow) DownloadsWindow.CloseForShutdown();
            }
            catch (Exception ex)
            {
                AppLogger.Exception("Window_Closing closing downloads window", ex);
            }

            try { _zoomHideTimer?.Stop(); } catch { }
            try { _tabWidthDebounceTimer?.Stop(); } catch { }
            try { _tabSleepTimer?.Stop(); } catch { }
            HideDragPreview();
            DisposeBrowsers();
        }

        // Per-window pending closed tabs (not static to avoid cross-window interference)
        private readonly List<string> _pendingClosedTabs = new();
        private readonly object _pendingClosedTabsLock = new();

        private List<string> CollectOpenTabs()
        {
            var tabsToSave = new List<string>();

            foreach (TabItem tab in BrowserContainer.Items)
            {
                string url = "";
                if (tab.Tag is WindowsFormsHost host && host.Child is ChromiumWebBrowser b && !b.IsDisposed)
                {
                    url = b.Address ?? "https://www.google.com";
                }
                else if (tab.Tag is LazyTabInfo lazy)
                {
                    url = lazy.Url;
                }

                if (!string.IsNullOrWhiteSpace(url))
                {
                    string title = GetTabTitle(tab);
                    string prefix = (tab == BrowserContainer.SelectedItem) ? "1:" : "0:";
                    tabsToSave.Add(string.IsNullOrWhiteSpace(title) ? $"{prefix}{url}" : $"{prefix}{url}\t{title}");
                }
            }

            return tabsToSave;
        }

        private bool _browsersDisposed;

        /// <summary>
        /// Releases every Chromium instance owned by this window. Called when the
        /// window closes and during application shutdown so Cef.Shutdown() does not
        /// have to tear down live browsers (a common source of exit hangs).
        /// </summary>
        internal void DisposeBrowsers()
        {
            if (_browsersDisposed) return;
            _browsersDisposed = true;

            try
            {
                var hosts = BrowserLayerGrid.Children.OfType<WindowsFormsHost>().ToList();
                AppLogger.Info($"DisposeBrowsers: disposing {hosts.Count} browser host(s)");
                foreach (var host in hosts)
                {
                    var browser = host.Child as ChromiumWebBrowser;
                    try { host.Child = null; } catch { }
                    try { BrowserLayerGrid.Children.Remove(host); } catch { }

                    if (browser != null)
                    {
                        try { browser.Dispose(); AppLogger.Debug("Disposed ChromiumWebBrowser"); }
                        catch (Exception ex) { AppLogger.Exception("Disposing ChromiumWebBrowser", ex); }
                    }

                    try { host.Dispose(); } catch { }
                }

                BrowserContainer.Items.Clear();
                AppLogger.Info("DisposeBrowsers: completed");
            }
            catch (Exception ex)
            {
                AppLogger.Exception("DisposeBrowsers", ex);
            }
        }

        // ─── Custom title bar caption buttons ────────────────────────────────

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
            => ToggleMaximize();

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Close();

        private void ToggleMaximize()
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                BtnMaximize.Content = "\uE922";
                BtnMaximize.ToolTip = "Maximize";
            }
            else
            {
                WindowState = WindowState.Maximized;
                BtnMaximize.Content = "\uE923";
                BtnMaximize.ToolTip = "Restore Down";
            }
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (_isFullscreen) return;

            if (WindowState == WindowState.Maximized)
            {
                WindowBorder.BorderThickness = new Thickness(0);
                BtnMaximize.Content = "\uE923";
                BtnMaximize.ToolTip = "Restore Down";
            }
            else if (WindowState == WindowState.Normal)
            {
                WindowBorder.BorderThickness = new Thickness(1);
                BtnMaximize.Content = "\uE922";
                BtnMaximize.ToolTip = "Maximize";
            }
            UpdateTabWidths();
        }

        // ─── Win32 WM_GETMINMAXINFO Hook (Fixes Maximize WorkArea clipping & Fullscreen) ───

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO))!;
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    var rc = _isFullscreen ? monitorInfo.rcMonitor : monitorInfo.rcWork;
                    mmi.ptMaxPosition.X = Math.Abs(rc.Left - monitorInfo.rcMonitor.Left);
                    mmi.ptMaxPosition.Y = Math.Abs(rc.Top - monitorInfo.rcMonitor.Top);
                    mmi.ptMaxSize.X = Math.Abs(rc.Right - rc.Left);
                    mmi.ptMaxSize.Y = Math.Abs(rc.Bottom - rc.Top);
                    mmi.ptMaxTrackSize.X = Math.Abs(monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left);
                    mmi.ptMaxTrackSize.Y = Math.Abs(monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top);
                }
            }
            Marshal.StructureToPtr(mmi, lParam, true);
        }

        // ─── Fullscreen ──────────────────────────────────────────────────────

        public void SetFullscreen(bool fullscreen)
        {
            if (fullscreen == _isFullscreen) return;
            _isFullscreen = fullscreen;

            if (_isFullscreen)
            {
                _preFullscreenState = WindowState;
                _preFullscreenResizeMode = ResizeMode;
                _preFullscreenBounds = new Rect(Left, Top, Width, Height);

                TitleBarRow.Height = new GridLength(0);
                ToolbarRow.Height = new GridLength(0);
                BrowserContainer.Visibility = Visibility.Collapsed;
                if (BrowserAreaGrid != null)
                {
                    BrowserAreaGrid.Margin = new Thickness(0);
                }
                WindowBorder.BorderThickness = new Thickness(0);
                SettingsPopup.IsOpen = false;

                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
                if (chrome != null)
                {
                    chrome.ResizeBorderThickness = new Thickness(0);
                }

                ResizeMode = ResizeMode.NoResize;

                // Use WPF's native monitor detection for correct multi-monitor/DPI handling
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    IntPtr monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
                    if (monitor != IntPtr.Zero)
                    {
                        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                        if (GetMonitorInfo(monitor, ref monitorInfo))
                        {
                            // Use monitor work area (excludes taskbar) for proper fullscreen
                            // Convert to WPF device-independent pixels
                            var dpi = VisualTreeHelper.GetDpi(this);
                            double scaleX = dpi.DpiScaleX;
                            double scaleY = dpi.DpiScaleY;

                            Left = monitorInfo.rcMonitor.Left / scaleX;
                            Top = monitorInfo.rcMonitor.Top / scaleY;
                            Width = (monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left) / scaleX;
                            Height = (monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top) / scaleY;

                            WindowState = WindowState.Normal; // Must be Normal for explicit positioning
                        }
                    }
                }

                // No global keyboard hook - let Escape key naturally exit pointer lock
                // via ApolionKeyboardHandler (F11 still toggles fullscreen)
            }
            else
            {
                TitleBarRow.Height = new GridLength(36);
                ToolbarRow.Height = new GridLength(50);
                BrowserContainer.Visibility = Visibility.Visible;
                if (BrowserAreaGrid != null)
                {
                    BrowserAreaGrid.Margin = new Thickness(10, 0, 10, 10);
                }

                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
                if (chrome != null)
                {
                    chrome.ResizeBorderThickness = new Thickness(6);
                }

                ResizeMode = _preFullscreenResizeMode;

                if (_preFullscreenState == WindowState.Maximized)
                {
                    WindowState = WindowState.Maximized;
                    WindowBorder.BorderThickness = new Thickness(0);
                }
                else
                {
                    WindowState = WindowState.Normal;
                    Left = _preFullscreenBounds.Left;
                    Top = _preFullscreenBounds.Top;
                    Width = _preFullscreenBounds.Width;
                    Height = _preFullscreenBounds.Height;
                    WindowBorder.BorderThickness = new Thickness(1);
                }

                UpdateTabWidths();
            }
        }

        public void ToggleFullscreen() => SetFullscreen(!_isFullscreen);

// ─── Browser Tabs (Infinite Tabs, Compressed, Layered Graphics Container) ───

        private void BrowserContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateTabWidths();
        }

        private void UpdateTabWidths()
        {
            // Debounce: schedule the actual work if not already scheduled
            if (_tabWidthDebounceTimer == null)
            {
                _tabWidthDebounceTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                _tabWidthDebounceTimer.Tick += (_, _) =>
                {
                    _tabWidthDebounceTimer!.Stop();
                    PerformUpdateTabWidths();
                };
            }
            _tabWidthDebounceTimer.Stop();
            _tabWidthDebounceTimer.Start();
        }

        private void PerformUpdateTabWidths()
        {
            if (BrowserContainer == null || BrowserContainer.Items.Count == 0) return;

            int count = BrowserContainer.Items.Count;
            double availableWidth = BrowserContainer.ActualWidth;
            if (availableWidth <= 0)
            {
                availableWidth = this.ActualWidth > 50 ? this.ActualWidth - 40 : 800;
            }

            // Skip if nothing meaningful changed (tab count + available width)
            if (count == _lastTabCount && Math.Abs(availableWidth - _lastAvailableWidth) < 1.0)
            {
                return;
            }

            _lastTabCount = count;
            _lastAvailableWidth = availableWidth;

            double usableWidth = availableWidth - (count * 2);
            if (usableWidth < 36 * count) usableWidth = 36 * count;

            double targetWidth = Math.Floor(usableWidth / count);
            if (targetWidth > 220) targetWidth = 220;
            if (targetWidth < 36) targetWidth = 36;

            for (int i = 0; i < BrowserContainer.Items.Count; i++)
            {
                if (BrowserContainer.Items[i] is TabItem tab)
                {
                    tab.Width = targetWidth;

                    if (tab.Header is Grid grid && grid.Children.Count > 2)
                    {
                        var closeBtn = grid.Children[2] as UIElement;
                        if (closeBtn != null)
                        {
                            if (targetWidth < 60 && !tab.IsSelected && !tab.IsMouseOver)
                            {
                                closeBtn.Visibility = Visibility.Collapsed;
                            }
                            else
                            {
                                closeBtn.Visibility = Visibility.Visible;
                            }
                        }
                    }
                }
            }
        }

        public void CloseCurrentTab()
        {
            if (BrowserContainer.SelectedItem is TabItem currentTab)
            {
                CloseTab(currentTab);
            }
        }

        // ─── Tab Drag & Drop Reordering & Cross-Window Detach/Attach ───────────

        private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsChildOfOrIs<Button>(e.OriginalSource as DependencyObject))
            {
                _tabDragStartPoint = null;
                return;
            }

            if (sender is TabItem tab)
            {
                _tabDragStartPoint = e.GetPosition(this);
                _dragOffset = e.GetPosition(tab);
            }
        }

        private void TabItem_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || !_tabDragStartPoint.HasValue)
                return;

            if (sender is not TabItem tab)
                return;

            if (IsChildOfOrIs<Button>(e.OriginalSource as DependencyObject))
                return;

            Point cur = e.GetPosition(this);
            Vector diff = _tabDragStartPoint.Value - cur;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _draggedTab = tab;
                _currentDropIndex = BrowserContainer.Items.IndexOf(tab);
                _tabDraggedOutside = false;

                tab.Opacity = 0.4;
                try
                {
                    var dragData = new TabDragData
                    {
                        SourceWindow = this,
                        SourceTab = tab
                    };
                    var data = new DataObject("ApolionTabDragData", dragData);
                    data.SetData("ApolionTabItem", tab);

                    ShowDragPreview(tab);

                    var result = DragDrop.DoDragDrop(tab, data, DragDropEffects.Move);

                    // Check if released over an Apolion window where OLE drop didn't fire directly
                    if (!dragData.DropHandled && GetCursorPos(out var pt))
                    {
                        MainWindow? winUnderCursor = GetApolionWindowUnderPoint(pt);
                        if (winUnderCursor != null)
                        {
                            if (winUnderCursor == this)
                            {
                                // Only suppress tear-off if the cursor is still within the tab strip area;
                                // if it's below the tab strip (over the browser content), let it fall
                                // through to the _tabDraggedOutside tear-off check
                                try
                                {
                                    Point localPt = BrowserContainer.PointFromScreen(new Point(pt.X, pt.Y));
                                    bool isOverTabStrip = localPt.X >= -10 && localPt.X <= BrowserContainer.ActualWidth + 50 &&
                                                         localPt.Y >= -15 && localPt.Y <= BrowserContainer.ActualHeight + 15;
                                    if (isOverTabStrip)
                                    {
                                        dragData.DropHandled = true;
                                    }
                                }
                                catch
                                {
                                    dragData.DropHandled = true;
                                }
                            }
                            else
                            {
                                // Released over another Apolion window
                                int dropIdx = winUnderCursor._currentDropIndex >= 0
                                    ? winUnderCursor._currentDropIndex
                                    : winUnderCursor.BrowserContainer.Items.Count;

                                winUnderCursor.TransferTabFrom(this, tab, dropIdx);
                                winUnderCursor.Activate();
                                dragData.DropHandled = true;
                            }
                        }
                    }

                    // If dropped outside of any Apolion window, tear off into a new window (if multiple tabs exist)
                    if (!dragData.DropHandled && _tabDraggedOutside && BrowserContainer.Items.Count > 1)
                    {
                        DetachTabToNewWindow(tab);
                    }
                }
                finally
                {
                    tab.Opacity = 1.0;
                    HideDragVisuals();
                    foreach (var win in Application.Current.Windows.OfType<MainWindow>())
                    {
                        win.HideTabInsertionIndicator();
                    }
                    _draggedTab = null;
                    _tabDragStartPoint = null;
                    _tabDraggedOutside = false;
                }
            }
        }

        private void TabItem_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            e.UseDefaultCursors = false;
            Mouse.SetCursor(Cursors.Arrow);

            UpdateDragPreviewPosition();

            if (GetCursorPos(out var pt))
            {
                MainWindow? winUnderCursor = GetApolionWindowUnderPoint(pt);
                Point screenPt = new Point(pt.X, pt.Y);

                if (winUnderCursor == null)
                {
                    _tabDraggedOutside = true;
                }
                else if (winUnderCursor == this)
                {
                    Point localPt = BrowserContainer.PointFromScreen(screenPt);
                    bool isOverThisTabArea = localPt.X >= -10 && localPt.X <= BrowserContainer.ActualWidth + 50 &&
                                             localPt.Y >= -15 && localPt.Y <= BrowserContainer.ActualHeight + 15;
                    _tabDraggedOutside = !isOverThisTabArea;
                }
                else
                {
                    _tabDraggedOutside = false;
                }

                foreach (var win in Application.Current.Windows.OfType<MainWindow>())
                {
                    if (win == winUnderCursor)
                    {
                        try
                        {
                            Point localPt = win.BrowserContainer.PointFromScreen(screenPt);
                            if (localPt.X >= -10 && localPt.X <= win.BrowserContainer.ActualWidth + 50 &&
                                localPt.Y >= -15 && localPt.Y <= win.BrowserContainer.ActualHeight + 15)
                            {
                                win.UpdateTabInsertionIndicator(localPt);
                            }
                            else
                            {
                                win.HideTabInsertionIndicator();
                            }
                        }
                        catch
                        {
                            win.HideTabInsertionIndicator();
                        }
                    }
                    else
                    {
                        win.HideTabInsertionIndicator();
                    }
                }
            }

            e.Handled = true;
        }

        private void TabItem_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("ApolionTabDragData") || e.Data.GetDataPresent("ApolionTabItem"))
            {
                e.Effects = DragDropEffects.Move;
                Point containerPos = e.GetPosition(BrowserContainer);
                UpdateTabInsertionIndicator(containerPos);
                e.Handled = true;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void TabItem_Drop(object sender, DragEventArgs e)
        {
            PerformTabDrop(e);
        }

        private void BrowserContainer_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("ApolionTabDragData") || e.Data.GetDataPresent("ApolionTabItem"))
            {
                e.Effects = DragDropEffects.Move;
                Point containerPos = e.GetPosition(BrowserContainer);
                UpdateTabInsertionIndicator(containerPos);
                e.Handled = true;
            }
        }

        private void BrowserContainer_Drop(object sender, DragEventArgs e)
        {
            PerformTabDrop(e);
        }

        private void MainWindow_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("ApolionTabDragData") || e.Data.GetDataPresent("ApolionTabItem"))
            {
                e.Effects = DragDropEffects.Move;
                Point containerPos = e.GetPosition(BrowserContainer);
                if (containerPos.X >= -10 && containerPos.X <= BrowserContainer.ActualWidth + 50 &&
                    containerPos.Y >= -15 && containerPos.Y <= BrowserContainer.ActualHeight + 15)
                {
                    UpdateTabInsertionIndicator(containerPos);
                }
                else
                {
                    HideTabInsertionIndicator();
                }
                e.Handled = true;
            }
        }

        private void MainWindow_Drop(object sender, DragEventArgs e)
        {
            PerformTabDrop(e);
        }

        private void PerformTabDrop(DragEventArgs e)
        {
            TabDragData? dragData = null;
            if (e.Data.GetDataPresent("ApolionTabDragData"))
            {
                dragData = e.Data.GetData("ApolionTabDragData") as TabDragData;
            }

            TabItem? sourceTab = dragData?.SourceTab ?? e.Data.GetData("ApolionTabItem") as TabItem;
            if (sourceTab == null) return;

            MainWindow sourceWin = dragData?.SourceWindow ?? this;

            if (sourceWin == this)
            {
                int sourceIndex = BrowserContainer.Items.IndexOf(sourceTab);
                int newIndex = _currentDropIndex;

                if (sourceIndex >= 0 && newIndex >= 0)
                {
                    if (newIndex > sourceIndex)
                    {
                        newIndex--;
                    }
                    if (newIndex >= BrowserContainer.Items.Count)
                    {
                        newIndex = BrowserContainer.Items.Count - 1;
                    }
                    if (newIndex != sourceIndex)
                    {
                        MoveTab(sourceIndex, newIndex, sourceTab);
                    }
                }
            }
            else
            {
                int dropIndex = _currentDropIndex >= 0 ? _currentDropIndex : BrowserContainer.Items.Count;
                TransferTabFrom(sourceWin, sourceTab, dropIndex);
            }

            if (dragData != null)
            {
                dragData.DropHandled = true;
            }

            HideDragVisuals();
            e.Handled = true;
        }

        internal void UpdateTabInsertionIndicator(Point containerPos)
        {
            if (BrowserContainer == null || BrowserContainer.Items.Count == 0)
            {
                _currentDropIndex = 0;
                if (TabInsertionIndicator != null)
                {
                    Canvas.SetLeft(TabInsertionIndicator, 2);
                    Canvas.SetTop(TabInsertionIndicator, 2);
                    TabInsertionIndicator.Height = 26;
                    TabInsertionIndicator.Visibility = Visibility.Visible;
                }
                return;
            }

            int targetIndex = -1;
            double indicatorX = 0;

            for (int i = 0; i < BrowserContainer.Items.Count; i++)
            {
                if (BrowserContainer.Items[i] is TabItem t && t.IsVisible)
                {
                    Point tabPos = t.TransformToAncestor(BrowserContainer).Transform(new Point(0, 0));
                    double tabLeft = tabPos.X;
                    double tabRight = tabLeft + t.ActualWidth;
                    double tabMid = tabLeft + (t.ActualWidth / 2.0);

                    if (containerPos.X < tabMid)
                    {
                        targetIndex = i;
                        indicatorX = tabLeft;
                        break;
                    }
                    else if (containerPos.X <= tabRight)
                    {
                        targetIndex = i + 1;
                        indicatorX = tabRight;
                        break;
                    }
                }
            }

            if (targetIndex == -1)
            {
                targetIndex = BrowserContainer.Items.Count;
                if (BrowserContainer.Items.Count > 0 && BrowserContainer.Items[^1] is TabItem lastTab)
                {
                    Point lastPos = lastTab.TransformToAncestor(BrowserContainer).Transform(new Point(0, 0));
                    indicatorX = lastPos.X + lastTab.ActualWidth;
                }
            }

            _currentDropIndex = targetIndex;

            if (TabInsertionIndicator != null)
            {
                Canvas.SetLeft(TabInsertionIndicator, Math.Max(0, indicatorX - 1.5));
                Canvas.SetTop(TabInsertionIndicator, 2);
                TabInsertionIndicator.Height = Math.Max(26, BrowserContainer.ActualHeight - 6);
                TabInsertionIndicator.Visibility = Visibility.Visible;
            }
        }

        internal void HideTabInsertionIndicator()
        {
            if (TabInsertionIndicator != null)
            {
                TabInsertionIndicator.Visibility = Visibility.Collapsed;
            }
            _currentDropIndex = -1;
        }

        private void HideDragVisuals()
        {
            HideTabInsertionIndicator();
            HideDragPreview();
        }

        private void ShowDragPreview(TabItem tab)
        {
            HideDragPreview();

            var preview = CreateTabDragPreview(tab);
            var grid = new Grid { Margin = new Thickness(16) };
            grid.Children.Add(preview);

            _dragPreviewWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                IsHitTestVisible = false,
                Focusable = false,
                ShowActivated = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = grid,
                Resources = this.Resources
            };

            _dragPreviewWindow.SourceInitialized += (s, e) =>
            {
                try
                {
                    var helper = new WindowInteropHelper(_dragPreviewWindow);
                    int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
                    SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                }
                catch { }
            };

            UpdateDragPreviewPosition();
            _dragPreviewWindow.Show();
        }

        private void UpdateDragPreviewPosition()
        {
            if (_dragPreviewWindow == null) return;
            try
            {
                if (GetCursorPos(out var pt))
                {
                    var dpi = VisualTreeHelper.GetDpi(_dragPreviewWindow);
                    double x = (pt.X / dpi.DpiScaleX) - _dragOffset.X - 16;
                    double y = (pt.Y / dpi.DpiScaleY) - _dragOffset.Y - 16;
                    _dragPreviewWindow.Left = x;
                    _dragPreviewWindow.Top = y;
                }
            }
            catch { }
        }

        private void HideDragPreview()
        {
            if (_dragPreviewWindow != null)
            {
                try { _dragPreviewWindow.Close(); } catch { }
                _dragPreviewWindow = null;
            }
        }

        private static MainWindow? GetApolionWindowUnderPoint(POINT2 pt)
        {
            try
            {
                IntPtr hwnd = WindowFromPoint(pt);
                if (hwnd == IntPtr.Zero) return null;

                IntPtr rootHwnd = GetAncestor(hwnd, GA_ROOT);
                if (rootHwnd == IntPtr.Zero) rootHwnd = hwnd;

                foreach (Window win in System.Windows.Application.Current.Windows)
                {
                    if (win is MainWindow mw && mw.IsVisible)
                    {
                        var helper = new WindowInteropHelper(mw);
                        if (helper.Handle == rootHwnd || helper.Handle == hwnd)
                        {
                            return mw;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        internal void TransferTabFrom(MainWindow sourceWindow, TabItem sourceTab, int targetIndex)
        {
            if (sourceWindow == null || sourceTab == null) return;
            if (!sourceWindow.BrowserContainer.Items.Contains(sourceTab)) return;

            string title = GetTabTitle(sourceTab);
            ImageSource? faviconSource = null;
            if (sourceTab.Header is Grid g && g.Children.Count > 0 && g.Children[0] is Image img)
            {
                faviconSource = img.Source;
            }

            try
            {
                Cef.GetGlobalCookieManager()?.FlushStore(null);
            }
            catch { }

            int sourceIndex = sourceWindow.BrowserContainer.Items.IndexOf(sourceTab);

            if (sourceTab.Tag is WindowsFormsHost oldHost && oldHost.Child is ChromiumWebBrowser browser)
            {
                // Detach browser from the old host container so oldHost does not dispose it
                oldHost.Child = null;
                sourceWindow.BrowserLayerGrid.Children.Remove(oldHost);
                try { oldHost.Dispose(); } catch { }

                // Remove sourceTab from source window
                sourceWindow.BrowserContainer.Items.Remove(sourceTab);
                sourceWindow.UpdateTabWidths();

                // Create new TabItem and header wired to this window
                var newTabItem = new TabItem();
                var (_, headerText, faviconImage) = CreateTabHeader(newTabItem, title);
                if (faviconSource != null)
                {
                    faviconImage.Source = faviconSource;
                    faviconImage.Visibility = Visibility.Visible;
                }

                // Wire browser handlers to this window
                browser.LifeSpanHandler = new ApolionLifeSpanHandler(this);
                browser.RequestHandler = new ApolionRequestHandler(this);
                
                // Re-assign download handler to prevent Chromium's default download UI
                var downloadHandler = new ApolionDownloadHandler();
                downloadHandler.DownloadUpdated += (id, fileName, received, total, speed, isComplete, isCancelled) =>
                {
                    try
                    {
                        DownloadsWindow.UpdateIfOpen(id, fileName, received, total, speed, isComplete, isCancelled);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Exception("Download update", ex);
                    }
                };
                browser.DownloadHandler = downloadHandler;
                
                browser.AddressChanged += Browser_AddressChanged;

                browser.TitleChanged += (s, e) =>
                {
                    string newTitle = e.Title ?? "";
                    UiDispatch.Post(Dispatcher, () =>
                    {
                        headerText.Text = string.IsNullOrWhiteSpace(newTitle) ? "New Tab" : newTitle;
                        headerText.ToolTip = newTitle;

                        if (CurrentBrowser == browser)
                        {
                            TitleBarText.Text = string.IsNullOrEmpty(newTitle)
                                ? "Apolion"
                                : $"Apolion — {newTitle}";
                        }
                    }, "TransferTabFrom.TitleChanged");
                };

                var newHost = new WindowsFormsHost
                {
                    Child = browser
                };

                newTabItem.Tag = newHost;

                // Add to BrowserLayerGrid
                Panel.SetZIndex(newHost, 1);
                newHost.Visibility = Visibility.Visible;
                BrowserLayerGrid.Children.Add(newHost);

                // Hide other hosts in this window
                foreach (UIElement child in BrowserLayerGrid.Children)
                {
                    if (child != newHost)
                    {
                        child.Visibility = Visibility.Hidden;
                        Panel.SetZIndex(child, 0);
                    }
                }

                // Insert into BrowserContainer
                if (targetIndex < 0 || targetIndex > BrowserContainer.Items.Count)
                {
                    targetIndex = BrowserContainer.Items.Count;
                }
                BrowserContainer.Items.Insert(targetIndex, newTabItem);
                BrowserContainer.SelectedItem = newTabItem;

                TxtAddress.Text = browser.Address ?? "";
                TitleBarText.Text = string.IsNullOrEmpty(title) || title == "New Tab"
                    ? "Apolion"
                    : $"Apolion — {title}";

                UpdateTabWidths();

                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
                Activate();

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        browser.Invalidate();
                        browser.Update();
                    }
                    catch { }
                }, DispatcherPriority.Render);
            }
            else if (sourceTab.Tag is LazyTabInfo lazy)
            {
                string url = lazy.Url;
                sourceWindow.BrowserContainer.Items.Remove(sourceTab);
                sourceWindow.UpdateTabWidths();

                var newTabItem = new TabItem();
                var (_, headerText, faviconImage) = CreateTabHeader(newTabItem, title);
                if (faviconSource != null)
                {
                    faviconImage.Source = faviconSource;
                    faviconImage.Visibility = Visibility.Visible;
                }
                newTabItem.Tag = new LazyTabInfo { Url = url, HeaderText = headerText };

                if (targetIndex < 0 || targetIndex > BrowserContainer.Items.Count)
                {
                    targetIndex = BrowserContainer.Items.Count;
                }
                BrowserContainer.Items.Insert(targetIndex, newTabItem);
                BrowserContainer.SelectedItem = newTabItem;
                UpdateTabWidths();

                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
                Activate();
            }

            // If source window has no more tabs, close it
            if (sourceWindow.BrowserContainer.Items.Count == 0)
            {
                sourceWindow.Close();
            }
            else
            {
                if (sourceWindow.BrowserContainer.SelectedItem == null && sourceWindow.BrowserContainer.Items.Count > 0)
                {
                    int nextIdx = Math.Min(sourceIndex, sourceWindow.BrowserContainer.Items.Count - 1);
                    sourceWindow.BrowserContainer.SelectedIndex = nextIdx;
                }
            }
        }

        private void DetachTabToNewWindow(TabItem tab)
        {
            GetCursorPos(out var cursorPt);
            var dpi = VisualTreeHelper.GetDpi(this);

            var newWin = new MainWindow();
            newWin.Left = Math.Max(0, (cursorPt.X / dpi.DpiScaleX) - 200);
            newWin.Top = Math.Max(0, (cursorPt.Y / dpi.DpiScaleY) - 20);
            newWin.Width = 1024;
            newWin.Height = 600;
            newWin.Show();

            newWin.TransferTabFrom(this, tab, 0);
        }

        private FrameworkElement CreateTabDragPreview(TabItem tab)
        {
            var preview = new Border
            {
                Width = Math.Max(60, tab.ActualWidth),
                Height = Math.Max(28, tab.ActualHeight),
                Background = (Brush)FindResource("ButtonHover"),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 56, 139, 253)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Opacity = 0.9,
                Padding = new Thickness(8, 4, 8, 4),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 14,
                    ShadowDepth = 3,
                    Opacity = 0.65
                }
            };

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (tab.Header is Grid g)
            {
                if (g.Children.Count > 0 && g.Children[0] is Image originalFavicon && originalFavicon.Source != null)
                {
                    panel.Children.Add(new Image
                    {
                        Source = originalFavicon.Source,
                        Width = 16,
                        Height = 16,
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                panel.Children.Add(new TextBlock
                {
                    Text = GetTabTitle(tab),
                    Foreground = (Brush)FindResource("ForegroundBrush"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = Math.Max(40, tab.ActualWidth - 36)
                });
            }

            preview.Child = panel;
            return preview;
        }

        private static string GetTabTitle(TabItem tab)
        {
            if (tab.Header is Grid headerGrid)
            {
                foreach (var child in headerGrid.Children)
                {
                    if (child is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
                    {
                        return tb.Text;
                    }
                }
            }
            return "";
        }

        private void MoveTab(int oldIndex, int newIndex, TabItem tab)
        {
            if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return;
            if (oldIndex >= BrowserContainer.Items.Count || newIndex >= BrowserContainer.Items.Count) return;

            _isReorderingTabs = true;
            try
            {
                BrowserContainer.Items.RemoveAt(oldIndex);
                BrowserContainer.Items.Insert(newIndex, tab);
                BrowserContainer.SelectedItem = tab;
            }
            finally
            {
                _isReorderingTabs = false;
            }

            BrowserContainer_SelectionChanged(BrowserContainer, null);
            UpdateTabWidths();
        }

        private static bool IsChildOfOrIs<T>(DependencyObject? element) where T : DependencyObject
        {
            while (element != null)
            {
                if (element is T) return true;
                if (element is Visual || element is System.Windows.Media.Media3D.Visual3D)
                    element = VisualTreeHelper.GetParent(element);
                else
                    element = LogicalTreeHelper.GetParent(element);
            }
            return false;
        }

        private static TabItem? FindParentTabItem(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is TabItem tab) return tab;
                if (element is Visual || element is System.Windows.Media.Media3D.Visual3D)
                    element = VisualTreeHelper.GetParent(element);
                else
                    element = LogicalTreeHelper.GetParent(element);
            }
            return null;
        }

        internal (Grid headerGrid, TextBlock headerText, Image faviconImage) CreateTabHeader(TabItem tabItem, string initialTitle)
        {
            tabItem.AllowDrop = true;
            tabItem.PreviewMouseLeftButtonDown += TabItem_PreviewMouseLeftButtonDown;
            tabItem.PreviewMouseLeftButtonUp += (s, e) => _tabDragStartPoint = null;
            tabItem.PreviewMouseMove += TabItem_PreviewMouseMove;
            tabItem.DragOver += TabItem_DragOver;
            tabItem.Drop += TabItem_Drop;
            tabItem.GiveFeedback += TabItem_GiveFeedback;

            var headerGrid = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var faviconImage = new Image
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 6, 0),
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(faviconImage, 0);

            var headerText = new TextBlock
            {
                Text = initialTitle,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap
            };
            headerText.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundBrush");
            Grid.SetColumn(headerText, 1);

            var closeButton = new Button
            {
                Content = "✕",
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = tabItem,
                FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeButton.SetResourceReference(Button.ForegroundProperty, "ForegroundBrush");
            closeButton.Click += (s, e) =>
            {
                if (s is Button btn && btn.Tag is TabItem t)
                {
                    CloseTab(t);
                }
            };
            Grid.SetColumn(closeButton, 2);

            tabItem.MouseEnter += (s, e) =>
            {
                if (closeButton.Visibility != Visibility.Visible)
                {
                    closeButton.Visibility = Visibility.Visible;
                }
            };
            tabItem.MouseLeave += (s, e) =>
            {
                if (tabItem.ActualWidth < 60 && !tabItem.IsSelected)
                {
                    closeButton.Visibility = Visibility.Collapsed;
                }
            };

            headerGrid.Children.Add(faviconImage);
            headerGrid.Children.Add(headerText);
            headerGrid.Children.Add(closeButton);
            tabItem.Header = headerGrid;

            // Middle click to close tab
            tabItem.MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle)
                {
                    e.Handled = true;
                    CloseTab(tabItem);
                }
            };

            return (headerGrid, headerText, faviconImage);
        }

        private ChromiumWebBrowser CreateBrowserControl(string url, TextBlock headerText)
        {
            var browser = new ChromiumWebBrowser(url)
            {
                LifeSpanHandler = new ApolionLifeSpanHandler(this),
                MenuHandler = new ApolionContextMenuHandler(),
                PermissionHandler = new ApolionPermissionHandler(),
                RequestHandler = new ApolionRequestHandler(this),
                DisplayHandler = new ApolionDisplayHandler(),
                JsDialogHandler = new ApolionJsDialogHandler(),
                KeyboardHandler = new ApolionKeyboardHandler(this),
                RenderProcessMessageHandler = new ApolionRenderProcessMessageHandler(),
                Dock = System.Windows.Forms.DockStyle.Fill
            };

#if DEBUG
            MediaDiagnostics.Attach(browser);
#endif

            var downloadHandler = new ApolionDownloadHandler();
            downloadHandler.DownloadUpdated += (id, fileName, received, total, speed, isComplete, isCancelled) =>
            {
                try
                {
                    DownloadsWindow.UpdateIfOpen(id, fileName, received, total, speed, isComplete, isCancelled);
                }
                catch (Exception ex)
                {
                    AppLogger.Exception("Download update", ex);
                }
            };
            browser.DownloadHandler = downloadHandler;
            browser.AddressChanged += Browser_AddressChanged;
            browser.FrameLoadStart += (s, e) =>
            {
                // Keep navigator.language in sync after renderer/process changes.
                if (e.Frame.IsMain)
                {
                    _ = ApplyLocaleOverrideAsync(browser);
                }
            };
            browser.IsBrowserInitializedChanged += async (s, e) =>
            {
                if (browser.IsBrowserInitialized)
                {
                    try
                    {
                        using var client = browser.GetDevToolsClient();
                        bool isDark = SettingsManager.IsDarkModeEnabled();
                        await client.Emulation.SetEmulatedMediaAsync(
                            media: "screen",
                            features: new List<CefSharp.DevTools.Emulation.MediaFeature>
                            {
                                new CefSharp.DevTools.Emulation.MediaFeature
                                {
                                    Name = "prefers-color-scheme",
                                    Value = isDark ? "dark" : "light"
                                }
                            });

                        await client.Emulation.SetLocaleOverrideAsync(LanguageManager.IcuLocale);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug($"Browser localization setup failed: {ex.Message}");
                    }
                }
            };

            browser.TitleChanged += (s, e) =>
            {
                string newTitle = e.Title ?? "";
                UiDispatch.Post(Dispatcher, () =>
                {
                    headerText.Text = string.IsNullOrWhiteSpace(newTitle) ? "New Tab" : newTitle;
                    headerText.ToolTip = newTitle;

                    if (CurrentBrowser == browser)
                    {
                        TitleBarText.Text = string.IsNullOrEmpty(newTitle)
                            ? "Apolion"
                            : $"Apolion — {newTitle}";
                    }
                }, "Browser.TitleChanged");
            };

            browser.FrameLoadEnd += (s, e) =>
            {
                if (!e.Frame.IsMain) return;
            };

            return browser;
        }

        private void RealizeLazyTab(TabItem tabItem, LazyTabInfo lazy)
        {
            try
            {
                var browser = CreateBrowserControl(lazy.Url, lazy.HeaderText);
                var host = new WindowsFormsHost
                {
                    Child = browser,
                    Visibility = Visibility.Visible
                };
                BrowserLayerGrid.Children.Add(host);
                tabItem.Tag = host;
            }
            catch (Exception ex)
            {
                AppLogger.Exception($"RealizeLazyTab('{lazy.Url}')", ex);
            }
        }

        public void AddNewTab(string url = "https://www.google.com", bool inBackground = false, string? initialTitle = null)
        {
            try
            {
                if (!_cefReady)
                {
                    // Queue the tab for when CEF is ready
                    lock (_pendingTabsLock)
                    {
                        _pendingTabs.Add((url, inBackground, initialTitle));
                    }
                    return;
                }
                AddNewTabCore(url, inBackground, initialTitle);
            }
            catch (Exception ex)
            {
                AppLogger.Exception($"AddNewTab('{url}')", ex);
            }
        }

        private void AddNewTabCore(string url, bool inBackground, string? initialTitle)
        {
            if (BrowserContainer == null) return;

            string title = initialTitle ?? "";
            if (string.IsNullOrWhiteSpace(title))
            {
                if (!string.IsNullOrWhiteSpace(url) && !url.Equals("https://www.google.com", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var uri = new Uri(url);
                        title = uri.Host;
                    }
                    catch
                    {
                        title = "New Tab";
                    }
                }
                else
                {
                    title = "New Tab";
                }
            }

            var tabItem = new TabItem();
            var (_, headerText, _) = CreateTabHeader(tabItem, title);

            var browser = CreateBrowserControl(url, headerText);
            var host = new WindowsFormsHost
            {
                Child = browser
            };

            if (inBackground)
            {
                // Background tab:
                // To allow CefSharp to fully initialize its CEF browser instance and begin loading
                // the web page and title in the background, WPF's HwndHost must create the Win32 window handle.
                // We add it to BrowserLayerGrid with Visibility.Visible behind the active tab (ZIndex = -1),
                // and once loaded, set it to Visibility.Hidden (keeping the HWND attached and CEF actively loading).
                Panel.SetZIndex(host, -1);
                host.Visibility = Visibility.Visible;
                BrowserLayerGrid.Children.Add(host);
                tabItem.Tag = host;

                BrowserContainer.Items.Add(tabItem);

                host.Loaded += (s, e) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (BrowserContainer.SelectedItem != tabItem)
                        {
                            host.Visibility = Visibility.Hidden;
                            Panel.SetZIndex(host, 0);
                        }
                    }, DispatcherPriority.Loaded);
                };
            }
            else
            {
                Panel.SetZIndex(host, 1);
                host.Visibility = Visibility.Visible;
                BrowserLayerGrid.Children.Add(host);
                tabItem.Tag = host;

                BrowserContainer.Items.Add(tabItem);
                BrowserContainer.SelectedItem = tabItem;

                foreach (UIElement child in BrowserLayerGrid.Children)
                {
                    if (child != host)
                    {
                        child.Visibility = Visibility.Hidden;
                        Panel.SetZIndex(child, 0);
                    }
                }
            }

            UpdateTabWidths();
        }

        /// <summary>
        /// Opens a new browser window with the given URL, offset from this window
        /// so it's easily recognizable as a new window.
        /// </summary>
        public void OpenNewWindow(string url)
        {
            var newWin = new MainWindow();
            
            // Offset new window by 40px down and right from current window
            // Clamp to screen bounds
            var dpi = VisualTreeHelper.GetDpi(this);
            double offsetX = 40 / dpi.DpiScaleX;
            double offsetY = 40 / dpi.DpiScaleY;

            newWin.Left = Math.Max(0, Left + offsetX);
            newWin.Top = Math.Max(0, Top + offsetY);
            
            // Ensure window doesn't go off screen
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(newWin).EnsureHandle());
            double maxLeft = (screen.WorkingArea.Right - newWin.Width) / dpi.DpiScaleX;
            double maxTop = (screen.WorkingArea.Bottom - newWin.Height) / dpi.DpiScaleY;
            newWin.Left = Math.Min(newWin.Left, Math.Max(0, maxLeft));
            newWin.Top = Math.Min(newWin.Top, Math.Max(0, maxTop));

            newWin.Show();
            newWin.AddNewTab(url, inBackground: false);
        }

        public void CloseTab(TabItem tab)
        {
            try
            {
                if (tab.Tag is WindowsFormsHost host)
                {
                    var browser = host.Child as ChromiumWebBrowser;

                    // Detach before disposing so neither the host nor the browser
                    // disposes the other a second time (previously caused native
                    // access-violation crashes while closing tabs).
                    try { BrowserLayerGrid.Children.Remove(host); } catch { }
                    try { host.Child = null; } catch { }

                    if (browser != null)
                    {
                        try { browser.Dispose(); }
                        catch (Exception ex) { AppLogger.Exception("CloseTab browser dispose", ex); }
                    }

                    try { host.Dispose(); } catch (Exception ex) { AppLogger.Exception("CloseTab host dispose", ex); }
                }

                // Clean up tab tracking to prevent memory leaks
                _tabLastActive.Remove(tab);

                BrowserContainer.Items.Remove(tab);
                UpdateTabWidths();

                if (BrowserContainer.Items.Count == 0)
                {
                    Close();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Exception("CloseTab", ex);
            }
        }

        public void BrowserContainer_SelectionChanged(object sender, SelectionChangedEventArgs? e)
        {
            if (_isReorderingTabs) return;

            if ((e == null || e.Source is TabControl) && BrowserContainer.SelectedItem is TabItem selectedTab)
            {
                // Update last active time for the newly selected tab
                _tabLastActive[selectedTab] = DateTime.UtcNow;

                if (selectedTab.Tag is LazyTabInfo lazy)
                {
                    RealizeLazyTab(selectedTab, lazy);
                }

                var activeHost = selectedTab.Tag as WindowsFormsHost;
                if (activeHost != null)
                {
                    Panel.SetZIndex(activeHost, 1);
                }
                foreach (UIElement child in BrowserLayerGrid.Children)
                {
                    // Active tab host is Visible, background tab hosts are Hidden.
                    // Hidden keeps the Win32 HWND attached so background audio continues playing.
                    bool isActive = (child == activeHost);
                    child.Visibility = isActive ? Visibility.Visible : Visibility.Hidden;
                    if (!isActive)
                    {
                        Panel.SetZIndex(child, 0);
                    }
                }

                if (CurrentBrowser != null)
                {
                    TxtAddress.Text = CurrentBrowser.Address ?? "";
                    if (selectedTab.Header is System.Windows.Controls.Panel headerPanel &&
                        headerPanel.Children.Count > 1 &&
                        headerPanel.Children[1] is TextBlock textBlock)
                    {
                        TitleBarText.Text = textBlock.Text == "New Tab"
                            ? "Apolion"
                            : $"Apolion — {textBlock.Text}";
                    }
                    else
                    {
                        TitleBarText.Text = "Apolion";
                    }
                }

                UpdateTabWidths();
            }
        }

        /// <summary>
        /// Checks for inactive tabs that should be slept/discarded to save memory/CPU.
        /// Tabs inactive for _tabSleepTimeout are discarded (browser disposed) and will
        /// reload on next activation.
        /// </summary>
        private void CheckTabSleep()
        {
            if (BrowserContainer == null || BrowserContainer.Items.Count == 0) return;

            var now = DateTime.UtcNow;
            var tabsToSleep = new List<TabItem>();

            foreach (TabItem tab in BrowserContainer.Items)
            {
                // Don't sleep the active tab
                if (tab == BrowserContainer.SelectedItem) continue;

                // Don't sleep lazy tabs (not yet realized)
                if (tab.Tag is LazyTabInfo) continue;

                if (_tabLastActive.TryGetValue(tab, out var lastActive))
                {
                    if (now - lastActive > _tabSleepTimeout)
                    {
                        tabsToSleep.Add(tab);
                    }
                }
                else
                {
                    // First time seeing this tab, initialize its last active time
                    _tabLastActive[tab] = now;
                }
            }

            foreach (var tab in tabsToSleep)
            {
                SleepTab(tab);
            }
        }

        /// <summary>
        /// Discards a background tab's browser to free memory/CPU.
        /// The tab remains in the UI with its URL; selecting it will reload.
        /// </summary>
        private void SleepTab(TabItem tab)
        {
            try
            {
                if (tab.Tag is not WindowsFormsHost host) return;
                if (host.Child is not ChromiumWebBrowser browser) return;
                if (browser.IsDisposed) return;

                string url = browser.Address ?? "https://www.google.com";
                string? title = GetTabTitle(tab);

                // Remove the browser but keep the tab header
                BrowserLayerGrid.Children.Remove(host);
                host.Child = null;
                host.Dispose();
                browser.Dispose();

                // Clean up tab tracking to prevent memory leaks
                _tabLastActive.Remove(tab);

                // Convert to lazy tab (will reload on selection)
                tab.Tag = new LazyTabInfo { Url = url, HeaderText = GetHeaderTextBlock(tab) };
                
                AppLogger.Info($"Tab slept (inactive > {_tabSleepTimeout.TotalMinutes} min): {url}");
            }
            catch (Exception ex)
            {
                AppLogger.Exception($"SleepTab failed", ex);
            }
        }

        private TextBlock GetHeaderTextBlock(TabItem tab)
        {
            if (tab.Header is System.Windows.Controls.Panel panel && panel.Children.Count > 1)
            {
                return panel.Children[1] as TextBlock ?? new TextBlock { Text = "New Tab" };
            }
            return new TextBlock { Text = "New Tab" };
        }

        internal void Browser_AddressChanged(object? sender, AddressChangedEventArgs e)
        {
            string address = e.Address ?? "";
            UiDispatch.Post(Dispatcher, () =>
            {
                // A same-document URL change (pushState/hash) must not break an active
                // pointer lock. Real navigations are handled by FrameLoadStart.
                if (ReferenceEquals(sender, CurrentBrowser))
                    TxtAddress.Text = address;
            }, "Browser.AddressChanged");
        }

        // ─── Navigation ──────────────────────────────────────────────────────

        private void BtnBack_Click(object sender, RoutedEventArgs e) => CurrentBrowser?.Back();
        private void BtnForward_Click(object sender, RoutedEventArgs e) => CurrentBrowser?.Forward();
        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => CurrentBrowser?.Reload();

        private void TxtAddress_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            var url = TxtAddress.Text;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
             && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = url.Contains('.') && !url.Contains(' ')
                    ? "https://" + url
                    : "https://www.google.com/search?q=" + Uri.EscapeDataString(url);
            }

            CurrentBrowser?.Load(url);
        }

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (_isFullscreen)
                {
                    SetFullscreen(false);
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.F12 ||
                ((e.Key == Key.I || e.Key == Key.C) && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
            {
                if (App.DevToolsEnabled)
                {
                    var browser = CurrentBrowser;
                    if (browser != null)
                    {
                        var b = browser.GetBrowser();
                        var host = b?.GetHost();
                        if (host != null && host.HasDevTools)
                            browser.CloseDevTools();
                        else
                            browser.ShowDevTools();
                    }
                }
                else
                {
                    MessageBox.Show("Developer Tools are disabled. Enable them in Settings.",
                        "Apolion", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.T)
                {
                    AddNewTab("https://www.google.com");
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.W)
                {
                    CloseCurrentTab();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.N)
                {
                    var newWin = new MainWindow();
                    newWin.Show();
                    newWin.AddNewTab("https://www.google.com");
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.OemPlus || e.Key == Key.Add)
                {
                    AdjustZoom(+ZoomStep);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
                {
                    AdjustZoom(-ZoomStep);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.D0 || e.Key == Key.NumPad0)
                {
                    SetZoom(0.0);
                    e.Handled = true;
                    return;
                }
            }
        }

        // ─── Zoom helpers ────────────────────────────────────────────────────

        private async void AdjustZoom(double delta)
        {
            var browser = CurrentBrowser;
            if (browser == null) return;

            try
            {
                double current = await browser.GetZoomLevelAsync();
                SetZoom(current + delta);
            }
            catch { }
        }

        private void SetZoom(double level)
        {
            var browser = CurrentBrowser;
            if (browser == null) return;

            level = Math.Max(-5.0, Math.Min(5.0, level));
            browser.SetZoomLevel(level);

            double percent = Math.Round(100.0 * Math.Pow(1.2, level));
            ZoomIndicatorText.Text = $"{(int)percent}%";
            ZoomIndicatorBorder.Visibility = Visibility.Visible;
            _zoomHideTimer?.Stop();
            _zoomHideTimer?.Start();
        }

        // ─── Settings panel ──────────────────────────────────────────────────

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            DevToolsToggle.IsChecked = App.DevToolsEnabled;

            _suppressLanguageChange = true;
            LstLanguage.SelectedItem = LanguageManager.Current;
            _suppressLanguageChange = false;

            SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
        }

        private void BtnThemeLight_Click(object sender, RoutedEventArgs e)
            => ChangeTheme("Light");

        private void BtnThemeDark_Click(object sender, RoutedEventArgs e)
            => ChangeTheme("Dark");

        private void ChangeTheme(string newTheme)
        {
            if (_currentTheme == newTheme) return;

            _currentTheme = newTheme;
            SettingsManager.SetTheme(newTheme);
            AppLogger.Info($"Theme changed to {newTheme}");

            // Apply the theme to every open window immediately (WPF resources only).
            // New browsers will pick up the theme at creation via CreateBrowserControl.
            // Existing browsers don't need immediate update (will apply on next navigation).
            foreach (var window in Application.Current.Windows.OfType<MainWindow>().ToList())
            {
                window._currentTheme = newTheme;
                window.ApplySavedTheme();
            }

            // REMOVED: ApplyThemeToBrowsersAsync - redundant, creates DevTools connections per browser
            // Theme is applied at browser creation in CreateBrowserControl.IsBrowserInitializedChanged
        }

        // REMOVED: ApplyThemeToBrowsersAsync and ApplyColorSchemeToBrowsersAsync
        // Theme is applied at browser creation time, no need to retrofit existing browsers

        private void UpdateThemeButtons()
        {
            BtnThemeLight.Style = _currentTheme == "Light"
                ? (Style)Resources["ThemeSegBtnActiveStyle"]
                : (Style)Resources["ThemeSegBtnStyle"];
            BtnThemeDark.Style = _currentTheme == "Dark"
                ? (Style)Resources["ThemeSegBtnActiveStyle"]
                : (Style)Resources["ThemeSegBtnStyle"];
        }

        // ─── Website language ────────────────────────────────────────────────

        private async void LstLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressLanguageChange) return;
            if (LstLanguage.SelectedItem is not LanguageOption option) return;
            if (string.Equals(option.Code, LanguageManager.CurrentCode, StringComparison.OrdinalIgnoreCase)) return;

            LanguageManager.SetLanguage(option.Code);
            LanguageManager.ApplyLive();

            // REMOVED: ApplyLanguageToBrowsersAsync - redundant, creates DevTools connections per browser
            // Language is applied at browser creation in CreateBrowserControl.FrameLoadStart
            // and via RenderProcessMessageHandler.OnContextCreated (JS override)
            
            await Task.CompletedTask; // Suppress CS1998 warning
        }

        // Private method to apply locale at browser creation time (FrameLoadStart)
        private static async Task ApplyLocaleOverrideAsync(ChromiumWebBrowser browser)
        {
            try
            {
                if (browser.IsDisposed || !browser.IsBrowserInitialized) return;

                using var client = browser.GetDevToolsClient();
                await client.Emulation.SetLocaleOverrideAsync(LanguageManager.IcuLocale);
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Locale override failed: {ex.Message}");
            }
        }


        private void BtnOpenDownloads_Click(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;
            var win = DownloadsWindow.Instance;
            win.ApplySavedTheme();
            if (win.Owner == null) win.Owner = this;
            if (!win.IsVisible)
                win.Show();
            else
                win.Activate();
        }

        private async void BtnClearCookies_Click(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;
            try
            {
                var cookieManager = Cef.GetGlobalCookieManager();
                await cookieManager.DeleteCookiesAsync("", "");
                MessageBox.Show("All cookies have been cleared.", "Apolion",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to clear cookies: {ex.Message}", "Apolion",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnClearSavedTabs_Click(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;
            try
            {
                SettingsManager.ClearSavedTabs();
                _savedTabsCleared = true;
                MessageBox.Show("Saved tabs data has been removed.\nOn the next startup, Apolion will open with only a single new tab.",
                    "Apolion", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to remove saved tabs data: {ex.Message}", "Apolion",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnOpenLogsFolder_Click(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = AppLogger.LogDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Exception("Open logs folder", ex);
                MessageBox.Show($"Could not open the log folder:\n{AppLogger.LogDirectory}\n\n{ex.Message}",
                    "Apolion", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void DevToolsToggle_Click(object sender, RoutedEventArgs e)
        {
            App.DevToolsEnabled = DevToolsToggle.IsChecked == true;
        }

        private void BtnNewTab_Click(object sender, RoutedEventArgs e)
        {
            bool inBackground = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            bool newWindow = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (newWindow)
            {
                var win = new MainWindow();
                win.Show();
                win.AddNewTab("https://www.google.com");
            }
            else
            {
                AddNewTab("https://www.google.com", inBackground: inBackground);
            }
        }

        public async void UpdateTabFavicon(IWebBrowser browser, string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return;

                // Find the tab that owns this browser (only in THIS window)
                TabItem? targetTab = null;
                foreach (TabItem tab in BrowserContainer.Items)
                {
                    if (tab.Tag is WindowsFormsHost host && ReferenceEquals(host.Child, browser))
                    {
                        targetTab = tab;
                        break;
                    }
                }

                if (targetTab == null || targetTab.Header is not System.Windows.Controls.Panel headerPanel)
                    return;
                if (headerPanel.Children.Count == 0 || headerPanel.Children[0] is not Image img)
                    return;

                // Try cache first (LRU)
                if (_faviconCache.TryGetValue(url, out var cachedBitmap))
                {
                    img.Source = cachedBitmap;
                    img.Visibility = Visibility.Visible;
                    return;
                }

                // Deduplicate: get or start the download task
                var downloadTask = _pendingFaviconDownloads.GetOrAdd(url, DownloadFaviconAsync);
                
                // Wait for download (or get cached result)
                var bitmap = await downloadTask;
                
                // Clean up pending task - also remove faulted tasks to prevent returning faulted tasks on retry
                _pendingFaviconDownloads.TryRemove(url, out _);

                if (bitmap == null) return;

                // Cache the result (LRU will evict oldest if needed)
                _faviconCache.GetOrAdd(url, _ => bitmap);

                // The tab may have been closed while the favicon downloaded.
                if (!BrowserContainer.Items.Contains(targetTab)) return;

                img.Source = bitmap;
                img.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Favicon download failed for '{url}': {ex.Message}");
            }
        }

        private static async Task<System.Windows.Media.Imaging.BitmapImage?> DownloadFaviconAsync(string url)
        {
            try
            {
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                using var response = await _httpClient.SendAsync(req);
                if (!response.IsSuccessStatusCode) return null;

                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0) return null;

                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                using (var mem = new MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = mem;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
    }
}
