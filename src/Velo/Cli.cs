using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;

namespace Velo;

public sealed class Cli
{
    private const string StartUsage = "velo start [--once] [-d] [--port N]";
    private const long MaxStartLogBytes = 1024 * 1024;
    // Set only for the background process that `start -d` launches.
    private const string DetachedVariable = "VELO_START_DETACHED";
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly Config _config;
    private readonly WorkQueue _queue;
    private readonly Workspace _workspace;
    private readonly AgentRunner _agent;
    private readonly WorkIntake _intake;
    private readonly WorkReview _review;

    public Cli() : this(Config.Load()) { }

    internal Cli(Config config)
    {
        _config = config;
        _queue = new WorkQueue(_config);
        _workspace = new Workspace(_config);
        _agent = new AgentRunner(_config);
        _intake = new WorkIntake(_queue, _workspace);
        _review = new WorkReview(_config, _queue, _workspace);
    }

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        _config.Initialize();
        return args[0] switch
        {
            "add" => await AddAsync(args[1..]),
            "list" => List(args[1..]),
            "start" => await StartAsync(args[1..]),
            "stop" => await StopAsync(args[1..]),
            "retry" => Retry(args[1..]),
            "cancel" => Cancel(args[1..]),
            "edit" => Edit(args[1..]),
            "delete" => Delete(args[1..]),
            "diff" => await DiffAsync(args[1..]),
            "accept" => await AcceptAsync(args[1..]),
            "discard" => await DiscardAsync(args[1..]),
            "logs" => Logs(args[1..]),
            _ => Error($"Unknown command: {args[0]}")
        };
    }

    private async Task<int> AddAsync(string[] args)
    {
        var worktree = args is ["--worktree", ..];
        if (worktree) args = args[1..];

        var escaped = args is ["--", ..];
        if (escaped) args = args[1..];
        if (!escaped && args is [var option, ..]
            && option.StartsWith("--", StringComparison.Ordinal))
            return Usage("velo add [--worktree] [--] <prompt>");

        var prompt = string.Join(' ', args).Trim();
        if (prompt.Length == 0)
            return Usage("velo add [--worktree] [--] <prompt>");

        var id = await _intake.AddAsync(prompt, Environment.CurrentDirectory, worktree);
        Console.WriteLine(id);
        return 0;
    }

    private int List(string[] args)
    {
        var state = args is [var value] && WorkStates.TryParse(value, out var parsed)
            ? parsed
            : (WorkState?)null;
        if (args.Length > 0 && state is null)
            return Usage("velo list [pending|running|succeeded|failed]");

        var work = _queue.List()
            .Where(item => state is null || item.State == state)
            .ToArray();
        if (work.Length == 0)
        {
            Console.WriteLine("No work.");
            return 0;
        }

        Console.WriteLine($"{"ID",-23} {"STATE",-9} {"CREATED (UTC)",-19} PROMPT");
        foreach (var item in work)
        {
            Console.WriteLine(
                $"{item.Id,-23} {item.State.Text(),-9} {item.CreatedAt:yyyy-MM-dd HH:mm:ss} {item.Summary()}");
        }
        return 0;
    }

    private async Task<int> StartAsync(string[] args)
    {
        if (ParseStart(args) is not { } options) return Usage(StartUsage);
        if (options.Detach) return Detach(options.Port);
        if (IsRunning(out var pid))
            return Error($"velo is already running (pid {pid}).");

        var background = Environment.GetEnvironmentVariable(DetachedVariable) is "1";
        Environment.SetEnvironmentVariable(DetachedVariable, null);
        if (background) RedirectOutputToStartLog();
        return await RunQueueAsync(options.Once, background, options.Port);
    }

    // Asks the running start to end, as Ctrl+C does: its Codex processes are stopped and running
    // work returns to pending. The request names the start's pid, so only that start acts on it.
    private async Task<int> StopAsync(string[] args)
    {
        if (args.Length != 0) return Usage("velo stop");
        if (!IsRunning(out var pid)) return Printed("No velo start is running.");
        if (pid == "unknown") return Error("velo start is still starting; try again in a moment.");

        File.WriteAllText(_config.StopRequest, pid);
        var deadline = DateTime.UtcNow.Add(StopTimeout);
        while (IsRunning(out _))
        {
            if (DateTime.UtcNow > deadline)
                return Error($"velo start (pid {pid}) is still running {StopTimeout.TotalSeconds} seconds after the stop request.");
            await Task.Delay(StopPollInterval);
        }
        return Printed($"Stopped velo start (pid {pid}).");
    }

    // --once drains the queue and exits, so it cannot be combined with a process that keeps running.
    private static StartOptions? ParseStart(string[] args)
    {
        var once = false;
        var detach = false;
        int? port = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--once") once = true;
            else if (args[i] == "-d") detach = true;
            else if (args[i] == "--port" && i + 1 < args.Length && TryParsePort(args[++i], out var value)) port = value;
            else return null;
        }
        return once && (detach || port is not null) ? null : new StartOptions(once, detach, port);
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, out port) && port is >= 0 and <= 65535;

    private sealed record StartOptions(bool Once, bool Detach, int? Port);

    private int Detach(int? port)
    {
        if (IsRunning(out var pid))
            return Error($"velo is already running (pid {pid}).");

        var self = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot find the velo executable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = self,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Redirected so the child does not inherit the caller's stdio. It switches to
            // start.log once it is running.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Under `dotnet velo.dll` the host is dotnet, so the assembly path must be passed through.
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        startInfo.ArgumentList.Add("start");
        if (port is { } listenPort)
        {
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(listenPort.ToString());
        }
        startInfo.Environment[DetachedVariable] = "1";

        StdHandles.MakeNonInheritable();
        // The ignore is inherited through exec, so the background start and its Codex children ignore
        // SIGHUP. This process exits once it returns, so its own disposition afterwards does not matter.
        if (!OperatingSystem.IsWindows() && !PosixSignals.IgnoreHangup())
            return Error("Cannot ignore SIGHUP for the background start.");
        using var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start velo in the background.");
        child.StandardInput.Close();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (ReadPid() == child.Id)
                return Printed($"Started velo in the background (pid {child.Id}). Log: {_config.StartLog}");
            if (child.HasExited) break;
            Thread.Sleep(50);
        }
        return Error($"velo did not start in the background. See {_config.StartLog}.");
    }

    // run.lock is the authority; start.pid is only trusted while the lock is held.
    private bool IsRunning(out string pid)
    {
        using var probe = FileLock.TryOpenExclusive(_config.RunLock);
        pid = ReadPid()?.ToString() ?? "unknown";
        return probe is null;
    }

    private int? ReadPid()
    {
        try
        {
            return File.Exists(_config.PidFile)
                && int.TryParse(File.ReadAllText(_config.PidFile).Trim(), out var pid)
                    ? pid
                    : null;
        }
        catch (IOException)
        {
            // The starting process may be rewriting the file; the caller polls again.
            return null;
        }
    }

    private void RedirectOutputToStartLog()
    {
        // Each background start rotates an oversized log, so the file stays bounded across restarts.
        if (File.Exists(_config.StartLog) && new FileInfo(_config.StartLog).Length > MaxStartLogBytes)
            File.Move(_config.StartLog, _config.StartLog + ".1", overwrite: true);

        var log = new StreamWriter(
            new FileStream(_config.StartLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
        Console.SetOut(log);
        Console.SetError(log);
        Console.SetIn(TextReader.Null);
    }

    internal async Task<int> RunQueueAsync(bool exitWhenIdle, bool background, int? port)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        using var finished = new CancellationTokenSource();
        var watching = WatchForStopAsync(cancellation, finished.Token);
        // A hangup stops a foreground start as Ctrl+C does. A background start registers nothing: Detach
        // ignored SIGHUP before launching it, so the ignore reaches this process and its Codex children.
        using var hangup = !background && !OperatingSystem.IsWindows()
            ? PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            })
            : null;
        var refresh = new RefreshSignal();
        WebApplication? api = null;
        FileStream? runLock = null;
        try
        {
            runLock = FileLock.TryOpenExclusive(_config.RunLock)
                ?? throw new InvalidOperationException("Another velo run is already active for this VELO_HOME.");
            if (port is { } listenPort)
            {
                try
                {
                    api = await HttpApi.StartAsync(_config, _queue, _intake, _review, refresh, listenPort);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException)
                {
                    return Error($"Cannot listen on 127.0.0.1:{listenPort}: {error.Message}");
                }
            }

            await new Orchestrator(
                _queue,
                _agent.RunAsync,
                _config,
                _config.Concurrency(),
                exitWhenIdle,
                onStarted: () =>
                {
                    // The run lock is held here, so a request left by an earlier start is stale.
                    File.Delete(_config.StopRequest);
                    File.WriteAllText(_config.PidFile, Environment.ProcessId.ToString());
                },
                refresh: refresh)
                .RunAsync(cancellation.Token, runLock);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            finished.Cancel();
            await watching;
            try
            {
                if (api is not null) await api.DisposeAsync();
            }
            finally
            {
                runLock?.Dispose();
                Console.CancelKeyPress -= onCancel;
            }
        }
    }

    // Polls for a stop request that names this process. Cancelling here stops the queue as Ctrl+C does.
    private async Task WatchForStopAsync(CancellationTokenSource cancellation, CancellationToken finished)
    {
        try
        {
            while (!finished.IsCancellationRequested)
            {
                if (StopRequestNamesThisProcess())
                {
                    cancellation.Cancel();
                    return;
                }
                await Task.Delay(StopPollInterval, finished);
            }
        }
        catch (OperationCanceledException) when (finished.IsCancellationRequested)
        {
            // The queue finished on its own; there is nothing left to watch.
        }
    }

    private bool StopRequestNamesThisProcess()
    {
        try
        {
            return File.Exists(_config.StopRequest)
                && File.ReadAllText(_config.StopRequest).Trim() == Environment.ProcessId.ToString();
        }
        catch (IOException)
        {
            // The requesting process may be rewriting the file; the next poll reads it again.
            return false;
        }
    }

    private int Retry(string[] args)
    {
        if (args is not [var id]) return Usage("velo retry <id>");
        if (_queue.TryRetry(id)) return 0;

        var work = _queue.Get(id);
        return Error(work is null
            ? $"Work not found: {id}"
            : $"Only failed work can be retried; work is {work.State.Text()}.");
    }

    private int Cancel(string[] args)
    {
        if (args is not [var id]) return Usage("velo cancel <id>");

        switch (_queue.Cancel(id))
        {
            case CancelResult.Cancelled:
                Console.WriteLine($"Cancelled {id}.");
                return 0;
            case CancelResult.Requested:
                // Only a live orchestrator acts on the request; otherwise the next start does.
                Console.WriteLine(IsRunning(out _)
                    ? $"Cancel requested for {id}."
                    : $"Cancel recorded for {id}, but no velo start is running. It takes effect when the next start recovers the work.");
                return 0;
            case CancelResult.NotCancellable:
                return Error("Only pending or running work can be cancelled.");
            default:
                return Error($"Work not found: {id}");
        }
    }

    private int Edit(string[] args)
    {
        if (args is not [var id, .. var rest]) return Usage("velo edit <id> [--] <prompt>");
        if (rest is ["--", ..]) rest = rest[1..];
        var prompt = string.Join(' ', rest).Trim();
        if (prompt.Length == 0) return Usage("velo edit <id> [--] <prompt>");

        return _queue.Edit(id, prompt) switch
        {
            WorkChange.Done => Printed($"Edited {id}."),
            WorkChange.NotPending => Error("Only pending work can be edited."),
            _ => Error($"Work not found: {id}")
        };
    }

    private int Delete(string[] args)
    {
        if (args is not [var id]) return Usage("velo delete <id>");

        return _queue.Delete(id) switch
        {
            WorkChange.Done => Printed($"Deleted {id}."),
            WorkChange.Running => Error("Running work cannot be deleted; cancel it first."),
            _ => Error($"Work not found: {id}")
        };
    }

    private async Task<int> DiffAsync(string[] args)
    {
        if (args is not [var id]) return Usage("velo diff <id>");

        var outcome = await _review.DiffAsync(id);
        if (outcome.Result != ReviewResult.Done) return Error(outcome.Message);

        var diff = outcome.Diff!;
        if (diff.Patch.Length == 0)
            return Printed("No changes against the base commit.");

        Console.Write(diff.Stat);
        Console.WriteLine();
        Console.Write(diff.Patch);
        if (diff.Truncated) Console.WriteLine($"... patch cut off after {Workspace.MaxPatchChars} characters.");
        return 0;
    }

    // Applies the changes to the repository as uncommitted changes. The worktree stays until discard.
    private async Task<int> AcceptAsync(string[] args)
    {
        if (args is not [var id]) return Usage("velo accept <id>");

        var outcome = await _review.AcceptAsync(id);
        return outcome.Result == ReviewResult.Done
            ? Printed($"Accepted {id}: its changes are applied as uncommitted changes. Commit them in the repository; discard removes the worktree.")
            : Error(outcome.Message);
    }

    // Removes the worktree and its branch. Changes that were not accepted are lost with them.
    private async Task<int> DiscardAsync(string[] args)
    {
        if (args is not [var id]) return Usage("velo discard <id>");

        var outcome = await _review.DiscardAsync(id);
        return outcome.Result == ReviewResult.Done
            ? Printed($"Discarded {id}: its worktree and branch velo/{id} were removed.")
            : Error(outcome.Message);
    }

    private int Logs(string[] args)
    {
        if (args is not [var id]) return Usage("velo logs <id>");
        if (_queue.Get(id) is null) return Error($"Work not found: {id}");

        var path = _config.LogFile(id);
        if (!File.Exists(path)) return Error($"Work has no log yet: {id}");
        using var log = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(log);
        Console.Write(reader.ReadToEnd());
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Usage:
              velo add [--worktree] [--] <prompt>
              velo list [pending|running|succeeded|failed]
              velo start [--once] [-d] [--port N]
              velo stop
              velo retry <id>
              velo cancel <id>
              velo edit <id> [--] <prompt>
              velo delete <id>
              velo diff <id>
              velo accept <id>
              velo discard <id>
              velo logs <id>

            Defaults:
              Codex runs with the workspace-write sandbox.
              Concurrency is 2; set VELO_CONCURRENCY to change it.
              start runs in the foreground until Ctrl+C; --once drains the queue and exits.
              -d runs in the background; its output goes to ~/.velo/start.log.
              stop ends the running start as Ctrl+C does; running work returns to pending.
              --port N serves a local HTTP API on 127.0.0.1:N (0 picks a free port).
              Its token is in ~/.velo/start.token while start runs.
              Worktrees are created below ~/.velo/workspaces and are kept by delete.
              accept applies a succeeded worktree's changes to its repository as uncommitted changes.
              discard removes a succeeded worktree and its branch; the record and log stay.
            """);
    }

    private static int Usage(string usage)
    {
        Console.Error.WriteLine($"Usage: {usage}");
        return 2;
    }

    private static int Printed(string message)
    {
        Console.WriteLine(message);
        return 0;
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }
}
