// FakeCodex: emulates `codex app-server` (codex-cli 0.163.0 protocol subset) over stdio for Velo tests.
//
//   FakeCodex --version            -> "codex-cli 0.163.0"
//   FakeCodex app-server           -> JSON lines on stdin/stdout (no "jsonrpc" member, like the real server)
//
// Environment:
//   FAKE_CODEX_SCENARIO  complete | multi_turn | invalid_json | needs_input | crash | stall | approval | failed_turn |
//                        stderr_flood | git_commit | write_artifact | write_file | write_stall   (default: complete)
//                        write_file: writes a new written-N.txt without committing; write_stall: same, then never finishes
//   FAKE_CODEX_LOG       optional file: every received client message is appended as one JSON line
//                        (fallback: the path stored in "log.txt" next to the executable)
//   FAKE_CODEX_VERSION   override the --version output (e.g. "codex-cli 0.99.0")
//   FAKE_CODEX_IGNORE_INTERRUPT=1  never complete a turn on turn/interrupt
//

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length > 0 && args[0] == "--version")
{
    Console.Out.WriteLine(Environment.GetEnvironmentVariable("FAKE_CODEX_VERSION") ?? "codex-cli 0.163.0");
    return 0;
}
if (args.Length == 0 || args[0] != "app-server")
{
    Console.Error.WriteLine("usage: FakeCodex app-server | --version");
    return 2;
}

// Velo passes only a whitelisted environment to Codex, so a FakeCodex launched by a real `velo serve` cannot see
// FAKE_CODEX_SCENARIO: a "scenario.txt" next to the executable is the fallback (used by the AOT smoke test).
string? scenarioFromFile = FakeServer.ReadBesideExe("scenario.txt");
var server = new FakeServer(Environment.GetEnvironmentVariable("FAKE_CODEX_SCENARIO") ?? scenarioFromFile ?? "complete");
return await server.RunAsync();

sealed class FakeServer(string scenario)
{
    public static string? ReadBesideExe(string name)
    {
        string file = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    private readonly Stream _stdout = Console.OpenStandardOutput();
    private readonly object _writeLock = new();
    private readonly string? _logPath = Environment.GetEnvironmentVariable("FAKE_CODEX_LOG") ?? ReadBesideExe("log.txt");
    private readonly bool _ignoreInterrupt = Environment.GetEnvironmentVariable("FAKE_CODEX_IGNORE_INTERRUPT") == "1";
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private bool _initialized;
    private string? _threadId;
    private string _cwd = Environment.CurrentDirectory;
    private int _turnNumber;
    private int _serverRequestId;
    private long _totalInput, _totalOutput, _totalCached, _totalReasoning;
    private CancellationTokenSource? _activeTurn;
    private string? _activeTurnId;
    private Task? _turnTask;

    public async Task<int> RunAsync()
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (true)
        {
            string? line = await stdin.ReadLineAsync();
            if (line is null) break; // stdin closed -> graceful exit
            if (string.IsNullOrWhiteSpace(line)) continue;
            Log(line);
            JsonNode? msg;
            try { msg = JsonNode.Parse(line); }
            catch (JsonException) { Console.Error.WriteLine("fake: invalid json from client"); continue; }
            if (msg is not JsonObject obj) continue;

            string? method = (string?)obj["method"];
            JsonNode? id = obj["id"];
            if (method is null && id is not null)
            {
                string key = id.ToJsonString();
                TaskCompletionSource<JsonNode?>? tcs;
                lock (_pending) _pending.Remove(key, out tcs);
                if (obj["error"] is JsonNode err) tcs?.TrySetResult(new JsonObject { ["__error"] = err.DeepClone() });
                else tcs?.TrySetResult(obj["result"]?.DeepClone());
                continue;
            }
            if (method is null) continue;
            if (id is null) { HandleNotification(method); continue; }
            HandleRequest(id.DeepClone(), method, obj["params"] as JsonObject);
        }
        if (_turnTask is not null) await Task.WhenAny(_turnTask, Task.Delay(2000));
        return 0;
    }

    private void HandleNotification(string method)
    {
        if (method == "initialized") _initialized = true;
    }

