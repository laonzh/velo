using System.Reflection;
using System.Text.Json;

namespace Velo;

/// <summary>Ends the CLI with an exit code: 1 error, 2 usage, 3 merge conflict on approve.</summary>
public sealed class CliException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
    public int HttpStatus { get; init; } // of the server response that caused it, 0 otherwise
}

/// <summary>The command line: every command except <c>serve</c> talks to the running server over HTTP.</summary>
public static partial class Cli
{
    public static string Version { get; } =
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private const string Help = """
        usage: velo [command] [options]      (no command = ls)

        server:
          serve                        run the server in the foreground (port from config.json)
          web                          open the web UI in the browser

        issues:
          add "title" [options]        create an issue and queue it
          ls [-a]                      list issues; -a, --all includes done
          show <id>                    details, runs and how to take over with codex resume
          run <id> [-m MESSAGE]        queue a todo / review issue again, with an optional follow-up
                                       [--model M] [--effort E] switch model / effort first (blank = default)
          stop <id>                    stop a running agent, or take a todo issue out of the queue
          wait <id>                    block until the issue is out of the queue and not running; exit 0 if it succeeded
          logs <id> [-f] [--run N]     run log; -f follows until the run ends
          diff <id>                    committed changes of the issue branch
          approve <id>                 merge into the repository's current branch and mark done (exit 3: merge conflict)
          rm <id> [-y]                 delete the issue, its worktree, branch and logs; -y skips the prompt

        add options:
          -d DIR                       git repository to work in (default: none)
          -m DESCRIPTION               issue description / prompt; "-m -" reads it from stdin
          --model MODEL                agent model
          --effort LEVEL               reasoning effort

        shell:
          completion bash|powershell   print a tab-completion script, e.g.
                                       velo completion powershell | Out-String | Invoke-Expression

        examples:
          velo add "Fix login bug" -d D:\repo -m "Users cannot log in with SSO"
          Get-Content spec.md | velo add "Implement the spec" -d D:\repo -m -
          velo logs 42 -f
          velo approve 42

        options:
          --json                       print the API JSON
          -h, --help                   show this help
          -v, --version                show the version

        <id> is the issue number: 42, VL-42 or vl-42.
        """;

