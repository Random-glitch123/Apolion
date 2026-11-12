using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CefSharp;

namespace Apolion
{
    // Context Menu Handler to add missing options like Copy Image
    public class ApolionContextMenuHandler : IContextMenuHandler
    {
        private static readonly System.Net.Http.HttpClient _imgClient = new() { Timeout = TimeSpan.FromSeconds(15) };

        private const int InspectCmd = 26503;
        private const int CopyImageUrlCmd = 26504;
        private const int CopyLinkUrlCmd = 26505;
        private const int CopyImageCmd = 26506;
        private const int SaveImageAsCmd = 26507;

        private static void SafeSetClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    System.Windows.Clipboard.SetDataObject(text, true);
                    return;
                }
                catch
                {
                    System.Threading.Thread.Sleep(50);
                }
            }
        }

        private static void SetImageToClipboard(byte[] bytes)
        {
            UiDispatch.Post(() =>
            {
                try
                {
                    var bitmap = new BitmapImage();
                    using (var mem = new System.IO.MemoryStream(bytes))
                    {
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = mem;
                        bitmap.EndInit();
                        bitmap.Freeze();
                    }
                    for (int i = 0; i < 5; i++)
                    {
                        try
                        {
                            System.Windows.Clipboard.SetImage(bitmap);
                            break;
                        }
                        catch { System.Threading.Thread.Sleep(50); }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"Copy image to clipboard failed: {ex.Message}");
                }
            }, "SetImageToClipboard");
        }

        public void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IContextMenuParams parameters, IMenuModel model)
        {
            // Do not clear the model. Let CEF retain native options like Print, View Source, Copy, etc.

            // If they clicked a link, add custom Copy Link
            if (!string.IsNullOrEmpty(parameters.LinkUrl))
            {
                model.AddItem((CefMenuCommand)CopyLinkUrlCmd, "Copy Link Address");
            }

            // If they clicked an image, add custom Copy Image URL and Copy Image
            if (parameters.HasImageContents && !string.IsNullOrEmpty(parameters.SourceUrl))
            {
                model.AddItem((CefMenuCommand)SaveImageAsCmd, "Save Image As...");
                model.AddItem((CefMenuCommand)CopyImageCmd, "Copy Image");
                model.AddItem((CefMenuCommand)CopyImageUrlCmd, "Copy Image URL");
            }

            // Custom Inspect Element command
            model.AddSeparator();
            model.AddItem((CefMenuCommand)InspectCmd, "Inspect Element");
        }

        public bool OnContextMenuCommand(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IContextMenuParams parameters, CefMenuCommand commandId, CefEventFlags eventFlags)
        {
            if ((int)commandId == InspectCmd)
            {
                if (App.DevToolsEnabled)
                {
                    browser.ShowDevTools();
                }
                else
                {
                    System.Windows.MessageBox.Show("Developer Tools are disabled. Enable them in Settings.", "Apolion", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }
                return true;
            }
            if ((int)commandId == CopyImageUrlCmd)
            {
                var text = parameters.SourceUrl;
                UiDispatch.Post(() => SafeSetClipboardText(text), "CopyImageUrl");
                return true;
            }
            if ((int)commandId == CopyLinkUrlCmd)
            {
                var text = parameters.LinkUrl;
                UiDispatch.Post(() => SafeSetClipboardText(text), "CopyLinkUrl");
                return true;
            }
            if ((int)commandId == CopyImageCmd)
            {
                ProcessImageUrl(parameters.SourceUrl, browser, bytes => SetImageToClipboard(bytes));
                return true;
            }
            if ((int)commandId == SaveImageAsCmd)
            {
                ProcessImageUrl(parameters.SourceUrl, browser, bytes =>
                {
                    UiDispatch.Post(() =>
                    {
                        try
                        {
                            var dlg = new Microsoft.Win32.SaveFileDialog
                            {
                                Filter = "Image files (*.png;*.jpeg;*.jpg)|*.png;*.jpeg;*.jpg|All files (*.*)|*.*",
                                Title = "Save Image As"
                            };
                            if (dlg.ShowDialog() == true)
                            {
                                System.IO.File.WriteAllBytes(dlg.FileName, bytes);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Exception("Save image as", ex);
                        }
                    }, "SaveImageAs");
                });
                return true;
            }
            // For standard commands, return false to let CEF handle them automatically.
            return false;
        }

        private static void ProcessImageUrl(string imageUrl, IBrowser browser, Action<byte[]> onBytesFetched)
        {
            if (imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    int comma = imageUrl.IndexOf(',');
                    if (comma < 0) return;
                    var base64Data = imageUrl[(comma + 1)..];
                    var bytes = Convert.FromBase64String(base64Data);
                    onBytesFetched(bytes);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"Decoding data image failed: {ex.Message}");
                }
            }
            else if (imageUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            {
                var mainFrame = browser.MainFrame;
                if (mainFrame == null) return;

                string encodedUrl = System.Text.Json.JsonSerializer.Serialize(imageUrl);
                var script = $@"
                    (function() {{
                        return new Promise((resolve) => {{
                            fetch({encodedUrl}).then(r => r.blob()).then(b => {{
                                var reader = new FileReader();
                                reader.onloadend = () => resolve(reader.result);
                                reader.readAsDataURL(b);
                            }}).catch(e => resolve(null));
                        }});
                    }})();
                ";
                try
                {
                    mainFrame.EvaluateScriptAsync(script).ContinueWith(t =>
                    {
                        try
                        {
                            if (!t.IsCompletedSuccessfully) return;

                            var response = t.Result;
                            if (response.Success && response.Result != null)
                            {
                                string dataUrl = response.Result.ToString() ?? "";
                                int comma = dataUrl.IndexOf(',');
                                if (comma < 0) return;
                                var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
                                onBytesFetched(bytes);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug($"Reading blob image failed: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"Queuing blob image read failed: {ex.Message}");
                }
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, imageUrl);
                        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                        using var response = await _imgClient.SendAsync(req);
                        if (response.IsSuccessStatusCode)
                        {
                            var bytes = await response.Content.ReadAsByteArrayAsync();
                            onBytesFetched(bytes);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug($"Downloading image failed: {ex.Message}");
                    }
                });
            }
        }

        public void OnContextMenuDismissed(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame)
        {
        }

        public bool RunContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IContextMenuParams parameters, IMenuModel model, IRunContextMenuCallback callback)
        {
            return false; // let CEF display its default native context menu built from the model
        }
    }

    // Permission Handler for Pointer Lock
    public class ApolionPermissionHandler : IPermissionHandler
    {
        public bool OnRequestMediaAccessPermission(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string requestingUrl, MediaAccessPermissionType requestedPermissions, IMediaAccessCallback callback)
        {
            // Auto-grant mic/camera so sites like Discord and Google Meet work without a broken
            // native permission dialog that doesn't surface correctly in the WinForms host.
            if (requestedPermissions.HasFlag(MediaAccessPermissionType.AudioCapture) ||
                requestedPermissions.HasFlag(MediaAccessPermissionType.VideoCapture))
            {
                callback.Continue(requestedPermissions);
                return true;
            }
            return false;
        }

        public bool OnShowPermissionPrompt(IWebBrowser chromiumWebBrowser, IBrowser browser, ulong promptId, string requestingOrigin, PermissionRequestType requestedPermissions, IPermissionPromptCallback callback)
        {
            // If pointer lock is requested, grant it
            if (requestedPermissions.HasFlag(PermissionRequestType.PointerLock))
            {
                callback.Continue(PermissionRequestResult.Accept);
                return true;
            }
            return false;
        }

        public void OnDismissPermissionPrompt(IWebBrowser chromiumWebBrowser, IBrowser browser, ulong promptId, PermissionRequestResult result)
        {
        }
    }

    // Display Handler — handles fullscreen requests from web pages (e.g., YouTube)
    public class ApolionDisplayHandler : CefSharp.Handler.DisplayHandler
    {
        protected override void OnFullscreenModeChange(IWebBrowser chromiumWebBrowser, IBrowser browser, bool fullscreen)
        {
            UiDispatch.Post(() =>
            {
                if (System.Windows.Application.Current?.MainWindow is MainWindow mainWindow &&
                    mainWindow.IsActiveBrowser(chromiumWebBrowser))
                {
                    mainWindow.SetFullscreen(fullscreen);
                }
            }, "OnFullscreenModeChange");
        }

        protected override void OnFaviconUrlChange(IWebBrowser chromiumWebBrowser, IBrowser browser, IList<string> urls)
        {
            if (urls == null || urls.Count == 0) return;
            string faviconUrl = urls[0];

            UiDispatch.Post(() =>
            {
                var app = System.Windows.Application.Current;
                if (app == null) return;

                // Only call UpdateTabFavicon on the window that owns this browser
                // Each window will find the matching tab internally
                foreach (var window in app.Windows.OfType<MainWindow>().ToList())
                {
                    window.UpdateTabFavicon(chromiumWebBrowser, faviconUrl);
                }
            }, "OnFaviconUrlChange");
        }
    }

    // LifeSpan Handler to route popups and link clicks
    public class ApolionLifeSpanHandler : ILifeSpanHandler
    {
        private readonly WeakReference<MainWindow>? _mainWindowRef;

        public ApolionLifeSpanHandler(MainWindow? mainWindow = null)
        {
            if (mainWindow != null)
            {
                _mainWindowRef = new WeakReference<MainWindow>(mainWindow);
            }
        }

        public bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl, string targetFrameName, WindowOpenDisposition targetDisposition, bool userGesture, IPopupFeatures popupFeatures, IWindowInfo windowInfo, IBrowserSettings browserSettings, ref bool noJavascriptAccess, out IWebBrowser newBrowser)
        {
            newBrowser = null!;

            // CEF creates an unmanaged top-level window (titled "Chromium" before the
            // page loads) whenever we let the default popup path run. Every popup must
            // therefore be cancelled here and re-created as an Apolion tab/window.
            string finalUrl = string.IsNullOrWhiteSpace(targetUrl) ? "about:blank" : targetUrl;

            try
            {
                AppLogger.Debug(
                    $"[POPUP] request url='{finalUrl}' disposition={targetDisposition} " +
                    $"userGesture={userGesture} frame='{targetFrameName}'");
            }
            catch { }

            // Privileged pages cannot be hosted in an ordinary tab and would fall
            // back to a native CEF window if we allowed them through.
            if (finalUrl.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase) ||
                finalUrl.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Warn($"[POPUP] Blocked privileged popup '{finalUrl}'");
                return true;
            }

            // Automatic popups (ads, popunders, timer-driven window.open) carry no
            // user gesture. Chromium blocks these in normal Chrome; do the same so
            // they never turn into stray Apolion tabs or windows.
            if (!userGesture)
            {
                AppLogger.Debug($"[POPUP] Blocked automatic popup '{finalUrl}'");
                return true;
            }

            // Route the popup according to targetDisposition (standard browser behavior):
            // - NewWindow (Shift+Click) → new window
            // - NewBackgroundTab (Ctrl+Click / Middle Click) → background tab
            // - NewForegroundTab (Ctrl+Shift+Click) → foreground tab
            // - NewPopup / CurrentTab / etc. → current window foreground tab
            UiDispatch.Post(() =>
            {
                MainWindow? ownerWindow = null;
                _mainWindowRef?.TryGetTarget(out ownerWindow);
                ownerWindow ??= System.Windows.Application.Current?.MainWindow as MainWindow;

                if (targetDisposition == WindowOpenDisposition.NewWindow)
                {
                    // Shift + Click: Open in a new window (offset from parent)
                    if (ownerWindow != null)
                    {
                        ownerWindow.OpenNewWindow(finalUrl);
                    }
                    else
                    {
                        var newWin = new MainWindow();
                        newWin.Show();
                        newWin.AddNewTab(finalUrl, inBackground: false);
                    }
                }
                else if (targetDisposition == WindowOpenDisposition.NewBackgroundTab)
                {
                    // Ctrl + Click / Middle Click: Open in background tab
                    if (ownerWindow != null)
                    {
                        ownerWindow.AddNewTab(finalUrl, inBackground: true);
                    }
                }
                else if (targetDisposition == WindowOpenDisposition.NewForegroundTab)
                {
                    // Ctrl + Shift + Click: Open in foreground tab
                    if (ownerWindow != null)
                    {
                        ownerWindow.AddNewTab(finalUrl, inBackground: false);
                    }
                }
                else
                {
                    // Normal navigation / popup: Open in current window foreground tab
                    if (ownerWindow != null)
                    {
                        ownerWindow.AddNewTab(finalUrl, inBackground: false);
                    }
                }
            }, "OnBeforePopup");

            return true; // return true cancels the native popup window creation
        }

        public void OnAfterCreated(IWebBrowser chromiumWebBrowser, IBrowser browser)
        {
            // Any popup browser that still reaches this point escaped OnBeforePopup
            // (for example a CEF-owned UI path or a renderer race). Apolion never
            // hosts native popup windows: OnBeforePopup already re-created the
            // request as an Apolion tab/window. Force-close the escaped browser so
            // its top-level window cannot linger, regardless of the title CEF has
            // assigned it by the time StrayWindowGuard would look.
            try
            {
                if (browser == null || !browser.IsPopup) return;

                var host = browser.GetHost();
                if (host == null) return;

                // Defer to the next CEF UI task so the browser is fully registered
                // and DevTools (which shares this CefClient) has exposed its URL.
                Cef.PostAction(CefThreadIds.TID_UI, () =>
                {
                    try
                    {
                        if (browser.IsDisposed) return;

                        string currentUrl = "";
                        try { currentUrl = browser.MainFrame?.Url ?? ""; } catch { }

                        // CEF opens DevTools through this same client, using a
                        // popup-style native window named "DevTools". That window is
                        // an intentional Apolion feature and must never be closed.
                        if (IsDevTools(browser, currentUrl))
                        {
                            AppLogger.Debug("[POPUP] Popup browser is DevTools; keeping it open");
                            return;
                        }

                        AppLogger.Warn(
                            $"[POPUP] Closing escaped native popup browser (id={browser.Identifier}) url='{currentUrl}'");
                        host.CloseBrowser(forceClose: true);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Exception("Closing escaped popup browser", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                // Never let popup cleanup break browser creation.
                AppLogger.Debug($"Escaped popup cleanup failed: {ex.Message}");
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        private static bool IsDevTools(IBrowser browser, string url)
        {
            if (url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("chrome-devtools://", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // The DevTools browser's title is set at window creation, before the
            // inspector URL has necessarily been assigned to the main frame.
            try
            {
                IntPtr hwnd = browser.GetHost()?.GetWindowHandle() ?? IntPtr.Zero;
                if (hwnd == IntPtr.Zero) return false;

                var title = new StringBuilder(128);
                GetWindowText(hwnd, title, title.Capacity);
                return title.ToString().Contains("DevTools", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
        public void OnBeforeClose(IWebBrowser chromiumWebBrowser, IBrowser browser) { }
        public bool DoClose(IWebBrowser chromiumWebBrowser, IBrowser browser) { return false; }
    }

    public class ApolionJsDialogHandler : IJsDialogHandler
    {
        public bool OnJSDialog(IWebBrowser chromiumWebBrowser, IBrowser browser, string originUrl, CefJsDialogType dialogType, string messageText, string defaultPromptText, IJsDialogCallback callback, ref bool suppressMessage)
        {
            string message = messageText ?? "";

            // Never block the CEF callback thread on a WPF message loop. Post the
            // dialog to the UI thread and continue the callback there.
            UiDispatch.Post(() =>
            {
                try
                {
                    if (callback.IsDisposed) return;

                    if (dialogType == CefJsDialogType.Alert)
                    {
                        System.Windows.MessageBox.Show(message, "Apolion", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                        callback.Continue(true);
                    }
                    else if (dialogType == CefJsDialogType.Confirm)
                    {
                        var result = System.Windows.MessageBox.Show(message, "Apolion", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                        callback.Continue(result == System.Windows.MessageBoxResult.Yes);
                    }
                    else
                    {
                        // Block prompts and other dialogs for security against annoying websites
                        callback.Continue(false);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Exception("OnJSDialog", ex);
                }
            }, "OnJSDialog");

            return true;
        }

        public bool OnBeforeUnloadDialog(IWebBrowser chromiumWebBrowser, IBrowser browser, string messageText, bool isReload, IJsDialogCallback callback)
        {
            UiDispatch.Post(() =>
            {
                try
                {
                    if (callback.IsDisposed) return;

                    var result = System.Windows.MessageBox.Show("Are you sure you want to leave this page?", "Apolion", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                    callback.Continue(result == System.Windows.MessageBoxResult.Yes);
                }
                catch (Exception ex)
                {
                    AppLogger.Exception("OnBeforeUnloadDialog", ex);
                }
            }, "OnBeforeUnloadDialog");

            return true;
        }

        public void OnResetDialogState(IWebBrowser chromiumWebBrowser, IBrowser browser)
        {
        }

        public void OnDialogClosed(IWebBrowser chromiumWebBrowser, IBrowser browser)
        {
        }
    }

    public class ApolionRequestHandler : CefSharp.Handler.RequestHandler
    {
        private readonly WeakReference<MainWindow>? _mainWindowRef;

        public ApolionRequestHandler(MainWindow? mainWindow = null)
        {
            if (mainWindow != null)
            {
                _mainWindowRef = new WeakReference<MainWindow>(mainWindow);
            }
        }

        protected override IResourceRequestHandler? GetResourceRequestHandler(
            IWebBrowser chromiumWebBrowser,
            IBrowser browser,
            IFrame frame,
            IRequest request,
            bool isNavigation,
            bool isDownload,
            string requestInitiator,
            ref bool disableDefaultHandling)
        {
            // Disabled: global CEF preference "intl.accept_languages" (set via
            // LanguageManager.ApplyLive()) handles Accept-Language for all requests.
            // Returning null avoids allocating a handler per request.
            return null;
        }

        protected override void OnRenderProcessTerminated(
            IWebBrowser chromiumWebBrowser,
            IBrowser browser,
            CefTerminationStatus status,
            int errorCode,
            string errorString)
        {
            try
            {
                string url = browser?.MainFrame?.Url ?? "";
                AppLogger.Error(
                    $"[RENDERER] Process terminated: status={status}, code={errorCode}, " +
                    $"error='{errorString}', url='{url}'");
            }
            catch
            {
                // Logging the renderer crash must never cause another crash.
            }

            base.OnRenderProcessTerminated(chromiumWebBrowser, browser, status, errorCode, errorString);
        }

        protected override bool OnOpenUrlFromTab(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl, WindowOpenDisposition targetDisposition, bool userGesture)
        {
            if (string.IsNullOrWhiteSpace(targetUrl) || targetUrl.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                AppLogger.Debug(
                    $"[OPENURL] url='{targetUrl}' disposition={targetDisposition} " +
                    $"userGesture={userGesture}");
            }
            catch { }

            // Standard browser behavior:
            // - NewWindow (Shift+Click) → new window
            // - NewBackgroundTab (Ctrl+Click / Middle Click) → background tab
            // - NewForegroundTab (Ctrl+Shift+Click) → foreground tab
            // - NewPopup / CurrentTab / etc. → current window foreground tab

            if (targetDisposition == WindowOpenDisposition.NewWindow)
            {
                // Shift + Click: Open link in a new window (offset from parent)
                UiDispatch.Post(() =>
                {
                    MainWindow? owner = null;
                    _mainWindowRef?.TryGetTarget(out owner);
                    owner ??= System.Windows.Application.Current.MainWindow as MainWindow;
                    owner?.OpenNewWindow(targetUrl);
                }, "OpenUrlFromTab.NewWindow");
                return true;
            }

            if (targetDisposition == WindowOpenDisposition.NewBackgroundTab)
            {
                // Ctrl + Click / Middle Click: Open link in background tab
                UiDispatch.Post(() =>
                {
                    MainWindow? owner = null;
                    _mainWindowRef?.TryGetTarget(out owner);
                    owner ??= System.Windows.Application.Current.MainWindow as MainWindow;
                    owner?.AddNewTab(targetUrl, inBackground: true);
                }, "OpenUrlFromTab.NewBackgroundTab");
                return true;
            }

            if (targetDisposition == WindowOpenDisposition.NewForegroundTab ||
                targetDisposition == WindowOpenDisposition.NewPopup)
            {
                // Ctrl + Shift + Click or a popup: Open link in foreground tab
                UiDispatch.Post(() =>
                {
                    MainWindow? owner = null;
                    _mainWindowRef?.TryGetTarget(out owner);
                    owner ??= System.Windows.Application.Current.MainWindow as MainWindow;
                    owner?.AddNewTab(targetUrl, inBackground: false);
                }, "OpenUrlFromTab.NewForegroundTab");
                return true;
            }

            // Navigating in the same tab is the only case CEF should handle itself.
            if (targetDisposition == WindowOpenDisposition.CurrentTab ||
                targetDisposition == WindowOpenDisposition.SingletonTab)
            {
                return false;
            }

            // Everything else (SaveToDisk, OffTheRecord, IgnoreAction, SwitchToTab,
            // NewPictureInPicture, Unknown, ...) must not fall through to CEF's
            // default handler, which can create an unmanaged native window.
            AppLogger.Debug($"[OPENURL] Ignored disposition {targetDisposition} for '{targetUrl}'");
            return true;
        }
    }

    /// <summary>
    /// Forces the user's chosen website language on every outgoing request so sites
    /// do not fall back to the OS locale or IP-based geolocation.
    /// </summary>
    /// <remarks>
    /// NOTE: This per-request header injection is now REDUNDANT because
    /// LanguageManager.ApplyLive() sets the global CEF preference "intl.accept_languages"
    /// which Chromium automatically applies to all new requests. Kept as a safety net
    /// but can be removed entirely for a small perf gain (avoids managed/native crossing
    /// on every resource load).
    /// </remarks>
    public sealed class ApolionResourceRequestHandler : CefSharp.Handler.ResourceRequestHandler
    {
        protected override CefReturnValue OnBeforeResourceLoad(
            IWebBrowser chromiumWebBrowser,
            IBrowser browser,
            IFrame frame,
            IRequest request,
            IRequestCallback callback)
        {
            // Disabled: global CEF preference handles this more efficiently
            // Uncomment only if sites still don't respect the language preference
            /*
            try
            {
                string header = LanguageManager.AcceptLanguageHeader;
                if (!string.IsNullOrEmpty(header))
                {
                    request.SetHeaderByName("Accept-Language", header, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Accept-Language injection failed: {ex.Message}");
            }
            */

            return CefReturnValue.Continue;
        }
    }

    // Keyboard Handler to intercept shortcuts like F11 when CEF has focus
    public class ApolionKeyboardHandler : IKeyboardHandler
    {
        private readonly MainWindow? _mainWindow;

        public ApolionKeyboardHandler(MainWindow? mainWindow = null)
        {
            _mainWindow = mainWindow;
        }

        public bool OnPreKeyEvent(IWebBrowser chromiumWebBrowser, IBrowser browser, KeyType type, int windowsKeyCode, int nativeKeyCode, CefEventFlags modifiers, bool isSystemKey, ref bool isKeyboardShortcut)
        {
            if (type == KeyType.RawKeyDown)
            {
                var targetWindow = _mainWindow ?? (System.Windows.Application.Current?.MainWindow as MainWindow);

                // F11
                if (windowsKeyCode == 0x7A)
                {
                    UiDispatch.Post(targetWindow?.Dispatcher, () =>
                    {
                        if (targetWindow != null)
                            targetWindow.SetFullscreen(!targetWindow._isFullscreen);
                    }, "F11 shortcut");
                    return true; // Handled
                }

                // F12 or Ctrl+Shift+I or Ctrl+Shift+C (DevTools / Inspect Element)
                if (windowsKeyCode == 0x7B ||
                    ((windowsKeyCode == 0x49 || windowsKeyCode == 0x43) && modifiers.HasFlag(CefEventFlags.ControlDown) && modifiers.HasFlag(CefEventFlags.ShiftDown)))
                {
                    UiDispatch.Post(() =>
                    {
                        try
                        {
                            if (!App.DevToolsEnabled)
                            {
                                System.Windows.MessageBox.Show("Developer Tools are disabled. Enable them in Settings.", "Apolion", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                                return;
                            }

                            if (chromiumWebBrowser == null || chromiumWebBrowser.IsDisposed) return;

                            var host = browser.GetHost();
                            if (host != null && host.HasDevTools)
                                chromiumWebBrowser.CloseDevTools();
                            else
                                chromiumWebBrowser.ShowDevTools();
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Exception("DevTools shortcut", ex);
                        }
                    }, "DevTools shortcut");
                    return true;
                }

                // Ctrl+N (New Window)
                if (windowsKeyCode == 0x4E && modifiers.HasFlag(CefEventFlags.ControlDown) && !modifiers.HasFlag(CefEventFlags.ShiftDown))
                {
                    UiDispatch.Post(() =>
                    {
                        var newWin = new MainWindow();
                        newWin.Show();
                        newWin.AddNewTab("https://www.google.com");
                    }, "Ctrl+N shortcut");
                    return true;
                }

            }
            return false;
        }

        public bool OnKeyEvent(IWebBrowser chromiumWebBrowser, IBrowser browser, KeyType type, int windowsKeyCode, int nativeKeyCode, CefEventFlags modifiers, bool isSystemKey)
        {
            return false;
        }
    }
}

