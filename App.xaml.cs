using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CefSharp;

namespace Apolion
{
    public partial class App : System.Windows.Application
    {
        /// <summary>
        /// Primary language subtag (e.g. "de", "en") of the selected website language.
        /// </summary>
        public static string UILanguage { get; private set; } = "en";

        /// <summary>Controls whether Ctrl+Shift+C / Inspect Element open DevTools.</summary>
        public static bool DevToolsEnabled { get; set; } = true;

        /// <summary>True once CEF has been initialized.</summary>
        public static bool IsCefInitialized { get; private set; }

        /// <summary>
        /// True while warm start is enabled: the process stays alive in the
        /// tray after the last window closes so the next launch is instant.
        /// </summary>
        public static bool WarmStartEnabled { get; private set; }

        /// <summary>True once OnExit has begun; suppresses "last window" restarts.</summary>
        public static bool IsExiting { get; private set; }

        /// <summary>Task that completes when CEF initialization finishes.</summary>
        internal static TaskCompletionSource<bool>? CefInitTcs { get; private set; }

        /// <summary>Thread that called Cef.Initialize; it also owns Cef.Shutdown.</summary>
        private Thread? _cefOwnerThread;
        private readonly ManualResetEventSlim _cefShutdownRequested = new(false);
        private readonly ManualResetEventSlim _cefShutdownCompleted = new(false);
        private volatile bool _cefShutdownSucceeded;

