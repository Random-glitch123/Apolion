using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Apolion
{
    public partial class DownloadsWindow : Window
    {
        private static DownloadsWindow? _instance;
        private static readonly Lock _instanceLock = new();
        private bool _allowClose;

        /// <summary>
        /// Thread-safe singleton. Creation is marshalled to the UI thread without
        /// holding the lock during the dispatch, which avoids deadlocks when CEF
        /// calls this while the UI thread is waiting on CEF.
        /// </summary>
        public static DownloadsWindow Instance
        {
            get
            {
                lock (_instanceLock)
                {
                    if (_instance != null) return _instance;
                }

                var app = System.Windows.Application.Current
                    ?? throw new InvalidOperationException("No WPF application is running.");

                if (app.Dispatcher.CheckAccess())
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new DownloadsWindow();
                        return _instance;
                    }
                }

                return app.Dispatcher.Invoke(() =>
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new DownloadsWindow();
                        return _instance;
                    }
                });
            }
        }

        /// <summary>
        /// Downloads that started before the Downloads window was opened. Kept so they
        /// still appear when the user opens the window later ("Nothing appears" fix).
        /// </summary>
        private static readonly Dictionary<int, (string fileName, long receivedBytes, long totalBytes, double speedBytesPerSec, bool isComplete, bool isCancelled)> _pendingDownloads = [];
        private static readonly Lock _pendingLock = new();

        public static void UpdateIfOpen(int id, string fileName, long receivedBytes, long totalBytes,
            double speedBytesPerSec, bool isComplete, bool isCancelled)
        {
            // Always persist so downloads started before the window was opened still appear.
            lock (_pendingLock)
            {
                _pendingDownloads[id] = (fileName, receivedBytes, totalBytes, speedBytesPerSec, isComplete, isCancelled);
                if (isComplete || isCancelled)
                    _pendingDownloads.Remove(id);
            }

            DownloadsWindow? instance;
            lock (_instanceLock)
            {
                instance = _instance;
            }

            instance?.UpdateDownload(id, fileName, receivedBytes, totalBytes,
                speedBytesPerSec, isComplete, isCancelled);
        }

        /// <summary>Closes the panel during application shutdown (bypasses hide-on-close).</summary>
        public static void CloseForShutdown()
        {
            DownloadsWindow? instance;
            lock (_instanceLock)
            {
                instance = _instance;
            }

            if (instance == null) return;

            var app = System.Windows.Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                // Use BeginInvoke to avoid deadlock if dispatcher is shutting down
                app.Dispatcher.BeginInvoke(new Action(CloseForShutdown));
                return;
            }

            instance._allowClose = true;
            try { instance.Close(); } catch { }

            lock (_instanceLock)
            {
                if (ReferenceEquals(_instance, instance)) _instance = null;
            }
        }

        private readonly Dictionary<int, DownloadRowView> _rows = [];

        // Throttling: track last update per download (min 250ms between updates, or 5% progress change)
        private readonly Dictionary<int, (DateTime time, double progress)> _lastDownloadUpdate = [];
        private const int MinUpdateIntervalMs = 250;
        private const double MinProgressChange = 0.05; // 5%

        public DownloadsWindow()
        {
            // Claim the singleton before InitializeComponent so that DownloadRowView
            // (created during pending-download sync below) can resolve Instance
            // without recursing into a second window construction.
            // Set directly (no lock) to avoid re-entering _instanceLock, which the
            // Instance property already holds while constructing us.
            _instance = this;

            InitializeComponent();
            Closing += (s, e) =>
            {
                if (!_allowClose)
                {
                    e.Cancel = true;
                    Hide();
                }
            };

            TitleBarGrid.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2) ToggleMaximize();
                else if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
            };
            this.StateChanged += Window_StateChanged;
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) ApplySavedTheme();
            };
            
            ApplySavedTheme();

            // Surface downloads that started before this window was opened.
            List<(int id, string fileName, long receivedBytes, long totalBytes, double speedBytesPerSec, bool isComplete, bool isCancelled)> pending;
            lock (_pendingLock)
            {
                pending = _pendingDownloads.Select(kv => (kv.Key, kv.Value.fileName, kv.Value.receivedBytes, kv.Value.totalBytes, kv.Value.speedBytesPerSec, kv.Value.isComplete, kv.Value.isCancelled)).ToList();
            }
            foreach (var p in pending)
            {
                UpdateDownload(p.id, p.fileName, p.receivedBytes, p.totalBytes,
                    p.speedBytesPerSec, p.isComplete, p.isCancelled);
            }
        }

        public void ApplySavedTheme()
        {
            var theme = SettingsManager.GetTheme();
            bool isDark = theme == "Dark";

            var themeDict = isDark
                ? (ResourceDictionary)Resources["DarkTheme"]
                : (ResourceDictionary)Resources["LightTheme"];
            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(themeDict);
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        
        private void BtnMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
        
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

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
            if (WindowState == WindowState.Maximized)
            {
                BtnMaximize.Content = "\uE923";
                BtnMaximize.ToolTip = "Restore Down";
            }
            else if (WindowState == WindowState.Normal)
            {
                BtnMaximize.Content = "\uE922";
                BtnMaximize.ToolTip = "Maximize";
            }
        }

        public void UpdateDownload(int id, string fileName, long receivedBytes, long totalBytes, double speedBytesPerSec, bool isComplete, bool isCancelled)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

            // Throttle: skip UI update if too soon or progress hasn't changed enough
            double currentProgress = totalBytes > 0 ? (double)receivedBytes / totalBytes : 0;
            var now = DateTime.UtcNow;
            bool shouldUpdate = true;

            lock (_lastDownloadUpdate)
            {
                if (_lastDownloadUpdate.TryGetValue(id, out var last))
                {
                    double timeElapsed = (now - last.time).TotalMilliseconds;
                    double progressDelta = Math.Abs(currentProgress - last.progress);
                    shouldUpdate = timeElapsed >= MinUpdateIntervalMs || progressDelta >= MinProgressChange || isComplete || isCancelled;
                }

                if (shouldUpdate)
                {
                    _lastDownloadUpdate[id] = (now, currentProgress);
                }
            }

            if (!shouldUpdate) return;

            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (!_rows.TryGetValue(id, out var row))
                    {
                        var foregroundBrush = TryFindResource("ForegroundBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
                        var secondaryTextBrush = TryFindResource("SecondaryTextBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
                        var progressFillBrush = TryFindResource("ProgressBarFillBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Blue;
                        var progressTrackBrush = TryFindResource("ProgressBarTrackBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray;
                        row = new DownloadRowView(id, fileName, foregroundBrush, secondaryTextBrush, progressFillBrush, progressTrackBrush);
                        _rows[id] = row;
                        DownloadsList.Items.Add(row);
                    }
                    row.Update(receivedBytes, totalBytes, speedBytesPerSec, isComplete, isCancelled, fileName);
                    NoDownloadsText.Visibility = DownloadsList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    AppLogger.Exception("DownloadsWindow.UpdateDownload", ex);
                }
            });
        }

        private void BtnClearDone_Click(object sender, RoutedEventArgs e)
        {
            var toRemove = new List<int>();
            foreach (var kv in _rows)
            {
                if (kv.Value.IsFinished)
                    toRemove.Add(kv.Key);
            }
            foreach (var key in toRemove)
            {
                DownloadsList.Items.Remove(_rows[key]);
                _rows.Remove(key);
                lock (_lastDownloadUpdate) _lastDownloadUpdate.Remove(key);
            }
            lock (_pendingLock)
            {
                foreach (var key in toRemove)
                    _pendingDownloads.Remove(key);
            }
            NoDownloadsText.Visibility = DownloadsList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public class DownloadRowView : ListBoxItem
    {
        public int Id { get; }
        public bool IsFinished { get; private set; }

        private readonly TextBlock _nameText;
        private readonly TextBlock _statsText;
        private readonly System.Windows.Controls.ProgressBar _progressBar;
        private readonly TextBlock _statusIcon;
        private readonly TextBlock _fileIcon;

        public DownloadRowView(int id, string fileName, System.Windows.Media.Brush foregroundBrush, System.Windows.Media.Brush secondaryTextBrush, System.Windows.Media.Brush progressFillBrush, System.Windows.Media.Brush progressTrackBrush)
        {
            Id = id;
            IsFinished = false;
            Background = System.Windows.Media.Brushes.Transparent;
            Padding = new Thickness(0);
            Margin = new Thickness(0, 0, 0, 4);
            Focusable = false;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) }); // Status icon
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); // File type icon
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Info

            _statusIcon = new TextBlock
            {
                Text = "\uE896",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 18,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Foreground = progressFillBrush
            };
            Grid.SetColumn(_statusIcon, 0);

            // File type icon (using Segoe MDL2 Assets font)
            _fileIcon = new TextBlock
            {
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                Foreground = foregroundBrush
            };
            Grid.SetColumn(_fileIcon, 1);
            SetFileIcon(fileName);

            var infoStack = new StackPanel { Margin = new Thickness(8, 4, 0, 4) };

            _nameText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(fileName) ? "Unknown file" : fileName,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = foregroundBrush
            };

            _progressBar = new System.Windows.Controls.ProgressBar
            {
                Height = 4,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Margin = new Thickness(0, 4, 0, 4),
                BorderThickness = new Thickness(0),
                Foreground = progressFillBrush,
                Background = progressTrackBrush
            };

            _statsText = new TextBlock
            {
                FontSize = 11,
                Foreground = secondaryTextBrush
            };

            infoStack.Children.Add(_nameText);
            infoStack.Children.Add(_progressBar);
            infoStack.Children.Add(_statsText);

            Grid.SetColumn(infoStack, 2);
            grid.Children.Add(_statusIcon);
            grid.Children.Add(_fileIcon);
            grid.Children.Add(infoStack);
            Content = grid;
        }

        private void SetFileIcon(string fileName)
        {
            string ext = System.IO.Path.GetExtension(fileName)?.ToLowerInvariant() ?? "";
            _fileIcon.Text = ext switch
            {
                ".pdf" => "\uE8A5",      // Document (PDF)
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "\uE7BA", // Archive/Folder
                ".exe" or ".msi" => "\uE774", // App/Executable
                ".txt" or ".log" => "\uE8A5", // Document
                ".doc" or ".docx" => "\uE8A5", // Word document
                ".xls" or ".xlsx" => "\uE71D", // Excel spreadsheet
                ".ppt" or ".pptx" => "\uE71E", // PowerPoint
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" => "\uE91E", // Image/Picture
                ".mp3" or ".wav" or ".flac" or ".ogg" => "\uE993", // Music/Audio
                ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "\uE914", // Video
                ".html" or ".htm" or ".css" or ".js" or ".ts" or ".json" or ".xml" => "\uE773", // Code/Script
                _ => "\uE8A5" // Generic document/file
            };
        }

        public void Update(long receivedBytes, long totalBytes, double speedBytesPerSec, bool isComplete, bool isCancelled, string? fileName = null)
        {
            IsFinished = isComplete || isCancelled;

            // Refresh the file name if a non-empty one arrives after the row was created.
            if (!string.IsNullOrWhiteSpace(fileName) && _nameText.Text != fileName)
                _nameText.Text = fileName;

            if (isCancelled)
            {
                _statusIcon.Text = "\uE711";
                _statusIcon.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0x44, 0x44));
                _progressBar.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0x44, 0x44));
                _statsText.Text = "Cancelled";
                return;
            }

            if (isComplete)
            {
                _statusIcon.Text = "\uE73E";
                _statusIcon.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
                _progressBar.Value = 100;
                _statsText.Text = $"Done  ·  {FormatSize(totalBytes > 0 ? totalBytes : receivedBytes)}";
                return;
            }

            double pct = totalBytes > 0 ? (double)receivedBytes / totalBytes * 100.0 : 0;
            _progressBar.IsIndeterminate = totalBytes <= 0;
            _progressBar.Value = pct;

            string receivedStr = FormatSize(receivedBytes);
            string totalStr = totalBytes > 0 ? FormatSize(totalBytes) : "?";
            string speedStr = FormatSize((long)speedBytesPerSec) + "/s";
            string etaStr = "–";
            if (speedBytesPerSec > 0 && totalBytes > receivedBytes)
            {
                double secsLeft = (totalBytes - receivedBytes) / speedBytesPerSec;
                etaStr = FormatEta(secsLeft);
            }

            _statsText.Text = $"{receivedStr} / {totalStr}  ·  {speedStr}  ·  ETA {etaStr}";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
        }

        private static string FormatEta(double secs)
        {
            if (secs < 60) return $"{(int)secs}s";
            if (secs < 3600) return $"{(int)(secs / 60)}m {(int)(secs % 60)}s";
            return $"{(int)(secs / 3600)}h {(int)(secs % 3600 / 60)}m";
        }
    }
}

