using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace Apolion
{
    public partial class UpdateWindow : Window
    {
        private bool _isDownloading = false;
        private bool _updateCompleted = false;
        private CancellationTokenSource? _cancellation;

        public UpdateWindow(string currentVersion, string latestVersion, string? releaseNotes = null)
        {
            InitializeComponent();

            TxtTitle.Text = "Update Available";
            TxtVersionInfo.Text = $"Version {latestVersion} is available (you have {currentVersion})";

            if (!string.IsNullOrWhiteSpace(releaseNotes))
            {
                ReleaseNotesBorder.Visibility = Visibility.Visible;
                TxtReleaseNotes.Text = releaseNotes.Length > 2000 ? releaseNotes[..2000] + "…" : releaseNotes;
            }
            else
            {
                ReleaseNotesBorder.Visibility = Visibility.Collapsed;
            }

            // Apply theme
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            bool isDark = SettingsManager.IsDarkModeEnabled();
            var themeDict = isDark ? (ResourceDictionary)Resources["DarkTheme"] : (ResourceDictionary)Resources["LightTheme"];
            Resources.MergedDictionaries.Clear();
            Resources.MergedDictionaries.Add(themeDict);
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading) return;

            _isDownloading = true;
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();

            BtnUpdate.IsEnabled = false;
            BtnLater.IsEnabled = false;
            BtnClose.IsEnabled = true;

            ProgressPanel.Visibility = Visibility.Visible;
            TxtProgressStatus.Text = "Preparing update...";
            ProgressBar.IsIndeterminate = true;
            ProgressBar.Value = 0;
            TxtProgressDetail.Text = string.Empty;

            try
            {
                var progress = new Progress<UpdateProgress>(OnUpdateProgress);

                bool success = await Updater.DownloadAndInstallAsync(progress, _cancellation.Token);

                if (success)
                {
                    _updateCompleted = true;
                    ProgressBar.IsIndeterminate = false;
                    TxtProgressStatus.Text = "Update ready. Restarting...";
                    TxtProgressDetail.Text = "100%";
                    ProgressBar.Value = 1;

                    // Give user a moment to see completion
                    await Task.Delay(1000);

                    // Close this window and shut down the app (updater will restart)
                    Application.Current.Shutdown();
                }
                else
                {
                    string message = "Failed to download or install the update. Please try again later or download manually from GitHub.";
                    if (!string.IsNullOrWhiteSpace(Updater.LastError))
                        message += $"\n\n{Updater.LastError}";

                    MessageBox.Show(message, "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    ResetUI();
                }
            }
            catch (OperationCanceledException)
            {
                ResetUI();
            }
            catch (Exception ex)
            {
                AppLogger.Exception("Update failed", ex);
                MessageBox.Show($"Update failed: {ex.Message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ResetUI();
            }
        }

        private void OnUpdateProgress(UpdateProgress progress)
        {
            if (!string.IsNullOrWhiteSpace(progress.Message))
                TxtProgressStatus.Text = progress.Message;

            switch (progress.Phase)
            {
                case UpdatePhase.DownloadingArchive:
                case UpdatePhase.DownloadingDelta:
                    ProgressBar.IsIndeterminate = progress.Fraction < 0;
                    break;
                case UpdatePhase.DownloadingManifest:
                case UpdatePhase.ComparingFiles:
                case UpdatePhase.Verifying:
                case UpdatePhase.Extracting:
                case UpdatePhase.Preparing:
                    ProgressBar.IsIndeterminate = progress.Fraction < 0;
                    break;
                default:
                    ProgressBar.IsIndeterminate = false;
                    break;
            }

            if (progress.Fraction >= 0)
                ProgressBar.Value = Math.Clamp(progress.Fraction, 0, 1);

            if (progress.TotalBytes is long total && total > 0)
            {
                double percent = progress.Fraction >= 0 ? progress.Fraction * 100 : 0;
                TxtProgressDetail.Text =
                    $"{FormatBytes(progress.BytesReceived)} / {FormatBytes(total)} ({percent:0}%)";
            }
            else if (progress.Fraction >= 0)
            {
                TxtProgressDetail.Text = $"{(int)(progress.Fraction * 100)}%";
            }
            else
            {
                TxtProgressDetail.Text = string.Empty;
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
        }

        private void ResetUI()
        {
            _isDownloading = false;
            BtnUpdate.IsEnabled = true;
            BtnLater.IsEnabled = true;
            BtnClose.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 0;
        }

        private void BtnLater_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading)
            {
                var result = MessageBox.Show("An update is currently downloading. Are you sure you want to cancel?",
                                            "Cancel Update", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;

                _cancellation?.Cancel();
            }

            DialogResult = false;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Cancelling here also stops a download if the window is closed
            // through a path that does not go through BtnClose_Click.
            if (!_updateCompleted)
                _cancellation?.Cancel();

            _cancellation?.Dispose();
            _cancellation = null;

            base.OnClosed(e);
        }
    }
}

