using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcmCdbEditor.Infrastructure.Internal;

internal static class WindowsProcessJob
{
    private const uint KillOnJobClose = 0x00002000;
    private const int ExtendedLimitInformation = 9;

    public static SafeFileHandle Create()
    {
        SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        var limits = new ExtendedJobLimits
        {
            BasicLimits = new BasicJobLimits { LimitFlags = KillOnJobClose }
        };
        if (job.IsInvalid || !SetInformationJobObject(
                job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedJobLimits>()))
        {
            var failure = new Win32Exception(Marshal.GetLastWin32Error());
            job.Dispose();
            throw failure;
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicJobLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobIoCounters
    {
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedJobLimits
    {
        public BasicJobLimits BasicLimits;
        public JobIoCounters IoCounters;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job, int informationClass, ref ExtendedJobLimits information, int informationLength);
}
