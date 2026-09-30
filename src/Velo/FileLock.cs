using System.Runtime.InteropServices;

namespace Velo;

internal static class FileLock
{
    private const int LockExclusive = 0x2;
    private const int LockNonBlocking = 0x4;

    public static FileStream? TryOpenExclusive(string path)
    {
        try { return Open(path); }
        catch (IOException) { return null; }
    }

    public static FileStream OpenExclusive(string path)
    {
        while (true)
        {
            try { return Open(path); }
            catch (IOException) { Thread.Sleep(1); }
        }
    }

    private static FileStream Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                stream.Lock(0, 1);
            }
            else if (flock(
                stream.SafeFileHandle.DangerousGetHandle(),
                LockExclusive | LockNonBlocking) != 0)
            {
                throw new IOException($"Could not acquire lock: {path}");
            }
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(nint fd, int operation);
}
