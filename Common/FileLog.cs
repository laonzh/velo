using System.Text;
using Velo.Abstractions;

namespace Velo.Common;

public sealed class FileLog : ILogWriter
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    public string FilePath { get; }

    public FileLog(string filePath)
    {
        FilePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
    }

    public void Write(string tag, string message)
    {
        lock (_lock)
        {
            _writer.WriteLine($"[{DateTimeOffset.UtcNow:O}] [{tag}] {message}");
        }
    }

    public void WriteLine(string line)
    {
        lock (_lock)
        {
            _writer.WriteLine(line);
        }
    }

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
    public void Dispose() => _writer.Dispose();
}