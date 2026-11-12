using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ThreadingTimer = System.Threading.Timer;

namespace Apolion
{
    /// <summary>
    /// Central, crash-safe logging facility. All writes are best-effort: a logging
    /// failure must never take the browser down or throw into a CEF callback.
    /// Uses a buffered StreamWriter for performance, with periodic flushes.
    /// </summary>
    internal static class AppLogger
    {
        private static readonly object Sync = new();
        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private static string? _logDir;
        private static string? _debugLogPath;
        private static string? _cefLogPath;

        private const long MaxDebugLogBytes = 2L * 1024L * 1024L; // 2 MB
        private static DateTime _lastRotationCheck = DateTime.MinValue;

        // Buffered writer for debug log
        private static StreamWriter? _debugWriter;
        private static ThreadingTimer? _flushTimer;
        private const int FlushIntervalMs = 2000; // Flush every 2 seconds

        /// <summary>Set APOLION_DEBUG=1 to include verbose DEBUG lines.</summary>
        public static bool Verbose { get; } =
            string.Equals(Environment.GetEnvironmentVariable("APOLION_DEBUG"), "1", StringComparison.OrdinalIgnoreCase);

        /// <summary>Ensures log directory and paths are initialized (lazy, thread-safe).</summary>
        private static void EnsureInitialized()
        {
            if (_logDir != null) return;

            lock (Sync)
            {
                if (_logDir != null) return;

                _logDir = ResolveLogDirectory();
                _debugLogPath = Path.Combine(_logDir, "debug.log");
                _cefLogPath = Path.Combine(_logDir, "cef.log");

                try
                {
                    Directory.CreateDirectory(_logDir);
                    // Keep the previous run's log as debug.old.log so a crash can be
                    // compared with the run that led up to it.
                    Rotate(force: true);
                    RotateCefLogIfNeeded();

                    // Open buffered writer for debug log
                    _debugWriter = new StreamWriter(_debugLogPath, append: true, Utf8, bufferSize: 4096)
                    {
                        AutoFlush = false // We'll flush manually
                    };

                    // Start periodic flush timer
                    _flushTimer = new ThreadingTimer(_ => FlushDebugLog(), null, FlushIntervalMs, FlushIntervalMs);
                }
                catch
                {
                    // Logging is optional; never block startup because of it.
                }
            }
        }

        public static string LogDirectory
        {
            get
            {
                EnsureInitialized();
                return _logDir!;
            }
        }

        public static string DebugLog
        {
            get
            {
                EnsureInitialized();
                return _debugLogPath!;
            }
        }

        public static string CefLog
        {
            get
            {
                EnsureInitialized();
                return _cefLogPath!;
            }
        }

        public static void Debug(string message)
        {
            if (Verbose) Write("DEBUG", message, null);
        }

