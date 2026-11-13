using CefSharp;

namespace Apolion
{
    /// <summary>
    /// Injects host-side page preprocessing into every frame. Pointer Lock is
    /// handled natively by Chromium (Chrome runtime style) and is not touched
    /// here.
    /// </summary>
    internal sealed class ApolionRenderProcessMessageHandler : IRenderProcessMessageHandler
    {
        public void OnContextCreated(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame)
        {
            try
            {
                // Apply the chosen website language before any page script can read
                // navigator.language / navigator.languages.
                frame.ExecuteJavaScriptAsync(LanguageManager.NavigatorOverrideScript);

#if DEBUG
                // Debug builds only: capture HTMLMediaElement failures for diagnosis.
                MediaDiagnostics.ObserveFrame(frame);
#endif
            }
            catch (Exception ex)
            {
                // Frame can disappear while the script is being queued. Safe to ignore.
                AppLogger.Debug($"Language injection skipped: {ex.Message}");
            }
        }

        public void OnContextReleased(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame) { }
        public void OnFocusedNodeChanged(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IDomNode node) { }

        public void OnUncaughtException(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, JavascriptException exception)
        {
            try
            {
                AppLogger.Warn($"[JS] Uncaught exception in {frame?.Url}: {exception.Message}");
            }
            catch
            {
                // Diagnostics must never fail.
            }
        }
    }
}

