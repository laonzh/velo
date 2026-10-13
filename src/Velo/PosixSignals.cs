using System.Runtime.InteropServices;

namespace Velo;

internal static partial class PosixSignals
{
    // SIGHUP and SIG_IGN have these values on Linux and macOS.
    private const int SigHup = 1;
    private const nint SigIgn = 1;
    private const nint SigErr = -1;

    // An ignored signal stays ignored across exec, so the children a process starts inherit it.
    public static bool IgnoreHangup() => signal(SigHup, SigIgn) != SigErr;

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint signal(int signum, nint handler);
}