    private void HandleRequest(JsonNode id, string method, JsonObject? p)
    {
        switch (method)
        {
            case "initialize":
                if (_threadId is not null || _initialized) { Error(id, -32600, "Already initialized"); return; }
                if (p?["clientInfo"]?["name"] is null) { Error(id, -32602, "clientInfo.name is required"); return; }
                Respond(id, new JsonObject
                {
                    ["userAgent"] = $"{(string?)p["clientInfo"]!["name"]}/0.163.0 (FakeCodex)",
                    ["codexHome"] = Path.Combine(Path.GetTempPath(), "fake-codex-home"),
                    ["platformFamily"] = OperatingSystem.IsWindows() ? "windows" : "unix",
                    ["platformOs"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
                });
                Notify("remoteControl/status/changed", new JsonObject { ["status"] = "disabled" });
                break;

            case "thread/start":
            {
                if (!_initialized) { Error(id, -32600, "Not initialized"); return; }
                string? cwd = (string?)p?["cwd"];
                if (cwd is null || !Path.IsPathFullyQualified(cwd)) { Error(id, -32602, "cwd must be absolute"); return; }
                _cwd = cwd;
                _threadId = Guid.NewGuid().ToString();
                var thread = new JsonObject
                {
                    ["id"] = _threadId, ["cwd"] = cwd, ["cliVersion"] = "0.163.0", ["createdAt"] = Now() / 1000,
                    ["updatedAt"] = Now() / 1000, ["ephemeral"] = false, ["modelProvider"] = "fake", ["preview"] = "",
                    ["projectId"] = null, ["sessionId"] = _threadId, ["source"] = "vscode", ["status"] = new JsonObject { ["type"] = "idle" },
                    ["turns"] = new JsonArray(),
                };
                Respond(id, new JsonObject
                {
                    ["thread"] = thread,
                    ["model"] = (string?)p!["model"] ?? "fake-default-model",
                    ["modelProvider"] = "fake",
                    ["cwd"] = cwd,
                    ["approvalPolicy"] = (string?)p["approvalPolicy"] ?? "on-request",
                    ["approvalsReviewer"] = "user",
                    ["sandbox"] = new JsonObject { ["type"] = "workspaceWrite", ["writableRoots"] = new JsonArray(), ["networkAccess"] = false },
                });
                Notify("thread/started", new JsonObject { ["thread"] = thread.DeepClone() });
                break;
            }

            case "turn/start":
            {
                if (p is null || (string?)p["threadId"] != _threadId) { Error(id, -32600, "invalid thread id"); return; }
                if (p["input"] is not JsonArray input || input.Count == 0 || (string?)input[0]?["type"] != "text") { Error(id, -32602, "input must contain a text item"); return; }
                if (_activeTurn is not null) { Error(id, -32600, "a turn is already active"); return; }
                _turnNumber++;
                string turnId = Guid.NewGuid().ToString();
                _activeTurnId = turnId;
                _activeTurn = new CancellationTokenSource();
                var turn = new JsonObject { ["id"] = turnId, ["items"] = new JsonArray(), ["status"] = "inProgress" };
                Respond(id, new JsonObject { ["turn"] = turn });
                Notify("turn/started", new JsonObject { ["threadId"] = _threadId, ["turn"] = turn.DeepClone() });
                int n = _turnNumber;
                CancellationToken token = _activeTurn.Token;
                _turnTask = Task.Run(() => RunTurnAsync(turnId, n, token));
                break;
            }

            case "turn/interrupt":
                Respond(id, new JsonObject());
                if (!_ignoreInterrupt && (string?)p?["turnId"] == _activeTurnId) _activeTurn?.Cancel();
                break;

            default:
                Error(id, -32600, $"Invalid request: unknown variant `{method}`");
                break;
        }
    }

    private async Task RunTurnAsync(string turnId, int n, CancellationToken interrupt)
    {
        try
        {
            switch (scenario)
            {
                case "complete":
                    await AgentMessage(turnId, Outcome("completed", $"turn {n} done", [], []), stream: true);
                    Usage(turnId, 100, 50); Usage(turnId, 0, 0); // second update repeats the same cumulative total
                    Complete(turnId, "completed");
                    break;

                case "multi_turn":
                    if (n == 1)
                    {
                        await AgentMessage(turnId, "I have started the work and will continue in the next turn.", stream: true);
                        Usage(turnId, 100, 50); Usage(turnId, 0, 0);
                    }
                    else
                    {
                        await AgentMessage(turnId, Outcome("completed", $"turn {n} done", ["checked"], []), stream: true);
                        Usage(turnId, 70, 30);
                    }
                    Complete(turnId, "completed");
                    break;

                case "needs_input":
                    await AgentMessage(turnId, Outcome("needs_input", "Which database should I use?", [], []), stream: false);
                    Complete(turnId, "completed");
                    break;

                case "invalid_json":
                    RawLine("this line is not json at all");
                    await AgentMessage(turnId, "{\"status\": \"completed\", \"summary\": ", stream: false);
                    Complete(turnId, "completed");
                    break;

                case "crash":
                    Console.Error.WriteLine("fake codex: simulated fatal error, exiting");
                    Console.Error.Flush();
                    Environment.Exit(101);
                    break;

                case "stall":
                    await Task.Delay(Timeout.Infinite, interrupt);
                    break;

                case "approval":
                {
                    JsonNode? decision = await ServerRequest("item/commandExecution/requestApproval", new JsonObject
                    {
                        ["threadId"] = _threadId, ["turnId"] = turnId, ["itemId"] = "item-cmd-1", ["startedAtMs"] = Now(),
                        ["command"] = "rm -rf /", ["cwd"] = _cwd, ["reason"] = "cleanup",
                    });
                    JsonNode? unknown = await ServerRequest("velo/unknownServerRequest", new JsonObject { ["threadId"] = _threadId });
                    string d = (string?)decision?["decision"] ?? "none";
                    string u = unknown?["__error"] is not null ? "error" : "result";
                    await AgentMessage(turnId, Outcome("completed", $"decision={d};unknown={u}", [], []), stream: false);
                    Complete(turnId, "completed");
                    break;
                }

                case "failed_turn":
                    Notify("error", new JsonObject
                    {
                        ["threadId"] = _threadId, ["turnId"] = turnId, ["willRetry"] = false,
                        ["error"] = new JsonObject { ["message"] = "upstream overloaded", ["codexErrorInfo"] = "serverOverloaded" },
                    });
                    Complete(turnId, "failed", new JsonObject { ["message"] = "upstream overloaded", ["codexErrorInfo"] = "serverOverloaded" });
                    break;

                case "stderr_flood":
                {
                    // ~8 MiB on stderr: blocks forever if the client does not drain stderr.
                    using var stderr = Console.OpenStandardError();
                    byte[] chunk = Encoding.UTF8.GetBytes(new string('x', 1023) + "\n");
                    for (int i = 0; i < 8 * 1024; i++) stderr.Write(chunk);
                    stderr.Flush();
                    await AgentMessage(turnId, Outcome("completed", "survived stderr flood", [], []), stream: true);
                    Complete(turnId, "completed");
                    break;
                }

                case "write_artifact":
                {
                    string path = Path.Combine(_cwd, "report.md");
                    await File.WriteAllTextAsync(path, $"# Report\n\nWritten by FakeCodex turn {n}.\n");
                    ItemCompleted(turnId, new JsonObject
                    {
                        ["type"] = "fileChange", ["id"] = "fc-1", ["status"] = "completed",
                        ["changes"] = new JsonArray(new JsonObject { ["path"] = path, ["kind"] = new JsonObject { ["type"] = "add" }, ["diff"] = "" }),
                    });
                    await AgentMessage(turnId, Outcome("completed", "wrote report.md", ["report.md"], ["report.md"]), stream: false);
                    Usage(turnId, 10, 5);
                    Complete(turnId, "completed");
                    break;
                }

                case "write_file":
                case "write_stall":
                {
                    // Like a real agent under Velo: changes files, never commits.
                    string name = $"written-{Directory.GetFiles(_cwd, "written-*.txt").Length + 1}.txt";
                    await File.WriteAllTextAsync(Path.Combine(_cwd, name), $"change from FakeCodex turn {n}\n");
                    if (scenario == "write_stall") await Task.Delay(Timeout.Infinite, interrupt);
                    await AgentMessage(turnId, $"wrote {name}", stream: false);
                    Complete(turnId, "completed");
                    break;
                }

                case "git_commit":
                {
                    string name = $"fake-change-{Directory.GetFiles(_cwd, "fake-change-*.txt").Length + 1}.txt"; // unique across runs (a new process per run)
                    await File.WriteAllTextAsync(Path.Combine(_cwd, name), $"change from FakeCodex turn {n}\n");
                    int add = Git("add", "--", name);
                    int commit = Git("-c", "user.name=FakeCodex", "-c", "user.email=fake@codex.invalid", "commit", "-m", $"FakeCodex: add {name}", "--", name);
                    if (add != 0 || commit != 0)
                    {
                        await AgentMessage(turnId, Outcome("failed", $"git add={add} commit={commit}", [], []), stream: false);
                    }
                    else
                    {
                        await AgentMessage(turnId, Outcome("completed", $"committed {name}", ["git log"], [name]), stream: false);
                    }
                    Complete(turnId, "completed");
                    break;
                }

                default:
                    await AgentMessage(turnId, $"unknown scenario {scenario}", stream: false);
                    Complete(turnId, "failed", new JsonObject { ["message"] = "unknown scenario " + scenario });
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Complete(turnId, "interrupted");
        }
    }

    // ---- protocol helpers ----------------------------------------------------------------------------

    private static string Outcome(string status, string summary, string[] evidence, string[] artifacts) => new JsonObject
    {
        ["status"] = status,
        ["summary"] = summary,
        ["evidence"] = new JsonArray(evidence.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
        ["artifacts"] = new JsonArray(artifacts.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
    }.ToJsonString();

    private async Task AgentMessage(string turnId, string text, bool stream)
    {
        string itemId = "msg-" + Guid.NewGuid().ToString("N")[..8];
        Notify("item/started", new JsonObject
        {
            ["threadId"] = _threadId, ["turnId"] = turnId, ["startedAtMs"] = Now(),
            ["item"] = new JsonObject { ["type"] = "agentMessage", ["id"] = itemId, ["text"] = "" },
        });
        if (stream)
        {
            for (int i = 0; i < text.Length; i += 8)
            {
                Notify("item/agentMessage/delta", new JsonObject
                {
                    ["threadId"] = _threadId, ["turnId"] = turnId, ["itemId"] = itemId, ["delta"] = text.Substring(i, Math.Min(8, text.Length - i)),
                });
            }
            await Task.Yield();
        }
        ItemCompleted(turnId, new JsonObject { ["type"] = "agentMessage", ["id"] = itemId, ["text"] = text, ["phase"] = "final_answer" });
    }

    private void ItemCompleted(string turnId, JsonObject item) => Notify("item/completed", new JsonObject
    {
        ["threadId"] = _threadId, ["turnId"] = turnId, ["completedAtMs"] = Now(), ["item"] = item,
    });

    private void Usage(string turnId, long input, long output)
    {
        _totalInput += input;
        _totalOutput += output;
        _totalCached += input / 10;
        _totalReasoning += output / 5;
        JsonObject Breakdown(long i, long o, long c, long r) => new()
        {
            ["inputTokens"] = i, ["outputTokens"] = o, ["cachedInputTokens"] = c, ["reasoningOutputTokens"] = r, ["totalTokens"] = i + o,
        };
        Notify("thread/tokenUsage/updated", new JsonObject
        {
            ["threadId"] = _threadId, ["turnId"] = turnId,
            ["tokenUsage"] = new JsonObject
            {
                ["total"] = Breakdown(_totalInput, _totalOutput, _totalCached, _totalReasoning),
                ["last"] = Breakdown(input, output, input / 10, output / 5),
                ["modelContextWindow"] = 200000,
            },
        });
    }

    private void Complete(string turnId, string status, JsonObject? error = null)
    {
        _activeTurn = null;
        _activeTurnId = null;
        Notify("turn/completed", new JsonObject
        {
            ["threadId"] = _threadId,
            ["turn"] = new JsonObject
            {
                ["id"] = turnId, ["items"] = new JsonArray(), ["status"] = status, ["error"] = error,
                ["completedAt"] = Now() / 1000, ["durationMs"] = 5,
            },
        });
    }

    private async Task<JsonNode?> ServerRequest(string method, JsonObject p)
    {
        string id = "srv-" + Interlocked.Increment(ref _serverRequestId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[JsonValue.Create(id).ToJsonString()] = tcs;
        Write(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = p });
        return await tcs.Task;
    }

    private int Git(params string[] gitArgs)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _cwd, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in gitArgs) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Close(); // never let git inherit our protocol stdin (a pending read on it stalls MSYS git startup)
        var errTask = proc.StandardError.ReadToEndAsync();
        string output = proc.StandardOutput.ReadToEnd();
        string err = errTask.Result;
        proc.WaitForExit();
        Console.Error.WriteLine($"fake git {string.Join(' ', gitArgs)} -> {proc.ExitCode} in {sw.ElapsedMilliseconds} ms: {err.Trim()} {output.Trim()}");
        return proc.ExitCode;
    }

    private void Respond(JsonNode id, JsonNode result) => Write(new JsonObject { ["id"] = id, ["result"] = result });

    private void Error(JsonNode id, int code, string message) => Write(new JsonObject
    {
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }, ["id"] = id,
    });

    private void Notify(string method, JsonObject p) => Write(new JsonObject { ["method"] = method, ["params"] = p, ["emittedAtMs"] = Now() });

    private void Write(JsonObject message) => RawLine(message.ToJsonString());

    private void RawLine(string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_writeLock)
        {
            _stdout.Write(bytes);
            _stdout.Flush();
        }
    }

    private void Log(string line)
    {
        if (_logPath is null) return;
        lock (_writeLock)
        {
            for (int attempt = 0; ; attempt++) // several FakeCodex processes may share one log file
            {
                try { File.AppendAllText(_logPath, line + "\n"); return; }
                catch (IOException) when (attempt < 50) { Thread.Sleep(20); }
            }
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