        public static void Info(string message) => Write("INFO", message, null);
        public static void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);
        public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);
        public static void Exception(string context, Exception exception) => Write("ERROR", context, exception);

        private static void Write(string level, string message, Exception? exception)
        {
            try
            {
                var builder = new StringBuilder(256);
                builder.Append('[')
                       .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                       .Append("] [")
                       .Append(level.PadRight(5))
                       .Append("] [T")
                       .Append(Environment.CurrentManagedThreadId)
                       .Append("] ")
                       .Append(message);

                if (exception != null)
                {
                    builder.AppendLine();
                    builder.Append(exception);
                }

                string line = builder.ToString();

                lock (Sync)
                {
                    Rotate(force: false);
                    
                    // Write to buffered stream
                    _debugWriter?.WriteLine(line);
                }

                System.Diagnostics.Debug.WriteLine(line);
            }
            catch
            {
                // Swallow: a broken disk or antivirus lock must not crash the app.
            }
        }

        private static void FlushDebugLog()
        {
            try
            {
                lock (Sync)
                {
                    _debugWriter?.Flush();
                }
            }
            catch
            {
                // Ignore flush failures.
            }
        }

        private static void Rotate(bool force)
        {
            try
            {
                if (!File.Exists(_debugLogPath!)) return;

                if (!force)
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - _lastRotationCheck).TotalSeconds < 30) return;
                    _lastRotationCheck = now;

                    if (new FileInfo(_debugLogPath!).Length < MaxDebugLogBytes) return;
                }

                // Close current writer before rotation
                try { _debugWriter?.Flush(); _debugWriter?.Dispose(); } catch { }
                _debugWriter = null;

                string oldPath = Path.Combine(_logDir!, "debug.old.log");
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(_debugLogPath!, oldPath);

                // Reopen writer for new log file
                _debugWriter = new StreamWriter(_debugLogPath!, append: true, Utf8, bufferSize: 4096)
                {
                    AutoFlush = false
                };
            }
            catch
            {
                // Ignore rotation failures.
            }
        }

        private static void RotateCefLogIfNeeded()
        {
            try
            {
                string cefLog = Path.Combine(_logDir!, "cef.log");
                const long maxBytes = 8L * 1024L * 1024L;
                if (!File.Exists(cefLog) || new FileInfo(cefLog).Length < maxBytes) return;

                string oldPath = Path.Combine(_logDir!, "cef.old.log");
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(cefLog, oldPath);
            }
            catch
            {
                // Ignore.
            }
        }

        /// <summary>
        /// Writes a self-contained crash report (exception, environment, recent log
        /// tail) and returns the full path of the report file.
        /// </summary>
        public static string WriteCrashReport(Exception exception, string source, string? extra = null)
        {
            string reportPath;

            try
            {
                System.IO.Directory.CreateDirectory(LogDirectory);
                reportPath = Path.Combine(
                    LogDirectory,
                    $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");

                var builder = new StringBuilder(8192);
                builder.AppendLine("Apolion crash report");
                builder.AppendLine("===========================");
                builder.AppendLine($"Time        : {DateTime.Now:O}");
                builder.AppendLine($"Source      : {source}");
                builder.AppendLine($"Version     : {GetAppVersion()}");
                builder.AppendLine($"OS          : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
                builder.AppendLine($"Runtime     : {RuntimeInformation.FrameworkDescription}");
                builder.AppendLine($"Process     : {Environment.ProcessId} ({(Environment.Is64BitProcess ? "64-bit" : "32-bit")})");
                builder.AppendLine($"Working set : {Environment.WorkingSet / (1024 * 1024)} MB");
                builder.AppendLine($"Log folder  : {LogDirectory}");
                builder.AppendLine();

                builder.AppendLine("Exception:");
                builder.AppendLine("----------");
                builder.AppendLine(exception.ToString());
                builder.AppendLine();

                if (!string.IsNullOrWhiteSpace(extra))
                {
                    builder.AppendLine("Context:");
                    builder.AppendLine("--------");
                    builder.AppendLine(extra);
                    builder.AppendLine();
                }

                builder.AppendLine("Recent debug log:");
                builder.AppendLine("-----------------");
                builder.AppendLine(ReadLogTail(250));

                File.WriteAllText(reportPath, builder.ToString(), Utf8);
            }
            catch
            {
                reportPath = DebugLog;
            }

            Error($"[CRASH] {source}: {exception.GetType().Name}: {exception.Message} (report: {reportPath})");
            return reportPath;
        }

        public static string GetAppVersion()
        {
            try
            {
                var assembly = Assembly.GetEntryAssembly();
                string? informationalVersion = assembly?
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion;

                if (!string.IsNullOrWhiteSpace(informationalVersion))
                {
                    int metadataIndex = informationalVersion.IndexOf('+');
                    return metadataIndex >= 0
                        ? informationalVersion[..metadataIndex]
                        : informationalVersion;
                }

                return assembly?.GetName().Version?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private static string ReadLogTail(int maxLines)
        {
            try
            {
                if (!File.Exists(DebugLog)) return "(no debug log)";

                using var stream = new FileStream(
                    DebugLog, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Utf8);

                var lines = new LinkedList<string>();
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.AddLast(line);
                    if (lines.Count > maxLines) lines.RemoveFirst();
                }

                return lines.Count == 0 ? "(empty)" : string.Join(Environment.NewLine, lines);
            }
            catch (Exception ex)
            {
                return $"(failed to read debug log: {ex.Message})";
            }
        }

        private static string ResolveLogDirectory()
        {
            foreach (string root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         AppDomain.CurrentDomain.BaseDirectory
                     })
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    string dir = Path.Combine(root, "Apolion", "Logs");
                    System.IO.Directory.CreateDirectory(dir);
                    return dir;
                }
                catch
                {
                    // Try the next candidate.
                }
            }

            return Path.Combine(Path.GetTempPath(), "Apolion", "Logs");
        }
    }
}

