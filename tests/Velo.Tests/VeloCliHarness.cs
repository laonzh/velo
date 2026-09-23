using System.Diagnostics;

namespace Velo.Tests;

internal sealed class VeloCliHarness : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(10);
    private readonly string _repositoryRoot;
    private readonly string _configuration;
    private readonly string _veloAssembly;
    private readonly string _fakeBin;
    private readonly HashSet<int> _observedProcessIds = [];

    public VeloCliHarness()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Velo process tests currently require Windows.");

        _repositoryRoot = FindRepositoryRoot();
        _configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Cannot determine the test configuration.");
        _veloAssembly = Path.Combine(
            _repositoryRoot,
            "src",
            "Velo",
            "bin",
            _configuration,
            "net10.0",
            "velo.dll");
        if (!File.Exists(_veloAssembly))
            throw new FileNotFoundException("The Velo build output was not found.", _veloAssembly);

        RootPath = Path.Combine(
            Path.GetTempPath(),
            "Velo.Tests",
            Guid.NewGuid().ToString("N"));
        VeloHome = Path.Combine(RootPath, "home");
        _fakeBin = Path.Combine(RootPath, "fake-bin");
        Directory.CreateDirectory(VeloHome);
        Directory.CreateDirectory(_fakeBin);
        InstallFakeCodex();
    }

    public string RootPath { get; }
    public string VeloHome { get; }
    public string VeloLogPath => HomePath("velo.log");
    public IReadOnlyCollection<int> ObservedProcessIds => _observedProcessIds;

    public string HomePath(string name) => Path.Combine(VeloHome, name);

    public string TaskPath(string state, string id) =>
        Path.Combine(VeloHome, "tasks", state, id + ".json");

    public Task<CommandResult> RunAsync(params string[] arguments) =>
        RunAsync(_repositoryRoot, arguments);

    public async Task<CommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        using var process = StartProcess(
            arguments,
            redirectOutput: true,
            workingDirectory);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await WaitForExitAsync(process, CommandTimeout);
        var result = new CommandResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"velo {string.Join(' ', arguments)} exited with {result.ExitCode}." +
                $"{Environment.NewLine}stdout: {result.StandardOutput}" +
                $"{Environment.NewLine}stderr: {result.StandardError}");
        }
        return result;
    }

    public async Task StartWorkerAsync(
        int concurrency = 1,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
        using var process = StartProcess(
            [
                "start",
                "--concurrency", concurrency.ToString(),
                "--timeout", effectiveTimeout.ToString("c")
            ],
            redirectOutput: false);
        await WaitForExitAsync(process, CommandTimeout);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"velo start exited with {process.ExitCode}.");

        await WaitForFileAsync(HomePath("velo.pid"));
        _observedProcessIds.Add(ReadPid(HomePath("velo.pid")));
    }

    public async Task StopWorkerAsync()
    {
        var pidFile = HomePath("velo.pid");
        int? workerPid = File.Exists(pidFile) ? ReadPid(pidFile) : null;
        await RunAsync("stop");
        if (workerPid is not null)
            await WaitForProcessExitAsync(workerPid.Value);
    }

    public async Task<int> WaitForPidAsync(string relativePath)
    {
        var path = HomePath(relativePath);
        var pid = 0;
        await WaitUntilAsync(
            () => TryReadPid(path, out pid),
            $"valid PID in file: {path}",
            StateTimeout);
        _observedProcessIds.Add(pid);
        return pid;
    }

    public Task WaitForFileAsync(string path) => WaitUntilAsync(
        () => File.Exists(path),
        $"file to exist: {path}",
        StateTimeout);

    public Task WaitForProcessExitAsync(int processId) => WaitUntilAsync(
        () => !IsProcessRunning(processId),
        $"process {processId} to exit",
        StateTimeout);

    public void UseCmdCodexShim()
    {
        File.Delete(Path.Combine(_fakeBin, "codex.exe"));
        File.WriteAllText(
            Path.Combine(_fakeBin, "codex.cmd"),
            "@echo off\r\n\"%~dp0Velo.FakeCodex.exe\" %*\r\n",
            System.Text.Encoding.ASCII);
    }
    public Process StartFakeCodexProcess(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(_fakeBin, "Velo.FakeCodex.exe"),
            WorkingDirectory = RootPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["VELO_HOME"] = VeloHome;

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start fake Codex.");
        _observedProcessIds.Add(process.Id);
        return process;
    }
    public async ValueTask DisposeAsync()
    {
        var pidFile = HomePath("velo.pid");
        if (File.Exists(pidFile))
        {
            try { await StopWorkerAsync(); }
            catch { KillProcessTree(ReadPidOrDefault(pidFile)); }
        }

        foreach (var pid in _observedProcessIds)
            KillProcessTree(pid);

        foreach (var pid in _observedProcessIds)
        {
            try { await WaitForProcessExitAsync(pid); }
            catch { }
        }

        DeleteDirectoryWithRetry(RootPath);
    }

    private Process StartProcess(
        IReadOnlyList<string> arguments,
        bool redirectOutput,
        string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory ?? _repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput
        };
        startInfo.ArgumentList.Add(_veloAssembly);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["VELO_HOME"] = VeloHome;
        startInfo.Environment["PATH"] =
            _fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Velo.");
    }

    private void InstallFakeCodex()
    {
        var sourceDirectory = Path.Combine(
            _repositoryRoot,
            "tests",
            "Velo.FakeCodex",
            "bin",
            _configuration,
            "net10.0");
        var appHost = Path.Combine(sourceDirectory, "Velo.FakeCodex.exe");
        if (!File.Exists(appHost))
            throw new FileNotFoundException("The fake Codex apphost was not found.", appHost);

        foreach (var source in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(source, Path.Combine(_fakeBin, Path.GetFileName(source)));
        }
        File.Copy(appHost, Path.Combine(_fakeBin, "codex.exe"));
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
            }
            throw new TimeoutException($"Process {process.Id} did not exit within {timeout}.");
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        string description,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out waiting for {description}.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Velo.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Cannot find the Velo repository root.");
    }

    private static int ReadPid(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return int.TryParse(reader.ReadToEnd(), out var pid) && pid > 0
            ? pid
            : throw new InvalidDataException($"Invalid PID file: {path}");
    }

    private static bool TryReadPid(string path, out int pid)
    {
        try
        {
            pid = ReadPid(path);
            return true;
        }
        catch (IOException)
        {
            pid = 0;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            pid = 0;
            return false;
        }
        catch (InvalidDataException)
        {
            pid = 0;
            return false;
        }
    }

    private static int ReadPidOrDefault(string path)
    {
        try { return ReadPid(path); }
        catch { return 0; }
    }

    private static bool IsProcessRunning(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void KillProcessTree(int processId)
    {
        if (processId <= 0) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        if (!Directory.Exists(path)) return;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Thread.Sleep(100);
            }
        }
    }
}

internal sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);




