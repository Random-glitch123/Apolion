using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Apolion
{
    /// <summary>
    /// Enforces a single Apolion browser process per Windows user and forwards
    /// later launches to the running instance.
    ///
    /// CefSharp 154 cannot share one CEF <c>CachePath</c> between two processes
    /// (the second <c>Cef.Initialize</c> crashes), so running two copies is not
    /// an option. Forwarding has a second benefit: while Apolion is already
    /// running (including in the tray with warm start enabled), a new launch is
    /// effectively instant — it only asks the existing process to open a window
    /// or tab instead of initializing another CEF instance.
    /// </summary>
    internal static class AppInstance
    {
        private const string MutexPrefix = @"Local\Apolion.Browser.";
        private const string PipePrefix = "Apolion.Browser.";

        private static Mutex? _mutex;
        private static CancellationTokenSource? _serverCts;
        private static Task? _serverTask;
        private static string? _instanceId;

        private static string InstanceId => _instanceId ??= GetInstanceId();

        /// <summary>
        /// Creates the per-user mutex. Returns false when another Apolion
        /// process already owns it.
        /// </summary>
        public static bool TryAcquirePrimaryInstance()
        {
            try
            {
                _mutex = new Mutex(initiallyOwned: true, MutexPrefix + InstanceId, out bool createdNew);
                if (createdNew)
                {
                    AppLogger.Info($"[SINGLE-INSTANCE] Primary instance acquired (id={InstanceId})");
                    return true;
                }

                _mutex.Dispose();
                _mutex = null;
                return false;
            }
            catch (Exception ex)
            {
                // A broken ACL or an unsupported platform should not make the
                // browser unusable. Continue without the guard in that case.
                AppLogger.Warn("[SINGLE-INSTANCE] Could not create mutex; continuing without single-instance guard", ex);
                return true;
            }
        }

        /// <summary>
        /// Sends an "open" request to the running instance. Returns false when
        /// the process that owns the mutex is not reachable (hung or still
        /// starting up).
        /// </summary>
        public static bool NotifyExistingInstance(string? url)
        {
            string pipeName = PipePrefix + InstanceId;
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = new NamedPipeClientStream(
                        ".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                    client.Connect(200);

                    using var writer = new StreamWriter(client, new UTF8Encoding(false))
                    {
                        AutoFlush = true
                    };
                    writer.WriteLine(url ?? "");
                    AppLogger.Info($"[SINGLE-INSTANCE] Forwarded open request (url={url ?? "<none>"})");
                    return true;
                }
                catch (TimeoutException)
                {
                    // Server not listening yet; retry until the deadline.
                }
                catch (IOException ex)
                {
                    AppLogger.Debug($"[SINGLE-INSTANCE] Pipe not ready: {ex.Message}");
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"[SINGLE-INSTANCE] Forward attempt failed: {ex.Message}");
                }

                Thread.Sleep(100);
            }

            AppLogger.Warn("[SINGLE-INSTANCE] Running instance did not answer the open request");
            return false;
        }

        /// <summary>
        /// Starts the named-pipe listener. <paramref name="onOpen"/> is invoked
        /// on the WPF UI thread with the requested URL, or null when the caller
        /// only wants to activate/open Apolion.
        /// </summary>
        public static void StartServer(Dispatcher dispatcher, Action<string?> onOpen)
        {
            if (_serverCts != null) return;

            _serverCts = new CancellationTokenSource();
            CancellationToken token = _serverCts.Token;
            string pipeName = PipePrefix + InstanceId;

            _serverTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            pipeName,
                            PipeDirection.In,
                            maxNumberOfServerInstances: 1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                        using var reader = new StreamReader(
                            server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                            bufferSize: 1024, leaveOpen: false);
                        string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                        string? url = string.IsNullOrWhiteSpace(line) ? null : line.Trim();

                        try
                        {
                            _ = dispatcher.BeginInvoke(new Action(() =>
                            {
                                try { onOpen(url); }
                                catch (Exception ex) { AppLogger.Exception("[SINGLE-INSTANCE] Handling open request", ex); }
                            }));
                        }
                        catch (InvalidOperationException)
                        {
                            // Dispatcher already shut down.
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("[SINGLE-INSTANCE] Pipe server error", ex);
                        try { await Task.Delay(250, token).ConfigureAwait(false); }
                        catch { break; }
                    }
                }
            }, token);
        }

        public static void StopServer()
        {
            CancellationTokenSource? cts = Interlocked.Exchange(ref _serverCts, null);
            if (cts == null) return;

            try { cts.Cancel(); } catch { }
            try { _serverTask?.Wait(500); } catch { }
            try { cts.Dispose(); } catch { }
            _serverTask = null;
        }

        /// <summary>Releases the single-instance mutex at process exit.</summary>
        public static void Release()
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            _mutex = null;
        }

        private static string GetInstanceId()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    string? sid = identity.User?.Value;
                    if (!string.IsNullOrWhiteSpace(sid))
                    {
                        return sid.Replace('\\', '_');
                    }
                }
                catch
                {
                    // Fall through to the user-name based id.
                }
            }

            return Environment.UserName.Replace('\\', '_');
        }
    }
}
