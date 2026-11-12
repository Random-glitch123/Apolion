using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Apolion
{
    /// <summary>
    /// Friendly crash assistant shown when an unhandled exception is caught.
    /// It shows the crash report, lets the user copy it or mail it to the
    /// developer, opens the log folder, and can restart the browser.
    /// </summary>
    public partial class CrashWindow : Window
    {
        // TODO: replace with the developer's support address before shipping.
        public const string SupportEmail = "ohioemail7@gmail.com";

        private readonly string _summary;
        private readonly string _details;
        private readonly string _logPath;

        public CrashWindow(string summary, string details, string logPath)
        {
            InitializeComponent();

            _summary = summary ?? "Unknown error";
            _details = details ?? "";
            _logPath = logPath ?? AppLogger.DebugLog;

            SummaryText.Text = _summary;
            DetailsBox.Text = _details;
            LogPathText.Text = "Crash report: " + _logPath;
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(_details);
                BtnCopy.Content = "Copied!";
            }
            catch (Exception ex)
            {
                AppLogger.Exception("CrashWindow copy", ex);
                BtnCopy.Content = "Copy failed";
            }
        }

        private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(_logPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{_logPath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = AppLogger.LogDirectory,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Exception("CrashWindow open logs", ex);
            }
        }

        private void BtnEmail_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                const int maxBody = 6000;
                string body = _details.Length > maxBody ? _details[..maxBody] + "\n…" : _details;
                string subject = "Apolion crash report";
                string uri =
                    $"mailto:{SupportEmail}?subject={Uri.EscapeDataString(subject)}" +
                    $"&body={Uri.EscapeDataString(body + "\n\nCrash report: " + _logPath)}";

                Process.Start(new ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Exception("CrashWindow email", ex);
                System.Windows.MessageBox.Show(
                    "Could not open your e-mail app. Please use \"Copy details\" and send the crash report manually.\n\n" +
                    _logPath,
                    "Apolion", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = true
                    });
                }

                System.Windows.Application.Current?.Shutdown();
            }
            catch (Exception ex)
            {
                AppLogger.Exception("CrashWindow restart", ex);
                Close();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

