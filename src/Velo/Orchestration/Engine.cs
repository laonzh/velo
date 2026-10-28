using System.Globalization;
using System.Text;

namespace Velo;

/// <summary>
/// The actions add / run / stop / approve / rm, queries and restart recovery; scheduling lives in
/// <see cref="Scheduler"/>, runs in <see cref="RunExecutor"/>, approve and rm cleanup in <see cref="Approver"/>.
/// Errors are <see cref="VeloException"/> (400 / 404 / 409) or <see cref="GitException"/> (409).
/// </summary>
public sealed class Engine
{
    public const int MaxLogChunk = 256 * 1024;

    private readonly VeloPaths _paths;
    private readonly Store _store;
    private readonly Scheduler _scheduler;
    private readonly Approver _approver;
    private readonly Settings _settings;
    private readonly AgentRegistry _agents;

    public Engine(VeloPaths paths, Store store, Settings settings)
    {
        _paths = paths;
        _store = store;
        _settings = settings;
        _agents = new AgentRegistry(settings);
        RunExecutor executor = null!; // the scheduler starts runs only after construction
        _scheduler = new Scheduler(store, settings.MaxConcurrency, (issue, ct) => executor.ExecuteAsync(issue, ct));
        executor = new RunExecutor(paths, store, settings, _agents, () => _scheduler.IsStopping);
        _approver = new Approver(paths, store);
    }

    // ---- lifecycle -----------------------------------------------------------------------------------------------

