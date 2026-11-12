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
            
            // Show window with placeholder - CEF will initialize in background
            mainWindow.Show();
            mainWindow.WindowState = System.Windows.WindowState.Normal;
            
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            AppLogger.Info($"Main window shown at {startupStopwatch.ElapsedMilliseconds}ms");

            // Yield to ensure window is rendered before continuing
            await Task.Yield();

            // Start UI watchdog
            UiWatchdog.Start(Dispatcher);
            StrayWindowGuard.Start(Dispatcher);

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
                // initialization just process their queued tabs.
                foreach (var window in Windows.OfType<MainWindow>())
                {
                    window.OnCefInitialized(restoreSession: ReferenceEquals(window, mainWindow));
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
            // Profile sanitizer runs async on thread pool - doesn't block CEF init
            _ = CefProfileSanitizer.PrepareForStartupAsync(settings.CachePath);

            // NOTE: Cache cleanup moved to OnExit (shutdown)
            // This saves ~500ms-1s on startup

            settings.PersistSessionCookies = true;

            settings.CefCommandLineArgs.Add("enable-media-stream");

            // GPU / Rendering performance
            settings.CefCommandLineArgs.Add("num-raster-threads", "4");
            settings.CefCommandLineArgs.Add("enable-gpu-rasterization");
            settings.CefCommandLineArgs.Add("enable-zero-copy");
            settings.CefCommandLineArgs.Add("enable-oop-rasterization");
            settings.CefCommandLineArgs.Add("enable-native-gpu-memory-buffers");
            settings.CefCommandLineArgs.Add("enable-accelerated-video-decode");
            settings.CefCommandLineArgs.Add("enable-accelerated-video-encode");

            // Process model: process-per-site (one renderer per site)
            // Balances isolation (site crash doesn't kill all tabs) with memory efficiency
            settings.CefCommandLineArgs.Add("process-per-site");

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
            settings.CefCommandLineArgs.Add("disk-cache-size", "52428800");

            // V8 optimization - enable code caching for faster subsequent loads
            // V8 snapshot for faster isolate creation (~30-50% faster first load)
            settings.CefCommandLineArgs.Add("enable-v8-snapshot");
            settings.CefCommandLineArgs.Add("js-flags", "--max-old-space-size=512 --optimize-for-size");
            settings.CefCommandLineArgs.Add("enable-v8-code-cache");
            settings.CefCommandLineArgs.Add("v8-cache-strategies-for-cache-storage");
            // Compile hints for faster warmup
            settings.CefCommandLineArgs.Add("v8-compile-hints");

            var enableFeatures = new System.Collections.Generic.List<string>
            {
                "ThrottleDisplayNoneAndVisibilityHiddenCrossOriginIFrame",
                "IntensiveWakeUpThrottling",
                "CalculateNativeWinOcclusion",
                "DnsOverHttps<DoHTrial"
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
                "PictureInPicture",
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
                "Prerender2"
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

            Cef.Initialize(settings);
            AppLogger.Info("CEF initialized successfully");

            // Keep navigator.language and accept-language in sync
            LanguageManager.ApplyLive();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            var shutdownStopwatch = System.Diagnostics.Stopwatch.StartNew();
            AppLogger.Info("=== Apolion shutting down ===");
            
            // Start a hard-exit watchdog in case shutdown hangs
            var forceExitThread = new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(15));
                AppLogger.Error("Shutdown timeout reached - forcing process exit");
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

            try
            {
                // Ask the thread that owns CEF to shut it down. Calling
                // Cef.Shutdown() here (or on a Task.Run thread) violates CEF's thread
                // affinity and can leave Chromium helper processes alive.
                if (_cefOwnerThread is { IsAlive: true })
                {
                    _cefShutdownRequested.Set();
                    if (!_cefShutdownCompleted.Wait(TimeSpan.FromSeconds(10)))
                    {
                        AppLogger.Error("CEF shutdown timed out after 10 seconds - continuing exit");
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

            // CLEANUP CACHE ON SHUTDOWN (moved from startup to save ~500ms-1s)
            // This runs after CEF shutdown so no file locks
            Task.Run(() =>
            {
                try
                {
                    string cefCachePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Apolion", "CefCache");

                    if (Directory.Exists(cefCachePath))
                    {
                        AppLogger.Info($"Cleaning CefCache on shutdown: {cefCachePath}");

                        var staleDirs = new[] {
                            "Sessions",
                            Path.Combine("Default", "Code Cache"),
                            Path.Combine("Default", "GPUCache"),
                            "GrShaderCache",
                            Path.Combine("Default", "DawnGraphiteCache"),
                            Path.Combine("Default", "DawnWebGPUCache"),
                            Path.Combine("Default", "Service Worker"),
                            Path.Combine("Default", "blob_storage"),
                            Path.Combine("Default", "Cache"),
                            Path.Combine("Default", "Media Cache"),
                            Path.Combine("Default", "IndexedDB"),
                            Path.Combine("Default", "Local Storage"),
                            Path.Combine("Default", "Session Storage"),
                            Path.Combine("Default", "File System"),
                            Path.Combine("Default", "databases"),
                            "ShaderCache",
                            "Visited Links"
                        };

                        foreach (var dir in staleDirs)
                        {
                            try
                            {
                                var dirPath = Path.Combine(cefCachePath, dir);
                                if (Directory.Exists(dirPath))
                                {
                                    Directory.Delete(dirPath, true);
                                    AppLogger.Debug($"Deleted cache dir on shutdown: {dirPath}");
                                }
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warn($"Failed to delete cache dir {dir} on shutdown: {ex.Message}");
                            }
                        }

                        // Clean up leftover .log and .tmp files
                        try
                        {
                            foreach (var file in Directory.GetFiles(cefCachePath, "*.log"))
                            {
                                try { File.Delete(file); } catch { }
                            }
                            foreach (var file in Directory.GetFiles(cefCachePath, "*.tmp"))
                            {
                                try { File.Delete(file); } catch { }
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Exception("Shutdown cache cleanup failed", ex);
                }
            });

            AppLogger.Info($"=== Exit complete: {shutdownStopwatch.ElapsedMilliseconds}ms ===");
            base.OnExit(e);
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

