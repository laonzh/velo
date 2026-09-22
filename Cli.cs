using System.Diagnostics;
using System.Text;

namespace Velo;

public sealed class Cli
{
    private readonly TaskStore _store = new();

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        VeloPaths.Initialize();
        return args[0] switch
        {
            "add" => await AddAsync(args[1..]),
            "list" or "ls" => List(args[1..]),
            "show" => Show(args[1..]),
            "cancel" => Cancel(args[1..]),
            "retry" => Retry(args[1..]),
            "log" or "logs" => await LogsAsync(args[1..]),
            "start" => await StartAsync(args[1..]),
            "run" => await RunWorkerAsync(args[1..]),
            "stop" => Stop(args[1..]),
            "status" => Status(args[1..]),
            _ => Error($"Unknown command: {args[0]}")
        };
    }

    private async Task<int> AddAsync(string[] args)
    {
        var worktree = args.Length > 0 && args[0] == "--worktree";
        if (worktree) args = args[1..];

        var title = string.Join(' ', args).Trim();
        if (title.Length == 0) return Usage("velo add [--worktree] <prompt>");

        if (!worktree)
        {
            var task = _store.Add(title, Environment.CurrentDirectory);
            Console.WriteLine(task.Id);
            return 0;
        }

        var id = TaskStore.NewId();
        var source = Environment.CurrentDirectory;
        var repository = Path.GetFullPath(
            (await RunGitAsync(source, "rev-parse", "--show-toplevel")).Trim());
        var relativePath = Path.GetRelativePath(repository, source);
        var worktreeRoot = Path.GetFullPath(Path.Combine(VeloPaths.Workspaces, id));
        await RunGitAsync(
            repository,
            "worktree", "add", "-b", $"velo/{id}", worktreeRoot, "HEAD");

        var workspace = relativePath == "."
            ? worktreeRoot
            : Path.Combine(worktreeRoot, relativePath);
        _store.Add(id, title, workspace);
        Console.WriteLine(id);
        return 0;
    }

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"git exited with code {process.ExitCode}."
                    : error.Trim());
        }
        return output;
    }

    private int List(string[] args)
    {
        if (args.Length != 0) return Usage("velo list");
        var tasks = _store.List();
        if (tasks.Count == 0)
        {
            Console.WriteLine("No tasks.");
            return 0;
        }

        Console.WriteLine($"{"ID",-23} {"STATE",-10} {"UPDATED (UTC)",-19} PROMPT");
        foreach (var task in tasks)
        {
            Console.WriteLine(
                $"{task.Id,-23} {VeloPaths.StateName(task.State),-10} " +
                $"{task.Item.UpdatedAtUtc:yyyy-MM-dd HH:mm:ss} {task.Item.Title}");
        }
        return 0;
    }

    private int Show(string[] args)
    {
        if (!TryGetId(args, "velo show <id>", out var id)) return 1;
        var task = _store.Get(id);
        if (task is null) return Error($"Task not found: {id}");

        Console.WriteLine($"Id: {task.Id}");
        Console.WriteLine($"State: {VeloPaths.StateName(task.State)}");
        Console.WriteLine($"Prompt: {task.Item.Title}");
        Console.WriteLine($"Workspace: {task.Item.WorkspacePath}");
        Console.WriteLine($"Created: {task.Item.CreatedAtUtc:O}");
        Console.WriteLine($"Updated: {task.Item.UpdatedAtUtc:O}");
        Console.WriteLine($"Log: {VeloPaths.LogFile(task.Id)}");
        if (!string.IsNullOrWhiteSpace(task.Item.LastError))
            Console.WriteLine($"Error: {task.Item.LastError}");
        return 0;
    }

    private int Cancel(string[] args)
    {
        if (!TryGetId(args, "velo cancel <id>", out var id)) return 1;
        var task = _store.Get(id);
        if (task is null) return Error($"Task not found: {id}");
        if (task.State is not (TaskState.Todo or TaskState.Running))
            return Error($"Task {id} cannot be cancelled from {VeloPaths.StateName(task.State)}.");
        if (!_store.Cancel(id)) return Error($"Failed to cancel task: {id}");
        VeloPaths.WriteLog("INFO", $"Task {id} cancelled.");
        Console.WriteLine($"Cancelled: {id}");
        return 0;
    }

    private int Retry(string[] args)
    {
        if (!TryGetId(args, "velo retry <id>", out var id)) return 1;
        var task = _store.Get(id);
        if (task is null) return Error($"Task not found: {id}");
        if (task.State is not (TaskState.Failed or TaskState.Cancelled))
            return Error($"Task {id} cannot be retried from {VeloPaths.StateName(task.State)}.");
        if (!_store.Retry(id)) return Error($"Failed to retry task: {id}");
        Console.WriteLine($"Queued: {id}");
        return 0;
    }

    private async Task<int> LogsAsync(string[] args)
    {
        string? id = null;
        var tail = false;
        foreach (var arg in args)
        {
            if (arg == "--tail")
            {
                if (tail) return Usage("velo logs [<id>] [--tail]");
                tail = true;
            }
            else if (arg.StartsWith('-') || id is not null)
            {
                return Usage("velo logs [<id>] [--tail]");
            }
            else
            {
                id = arg;
            }
        }

        if (id is not null && _store.Get(id) is null) return Error($"Task not found: {id}");
        var path = id is null ? VeloPaths.VeloLog : VeloPaths.LogFile(id);
        if (!File.Exists(path)) return Error($"Log not found: {path}");

        if (!tail)
        {
            using var stream = OpenLog(path);
            using var reader = new StreamReader(stream);
            Console.Write(await reader.ReadToEndAsync());
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += OnCancel;
        try
        {
            await TailAsync(path, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
        return 0;

        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cancellation.Cancel();
        }
    }

    private static async Task TailAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = OpenLog(path);
        using var reader = new StreamReader(stream);
        var lines = new Queue<string>(20);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (lines.Count == 20) lines.Dequeue();
            lines.Enqueue(line);
        }
        foreach (var line in lines) Console.WriteLine(line);

        while (true)
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
                Console.WriteLine(line);
            await Task.Delay(250, cancellationToken);
        }
    }

    private static FileStream OpenLog(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);

    private async Task<int> StartAsync(string[] args)
    {
        if (!TryParseWorkerOptions(args, out var concurrency, out var timeout)) return 1;
        if (GetRunningPid() > 0) return Error("Velo is already running.");

        try { File.Delete(VeloPaths.StopFile); } catch (IOException) { }
        var startInfo = CreateSelfStartInfo("run", concurrency, timeout);
        var process = Process.Start(startInfo);
        if (process is null) return Error("Failed to start Velo.");

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && !process.HasExited)
        {
            if (GetRunningPid() == process.Id)
            {
                Console.WriteLine($"Velo started with pid {process.Id}.");
                return 0;
            }
            await Task.Delay(50);
        }

        return Error(process.HasExited
            ? "Velo failed to start."
            : "Velo did not become ready in time.");
    }

    private async Task<int> RunWorkerAsync(string[] args)
    {
        if (!TryParseWorkerOptions(args, out var concurrency, out var timeout)) return 1;

        using var pidFile = AcquirePidFile();
        if (pidFile is null) return Error("Velo is already running.");
        File.Delete(VeloPaths.StopFile);

        using (var writer = new StreamWriter(pidFile, Encoding.ASCII, leaveOpen: true))
        {
            pidFile.SetLength(0);
            writer.Write(Environment.ProcessId);
            writer.Flush();
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += OnCancel;
        var stopMonitor = MonitorStopFileAsync(cancellation);
        try
        {
            await new Worker(_store, concurrency, timeout).RunAsync(cancellation.Token);
        }
        finally
        {
            cancellation.Cancel();
            await stopMonitor;
            Console.CancelKeyPress -= OnCancel;
            pidFile.Dispose();
            File.Delete(VeloPaths.PidFile);
            File.Delete(VeloPaths.StopFile);
        }
        return 0;

        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cancellation.Cancel();
        }
    }

    private static int Stop(string[] args)
    {
        if (args.Length != 0) return Usage("velo stop");
        var pid = GetRunningPid();
        if (pid <= 0)
        {
            Console.WriteLine("Velo is not running.");
            return 0;
        }

        File.WriteAllText(VeloPaths.StopFile, string.Empty);
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.WaitForExit(10_000)) process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        Console.WriteLine("Velo stopped.");
        return 0;
    }

    private static int Status(string[] args)
    {
        if (args.Length != 0) return Usage("velo status");
        var pid = GetRunningPid();
        Console.WriteLine(pid > 0 ? $"Velo is running with pid {pid}." : "Velo is not running.");
        return 0;
    }

    private static async Task MonitorStopFileAsync(CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (File.Exists(VeloPaths.StopFile))
                {
                    cancellation.Cancel();
                    return;
                }
                await Task.Delay(250, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static FileStream? AcquirePidFile()
    {
        try
        {
            return new FileStream(
                VeloPaths.PidFile,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.Read);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static int GetRunningPid()
    {
        if (!File.Exists(VeloPaths.PidFile)) return 0;
        try
        {
            using var stream = new FileStream(
                VeloPaths.PidFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            if (!int.TryParse(reader.ReadToEnd(), out var pid) || pid <= 0) return 0;
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? 0 : pid;
        }
        catch
        {
            return 0;
        }
    }

    private static ProcessStartInfo CreateSelfStartInfo(
        string command,
        int concurrency,
        TimeSpan timeout)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the Velo executable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);

        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add("--concurrency");
        startInfo.ArgumentList.Add(concurrency.ToString());
        startInfo.ArgumentList.Add("--timeout");
        startInfo.ArgumentList.Add(timeout.ToString("c"));
        return startInfo;
    }

    private static bool TryParseWorkerOptions(
        string[] args,
        out int concurrency,
        out TimeSpan timeout)
    {
        concurrency = 1;
        timeout = TimeSpan.FromMinutes(30);

        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length)
            {
                Error($"Missing value for {args[i]}.");
                return false;
            }

            var value = args[++i];
            switch (args[i - 1])
            {
                case "--concurrency":
                    if (!int.TryParse(value, out concurrency) || concurrency <= 0)
                    {
                        Error("Concurrency must be a positive integer.");
                        return false;
                    }
                    break;
                case "--timeout":
                    if (!TimeSpan.TryParse(value, out timeout) || timeout <= TimeSpan.Zero)
                    {
                        Error("Timeout must be a positive TimeSpan.");
                        return false;
                    }
                    break;
                default:
                    Error($"Unknown option: {args[i - 1]}");
                    return false;
            }
        }
        return true;
    }

    private static bool TryGetId(string[] args, string usage, out string id)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            id = string.Empty;
            Usage(usage);
            return false;
        }
        id = args[0];
        return true;
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }

    private static int Usage(string usage)
    {
        Console.Error.WriteLine($"Usage: {usage}");
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: velo <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  add [--worktree] <prompt>");
        Console.WriteLine("                     Add a task for the current or a new Git worktree workspace");
        Console.WriteLine("  list               List tasks");
        Console.WriteLine("  show <id>          Show task details");
        Console.WriteLine("  cancel <id>        Cancel a queued or running task");
        Console.WriteLine("  retry <id>         Retry a failed or cancelled task");
        Console.WriteLine("  logs [<id>]        Show Velo or task logs (--tail to follow)");
        Console.WriteLine("  start [options]    Start the background worker");
        Console.WriteLine("  stop               Stop the background worker");
        Console.WriteLine("  status             Show worker status");
        Console.WriteLine();
        Console.WriteLine("Start options:");
        Console.WriteLine("  --concurrency <n>  Maximum concurrent tasks (default: 1)");
        Console.WriteLine("  --timeout <span>   Timeout per task (default: 00:30:00)");
    }
}
