# Apolion Browser — Code Structure & Architecture Documentation

## 1. Overview & Technology Stack

**Apolion Browser** is a lightweight, high-performance Windows desktop browser built with **WPF (.NET 9.0-windows)** hosting **Chromium Embedded Framework (CEF via CefSharp.WinForms)**.

- **Framework**: .NET 9.0 (`net9.0-windows`)
- **UI Toolkit**: WPF (XAML) for custom chrome, tabs, toolbars, settings, and modals; WinForms via `WindowsFormsHost` for hosting Chromium web views.
- **Web Engine**: CefSharp.WinForms.NETCore & CefSharp.Common (v156.0.150, Chromium 156).
- **Runtime Requirements**: Windows 10 or later (Chromium 156 no longer supports Windows 7/8/8.1) and the Microsoft Visual C++ 2022 Redistributable.
- **Architecture**: `win-x64`, Self-Contained executable, PerMonitorV2 High DPI aware.
- **Process Optimization**: Custom build target renames `CefSharp.BrowserSubprocess.exe` to `Apolion Helper.exe`. Build targets automatically strip unused optional Chromium dependencies (DirectX compiler, software Vulkan SwANGLE, non-English locales) saving ~90 MB+ disk space.

---

## 2. Directory & File Structure

Apolion/

├── Apolion.csproj          # MSBuild project definition, packages, and stripping targets
├── app.manifest            # Windows OS compatibility manifest (Win 10/11)
├── AssemblyInfo.cs         # Assembly-level metadata (ThemeInfo)
├── apolion.ico             # Application icon asset
├── EULA.md                 # End User License Agreement text (shipped in the zip as a
│                           # read-only reference; the app never reads it at runtime).
│                           # The EULA is shown only in the Inno Setup installer.
├── Todo.md                 # Development roadmap, issues, and bug tracker
├── Code structure.md       # This file
│
├── Updater.cs              # Consolidated update system: check, download, delta/full, install
├── App.xaml                # Application resource definitions (empty; all in code)
├── App.xaml.cs             # Startup entry point, CEF owner-thread lifetime, CLI args, session restore
│
├── MainWindow.xaml         # Custom title bar, tab strip, navigation toolbar, settings popup, layered browser area
├── MainWindow.xaml.cs      # Main browser window controller, tab lifecycle, drag/drop, shortcuts, fullscreen
│
├── CefHandlers.cs          # All custom CEF interface handlers (context menu, life span, keyboard, request, display, permissions, JS dialogs, load, render process messages)
├── CefProfileSanitizer.cs  # Clears Chrome session-restore files & normalizes profile.exit_type before Cef.Initialize
├── StrayWindowGuard.cs     # Periodic HWND whitelist sweep; closes escaped Chrome_WidgetWin_* top-level windows
├── AppLogger.cs            # Crash-safe debug/CEF/crash logging with rotation (buffered writer)
├── UiDispatch.cs           # Async CEF/native → WPF dispatcher bridge (deadlock-safe)
├── UiWatchdog.cs           # Detects and logs UI-thread stalls (> 3 s)
├── DownloadHandler.cs      # CefSharp IDownloadHandler for file downloads & speed metrics
├── LanguageManager.cs      # Website language catalog, persistence bridge, CEF/JS language injection
├── SettingsManager.cs      # Persistent state manager (theme, website language, saved tabs)
├── RenderProcessMessageHandler.cs # Injects navigator.language/languages override into every frame
│
├── DownloadsWindow.xaml    # Downloads manager UI window (custom title bar, theme-aware)
├── DownloadsWindow.xaml.cs # Thread-safe singleton, progress rows, speed/ETA display
│
├── CrashWindow.xaml        # Crash assistant UI (copy report, open logs, send report, restart)
├── CrashWindow.xaml.cs     # Crash assistant logic
│
├── UpdateWindow.xaml       # Update available dialog UI
├── UpdateWindow.xaml.cs    # Update dialog logic with progress/cancellation
│
├── pointer-lock-test.html     # Simple pointer lock test page
├── pointer-lock-iframe-test.html # Iframe pointer lock test page
├── click-popup-test.html      # Popup/click test page
└── popup-test.html            # Popup test page
│
├── Apolion.Updater/          # Dedicated updater executable project
│   ├── Apolion.Updater.csproj # Console app, self-contained, single-file publish
│   ├── updater.manifest       # Application manifest (runs as invoker)
│   └── Program.cs             # Waits for app exit, cleans cache, robocopies files, restarts
│
├── tools/
│   ├── Package-Release.ps1    # Release packaging: publish, zip, manifest, validate (includes updater)
│   ├── Build-Installer.ps1    # Installer build: publish + compile Inno Setup .iss (includes updater)
│   ├── Apolion.iss            # Inno Setup script (per-user, no-admin setup.exe)
│   └── README.md              # Packaging documentation
│
├── .vscode/
│   ├── launch.json            # VS Code debug configurations
│   ├── settings.json          # Roslyn project mapping
│   └── tasks.json             # Build/publish/watch tasks
│
└── artifacts/                 # Build outputs (zips, manifests, test packages)

