using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Velo;

// Codex runs in a job object that kills its processes once the last handle to the job closes.
// That handle belongs to the velo process, so a forced kill of velo also stops Codex.
internal static partial class ProcessJob
{
    private const int ExtendedLimitInformationClass = 9;
    private const uint KillOnJobClose = 0x2000;

    private static readonly Lazy<nint> Shared = new(Create);

    // False when the process could not be placed in the job. The run still proceeds, but
    // Codex would survive a forced kill of velo.
    public static bool TryAssign(Process process)
    {
        var job = Shared.Value;
        return job != 0 && AssignProcessToJobObject(job, process.Handle);
    }

    private static nint Create()
    {
        if (!OperatingSystem.IsWindows()) return 0;

        var job = CreateJobObjectW(0, 0);
        if (job == 0) return 0;

        var limits = new ExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = KillOnJobClose;
        if (SetInformationJobObject(job, ExtendedLimitInformationClass, ref limits, (uint)Unsafe.SizeOf<ExtendedLimitInformation>()))
            return job;

        CloseHandle(job);
        return 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateJobObjectW(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