        private static int _crashAssistantShown;
        private static int _fatalMessageShown;

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(
            [MarshalAs(UnmanagedType.LPWStr)] string AppID);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        protected override async void OnStartup(StartupEventArgs e)
        {
            var startupStopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            RegisterCrashHandlers();
            AppLogger.Info($"=== Apolion {AppLogger.GetAppVersion()} starting (pid {Environment.ProcessId}) ===");
            AppLogger.Info($"OS={RuntimeInformation.OSDescription}; Runtime={RuntimeInformation.FrameworkDescription}; BaseDir={AppDomain.CurrentDomain.BaseDirectory}");

            // Set AppUserModelID early so Windows groups all child processes
            SetCurrentProcessExplicitAppUserModelID("Apolion.Browser");
            AppLogger.Debug("AppUserModelID set");

            // Two processes cannot share one CEF CachePath (the second
            // Cef.Initialize fails), so forward this launch to the running
            // instance instead of starting a competing browser process.
            if (!AppInstance.TryAcquirePrimaryInstance())
            {
                string? forwardedUrl = GetStartupUrl(e.Args);
                bool forwarded = AppInstance.NotifyExistingInstance(forwardedUrl);
                if (!forwarded)
                {
                    ShowNativeMessage(
                        "Apolion is already running, but the running instance did not respond.\n\n" +
                        "Close it from Task Manager (or the tray icon) and try again.",
                        "Apolion");
                }

                Shutdown(0);
                return;
            }

            WarmStartEnabled = SettingsManager.GetWarmStartEnabled();
            string? startupUrl = GetStartupUrl(e.Args);
            if (!string.IsNullOrWhiteSpace(startupUrl))
            {
                AppLogger.Info($"Startup URL: {startupUrl}");
            }

            // Use CEF's Chrome runtime style
            CefSharpSettings.RuntimeStyle = CefRuntimeStyle.Chrome;

            // Apolion owns the CEF lifetime. CefSharp's automatic process-exit hook
            // would call Cef.Shutdown() from an arbitrary thread and conflict with
            // the shutdown performed on our dedicated CEF owner thread.
            CefSharpSettings.ShutdownOnExit = false;

            // Prepare CEF initialization task (starts immediately in background)
            CefInitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cefInitTask = InitializeCefAsync();

            // Run independent startup tasks in parallel while CEF initializes
            var parallelTasks = new List<Task>
            {
                // Start Menu registration (deferred, after UI is responsive)
            };

            // Create and show main window IMMEDIATELY (before CEF ready)
            var mainWindow = new MainWindow();
            System.Windows.Application.Current.MainWindow = mainWindow;

            // Set the shutdown policy before the window can be closed.
            ShutdownMode = WarmStartEnabled ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnLastWindowClose;
            if (WarmStartEnabled)
            {
                TrayIconManager.Show(OpenFromTray, RequestExit);
            }

            // Show window with placeholder - CEF will initialize in background
            mainWindow.Show();
            mainWindow.WindowState = System.Windows.WindowState.Normal;

            // Listen for launches that Windows hands to a second process. This
            // makes those launches instant while this process is warm.
            AppInstance.StartServer(Dispatcher, HandleForwardedOpenRequest);

            AppLogger.Info($"Main window shown at {startupStopwatch.ElapsedMilliseconds}ms");

            // Yield to ensure window is rendered before continuing
            await Task.Yield();

            // Start UI watchdog
            UiWatchdog.Start(Dispatcher);
            StrayWindowGuard.Start(Dispatcher); // dispatcher retained for signature compatibility; guard uses background timer

            // Power mode awareness: adjust behavior on battery vs AC
            PowerModeManager.Start(Dispatcher);

            // Wait for parallel tasks (language, StartMenu) - but NOT CEF
            await Task.WhenAll(parallelTasks);
            AppLogger.Info($"Parallel startup tasks done at {startupStopwatch.ElapsedMilliseconds}ms");

            // Now wait for CEF initialization to complete
            try
            {
                await cefInitTask;
                IsCefInitialized = true;
                AppLogger.Info($"CEF initialized at {startupStopwatch.ElapsedMilliseconds}ms");
                
                // Notify every window that was waiting on CEF. Only the startup
                // window restores the saved session; windows opened during
                // initialization just process their queued tabs. A launch with
                // an explicit URL opens that URL instead of the old session.
                bool restoreSavedSession = string.IsNullOrWhiteSpace(startupUrl);
                foreach (var window in Windows.OfType<MainWindow>())
                {
                    window.OnCefInitialized(
                        restoreSession: ReferenceEquals(window, mainWindow) && restoreSavedSession);
                }

                if (!string.IsNullOrWhiteSpace(startupUrl))
                {
                    mainWindow.AddNewTab(startupUrl, inBackground: false);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Exception("CEF initialization failed", ex);
                string reportPath = AppLogger.WriteCrashReport(ex, "Cef.Initialize");
                ShowCrashAssistant(ex, "CefSharp initialization", reportPath, blocking: true);
                Shutdown();
                return;
            }

            // Deferred update check - runs in background AFTER window is interactive
            _ = Task.Run(async () =>
            {
                try
                {
                    // Small delay to let UI settle
                    await Task.Delay(2000);

                    bool skipUpdateCheck = e.Args != null && Array.Exists(e.Args, a => a.Equals("--no-update-check", StringComparison.OrdinalIgnoreCase));
                    if (!skipUpdateCheck)
                    {
                        AppLogger.Info("Checking for updates (background)...");
                        bool updateAvailable = await Updater.CheckForUpdatesAsync();

                        if (updateAvailable)
                        {
                            AppLogger.Info($"Update available: {Updater.LatestVersion}");
                            await Dispatcher.InvokeAsync(() =>
                            {
                                var updateWindow = new UpdateWindow(
                                    Updater.CurrentVersion,
                                    Updater.LatestVersion ?? "unknown",
                                    Updater.LatestReleaseNotes);
                                updateWindow.Owner = mainWindow;
                                updateWindow.ShowDialog();
                            });
                        }
                        else
                        {
                            AppLogger.Info("No updates available");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Exception("Background update check failed", ex);
                }
            });

            base.OnStartup(e);
            
            AppLogger.Info($"=== Startup complete: {startupStopwatch.ElapsedMilliseconds}ms ===");
        }

        /// <summary>
        /// Returns the first HTTP/HTTPS/file URL passed on the command line.
        /// Used both for first launches and for forwarding to a running instance.
        /// </summary>
        private static string? GetStartupUrl(string[]? args)
        {
            if (args == null) return null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.IsNullOrWhiteSpace(arg)) continue;

                // Internal switch with a value that is not a URL.
                if (arg.Equals("--restore-tabs", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    continue;
                }

                if (arg.StartsWith("-", StringComparison.Ordinal)) continue;

                if (Uri.TryCreate(arg, UriKind.Absolute, out Uri? uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp ||
                     uri.Scheme == Uri.UriSchemeHttps ||
                     uri.Scheme == Uri.UriSchemeFile))
                {
                    return arg;
                }
            }

            return null;
        }

        private void OpenFromTray()
        {
            try
            {
                HandleForwardedOpenRequest(null);
            }
            catch (Exception ex)
            {
                AppLogger.Exception("Open from tray", ex);
            }
        }

        private void RequestExit()
        {
            IsExiting = true;
            TrayIconManager.Hide();
            Shutdown();
        }

        /// <summary>
        /// Handles a launch request forwarded by a second Apolion process.
        /// Must run on the WPF UI thread.
        /// </summary>
        private void HandleForwardedOpenRequest(string? url)
        {
            if (IsExiting) return;

            var windows = Windows.OfType<MainWindow>().Where(w => w.IsVisible).ToList();

            if (!string.IsNullOrWhiteSpace(url))
            {
                MainWindow target;
                if (windows.Count > 0)
                {
                    target = windows[^1];
                }
                else
                {
                    target = new MainWindow();
                    if (MainWindow == null || !MainWindow.IsVisible) MainWindow = target;
                    target.Show();
                }

                target.AddNewTab(url);
                if (target.WindowState == WindowState.Minimized)
                {
                    target.WindowState = WindowState.Normal;
                }

                target.Activate();
                target.Focus();
                AppLogger.Info($"Forwarded URL opened in a tab: {url}");
                return;
            }

            if (windows.Count > 0)
            {
                MainWindow target = windows[^1];
                if (target.WindowState == WindowState.Minimized)
                {
                    target.WindowState = WindowState.Normal;
                }

                target.Activate();
                target.Focus();
                return;
            }

            var newWindow = new MainWindow();
            if (MainWindow == null || !MainWindow.IsVisible) MainWindow = newWindow;
            newWindow.Show();
            newWindow.RequestSessionRestore();
        }

        /// <summary>Called by the settings toggle.</summary>
        public static void SetWarmStartEnabled(bool enabled)
        {
            if (Current is App app)
            {
                app.ApplyWarmStartSetting(enabled, persist: true);
            }
        }

        private void ApplyWarmStartSetting(bool enabled, bool persist)
        {
            WarmStartEnabled = enabled;
            if (persist)
            {
                SettingsManager.SetWarmStartEnabled(enabled);
            }

            ShutdownMode = enabled ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnLastWindowClose;

            if (enabled)
            {
                TrayIconManager.Show(OpenFromTray, RequestExit);
                AppLogger.Info("Warm start enabled");
            }
            else
            {
                TrayIconManager.Hide();
                AppLogger.Info("Warm start disabled");

                // Turning the setting off while Apolion is sitting in the tray
                // should exit instead of leaving an invisible process behind.
                if (!Windows.OfType<MainWindow>().Any(w => w.IsVisible))
                {
                    Shutdown();
                }
            }
        }

        private Task InitializeCefAsync()
        {
            CefInitTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Cef.Shutdown() must be called on the exact thread that called
            // Cef.Initialize(). A thread-pool thread cannot guarantee that, which
            // produced "Cef.Shutdown must be called on the same thread" errors and
            // could leave Chromium helper processes behind. Own a dedicated
            // background thread and keep it alive until shutdown is requested.
            _cefOwnerThread = new Thread(CefOwnerThreadMain)
            {
                IsBackground = true,
                Name = "Apolion CEF owner"
            };
            _cefOwnerThread.Start();

            return CefInitTcs.Task;
        }

        private void CefOwnerThreadMain()
        {
            try
            {
                InitializeCefCore();
                CefInitTcs?.TrySetResult(true);
            }
            catch (Exception ex)
            {
                CefInitTcs?.TrySetException(ex);
                _cefShutdownCompleted.Set();
                return;
            }

            // Block without consuming CPU until App.OnExit requests shutdown.
            _cefShutdownRequested.Wait();

            try
            {
                AppLogger.Info("Cef.Shutdown on the CEF owner thread");
                Cef.Shutdown();
                _cefShutdownSucceeded = true;
                AppLogger.Info("CEF shut down cleanly");
            }
            catch (Exception ex)
            {
                AppLogger.Exception("Cef.Shutdown", ex);
            }
            finally
            {
                _cefShutdownCompleted.Set();
            }
        }

        private void InitializeCefCore()
        {
            var settings = new CefSharp.WinForms.CefSettings();

            settings.LogFile = AppLogger.CefLog;
            
            // Power/perf: Reduce CEF log verbosity in release (Warning only).
            // Info/Debug only when APOLION_DEBUG=1 to avoid disk I/O on battery.
#if DEBUG
            settings.LogSeverity = CefSharp.LogSeverity.Info;
#else
            bool debugMode = string.Equals(Environment.GetEnvironmentVariable("APOLION_DEBUG"), "1", StringComparison.OrdinalIgnoreCase);
            settings.LogSeverity = debugMode ? CefSharp.LogSeverity.Info : CefSharp.LogSeverity.Warning;
#endif
            AppLogger.Info($"CEF log: {AppLogger.CefLog} (severity={settings.LogSeverity})");

            var cefSharpFolder = AppDomain.CurrentDomain.BaseDirectory;
            settings.ResourcesDirPath = cefSharpFolder;
            settings.LocalesDirPath = Path.Combine(cefSharpFolder, "locales");

            var language = LanguageManager.Current;
            UILanguage = language.PrimaryLanguage;

            string? cefLocale = null;
            foreach (var candidate in new[] { language.Code, language.PrimaryLanguage })
            {
                if (File.Exists(Path.Combine(settings.LocalesDirPath, $"{candidate}.pak")))
                {
                    cefLocale = candidate;
                    break;
                }
            }
            settings.Locale = cefLocale ?? "en-US";

            settings.AcceptLanguageList = LanguageManager.AcceptLanguageList;

            settings.CachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Apolion", "CefCache");

            // Chrome's session-restore service would otherwise re-create the
            // previous session's tabs as native "Chromium" windows outside our
            // CefClient. Apolion restores its own tabs, so drop that data first.
            // This must run before Cef.Initialize reads Preferences; it only
            // touches two small files and avoids a race with CEF startup.
            CefProfileSanitizer.PrepareForStartup(settings.CachePath);

            settings.PersistSessionCookies = true;

            // Media / codec support for Instagram, YouTube, etc.
            // H.264/AAC are a compile-time property of CEF/FFmpeg. Apolion uses the
            // codec-enabled CEF runtime fetched by tools\Fetch-CefCodecs.ps1 (see
            // Build.md); these switches enable capture streams and hardware decode.
            settings.CefCommandLineArgs.Add("enable-media-stream");

            // Keep the UA version in sync with the actual Chromium build. Sites
            // (Instagram in particular) compare it against the JS engine version.
            settings.CefCommandLineArgs.Add("user-agent", $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{GetUserAgentChromiumVersion()} Safari/537.36");

            // GPU / Rendering performance
            settings.CefCommandLineArgs.Add("num-raster-threads", "4");
            settings.CefCommandLineArgs.Add("enable-gpu-rasterization");
            settings.CefCommandLineArgs.Add("enable-zero-copy");
            settings.CefCommandLineArgs.Add("enable-oop-rasterization");
            settings.CefCommandLineArgs.Add("enable-native-gpu-memory-buffers");
            settings.CefCommandLineArgs.Add("enable-accelerated-video-decode");
            settings.CefCommandLineArgs.Add("enable-accelerated-video-encode");

            // Process model: process-per-site (one renderer per site) and a hard
            // renderer cap. Without the cap Chromium derives the limit from
            // installed RAM (~90+ renderers on a 16 GB machine), which is
            // effectively no limit and is the main reason a dozen tabs can cost
            // well over a gigabyte. The cap forces compatible sites to share
            // renderers while keeping crash isolation between groups.
            settings.CefCommandLineArgs.Add("process-per-site");
            settings.CefCommandLineArgs.Add("renderer-process-limit", "6");

            // Strip unneeded subsystems
            settings.CefCommandLineArgs.Add("disable-bluetooth");
            settings.CefCommandLineArgs.Add("disable-usb-keyboard-detect");
            settings.CefCommandLineArgs.Add("disable-speech-api");
            settings.CefCommandLineArgs.Add("disable-media-router");
            settings.CefCommandLineArgs.Add("disable-print-preview");
            settings.CefCommandLineArgs.Add("disable-extensions");
            settings.CefCommandLineArgs.Add("disable-component-update");
            settings.CefCommandLineArgs.Add("disable-client-side-phishing-detection");
            settings.CefCommandLineArgs.Add("disable-domain-reliability");
            settings.CefCommandLineArgs.Add("disable-sync");
            settings.CefCommandLineArgs.Add("disable-background-networking");
            settings.CefCommandLineArgs.Add("disable-default-apps");
            settings.CefCommandLineArgs.Add("dns-prefetch-disable");
            settings.CefCommandLineArgs.Add("aggressive-cache-discard");
            settings.CefCommandLineArgs.Add("disk-cache-size", "134217728");

            // V8 optimization - enable code caching for faster subsequent loads
            // V8 snapshot for faster isolate creation (~30-50% faster first load)
            settings.CefCommandLineArgs.Add("enable-v8-snapshot");
            settings.CefCommandLineArgs.Add("js-flags", "--max-old-space-size=384 --optimize-for-size --memory-reducer");
            settings.CefCommandLineArgs.Add("enable-v8-code-cache");
            settings.CefCommandLineArgs.Add("v8-cache-strategies-for-cache-storage");
            // Compile hints for faster warmup
            settings.CefCommandLineArgs.Add("v8-compile-hints");

            var enableFeatures = new System.Collections.Generic.List<string>
            {
                "ThrottleDisplayNoneAndVisibilityHiddenCrossOriginIFrame",
                "IntensiveWakeUpThrottling",
                "CalculateNativeWinOcclusion",
                "DnsOverHttps<DoHTrial",
                // Media features must live in the single --enable-features switch;
                // a second switch with the same name would simply overwrite this one.
                "MediaFoundationH264",
                "MediaFoundationAAC",
                "MediaFoundationAAC_ADTS",
                "HardwareMediaKeyHandling",
                "PictureInPicture"
            };

            settings.CefCommandLineArgs.Add("host-resolver-rules",
                "MAP *.doubleclick.net 0.0.0.0, " +
                "MAP *.googleadservices.com 0.0.0.0, " +
                "MAP *.googlesyndication.com 0.0.0.0, " +
                "MAP *.playwire.com 0.0.0.0, " +
                "MAP *.intergi.com 0.0.0.0, " +
                "MAP *.alocdn.com 0.0.0.0, " +
                "MAP *.nitropay.com 0.0.0.0, " +
                "MAP *.adnxs.com 0.0.0.0, " +
                "MAP *.criteo.com 0.0.0.0, " +
                "MAP *.pubmatic.com 0.0.0.0, " +
                "MAP *.rubiconproject.com 0.0.0.0, " +
                "MAP *.amazon-adsystem.com 0.0.0.0, " +
                "MAP *.taboola.com 0.0.0.0, " +
                "MAP *.outbrain.com 0.0.0.0, " +
                "MAP *.mediavine.com 0.0.0.0, " +
                "MAP *.ezoic.net 0.0.0.0, " +
                "MAP *.freestar.io 0.0.0.0, " +
                "MAP *.adthrive.com 0.0.0.0, " +
                "MAP *.raptive.com 0.0.0.0, " +
                "MAP *.monetag.com 0.0.0.0, " +
                "MAP *.propellerads.com 0.0.0.0, " +
                "MAP *.teads.tv 0.0.0.0, " +
                "MAP *.primis.tech 0.0.0.0, " +
                "MAP *.connatix.com 0.0.0.0, " +
                "MAP *.vntsm.com 0.0.0.0, " +
                "MAP *.snackmedia.com 0.0.0.0");

            settings.CefCommandLineArgs.Add("force-fieldtrials", "DoHTrial/Group1");
            settings.CefCommandLineArgs.Add("force-fieldtrial-params",
                "DoHTrial.Group1:Fallback/true/Templates/https%3A%2F%2Fdns.adguard-dns.com%2Fdns-query%20https%3A%2F%2Fdns.google%2Fdns-query");

            var disableFeatures = new System.Collections.Generic.List<string>
            {
                "WebGPU",
                "WebBluetooth",
                "WebUSB",
                "WebHID",
                "WebSerial",
                "WebMIDI",
                "WebNFC",
                "WebXR",
                "MediaRouter",
                "SpeechRecognition",
                "SpeechSynthesis",
                "AutofillServerCommunication",
                "BackForwardCache",
                "Prerender2",
                // Avoid keeping a spare renderer process warm for the most
                // recently used site; Apolion already caps renderer count.
                "SpareRendererForSitePerProcess",
                // Chromium's download bubble (the download arrow / progress
                // animation that Chrome paints into the toolbar area) is part of
                // the Chrome-runtime UI layer. Apolion draws its own download UI
                // (Save dialog + Downloads window), so disable the whole bubble
                // feature — not just the partial-view done card — so the arrow
                // animation never renders on top of Apolion's windows.
                "DownloadBubble"
            };

            settings.CefCommandLineArgs.Add("blink-settings",
                SettingsManager.IsDarkModeEnabled()
                    ? "preferredColorScheme=0"
                    : "preferredColorScheme=1");

            settings.CefCommandLineArgs.Add("enable-features", string.Join(",", enableFeatures));
            settings.CefCommandLineArgs.Add("disable-features", string.Join(",", disableFeatures));

            settings.CefCommandLineArgs.Add("disable-restore-session-state");
            settings.CefCommandLineArgs.Add("disable-session-crashed-bubble");
            settings.CefCommandLineArgs.Add("disable-session-restore");

            var helperPath = Path.Combine(cefSharpFolder, "Apolion Helper.exe");
            settings.BrowserSubprocessPath = File.Exists(helperPath)
                ? helperPath
                : Path.Combine(cefSharpFolder, "CefSharp.BrowserSubprocess.exe");

#if DEBUG
            // Opt-in Chrome DevTools Protocol endpoint for media debugging:
            //   set APOLION_MEDIA_DEBUG=1, then open http://127.0.0.1:9222/json
            // Debug-only and off by default; Release builds never contain this.
            if (string.Equals(Environment.GetEnvironmentVariable("APOLION_MEDIA_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
            {
                settings.CefCommandLineArgs.Add("remote-debugging-port", "9222");
                settings.CefCommandLineArgs.Add("remote-allow-origins", "*");
            }

            // Debug builds only: record the exact switches handed to Chromium.
            // Release builds never emit this (and never include the string literals).
            AppLogger.Info("[MEDIA-DIAG] CEF args: " + string.Join(" ", settings.CefCommandLineArgs.Select(kv =>
                string.IsNullOrEmpty(kv.Value) ? "--" + kv.Key : "--" + kv.Key + "=" + kv.Value)));
#endif

            Cef.Initialize(settings);
            AppLogger.Info("CEF initialized successfully");

            // Keep navigator.language and accept-language in sync
            LanguageManager.ApplyLive();

            // Apolion draws its own download UI (Save dialog + Downloads window).
            // Chromium's Chrome-runtime layer also owns a download bubble (the
            // download arrow / progress animation that Chrome paints into the
            // toolbar area). Returning showDialog:false from OnBeforeDownload only
            // suppresses Chrome's "Save As" dialog, so the bubble has to be turned
            // off separately. The DownloadBubble feature is disabled via
            // --disable-features=DownloadBubble (added to the disable-features list
            // above), and download_bubble.partial_view_enabled=false is set below as
            // belt-and-suspenders.
            DisableChromiumDownloadBubble();
        }

        /// <summary>
        /// Turns off Chromium's automatic partial download bubble — the dark
        /// "&lt;file&gt; • Done" card that otherwise floats on top of Apolion's UI
        /// when a download completes.
        ///
        /// The bubble is gated by the profile preference
        /// <c>download_bubble.partial_view_enabled</c> (registered in Chromium's
        /// <c>chrome/browser/download/download_prefs.cc</c>, default true). While it
        /// is false, <c>DownloadBubbleUIController</c> builds no partial view, so the
        /// automatic bubble is skipped. The full bubble (including the download
        /// arrow animation that Chrome paints into the toolbar area) is disabled at
        /// the command-line level via --disable-features=DownloadBubble (see the
        /// disable-features list in InitializeCefCore). The full bubble is only
        /// reachable from Chrome's toolbar download button, which Apolion never
        /// renders.
        ///
        /// Must run on the CEF UI thread: profile preferences cannot be changed from
        /// the WPF UI thread. Apolion uses the global request context, so this is the
        /// profile every tab shares.
        /// </summary>
        private static void DisableChromiumDownloadBubble()
        {
            try
            {
                Cef.UIThreadTaskFactory.StartNew(() =>
                {
                    try
                    {
                        const string pref = "download_bubble.partial_view_enabled";

                        var context = Cef.GetGlobalRequestContext();
                        if (context == null)
                        {
                            AppLogger.Warn($"[DOWNLOAD] No global request context; Chromium download bubble may still appear");
                            return;
                        }

                        if (!context.CanSetPreference(pref))
                        {
                            AppLogger.Warn($"[DOWNLOAD] Cannot set '{pref}'; Chromium download bubble may still appear");
                            return;
                        }

                        if (context.SetPreference(pref, false, out string error))
                        {
                            AppLogger.Info($"[DOWNLOAD] Chromium download bubble disabled ({pref}=false)");
                        }
                        else
                        {
                            AppLogger.Warn($"[DOWNLOAD] Failed to disable Chromium download bubble: {error}");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Exception("[DOWNLOAD] DisableChromiumDownloadBubble", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                // CEF is not initialized yet if startup failed earlier.
                AppLogger.Exception("[DOWNLOAD] DisableChromiumDownloadBubble (queue)", ex);
            }
        }

        /// <summary>
        /// Major Chromium version for the User-Agent string, formatted as
        /// "major.0.0.0". Prefers the native libcef version and falls back to the
        /// CefSharp assembly major (CefSharp's major tracks Chromium's) without
        /// touching native code.
        /// </summary>
        private static string GetUserAgentChromiumVersion()
        {
            try
            {
                string major = Cef.ChromiumVersion.Split('.', 2)[0];
                if (int.TryParse(major, out int parsed) && parsed > 0)
                {
                    return $"{parsed}.0.0.0";
                }
            }
            catch
            {
                // Cef.ChromiumVersion calls into libcef; not safe in every context.
            }

            int fallbackMajor = typeof(Cef).Assembly.GetName().Version?.Major ?? 154;
            return $"{fallbackMajor}.0.0.0";
        }

        protected override void OnExit(ExitEventArgs e)
        {
            IsExiting = true;

            var shutdownStopwatch = System.Diagnostics.Stopwatch.StartNew();
            AppLogger.Info("=== Apolion shutting down ===");

            // Stop listening for forwarded launches and remove the tray icon
            // first; no new window should appear while the app is tearing down.
            AppInstance.StopServer();
            TrayIconManager.Hide();

            // Start a hard-exit watchdog in case shutdown hangs (increased to 30s to allow proper CEF cleanup)
            var forceExitThread = new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(30));
                AppLogger.Error("Shutdown timeout reached - forcing process exit");
                AppLogger.Flush();
                try { Environment.Exit(0); } catch { }
            })
            {
                IsBackground = true,
                Name = "Apolion Force Exit Watchdog"
            };
            forceExitThread.Start();

            UiWatchdog.Stop();
            StrayWindowGuard.Stop();
            PowerModeManager.Stop();

            try
            {
                DownloadsWindow.CloseForShutdown();
            }
            catch (Exception ex)
            {
                AppLogger.Exception("Closing downloads window", ex);
            }

            try
            {
                foreach (var window in Windows.OfType<MainWindow>().ToList())
                {
                    try { window.DisposeBrowsers(); }
                    catch (Exception ex) { AppLogger.Exception("Disposing browsers on exit", ex); }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Exception("Disposing windows on exit", ex);
            }

            // Explicitly kill any remaining CEF browser subprocesses before Cef.Shutdown
            // This prevents orphaned "Apolion Helper.exe" processes.
            KillLeftoverBrowserSubprocesses();

            try
            {
                // Ask the thread that owns CEF to shut it down. Calling
                // Cef.Shutdown() here (or on a Task.Run thread) violates CEF's thread
                // affinity and can leave Chromium helper processes alive.
                if (_cefOwnerThread is { IsAlive: true })
                {
                    _cefShutdownRequested.Set();
                    if (!_cefShutdownCompleted.Wait(TimeSpan.FromSeconds(20)))
                    {
                        AppLogger.Error("CEF shutdown timed out after 20 seconds - continuing exit");
                    }
                    else if (!_cefShutdownSucceeded)
                    {
                        AppLogger.Warn("CEF shutdown did not complete cleanly");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Exception("Cef.Shutdown", ex);
            }

            // Final safety net: kill any subprocesses that survived Cef.Shutdown.
            KillLeftoverBrowserSubprocesses();

            // Deliberately keep the CEF profile intact. Deleting Code Cache,
            // GPUCache, Service Worker, Local Storage, IndexedDB, etc. on every
            // exit forced a cold start on the next launch and destroyed site
            // logins/offline data. Stale session files are removed by
            // CefProfileSanitizer.PrepareForStartup() before Cef.Initialize,
            // which is the only cleanup the browser actually needs.
            AppLogger.Info("CefCache preserved for warm startup");

            // Release the single-instance mutex last, after CEF and helpers are
            // gone, so a new launch cannot race this process for the profile.
            AppInstance.Release();

            AppLogger.Info($"=== Exit complete: {shutdownStopwatch.ElapsedMilliseconds}ms ===");
            AppLogger.Flush();
            base.OnExit(e);
        }

        /// <summary>
        /// Kills CEF helper processes that are direct children of this Apolion
        /// process (Apolion Helper.exe / CefSharp.BrowserSubprocess.exe) and
        /// survived Cef.Shutdown(). Parent-PID filtering is important: matching
        /// by name alone could kill another user's Apolion instance or an
        /// unrelated CefSharp application on the same machine.
        /// </summary>
        private static void KillLeftoverBrowserSubprocesses()
        {
            try
            {
                int currentProcessId = Environment.ProcessId;
                var childIds = ProcessTree.GetDirectChildProcessIds(currentProcessId);
                if (childIds.Count == 0) return;

                var helperNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Apolion Helper",
                    "CefSharp.BrowserSubprocess"
                };

                foreach (int pid in childIds)
                {
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById(pid);
                        if (proc.HasExited || !helperNames.Contains(proc.ProcessName)) continue;

                        AppLogger.Info($"Killing leftover browser subprocess: {proc.ProcessName} (PID={pid})");
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(2000);
                    }
                    catch (ArgumentException)
                    {
                        // Process already exited or the PID was reused.
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug($"Failed to kill subprocess (PID={pid}): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Exception("KillLeftoverBrowserSubprocesses", ex);
            }
        }

        // ─── Crash handling ─────────────────────────────────────────────────

        private void RegisterCrashHandlers()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Exception ex = e.Exception ?? new Exception("Unknown UI exception");
            AppLogger.Error("Unhandled UI exception", ex);
            string reportPath = AppLogger.WriteCrashReport(ex, "WPF Dispatcher");

            // Keep the browser alive so the user can save their work and send the
            // report. Truly fatal runtime failures are handled by the AppDomain
            // handler below, where recovery is not possible.
            e.Handled = true;
            ShowCrashAssistant(ex, "WPF UI thread", reportPath, blocking: false);
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception
                ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown fatal error");

            AppLogger.Error("Fatal AppDomain exception", ex);
            string reportPath = AppLogger.WriteCrashReport(ex, "AppDomain (fatal)");

            if (Interlocked.Exchange(ref _fatalMessageShown, 1) != 0) return;

            ShowNativeMessage(
                "Apolion has crashed.\n\n" +
                $"{ex.GetType().Name}: {ex.Message}\n\n" +
                "A crash report was saved to:\n" +
                reportPath + "\n\n" +
                "Please send this file to the developer.",
                "Apolion Crash");
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            AppLogger.Error("Unobserved background task exception", e.Exception);
            e.SetObserved();
        }

        private void ShowCrashAssistant(Exception ex, string source, string reportPath, bool blocking)
        {
            if (Interlocked.Exchange(ref _crashAssistantShown, 1) != 0) return;

            void ShowNow()
            {
                try
                {
                    string summary = $"{source}: {ex.GetType().Name} — {ex.Message}";
                    if (summary.Length > 300) summary = summary[..300] + "…";

                    var window = new CrashWindow(summary, BuildCrashDetails(ex, source, reportPath), reportPath);
                    window.ShowDialog();
                }
                catch (Exception showEx)
                {
                    AppLogger.Exception("Crash assistant failed", showEx);
                    ShowNativeMessage(
                        $"Apolion hit a problem.\n\n{ex.Message}\n\nCrash report:\n{reportPath}",
                        "Apolion Crash");
                }
            }

            if (blocking)
            {
                ShowNow();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(ShowNow), DispatcherPriority.Send);
            }
        }

        private static string BuildCrashDetails(Exception ex, string source, string reportPath)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"Source   : {source}");
            builder.AppendLine($"Time     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"Version  : {AppLogger.GetAppVersion()}");
            builder.AppendLine($"OS       : {RuntimeInformation.OSDescription}");
            builder.AppendLine($"Runtime  : {RuntimeInformation.FrameworkDescription}");
            builder.AppendLine($"Process  : {Environment.ProcessId}");
            builder.AppendLine($"Report   : {reportPath}");
            builder.AppendLine();
            builder.AppendLine(ex.ToString());
            return builder.ToString();
        }

        private static void ShowNativeMessage(string text, string caption)
        {
            try
            {
                const uint MB_OK = 0x00000000;
                const uint MB_ICONERROR = 0x00000010;
                MessageBoxW(IntPtr.Zero, text, caption, MB_OK | MB_ICONERROR);
            }
            catch
            {
                // Nothing else we can do on the fatal path.
            }
        }
    }
}

