using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BoothDesktop.Services;

/// <summary>
/// Windows job object: when BoothDesktop exits, the OS also terminates child sony_bridge.exe.
/// </summary>
internal static class BridgeParentJob
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private static IntPtr _jobHandle = IntPtr.Zero;
    private static readonly object JobLock = new();

    public static void TryAssign(Process? child)
    {
        if (child == null || child.HasExited) return;
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            child.Refresh();
            EnsureJob();
            if (_jobHandle == IntPtr.Zero) return;
            if (!AssignProcessToJobObject(_jobHandle, child.Handle))
                RuntimeLog.Warn("Bridge", $"AssignProcessToJobObject failed win32={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Bridge", $"BridgeParentJob.TryAssign: {ex.Message}");
        }
    }

    private static void EnsureJob()
    {
        lock (JobLock)
        {
            if (_jobHandle != IntPtr.Zero) return;

            _jobHandle = CreateJobObject(IntPtr.Zero, @"Local\BoothDesktop.BridgeJob");
            if (_jobHandle == IntPtr.Zero)
            {
                RuntimeLog.Warn("Bridge", $"CreateJobObject failed win32={Marshal.GetLastWin32Error()}");
                return;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, ptr,
                        (uint)length))
                    RuntimeLog.Warn("Bridge",
                        $"SetInformationJobObject KILL_ON_JOB_CLOSE failed win32={Marshal.GetLastWin32Error()}");
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, uint jobObjectInfoClass, IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public long PerProcessMemoryLimit;
        public long PerJobMemoryLimit;
        public long PeakProcessMemoryLimit;
        public long PeakJobMemoryLimit;
        public uint LimitFlags;
        public uint Padding;
        public long Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public IntPtr ProcessMemoryLimit;
        public IntPtr JobMemoryLimit;
        public IntPtr PeakProcessMemoryLimit;
        public IntPtr PeakJobMemoryLimit;
        public IntPtr JobMemoryLimitHigh;
    }
}
