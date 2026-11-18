using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Apolion
{
    /// <summary>
    /// Enumerates direct child processes without WMI. Used so shutdown only
    /// terminates CEF helpers that belong to this Apolion process instead of
    /// every process named "Apolion Helper" on the machine (which could belong
    /// to another user session or another CefSharp application).
    /// </summary>
    internal static class ProcessTree
    {
        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        public static HashSet<int> GetDirectChildProcessIds(int parentProcessId)
        {
            var result = new HashSet<int>();
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                return result;
            }

            try
            {
                var entry = new PROCESSENTRY32W
                {
                    dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>()
                };

                if (!Process32FirstW(snapshot, ref entry))
                {
                    return result;
                }

                do
                {
                    if (entry.th32ParentProcessID == (uint)parentProcessId && entry.th32ProcessID != 0)
                    {
                        result.Add((int)entry.th32ProcessID);
                    }
                }
                while (Process32NextW(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return result;
        }
    }
}