---

## 3. Core Component Breakdown

### 3.1 Application Lifecycle (`App.xaml` / `App.xaml.cs`)

- **Process Identity**: Sets `AppUserModelID` to `"Apolion.Browser"` via `SetCurrentProcessExplicitAppUserModelID` so Windows groups all child processes on the taskbar.
- **Windows Start Menu & System Integration**: Registered by the Inno Setup
  installer at install time (Start Menu shortcut under `%APPDATA%\Microsoft\Windows\Start Menu\Programs\`).
  The installer owns shortcut creation plus HKCU App Paths, StartMenuInternet,
  and RegisteredApplications registration (http/https associations). All
  registration is under HKCU / user shell folders, so no admin rights are needed.
- **Localization / Website Language**: User picks one of 10 website languages (en-US, de-DE, es-ES, fr-FR, cs-CZ, pl-PL, zh-CN, hi-IN, pt-BR, ar-SA) on first launch (EULA dialog) or later in Settings. Choice persisted to `language.dat` and applied through three layers:
  1. CEF `Accept-Language` list (comma-separated, ordered by quality).
  2. Full `Accept-Language` header with quality factors injected on every request (`ApolionResourceRequestHandler`).
  3. Pre-page script overriding `navigator.language` / `navigator.languages` in every frame (`ApolionRenderProcessMessageHandler`).
  - For `en-US`, an `en-GB` hint is appended because Google treats bare `en-US` as its implicit default and would fall back to IP country language.
  - Chromium UI locale follows choice when matching `.pak` exists (release builds ship only `en-US.pak`).
- **CEF Configuration & Tuning** (`InitializeCefCore`):
  - Cache folder: `%AppData%\Apolion\CefCache`.
  - **Profile sanitization** before init: `CefProfileSanitizer.PrepareForStartupAsync()` deletes `Default\Sessions\Session_*` / `Tabs_*` and rewrites `Preferences` `"exit_type"` to `"Normal"` so Chrome's session-restore service does not resurrect the previous session as clientless native windows. Runs async on thread pool.
  - GPU rasterization flags (`enable-gpu-rasterization`, `enable-zero-copy`, `num-raster-threads=4`, `enable-oop-rasterization`, `enable-native-gpu-memory-buffers`, accelerated video decode/encode).
  - Memory constraints: `renderer-process-limit=1`, `process-per-site`, V8 flags (`--max-old-space-size=512 --optimize-for-size`, `enable-v8-snapshot`, `enable-v8-code-cache`, `v8-compile-hints`).
  - Stripped subsystems: bluetooth, usb, speech, media-router, print-preview, extensions, component-update, phishing-detection, domain-reliability, sync, background-networking, default-apps, dns-prefetch, aggressive-cache-discard, disk-cache-size=50 MB.
  - Ad/tracking blocking via `host-resolver-rules` (MAP *.doubleclick.net etc. → 0.0.0.0) + DoH field trial.
  - Disabled session restore flags: `disable-restore-session-state`, `disable-session-crashed-bubble`, `disable-session-restore`.
  - `BrowserSubprocessPath` → `Apolion Helper.exe` (renamed by build target).
- **CEF Owner-Thread Lifetime Model** (fixes "Cef.Shutdown must be called on the same thread" errors and leftover helper processes):
  - `Cef.Initialize` and `Cef.Shutdown` both execute on a single dedicated background thread (`_cefOwnerThread`, name "Apolion CEF owner").
  - Thread parks on `_cefShutdownRequested` (ManualResetEventSlim) after init; `OnExit` signals it and waits on `_cefShutdownCompleted` (max 10 s).
  - `CefSharpSettings.ShutdownOnExit = false` to prevent CefSharp's automatic process-exit hook from calling `Cef.Shutdown()` from an arbitrary thread.
  - Force-exit watchdog (15 s) as last resort.
- **Startup Sequence**:
  1. Set AppUserModelID, RuntimeStyle=Chrome, ShutdownOnExit=false.
  2. Start CEF owner thread (runs `InitializeCefCore` → `Cef.Initialize` → `LanguageManager.ApplyLive()`).
  3. Create & show MainWindow immediately (shows "Initializing…" placeholder if CEF not ready).
  4. Start `UiWatchdog`, `StrayWindowGuard`, `PowerModeManager`.
  5. Run parallel tasks: (Start Menu registration is done by the installer at install time, not at runtime.)
  6. Await CEF init; on success, notify all `MainWindow` instances via `OnCefInitialized(restoreSession)` — only the startup window restores the saved session; windows opened during init process their queued tabs.
  7. Deferred update check (2 s delay, `--no-update-check` skips).
- **Shutdown Sequence** (`OnExit`):
  1. Force-exit watchdog (15 s).
  2. Stop `UiWatchdog`, `StrayWindowGuard`, `PowerModeManager`.
  3. `DownloadsWindow.CloseForShutdown()`.
  4. `DisposeBrowsers()` on all `MainWindow` instances (releases ChromiumWebBrowser HWNDs before CEF shutdown).
  5. Signal CEF owner thread to call `Cef.Shutdown()`; wait up to 10 s.
  6. Fire-and-forget `Task.Run` cache cleanup (deletes Sessions, Code Cache, GPUCache, Service Worker, Cache, IndexedDB, Local/Session Storage, ShaderCache, Visited Links, *.log, *.tmp).

### 3.2 Main Window & Tab Management (`MainWindow.xaml` / `MainWindow.xaml.cs`)

- **Frameless Window & WindowChrome**:
  - `WindowStyle=None` + `WindowChrome` with custom title bar (`TitleBarGrid`, 36 px) supporting drag, double-click maximize, caption buttons (Min/Max/Close).
  - Native Win32 hook (`WndProc` → `WM_GETMINMAXINFO`) enforces exact monitor work-area bounds; prevents taskbar clipping during maximize/fullscreen.
- **Layered Tab Architecture**:
  - `BrowserContainer` (`TabControl`) holds tab headers with drag-and-drop reordering.
  - `BrowserLayerGrid` (`Grid`) holds `WindowsFormsHost` controls containing `ChromiumWebBrowser` instances.
  - Tab switch: active host → `Visibility.Visible` + `ZIndex=1`; inactive hosts → `Visibility.Hidden` + `ZIndex=0`. **Hidden keeps the Win32 HWND attached** so background audio/media continues playing without freezing.
- **Tab Lifecycle**:
  - **Drag-and-Drop Reordering & Cross-Window Transfer**: Floating transparent non-activating preview window (`_dragPreviewWindow`) follows cursor; glowing vertical drop insertion indicator (`TabInsertionIndicator`) shows target position in any hovered Apolion window. Dragging a tab onto another Apolion window transfers the live `ChromiumWebBrowser` instance without reloading (session, cookies, playing media preserved). Source window closes if empty. Dropping outside all windows detaches into a new window. Order persisted on save.
  - **Dynamic Tab Compressing**: `UpdateTabWidths()` recalculates tab widths (clamped 36–220 px) based on available width and tab count. Close buttons hidden when tabs < 60 px unless hovered or selected.
  - **Lazy Background Tabs**: Restored background tabs and tabs opened in background are created as *lazy tabs* — a `TabItem` with `Tag = LazyTabInfo { Url, HeaderText }`. Real `ChromiumWebBrowser` is instantiated only when the tab is selected (`RealizeLazyTab`). This avoids creating 10+ browser instances on startup.
  - **Favicon Fetcher**: Asynchronously downloads site favicons via `HttpClient` into WPF `BitmapImage`; updates tab header `Image` control. Uses an **LRU cache (256 entries)** to bound memory usage. In-flight downloads are deduplicated; faulted tasks are cleaned up to allow retries.
  - **Tab Sleeping**: Inactive background tabs (>10 min on AC, >5 min on battery) are discarded (browser disposed) and converted back to lazy tabs. Selection reloads the page. Cleanly removes tracking entries to prevent memory leaks.
- **Fullscreen Mode** (`SetFullscreen`):
  - Saves pre-fullscreen geometry, collapses title bar & toolbar rows, removes window borders, positions window over screen bounds with `Topmost=true`. `F11` toggles; `Esc` exits.
- **Navigation & Address Bar**:
  - Enter in address bar: auto-prepends `https://` for domain-like input or forwards to Google Search.
  - Back/Forward/Reload/Zoom controls (popup indicator overlay fades after 1.5 s).
- **Settings Flyout** (Popup anchored to gear icon):
  - Appearance: Light/Dark segmented buttons.
  - Language: ListBox bound to `LanguageManager.Languages`; selection persists and triggers live CEF/JS language update.
  - Downloads: Opens `DownloadsWindow`.
  - Cookies: "Clear All Cookies" → `Cef.GetGlobalCookieManager().DeleteCookiesAsync`.
  - Tabs Data: "Remove Saved Tabs Data" → deletes `saved_tabs.dat`.
  - Developer: DevTools toggle (`App.DevToolsEnabled`).
  - Diagnostics: "Open Debug & Crash Logs" → opens log folder in Explorer.
- **Keyboard Shortcuts** (`MainWindow_PreviewKeyDown`):
  - `F11` / `Esc` — Fullscreen toggle/exit.
  - `F12` / `Ctrl+Shift+I` / `Ctrl+Shift+C` — DevTools toggle.
  - `Ctrl+T` — New tab; `Ctrl+W` — Close tab; `Ctrl+N` — New window.
  - `Ctrl++` / `Ctrl+-` / `Ctrl+0` — Zoom in/out/reset.
- **Multi-Window Support**: `OpenNewWindow(url)` creates offset `MainWindow`; `TransferTabFrom` moves live browser between windows.
- **Window Closing**: Stops all timers (`_zoomHideTimer`, `_tabWidthDebounceTimer`, `_tabSleepTimer`) to prevent leaks. Saves session from all windows when the last one closes.

### 3.3 CEF Event Handlers (`CefHandlers.cs` & `DownloadHandler.cs`)

| Handler Class | Interface | Responsibility |
| :--- | :--- | :--- |
| `ApolionContextMenuHandler` | `IContextMenuHandler` | Custom right-click menu: Inspect Element (DevTools), Copy Image URL, Copy Link, Copy Image (base64/blob/HTTP fetch pipeline with retry), Save Image As. |
| `ApolionPermissionHandler` | `IPermissionHandler` | Auto-grants Pointer Lock (3D web games) and Audio/Video capture (Discord/Meet web). |
| `ApolionDisplayHandler` | `DisplayHandler` | Intercepts web fullscreen requests (YouTube HTML5 player) → toggles WPF window fullscreen. Updates tab favicons via `OnFaviconUrlChange`. |
| `ApolionLifeSpanHandler` | `ILifeSpanHandler` | **Popup interception**: `OnBeforePopup` cancels native popup creation (returns `true`), re-creates as Apolion tab/window per `targetDisposition` (Shift+Click → new window, Ctrl+Click/Middle → background tab, Ctrl+Shift+Click → foreground tab, popup → foreground tab). Blocks privileged URLs (`devtools://`, `chrome://`) and automatic popups (no user gesture). `OnAfterCreated` force-closes any escaped popup browser on CEF UI thread **unless** it is DevTools (detected by `devtools://` URL or window title "DevTools"). |
| `ApolionJsDialogHandler` | `IJsDialogHandler` | Replaces CEF native alerts/confirms with themed WPF `MessageBox`. Prompts suppressed. Async via `UiDispatch.Post` to avoid deadlocking CEF callback thread. |
| `ApolionRequestHandler` | `RequestHandler` | `OnOpenUrlFromTab` routes link clicks per disposition (same mapping as LifeSpan). `GetResourceRequestHandler` returns per-request `ApolionResourceRequestHandler`. `OnRenderProcessTerminated` logs renderer crashes. |
| `ApolionResourceRequestHandler` | `ResourceRequestHandler` | `OnBeforeResourceLoad` injects `Accept-Language` header from `LanguageManager.AcceptLanguageHeader` on every request. |
| `ApolionKeyboardHandler` | `IKeyboardHandler` | Captures hotkeys inside Chromium: `F11`, `F12`/`Ctrl+Shift+I`/`Ctrl+Shift+C` (DevTools), `Ctrl+N` (new window). |
| `ApolionLoadHandler` | `ILoadHandler` | Stub (v156 EventArgs-based interface); no custom logic. |
| `ApolionRenderProcessMessageHandler` | `IRenderProcessMessageHandler` | `OnContextCreated` injects `LanguageManager.NavigatorOverrideScript` into every frame before page scripts run. `OnUncaughtException` logs JS errors. |
| `ApolionDownloadHandler` | `IDownloadHandler` | `OnBeforeDownload` shows native Windows "Save As" dialog synchronously via `Dispatcher.Invoke` so `callback.Continue` runs before CEF falls back to its own native download popup overlay. `OnDownloadUpdated` tracks bytes/speed/ETA and fires `DownloadUpdated` event for `DownloadsWindow`. |

### 3.4 Profile Sanitization & Stray Window Guard

#### `CefProfileSanitizer.cs`
- Runs **before** `Cef.Initialize` on the CEF owner thread.
- Deletes `Default\Sessions\Session_*` and `Tabs_*` files (Chrome's native session restore data).
- Rewrites `Default\Preferences` `"exit_type"` to `"Normal"` (or injects it under `"profile"` if missing) so Chrome does not treat the previous run as a crash.
- Logs: `[SESSION] Prevented native Chromium session restore (removed N file(s), exit_type normalized: true/false)`.
- Never blocks startup on failure.
- **Runs async only** (`PrepareForStartupAsync`) to avoid duplicate I/O.

#### `StrayWindowGuard.cs`
- DispatcherTimer every 500 ms on UI thread.
- Enumerates process top-level windows (`EnumWindows`), filters to current PID, visible, unowned (`GW_OWNER`), class `Chrome_WidgetWin*`.
- Maintains whitelist `KnownHandles`: every `MainWindow` HWND + every live tab's `ChromiumWebBrowser.Handle` and `BrowserHwnd` (includes temporarily re-parented tabs during drag).
- **DevTools exemption**: windows with title containing "DevTools" are never closed.
- Closes unlisted matching windows via `PostMessage(WM_CLOSE)`; logs `[STRAY] Closing escaped CEF window hwnd=0x… class='…' title='…' size=WxH`.
- Started in `App.OnStartup`, stopped in `OnExit`.

### 3.5 Data Persistence & Settings (`SettingsManager.cs`)

**Data folder resolution** (in order):
1. `%APPDATA%\Apolion` (roaming)
2. `%LOCALAPPDATA%\Apolion`
3. `<exe dir>\UserData`
4. `%TEMP%\Apolion`

**Files** (atomic write via `.tmp` → `Move`):
- `user_settings.dat` — Theme (`"Light"` or `"Dark"`).
- `language.dat` — Website language BCP-47 tag (e.g. `"de-DE"`).
- `saved_tabs.dat` — Lines: `1:url\ttitle` (active tab), `0:url\ttitle` (background tabs).

### 3.6 Diagnostics & Crash Handling

- **`AppLogger.cs`**: Crash-safe logging to `%LOCALAPPDATA%\Apolion\Logs\`:
  - `debug.log` (rotated to `debug.old.log` at 2 MB; `APOLION_DEBUG=1` enables `DEBUG` level).
  - `cef.log` (native Chromium log, rotated to `cef.old.log` at 8 MB).
  - `crash-<timestamp>-<pid>.log` with exception, environment, and last 250 debug lines.
  - **Buffered StreamWriter** with periodic flush (every 2 s) for performance; rotation-safe.
- **Global Handlers**: `DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` all write crash report.
- **`CrashWindow`**: Shown for recoverable UI crashes; copy report, open log folder, mailto developer, restart. Fatal AppDomain crashes use native `MessageBoxW` with report path.
- **`UiWatchdog.cs`**: Background thread wakes every 1 s; reads heartbeat updated by `DispatcherTimer` (Background priority). Logs `[WATCHDOG] UI thread unresponsive for X ms` on stall > 3 s, and recovery.
- **`UiDispatch.cs`**: Every CEF/native callback needing UI thread uses `BeginInvoke` (never `Invoke`) to avoid dispatcher deadlocks.
- **In-App Access**: Settings → Diagnostics → "Open Debug & Crash Logs".

### 3.7 Supplementary Windows

1. **Downloads Manager** (`DownloadsWindow.xaml` / `DownloadsWindow.xaml.cs`):
   - Thread-safe singleton (`Instance` property, double-checked locking with dispatcher marshaling).
   - Custom title bar, theme-matching styling.
   - List of `DownloadRowView` (filename, progress bar, speed, ETA, status icons). File name is refreshed on each update so downloads that arrive with an empty `SuggestedFileName` first are still shown once the name becomes available.
   - Keeps a static store of pending downloads started before the window was opened, so they appear when the user opens Downloads later ("Nothing appears" fix).
   - "Clear Done" removes completed/cancelled rows from both the UI list and the pending store.
   - Hidden on close (not closed) to keep app alive only when user explicitly opens it.

2. **Crash Assistant** (`CrashWindow.xaml` / `CrashWindow.xaml.cs`):
   - Summary + scrollable details + log path.
   - Copy, Open log folder (Explorer `/select`), Email (mailto with truncated body), Restart.

3. **Update Dialog** (`UpdateWindow.xaml` / `UpdateWindow.xaml.cs`):
   - Shows current/latest version, release notes (truncated to 2000 chars).
   - Reports `IProgress<UpdateProgress>`: phase, determinate/indeterminate bar, bytes and percent.
   - Supports cancellation; on success `Application.Current.Shutdown()` and the external updater replaces files and restarts.

4. **Update System** (`Updater.cs` — consolidated single file):
   - Fetches the GitHub releases list (owner/repo configurable in source) and selects the best asset via embedded `ReleaseSelector`.
   - Skips drafts and incompatible assets (arm64, x86-only, PDB/source/debug/patch/delta zips), prefers `win-x64`, and only considers prereleases when the installed version is a prerelease or `APOLION_ALLOW_PRERELEASE=1`.
   - **Version is extracted from the release `name` field** (e.g. `"Apolion v1.0.1"` → `"1.0.1"`), falling back to `tag_name`. The tag itself can be any string.
   - **Assets are matched by name pattern** (any x64 `.zip` that isn't pdb/symbol/debug), not by a version-specific filename.
   - Embedded `VersionComparer` implements semver precedence with a token fallback for unusual tags.
   - Downloads `update-manifest.json`, asks embedded `UpdatePlanner` which files changed, then embedded `RemoteZipReader` fetches only those entries with HTTP `Range` requests. Falls back to the full zip when the manifest is missing/invalid, ranges are unsupported, or the saving is too small.
   - Full-zip downloads resume from `.part` files and every result is verified by size/SHA-256 before staging.
   - `PrepareAppDataForUpdate()` deletes CEF cache directories; settings, language and saved tabs are preserved.
   - **Dedicated `Apolion Updater.exe` required** (no batch fallback). Launched with install dir, source staging dir, and optional remove-list path.
   - Failures are logged to `%TEMP%\Apolion_Update.log`.

5. **Dedicated Updater Executable** (`Apolion.Updater/Program.cs`):
   - Console app, self-contained, single-file (~71 MB), runs as invoker (no admin).
   - Args: `<installDir> <sourceDir> [removeListPath]`
   - Waits for `Apolion.exe` + `Apolion Helper.exe` to exit (max 120 s).
   - Cleans CEF cache directories.
   - Robocopies source → install dir (`/E /COPY:DAT /R:5 /W:1`).
   - Processes remove-list.txt (deletes obsolete files, cleans empty dirs).
   - Starts `Apolion.exe`.
   - Cleans up `%TEMP%\Apolion_Update`.

---

## 4. Persistence Locations Summary

| Data | Location |
| :--- | :--- |
| **Settings / Language / Saved Tabs** | `%APPDATA%\Apolion\` (roaming) → fallback to LocalAppData/exe/temp |
| **CEF Cache / Profile / Sessions / Preferences** | `%APPDATA%\Apolion\CefCache\` |
| **Logs (debug, cef, crash reports)** | `%LOCALAPPDATA%\Apolion\Logs\` |
| **Start Menu Shortcut** | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Apolion.lnk` (created by installer) |
| **Registry (HKCU)** | `Software\Microsoft\Windows\CurrentVersion\App Paths\Apolion.exe`, `Software\Classes\Applications\Apolion.exe`, `Software\Clients\StartMenuInternet\Apolion`, `Software\RegisteredApplications` |

---

## 5. Build & Deployment

- **Project**: `Apolion.csproj` — `WinExe`, `net9.0-windows`, `UseWPF` + `UseWindowsForms`, `PlatformTarget=x64`, `RuntimeIdentifier=win-x64`, `SelfContained=true`, `AllowUnsafeBlocks=true`, `ApplicationHighDpiMode=PerMonitorV2`, `GenerateAssemblyInfo=false`, `GenerateTargetFrameworkAttribute=false`.
- **Packages**: `CefSharp.WinForms.NETCore` 156.0.150, `CefSharp.Common` 156.0.150.
- **Build Targets** (`AfterTargets="Build"`):
  1. `RenameSubprocess`: Copies `CefSharp.BrowserSubprocess.exe` → `Apolion Helper.exe`.
  2. `StripChromiumFootprint`: Deletes `dxcompiler.dll`, `dxil.dll` (WebGPU DXC, ~27 MB), `vk_swiftshader.dll`, `vk_swiftshader_icd.json`, `vulkan-1.dll` (SwANGLE, ~5 MB), all `locales\*.pak` except `en-US.pak` (~42 MB).
- **Output**: `bin\Debug|Release\net9.0-windows\win-x64\Apolion.exe` (single-file-ish, self-contained, ~120 MB on disk after strip).
- **Debug Build**: `DebugType=full`, `DebugSymbols=true`.
- **Release Build**: `DebugType=none`, `DebugSymbols=false`, `SatelliteResourceLanguages=en-US` (omits .NET localization satellites, ~17.5 MB).
- **VS Code Tasks**: `build`, `publish`, `watch` (dotnet watch run).
- **Release Packaging**: `tools\Package-Release.ps1` publishes, creates `Apolion-<version>-win-x64.zip`, generates `update-manifest.json` with per-file SHA-256/CRC-32/ranges, and validates the pair. Also publishes `Apolion Updater.exe` and includes it in the zip.
- **Installer Build**: `tools\Build-Installer.ps1` publishes main app + updater, compiles `tools\Apolion.iss` into `Apolion-<version>-setup.exe` (Inno Setup, per-user `%LOCALAPPDATA%\Apolion\`, no admin). See `Build.md` and `tools\README.md`.

---

## 6. Key Design Decisions & Rationale

| Area | Decision | Rationale |
| :--- | :--- | :--- |
| **CEF Lifetime** | Dedicated owner thread for both Init & Shutdown | Eliminates "must be called on same thread" errors; guarantees clean helper process termination. |
| **Session Restore** | Custom (`saved_tabs.dat`) + Profile Sanitizer | Chrome's native session restore creates clientless windows; Apolion restores tabs itself. |
| **Stray Windows** | HWND whitelist sweep (500 ms) | Title-based detection misses most escapes (CEF titles "Chromium" until page loads). |
| **Background Tabs** | Lazy initialization (real browser on select) | Avoids creating 10+ browser instances on startup; saves memory & CPU. |
| **Tab Transfer** | Move live `ChromiumWebBrowser` between `WindowsFormsHost` | Preserves session, cookies, playing media; no reload. |
| **Language Injection** | Three-layer (CEF list, request header, JS override) | Guarantees sites honor choice regardless of OS locale, IP geolocation, or navigator probing. |
| **Cache Cleanup** | On shutdown (fire-and-forget) | Moved from startup to save ~500 ms–1 s cold-start time; runs after CEF shutdown so no file locks. |
| **Logging** | Buffered StreamWriter, crash-safe, rotated, `APOLION_DEBUG` opt-in | Debugging production issues without perf impact or crash loops. |
| **Pointer Lock** | Native (Chrome runtime style, `IPermissionHandler` grant) | No host-side shim; Chromium handles focus/gesture/ESC correctly. |
| **Update** | GitHub Releases manifest + HTTP Range delta, full-zip fallback | Downloads only changed DLLs instead of the ~120 MB archive while remaining dependency-free. |
| **Update Data** | Preserve settings/language/tabs, clear CEF cache | Avoids resets while ensuring stale Chromium data cannot persist. No EULA marker exists; EULA acceptance is installer-only. |
| **Updater** | Dedicated `Apolion Updater.exe` (no batch fallback) | Reliable, single-file, self-contained; works for both installer and zip distributions. |
| **Favicon Cache** | LRU (256 entries) + faulted-task cleanup | Bounds memory; allows retries on transient download failures. |
| **Tab Tracking** | Per-window `_tabLastActive` + cleanup on close/sleep | Prevents memory leaks from discarded tabs. |

---

## 7. Command-Line Arguments

| Argument | Effect |
| :--- | :--- |
| `--restore-tabs <url1|url2|…>` | Restores specific tabs (used by external launchers). |
| `--no-update-check` | Skips background GitHub update check on startup. |
| `APOLION_DEBUG=1` (env) | Enables `DEBUG` level in `debug.log`. |