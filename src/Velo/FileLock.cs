using System.Runtime.InteropServices;

namespace Velo;

internal static partial class FileLock
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
        // Lock holds are short, but back off so a waiter does not spin at a fixed 1 ms.
        var delay = 1;
        while (true)
        {
            try { return Open(path); }
            catch (IOException)
            {
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 20);
            }
        }
    }

    public static async Task<FileStream> OpenExclusiveAsync(string path)
    {
        var delay = 1;
        while (true)
        {
            try { return Open(path); }
            catch (IOException)
            {
                await Task.Delay(delay);
                delay = Math.Min(delay * 2, 20);
            }
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

    [LibraryImport("libc", SetLastError = true)]
    private static partial int flock(nint fd, int operation);
}
