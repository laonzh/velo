namespace Velo;

public sealed class Cli
{
    private readonly Config _config;
    private readonly WorkQueue _queue;
    private readonly Workspace _workspace;
    private readonly AgentRunner _agent;

    public Cli()
    {
        _config = Config.Load();
        _queue = new WorkQueue(_config);
        _workspace = new Workspace(_config);
        _agent = new AgentRunner(_config);
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
            "run" => await RunQueueAsync(args[1..]),
            "retry" => Retry(args[1..]),
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

        var id = WorkQueue.NewId();
        var workspace = worktree
            ? await _workspace.CreateAsync(id, Environment.CurrentDirectory)
            : Environment.CurrentDirectory;
        _queue.Add(id, prompt, workspace);
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

    private async Task<int> RunQueueAsync(string[] args)
    {
        if (args is not [])
            return Usage("velo run");

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            await new Orchestrator(
                _queue,
                _agent.RunAsync,
                _config,
                _config.Concurrency()).RunAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
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
              velo run
              velo retry <id>
              velo logs <id>

            Defaults:
              Codex runs with the workspace-write sandbox.
              Concurrency is 2; set VELO_CONCURRENCY to change it.
              Worktrees are created below ~/.velo/workspaces and are retained.
            """);
    }

    private static int Usage(string usage)
    {
        Console.Error.WriteLine($"Usage: {usage}");
        return 2;
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }
}
