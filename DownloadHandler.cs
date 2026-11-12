using System;
using System.Collections.Generic;
using System.Windows;
using CefSharp;
using WpfApplication = System.Windows.Application;

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
            // CRITICAL: Must complete the callback synchronously before returning
            // to prevent CEF from showing its native Chrome download UI overlay.
            bool completed = false;
            try
            {
                var app = WpfApplication.Current;
                if (app == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
                {
                    SafeDisposeCallback(callback, ref completed);
                    return true;
                }

                // Run the Save File dialog synchronously on the UI thread so that
                // callback.Continue is invoked before OnBeforeDownload returns.
                // Use Invoke (not BeginInvoke) to block until the dialog completes.
                app.Dispatcher.Invoke(() =>
                {
                    if (completed || callback.IsDisposed) return;
                    try
                    {
                        var dlg = new Microsoft.Win32.SaveFileDialog
                        {
                            FileName = SanitizeFileName(downloadItem.SuggestedFileName),
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
                            SafeDisposeCallback(callback, ref completed);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Exception("OnBeforeDownload dialog", ex);
                        SafeDisposeCallback(callback, ref completed);
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Exception("OnBeforeDownload", ex);
                SafeDisposeCallback(callback, ref completed);
            }
            finally
            {
                if (!completed)
                {
                    SafeDisposeCallback(callback, ref completed);
                }
            }
            return true;
        }

        private static void SafeDisposeCallback(IBeforeDownloadCallback callback, ref bool completed)
        {
            if (!completed)
            {
                try { callback.Dispose(); completed = true; } catch { }
            }
        }

        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "download";
            
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');
            
            return fileName;
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

