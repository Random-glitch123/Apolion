using System;
using System.Collections.Generic;
using CefSharp;

namespace Apolion
{
    /// <summary>
    /// CefSharp download handler that shows the native Windows "Save As" dialog
    /// and reports progress (bytes received, total bytes, speed in bytes/sec) 
    /// to the Downloads panel.
    /// </summary>
    public class ApolionDownloadHandler : IDownloadHandler
    {
        public event Action<int, string, long, long, double, bool, bool>? DownloadUpdated;

        // Track timestamps and byte counts for speed calculation
        private readonly Dictionary<int, (DateTime time, long bytes)> _lastSample = new();

        public bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod)
            => true;

        public bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem, IBeforeDownloadCallback callback)
        {
            // Always complete the callback exactly once. Leaving it pending lets
            // CEF fall back to its own native Chrome download popup, which overlays Apolion.
            bool completed = false;
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
                {
                    try { callback.Dispose(); completed = true; } catch { }
                    return true;
                }

                // Run the Save File dialog synchronously on the UI thread so that
                // callback.Continue is invoked before OnBeforeDownload returns.
                app.Dispatcher.Invoke(() =>
                {
                    if (completed || callback.IsDisposed) return;
                    try
                    {
                        var dlg = new Microsoft.Win32.SaveFileDialog
                        {
                            FileName = downloadItem.SuggestedFileName,
                            Title = "Save File As..."
                        };
                        if (dlg.ShowDialog() == true)
                        {
                            callback.Continue(dlg.FileName, showDialog: false);
                            completed = true;
                        }
                        else
                        {
                            // User cancelled: cancel the download instead of letting CEF show native UI.
                            callback.Dispose();
                            completed = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Exception("OnBeforeDownload dialog", ex);
                        try { callback.Dispose(); completed = true; } catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Exception("OnBeforeDownload", ex);
                try { callback.Dispose(); completed = true; } catch { }
            }
            finally
            {
                if (!completed)
                {
                    try { callback.Dispose(); } catch { }
                }
            }
            return true;
        }

        public void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser, DownloadItem downloadItem, IDownloadItemCallback callback)
        {
            try
            {
                int id = downloadItem.Id;
                long received = downloadItem.ReceivedBytes;
                long total = downloadItem.TotalBytes;
                bool isComplete = downloadItem.IsComplete;
                bool isCancelled = downloadItem.IsCancelled;

                // Calculate speed
                double speed = 0;
                var now = DateTime.UtcNow;
                if (_lastSample.TryGetValue(id, out var last))
                {
                    double elapsedSec = (now - last.time).TotalSeconds;
                    if (elapsedSec > 0)
                        speed = Math.Max(0, (received - last.bytes) / elapsedSec);
                }
                _lastSample[id] = (now, received);

                if (isComplete || isCancelled)
                    _lastSample.Remove(id);

                DownloadUpdated?.Invoke(id, downloadItem.SuggestedFileName, received, total, speed, isComplete, isCancelled);
            }
            catch (Exception ex)
            {
                AppLogger.Exception("OnDownloadUpdated", ex);
            }
        }
    }
}

