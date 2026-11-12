using System;
using System.Threading;
using System.Windows.Threading;

namespace Apolion
{
    /// <summary>
    /// Detects UI-thread stalls. A background thread watches a heartbeat updated
    /// by a DispatcherTimer; if the UI thread stops pumping for more than a few
    /// seconds the stall (and its recovery) is written to the debug log. This
    /// turns "it froze randomly" into a timestamped event that can be correlated
    /// with the rest of the log.
    /// </summary>
    internal static class UiWatchdog
    {
        private const int StallThresholdMs = 3000;

        // Power efficiency: heartbeat/check interval (increased from 500ms/1s to 5s)
        // Only enabled when APOLION_DEBUG=1 to avoid CPU wakeups in release
        private const int HeartbeatIntervalMs = 5000;
        private const int CheckIntervalMs = 5000;

        private static long _heartbeat = Environment.TickCount64;
        private static Thread? _thread;
        private static volatile bool _running;
        private static readonly bool _enabled;

        static UiWatchdog()
        {
            _enabled = string.Equals(Environment.GetEnvironmentVariable("APOLION_DEBUG"), "1", StringComparison.OrdinalIgnoreCase);
        }

        public static void Start(Dispatcher dispatcher)
        {
            if (!_enabled)
            {
                AppLogger.Debug("UI watchdog disabled (set APOLION_DEBUG=1 to enable)");
                return;
            }

            if (_running) return;
            _running = true;
            Volatile.Write(ref _heartbeat, Environment.TickCount64);

            var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(HeartbeatIntervalMs)
            };
            timer.Tick += (_, _) => Volatile.Write(ref _heartbeat, Environment.TickCount64);
            timer.Start();

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "Apolion UI Watchdog"
            };
            _thread.Start();

            AppLogger.Info($"UI watchdog started (heartbeat={HeartbeatIntervalMs}ms, check={CheckIntervalMs}ms)");
        }

        public static void Stop()
        {
            if (!_enabled) return;

            _running = false;
            if (_thread != null && !_thread.Join(TimeSpan.FromSeconds(3)))
            {
                AppLogger.Warn("UI watchdog thread did not exit within 3 seconds");
            }
            _thread = null;
        }

        private static void Loop()
        {
            bool stalled = false;
            long stallStarted = 0;

            while (_running)
            {
                try
                {
                    Thread.Sleep(CheckIntervalMs);

                    long now = Environment.TickCount64;
                    long age = now - Volatile.Read(ref _heartbeat);

                    if (age > StallThresholdMs)
                    {
                        if (!stalled)
                        {
                            stalled = true;
                            stallStarted = now - age;
                            AppLogger.Warn($"[WATCHDOG] UI thread unresponsive for {age} ms");
                        }
                    }
                    else if (stalled)
                    {
                        stalled = false;
                        AppLogger.Info($"[WATCHDOG] UI thread recovered after {now - stallStarted} ms");
                    }
                }
                catch
                {
                    // Keep the watchdog alive no matter what.
                }
            }
        }
    }
}