    /// <summary>Restart recovery (running → review, its run → interrupted), then the first scheduling pass.</summary>
    public void Start()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (Issue issue in _store.List().Where(i => i.Status == IssueStatus.Running))
        {
            _store.Update(issue.Number, i => i with
            {
                Status = IssueStatus.Review,
                UpdatedAt = now,
                Runs = [.. i.Runs.Select(r => r.Status == RunStatus.Running
                    ? r with { Status = RunStatus.Interrupted, Summary = "server stopped", EndedAt = now }
                    : r)],
            });
        }
        _scheduler.Wake();
    }

    /// <summary>Server shutdown: stops every agent; their runs end as interrupted.</summary>
    public Task ShutdownAsync() => _scheduler.ShutdownAsync();

    // ---- queries -------------------------------------------------------------------------------------------------

    public IReadOnlyList<Issue> List(bool all = false) => [.. _store.List().Where(i => all || i.Status != IssueStatus.Done)];

    /// <summary>Changes whenever any issue changes (<see cref="Store.Version"/>).</summary>
    public long Version => _store.Version;

    private static readonly TimeSpan s_modelsTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_modelsRetry = TimeSpan.FromSeconds(30);
    private readonly Lock _modelsLock = new();
    private Task<IReadOnlyList<AgentModel>>? _models;
    private DateTime _modelsAt;

    /// <summary>
    /// Models of the configured agent. Cached; an empty answer (agent missing/not logged in) is retried after 30 s.
    /// </summary>
    public Task<IReadOnlyList<AgentModel>> ModelsAsync()
    {
        lock (_modelsLock)
        {
            bool fresh = _models is not null && (!_models.IsCompleted
                || DateTime.UtcNow - _modelsAt < (_models.IsCompletedSuccessfully && _models.Result.Count > 0 ? s_modelsTtl : s_modelsRetry));
            if (!fresh)
            {
                IAgent? agent = _agents.Find(_settings.Agent);
                _models = agent is null ? Task.FromResult<IReadOnlyList<AgentModel>>([]) : agent.ListModelsAsync(CancellationToken.None);
                _modelsAt = DateTime.UtcNow;
            }
            return _models!;
        }
    }

    /// <summary>The issue with that id; 404 when there is none.</summary>
    public Issue Get(string id) => _store.Get(ParseId(id)) ?? throw VeloException.NotFound($"issue {id} not found");

    /// <summary>
    /// The issue as the API shows it: with the take-over command of the configured agent for its last session.
    /// </summary>
    public Issue WithTakeOver(Issue issue) =>
        issue.Runs.LastOrDefault(r => r.SessionId is not null) is { SessionId: { } session }
        && _agents.Find(_settings.Agent)?.TakeOver(session) is { } takeOver ? issue with { TakeOver = takeOver } : issue;

    /// <summary><c>42</c>, <c>VL-42</c> or <c>vl-42</c>.</summary>
    public static int ParseId(string id)
    {
        string digits = id.StartsWith("vl-", StringComparison.OrdinalIgnoreCase) ? id[3..] : id;
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0
            ? n
            : throw VeloException.BadRequest($"invalid issue id '{id}'");
    }

    /// <summary>
    /// The issue branch's committed changes, cut at <see cref="Git.MaxDiffChars"/> characters; 404 for a plain issue.
    /// </summary>
    public async Task<string> DiffAsync(string id)
    {
        Issue issue = Get(id);
        if (issue.Directory is null) throw VeloException.NotFound($"{issue.Key} has no directory");
        return await Git.DiffAsync(issue.Directory, Git.Branch(_store.HomeId, issue.Number));
    }

    /// <summary>How approving the issue would go (<see cref="MergeCheck"/>); 404 for a plain issue.</summary>
    public async Task<MergeCheck> MergeCheckAsync(string id)
    {
        Issue issue = Get(id);
        if (issue.Directory is null) throw VeloException.NotFound($"{issue.Key} has no directory");
        return await Git.MergeCheckAsync(issue.Directory, Git.Branch(_store.HomeId, issue.Number));
    }

    /// <summary>
    /// Log of run <paramref name="run"/> (default: the last one) from byte <paramref name="offset"/>,
    /// cut at a line end.
    /// </summary>
    public LogChunk ReadLog(string id, int? run, long offset)
    {
        Issue issue = Get(id);
        Run? r = run is null ? issue.Runs.LastOrDefault()
            : issue.Runs.FirstOrDefault(x => x.Number == run) ?? throw VeloException.NotFound($"{issue.Key} has no run {run}");
        if (r is null) return new LogChunk(0, 0, "", true);
        bool done = r.Status != RunStatus.Running; // read before the file: a finished run's log is complete
        string file = _paths.RunLog(issue.Number, r.Number);
        if (!File.Exists(file)) return new LogChunk(r.Number, 0, "", done);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        offset = Math.Clamp(offset, 0, stream.Length);
        stream.Position = offset;
        byte[] buffer = new byte[(int)Math.Min(MaxLogChunk, stream.Length - offset)];
        int n = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        int end = n == 0 ? 0 : Array.LastIndexOf(buffer, (byte)'\n', n - 1) + 1; // whole lines only, so UTF-8 is never split
        if (end == 0 && n == buffer.Length) end = n; // a single line longer than a chunk
        return new LogChunk(r.Number, offset + end, Encoding.UTF8.GetString(buffer, 0, end), done && offset + end >= stream.Length);
    }

    // ---- actions -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates a todo issue; a given directory must be an absolute path into a git repository that has commits.
    /// </summary>
    public async Task<Issue> AddAsync(NewIssue input)
    {
        string title = input.Title?.Trim() is { Length: > 0 } t ? t : throw VeloException.BadRequest("title is required");
        string? repo = null;
        if (!string.IsNullOrWhiteSpace(input.Directory))
        {
            if (!Path.IsPathFullyQualified(input.Directory)) throw VeloException.BadRequest("directory must be an absolute path");
            repo = await Git.TopLevelAsync(input.Directory) ?? throw VeloException.BadRequest($"not a git repository: {input.Directory}");
            if (!await Git.HasCommitsAsync(repo)) throw VeloException.BadRequest("repository has no commits yet — make an initial commit first");
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Issue issue = _store.Add(n => new Issue
        {
            Number = n,
            Title = title,
            Description = input.Description?.Trim() ?? "",
            Directory = repo,
            Model = Blank(input.Model),
            Effort = Blank(input.Effort),
            Status = IssueStatus.Todo,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _scheduler.Wake();
        return _store.Get(issue.Number) ?? issue;
    }

    /// <summary>
    /// todo / review → todo, with an optional follow-up message and, when given, a new model / effort for the next run.
    /// </summary>
    public Issue RunIssue(string id, string? message, string? model = null, string? effort = null)
    {
        bool retarget = model is not null || effort is not null;
        int number = Get(id).Number;
        using (_scheduler.BeginOperation(number))
            _store.Update(number, i => i.Status is IssueStatus.Todo or IssueStatus.Review
                ? i with
                {
                    Status = IssueStatus.Todo,
                    Message = Blank(message) ?? i.Message,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Model = retarget ? Blank(model) : i.Model,
                    Effort = retarget ? Blank(effort) : i.Effort,
                }
                : throw VeloException.Conflict($"{i.Key} is {Name(i.Status)}; only todo or review issues can be run"));
        return Get(id);
    }

    /// <summary>running: kills the agent and waits until the run is wrapped up; todo: → review.</summary>
    public async Task<Issue> StopAsync(string id)
    {
        int number = Get(id).Number;
        using var operation = _scheduler.BeginOperation(number);
        Scheduler.ActiveRun? active = _scheduler.ActiveOr(number, () => _store.Update(number, i => i.Status == IssueStatus.Todo
            ? i with { Status = IssueStatus.Review, UpdatedAt = DateTimeOffset.UtcNow }
            : throw VeloException.Conflict($"{i.Key} is {Name(i.Status)}; only running or todo issues can be stopped")));
        if (active is not null) await active.CancelAsync();
        return Get(id);
    }

    /// <summary>review → done; a git issue is first merged (--no-ff) into the repository's current branch.</summary>
    public async Task<Issue> ApproveAsync(string id)
    {
        using var operation = _scheduler.BeginOperation(Get(id).Number);
        return await _approver.ApproveAsync(Get(id), () => Get(id));
    }

    /// <summary>
    /// Stops a running agent, then deletes the issue, its worktree, branch (-D), workspace, tmp and logs.
    /// </summary>
    public async Task DeleteAsync(string id)
    {
        Issue issue = Get(id);
        using var operation = _scheduler.BeginOperation(issue.Number);
        while (_scheduler.ActiveOr(issue.Number, () => { }) is { } active) await active.CancelAsync();
        await _approver.CleanUpAsync(issue);
        _store.Remove(issue.Number);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>The enum value in lower case - the spelling the API and CLI use.</summary>
    public static string Name<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
