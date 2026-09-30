using System.Diagnostics;

namespace Velo.Tests;

[CollectionDefinition("Sequential", DisableParallelization = true)]
public sealed class SequentialCollection;

[Collection("Sequential")]
internal sealed class CliFixture : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _configuration;
    private readonly string _veloAssembly;
    private readonly string _fakeBin;

    public CliFixture()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("These process tests require Windows apphosts.");

        RepositoryRoot = FindRepositoryRoot();
        _configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Cannot determine test configuration.");
        _veloAssembly = Path.Combine(
            RepositoryRoot, "src", "Velo", "bin", _configuration, "net10.0", "velo.dll");
        if (!File.Exists(_veloAssembly))
            throw new FileNotFoundException("Velo build output was not found.", _veloAssembly);

        RootPath = Path.Combine(Path.GetTempPath(), "Velo.Tests", Guid.NewGuid().ToString("N"));
        VeloHome = Path.Combine(RootPath, "home");
        _fakeBin = Path.Combine(RootPath, "fake-bin");
        Directory.CreateDirectory(VeloHome);
        Directory.CreateDirectory(_fakeBin);
        InstallFakeCodex();
    }

    public string RepositoryRoot { get; }
    public string RootPath { get; }
    public string VeloHome { get; }
    public string FakeBin => _fakeBin;

    public string WorkPath(string id) => Path.Combine(VeloHome, "work", id + ".json");

    public string Workspace(string name = "workspace") =>
        Directory.CreateDirectory(Path.Combine(RootPath, name)).FullName;

    public async Task<CommandResult> RunAsync(
        string workingDirectory,
        params string[] arguments)
    {
        using var process = Start(workingDirectory, arguments);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await WaitForExitAsync(process);
        return new CommandResult(process.ExitCode, await output, await error);
    }

    public Process StartRun(
        string workingDirectory,
        int? concurrency = 2,
        string? veloHome = null) =>
        Start(workingDirectory, ["run"], concurrency, veloHome);

    public Task WaitForFileAsync(string path) => WaitUntilAsync(
        () => File.Exists(path),
        $"file {path}");

    public async Task<string> ReadAllTextAsync(string path)
    {
        var deadline = DateTime.UtcNow.Add(Timeout);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return await File.ReadAllTextAsync(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(25);
            }
        }

        throw new TimeoutException($"Timed out reading file {path}.");
    }

    public Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.Add(Timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return Task.CompletedTask;
            Thread.Sleep(25);
        }
        throw new TimeoutException($"Timed out waiting for {description}.");
    }

    public async Task WaitForExitAsync(Process process)
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Process {process.Id} did not exit.");
        }
    }

    private Process Start(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        int? concurrency = null,
        string? veloHome = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(_veloAssembly);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["VELO_HOME"] = veloHome ?? VeloHome;
        if (concurrency is not null)
            startInfo.Environment["VELO_CONCURRENCY"] = concurrency.Value.ToString();
        startInfo.Environment["PATH"] =
            _fakeBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Velo.");
    }

    private void InstallFakeCodex()
    {
        var source = Path.Combine(
            RepositoryRoot, "tests", "Velo.FakeCodex", "bin", _configuration, "net10.0");
        var appHost = Path.Combine(source, "Velo.FakeCodex.exe");
        if (!File.Exists(appHost))
            throw new FileNotFoundException("Fake Codex apphost was not found.", appHost);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(_fakeBin, Path.GetFileName(file)));
        File.Copy(appHost, Path.Combine(_fakeBin, "codex.exe"));
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
        throw new DirectoryNotFoundException("Cannot find the repository root.");
    }

    public async ValueTask DisposeAsync()
    {
        if (!Directory.Exists(RootPath)) return;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 49)
            {
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 49)
            {
                foreach (var file in Directory.EnumerateFiles(
                             RootPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
                await Task.Delay(100);
            }
        }
    }
}

internal sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
