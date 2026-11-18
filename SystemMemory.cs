using System;
using System.Runtime.InteropServices;

namespace Apolion
{
    /// <summary>
    /// Lightweight system-memory probe used to make tab discarding react to
    /// real memory pressure instead of relying only on an idle timer.
    /// </summary>
    internal static class SystemMemory
    {
        private const long LowMemoryThresholdBytes = 1536L * 1024 * 1024;
        private const long CriticalMemoryThresholdBytes = 768L * 1024 * 1024;

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public static bool TryGet(out long availableBytes, out long totalBytes)
        {
            availableBytes = 0;
            totalBytes = 0;

            try
            {
                var status = new MEMORYSTATUSEX
                {
                    dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
                };

                if (!GlobalMemoryStatusEx(ref status))
                {
                    return false;
                }

                availableBytes = (long)Math.Min(status.ullAvailPhys, (ulong)long.MaxValue);
                totalBytes = (long)Math.Min(status.ullTotalPhys, (ulong)long.MaxValue);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Available physical RAM in bytes. Returns <see cref="long.MaxValue"/>
        /// when the value cannot be read, so callers treat that as "no pressure".
        /// </summary>
        public static long AvailablePhysicalBytes =>
            TryGet(out long available, out _) ? available : long.MaxValue;

        public static bool IsLowMemory => AvailablePhysicalBytes < LowMemoryThresholdBytes;

        public static bool IsCriticalMemory => AvailablePhysicalBytes < CriticalMemoryThresholdBytes;
    }
}
