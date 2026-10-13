using System.Runtime.InteropServices;

namespace Velo;

// Process.Start passes bInheritHandles=TRUE, so an inheritable stdio pipe of this process
// is also inherited by the child. The caller then never sees EOF until the child exits.
internal static partial class StdHandles
{
    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;
    private const uint HandleFlagInherit = 0x1;

    public static void MakeNonInheritable()
    {
        if (!OperatingSystem.IsWindows()) return;

        foreach (var id in new[] { StdInput, StdOutput, StdError })
        {
            var handle = GetStdHandle(id);
            if (handle is 0 or -1) continue;
            SetHandleInformation(handle, HandleFlagInherit, 0);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint hObject, uint dwMask, uint dwFlags);
}