    public static async Task<int> RunAsync(string[] argv)
    {
        try
        {
            return await RunCommandAsync(argv);
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine("velo: " + ex.Message);
            return ex.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or GitException)
        {
            Console.Error.WriteLine("velo: " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunCommandAsync(string[] argv)
    {
        // Parse: positionals, "-x value" options and flags; "--" ends the options.
        var pos = new List<string>();
        var opts = new Dictionary<string, string>();
        for (int i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            if (a == "--") { pos.AddRange(argv[(i + 1)..]); break; }
            if (!a.StartsWith('-') || a == "-") pos.Add(a);
            else if (s_flagOptions.Contains(a)) opts[a] = "";
            else if (s_valueOptions.Contains(a)) opts[a] = i + 1 < argv.Length ? argv[++i] : throw Usage($"option {a} needs a value");
            else throw Usage($"unknown option '{a}'");
        }
        if (opts.ContainsKey("-h") || opts.ContainsKey("--help") || pos is ["help"]) return Print(Help);
        if (opts.ContainsKey("-v") || opts.ContainsKey("--version")) return Print("velo " + Version);

        string command = pos.Count > 0 ? pos[0] : "ls";
        string[] rest = [.. pos.Skip(1)];
        bool json = opts.ContainsKey("--json");
        string? Opt(string name) => opts.GetValueOrDefault(name);
        void Expect(int count, params string[] allowed)
        {
            if (rest.Length != count) throw Usage($"'{command}' takes {(count == 0 ? "no arguments" : count == 1 ? "one argument" : count + " arguments")} (see velo --help)");
            foreach (string o in opts.Keys)
                if (o != "--json" && !allowed.Contains(o)) throw Usage($"option {o} is not valid for '{command}'");
        }
        // "-m -" takes the text from stdin
        static async Task<string?> Text(string? value) => value == "-" ? (await Console.In.ReadToEndAsync()).Trim() : value;
        string Id()
        {
            try { return "VL-" + Engine.ParseId(rest[0]); }
            catch (VeloException ex) { throw Usage(ex.Message); }
        }

        VeloPaths paths = VeloPaths.Resolve();
        if (command == "serve")
        {
            Expect(0);
            return await Server.RunAsync(paths);
        }
        if (command == "completion")
        {
            Expect(1);
            return Print(Completion(rest[0]));
        }
        if (command is not ("web" or "add" or "ls" or "show" or "run" or "stop" or "wait" or "logs" or "diff" or "approve" or "rm"))
            throw Usage($"unknown command '{command}' (see velo --help)");

        Settings settings = Store.LoadSettings(paths);
        using var api = new Api(settings.Port);
        Issue Read(string raw) => Parse(raw, VeloJson.Default.Issue);
        switch (command)
        {
            case "web":
                Expect(0);
                await api.SendAsync(HttpMethod.Get, "/api/issues");
                string url = $"http://127.0.0.1:{settings.Port}/";
                OpenBrowser(url);
                return Print(json ? url : "opened " + url);
            case "add":
                Expect(1, "-d", "-m", "--model", "--effort");
                var input = new NewIssue(rest[0], await Text(Opt("-m")), Opt("-d") is { } d ? Path.GetFullPath(d) : null, Opt("--model"), Opt("--effort"));
                string created = await api.SendAsync(HttpMethod.Post, "/api/issues", JsonSerializer.Serialize(input, VeloJson.Default.NewIssue));
                return Print(json ? created : $"created {Read(created).Key} ({Status(Read(created))})");
            case "ls":
                Expect(0, "-a", "--all");
                bool all = opts.ContainsKey("-a") || opts.ContainsKey("--all");
                string list = await api.SendAsync(HttpMethod.Get, "/api/issues" + (all ? "?all=true" : ""));
                return Print(json ? list : FormatList(Parse(list, VeloJson.Default.IReadOnlyListIssue)));
            case "show":
                Expect(1);
                string shown = await api.SendAsync(HttpMethod.Get, $"/api/issues/{Id()}");
                if (json) return Print(shown);
                Issue shownIssue = Read(shown);
                string? merge = null;
                if (shownIssue is { Status: IssueStatus.Review, Directory: not null })
                {
                    try { merge = FormatMerge(Parse(await api.SendAsync(HttpMethod.Get, $"/api/issues/{Id()}/merge"), VeloJson.Default.MergeCheck)); }
                    catch (CliException) { } // the merge hint is optional
                }
                return Print(FormatIssue(shownIssue, paths, merge));
            case "run":
                Expect(1, "-m", "--model", "--effort");
                string body = JsonSerializer.Serialize(new RunRequest(await Text(Opt("-m")), Opt("--model"), Opt("--effort")), VeloJson.Default.RunRequest);
                string queued = await api.SendAsync(HttpMethod.Post, $"/api/issues/{Id()}/run", body);
                return Print(json ? queued : $"{Read(queued).Key} {Status(Read(queued))}");
            case "stop":
                Expect(1);
                string stopped = await api.SendAsync(HttpMethod.Post, $"/api/issues/{Id()}/stop", "");
                return Print(json ? stopped : $"{Read(stopped).Key} {Status(Read(stopped))}");
            case "wait":
                Expect(1);
                return await WaitAsync(api, Id(), json);
            case "approve":
                Expect(1);
                string approved;
                try
                {
                    approved = await api.SendAsync(HttpMethod.Post, $"/api/issues/{Id()}/approve", "");
                }
                catch (CliException ex) when (ex.HttpStatus == 409 && ex.Message.Contains(" conflicts with "))
                {
                    throw new CliException(3, ex.Message);
                }
                if (json) return Print(approved);
                Issue issue = Read(approved);
                if (issue.Directory is null) return Print($"{issue.Key} done");
                string branch = await Git.CurrentBranchAsync(issue.Directory);
                return Print($"{issue.Key} done, merged into {branch}");
            case "diff":
                Expect(1);
                Console.Write(await api.SendAsync(HttpMethod.Get, $"/api/issues/{Id()}/diff"));
                return 0;
            case "logs":
                Expect(1, "-f", "--run");
                return await LogsAsync(api, Id(), Opt("--run"), opts.ContainsKey("-f"), json);
            default: // rm
                Expect(1, "-y");
                string id = Id();
                if (!opts.ContainsKey("-y"))
                {
                    Console.Error.Write($"Delete {id} with its worktree, branch, workspace and logs? [y/N] ");
                    if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes")) throw new CliException(1, "not deleted");
                }
                await api.SendAsync(HttpMethod.Delete, $"/api/issues/{id}");
                return json ? 0 : Print("deleted " + id);
        }
    }
}
