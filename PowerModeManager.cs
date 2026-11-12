using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Apolion
{
    /// <summary>
    /// Monitors system power state (AC vs battery) and notifies components
    /// to adjust their behavior for power efficiency.
    /// </summary>
    internal static class PowerModeManager
    {
        private static DispatcherTimer? _pollTimer;
        private static bool _lastKnownOnBattery = false;
        private static readonly object _callbacksLock = new();
        private static readonly System.Collections.Generic.List<Action<bool>> _callbacks = new();

        // Poll interval for power state changes (Windows events can be unreliable)
        private const int PollIntervalSeconds = 30;

        public static bool IsOnBattery { get; private set; } = false;

        public static void Start(Dispatcher dispatcher)
        {
            if (_pollTimer != null) return;

            // Initial check
            UpdatePowerState();

            _pollTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(PollIntervalSeconds)
            };
            _pollTimer.Tick += (_, _) => UpdatePowerState();
            _pollTimer.Start();

            // Also listen to system power mode events
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            AppLogger.Info($"PowerModeManager started (poll={PollIntervalSeconds}s, onBattery={IsOnBattery})");
        }

        public static void Stop()
        {
            if (_pollTimer == null) return;

            try { _pollTimer.Stop(); } catch { }
            _pollTimer = null;

            SystemEvents.PowerModeChanged -= OnPowerModeChanged;

            AppLogger.Info("PowerModeManager stopped");
        }

        /// <summary>
        /// Register a callback to be notified when power state changes.
        /// </summary>
        public static void RegisterCallback(Action<bool> onPowerStateChanged)
        {
            lock (_callbacksLock)
            {
                _callbacks.Add(onPowerStateChanged);
            }
        }

        private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            // PowerModeChanged fires for suspend/resume, but not always for AC/battery transitions
            // We'll re-check the actual state
            UpdatePowerState();
        }

        private static void UpdatePowerState()
        {
            bool onBattery = GetIsOnBattery();
            if (onBattery != _lastKnownOnBattery)
            {
                _lastKnownOnBattery = onBattery;
                IsOnBattery = onBattery;
                AppLogger.Info($"Power state changed: {(onBattery ? "Battery" : "AC")}");

                // Notify all registered callbacks
                lock (_callbacksLock)
                {
                    foreach (var callback in _callbacks)
                    {
                        try { callback(onBattery); } catch { }
                    }
                }
            }
        }

        private static bool GetIsOnBattery()
        {
            try
            {
                if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
                {
                    // ACLineStatus: 0 = on battery, 1 = on AC, 255 = unknown
                    return status.ACLineStatus == 0;
                }
            }
            catch { }
            return false; // Assume AC if we can't determine
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;        // 0=offline, 1=online, 255=unknown
            public byte BatteryFlag;         // battery status flags
            public byte BatteryLifePercent;  // 0-100, 255=unknown
            public byte Reserved1;
            public int BatteryLifeTime;      // seconds remaining, -1=unknown
            public int BatteryFullLifeTime;  // seconds at full charge, -1=unknown
        }
    }
}
