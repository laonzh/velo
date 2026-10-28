using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Velo;

/// <summary>
/// One <c>codex app-server</c> process for one turn: start, handshake, notifications until the turn ends
/// (see <see cref="CodexAgent"/>).
/// </summary>
internal sealed class CodexSession(AgentRequest req, string? codexPath, Action<string> log, CancellationToken ct)
{
    /// <summary>A runtime problem whose message is the run's error as is.</summary>
    internal sealed class Failure(string message) : Exception(message);

    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly Queue<string> _stderr = new();
    private Process? _proc;
    private long _lastActivity = Environment.TickCount64;
    private long _nextId;
    private string? _threadId, _finalMessage, _lastError;
    private long _tokens;
    private bool _turnDone;
    private string? _turnFailure;

    public AgentResult Result(AgentStatus status, string? error) =>
        new(status, _finalMessage, _threadId, _tokens, error is null ? null : WithStderr(error));

    public async Task<AgentResult> ExecuteAsync()
    {
        (string? exe, bool npm, string? error) = CodexLocator.Locate(codexPath);
        if (exe is null) return Result(AgentStatus.Failed, error);
        Start(exe, npm);

        DateTime handshake = DateTime.UtcNow + CodexAgent.HandshakeTimeout;
        await Initialize(handshake);

        var thread = new JsonObject
        {
            ["cwd"] = req.Directory,
            ["approvalPolicy"] = "never",
            ["sandbox"] = "workspace-write",
        };
        if (!string.IsNullOrWhiteSpace(req.Model)) thread["model"] = req.Model;
        if (!string.IsNullOrWhiteSpace(req.Effort)) thread["config"] = new JsonObject { ["model_reasoning_effort"] = req.Effort };
        JsonNode started = await Request("thread/start", thread, handshake);
        _threadId = Str(started, "thread", "id") ?? throw new Failure("thread/start response has no thread.id");
        log($"thread {_threadId} started");

        var roots = new JsonArray();
        foreach (string root in req.WritableRoots) roots.Add((JsonNode?)JsonValue.Create(Path.GetFullPath(root)));
        var turn = new JsonObject
        {
            ["threadId"] = _threadId,
            ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = req.Prompt }),
            ["sandboxPolicy"] = new JsonObject
            {
                ["type"] = "workspaceWrite",
                ["writableRoots"] = roots,
                ["networkAccess"] = false,
                ["excludeTmpdirEnvVar"] = true,
                ["excludeSlashTmp"] = true,
            },
        };
        if (!string.IsNullOrWhiteSpace(req.Effort)) turn["effort"] = req.Effort;
        await Request("turn/start", turn, handshake);

        while (!_turnDone) await Handle(await Next(null));

        log($"tokens: {_tokens}");
        return _turnFailure is null
            ? Result(AgentStatus.Completed, null)
            : Result(AgentStatus.Failed, _turnFailure);
    }

    /// <summary>The models <c>model/list</c> offers (hidden ones left out), default model first.</summary>
    public async Task<IReadOnlyList<AgentModel>> ListModelsAsync()
    {
        (string? exe, bool npm, string? error) = CodexLocator.Locate(codexPath);
        if (exe is null) throw new Failure(error ?? "codex not found");
        Start(exe, npm);

        DateTime deadline = DateTime.UtcNow + CodexAgent.HandshakeTimeout;
        await Initialize(deadline);
        var models = new List<AgentModel>();
        string? cursor = null;
        do
        {
            var page = new JsonObject();
            if (cursor is not null) page["cursor"] = cursor;
            JsonNode result = await Request("model/list", page, deadline);
            foreach (JsonNode? m in result["data"] as JsonArray ?? [])
            {
                string? id = Str(m, "model") ?? Str(m, "id");
                if (id is null || m?["hidden"]?.GetValue<bool>() == true) continue;
                var efforts = new List<string>();
                foreach (JsonNode? e in m?["supportedReasoningEfforts"] as JsonArray ?? [])
                    if (Str(e, "reasoningEffort") is { } effort) efforts.Add(effort);
                models.Add(new AgentModel(id, Str(m, "displayName") ?? id, m?["isDefault"]?.GetValue<bool>() == true, Str(m, "defaultReasoningEffort"), efforts));
            }
            cursor = Str(result, "nextCursor");
        } while (cursor is not null);
        return [.. models.OrderByDescending(m => m.Default)];
    }

    private async Task Initialize(DateTime deadline)
    {
        await Request("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = "velo", ["title"] = "Velo", ["version"] = "0.1.0" },
        }, deadline);
        await Send(new JsonObject { ["method"] = "initialized" });
    }

    // ---- process -------------------------------------------------------------------------------

    private void Start(string exe, bool npm)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = req.Directory,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            InheritedHandles = [],
        };
        if (OperatingSystem.IsWindows()) psi.KillOnParentExit = true;
        psi.ArgumentList.Add("app-server");
        if (npm) psi.Environment["CODEX_MANAGED_BY_NPM"] = "1";
        if (req.Environment is not null)
            foreach (var (k, v) in req.Environment) psi.Environment[k] = v;
        try
        {
            _proc = Process.Start(psi) ?? throw new Failure("Process.Start returned null");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new Failure($"Failed to start '{exe}': {ex.Message}");
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _proc.StandardOutput.ReadLineAsync() is { } line)
                {
                    _lastActivity = Environment.TickCount64;
                    if (line.Length > 0) _lines.Writer.TryWrite(line);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            finally { _lines.Writer.TryComplete(); }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _proc.StandardError.ReadLineAsync() is { } line)
                {
                    _lastActivity = Environment.TickCount64;
                    lock (_stderr)
                    {
                        _stderr.Enqueue(line.Length > 500 ? line[..500] + "..." : line);
                        if (_stderr.Count > 30) _stderr.Dequeue();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });
    }

    public void Cleanup()
    {
        if (_proc is null) return;
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        try { _proc.WaitForExit(5000); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        _proc.Dispose();
    }

    private string WithStderr(string error)
    {
        string tail;
        lock (_stderr) tail = string.Join("\n", _stderr);
        if (tail.Length > 2000) tail = tail[^2000..];
        return tail.Length == 0 ? error : $"{error}\nstderr (tail):\n{tail}";
    }

    // ---- JSON-RPC ------------------------------------------------------------------------------

    private async Task Send(JsonObject message)
    {
        try
        {
            await _proc!.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), ct);
            await _proc.StandardInput.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new Failure($"could not write to codex: {ex.Message}");
        }
    }

    private async Task<JsonNode> Request(string method, JsonObject parameters, DateTime deadline)
    {
        string id = (++_nextId).ToString();
        await Send(new JsonObject { ["id"] = _nextId, ["method"] = method, ["params"] = parameters });
        while (true)
        {
            JsonObject m = await Next(deadline);
            if (m["method"] is not null) { await Handle(m); continue; }
            if (m["id"]?.ToJsonString() != id) continue;
            if (m["error"] is JsonNode e) throw new Failure($"{method} failed: {Str(e, "message") ?? e.ToJsonString()}");
            return m["result"] ?? new JsonObject();
        }
    }

    /// <summary>
    /// Next JSON message. Without a deadline only <see cref="CodexAgent.IdleTimeout"/> of total silence fails.
    /// </summary>
    private async Task<JsonObject> Next(DateTime? deadline)
    {
        while (true)
        {
            TimeSpan wait = deadline is { } d
                ? d - DateTime.UtcNow
                : TimeSpan.FromMilliseconds(_lastActivity - Environment.TickCount64) + CodexAgent.IdleTimeout;
            if (wait <= TimeSpan.Zero)
                throw new Failure(deadline is null
                    ? $"codex produced no output for {CodexAgent.IdleTimeout.TotalMinutes:0.#} minutes (idle timeout)"
                    : $"codex handshake did not complete within {CodexAgent.HandshakeTimeout.TotalSeconds:0} s");

            string line;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(wait);
            try { line = await _lines.Reader.ReadAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            catch (ChannelClosedException) { throw new Failure(await ExitMessage()); }

            try
            {
                if (JsonNode.Parse(line) is JsonObject obj) return obj;
            }
            catch (JsonException) { }
            log("ignored non-JSON output from codex: " + Truncate(line, 120));
        }
    }

    private async Task<string> ExitMessage()
    {
        try { await _proc!.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(2), ct); } catch (TimeoutException) { }
        await Task.Delay(100, ct); // let the stderr reader catch up
        return _proc!.HasExited ? $"codex exited unexpectedly (exit code {_proc.ExitCode})" : "codex closed its output stream";
    }

    // ---- messages ------------------------------------------------------------------------------

    private async Task Handle(JsonObject m)
    {
        string? method = Str(m, "method");
        if (method is null) return;
        JsonNode? p = m["params"];

        if (m["id"] is { } id) // server request: decline everything
        {
            var reply = new JsonObject { ["id"] = id.DeepClone() };
            switch (method)
            {
                case "item/commandExecution/requestApproval" or "item/fileChange/requestApproval":
                    reply["result"] = new JsonObject { ["decision"] = "decline" }; break;
                case "execCommandApproval" or "applyPatchApproval":
                    reply["result"] = new JsonObject { ["decision"] = "denied" }; break;
                case "mcpServer/elicitation/request":
                    reply["result"] = new JsonObject { ["action"] = "decline" }; break;
                case "item/tool/requestUserInput":
                    reply["result"] = new JsonObject { ["answers"] = new JsonObject() }; break;
                case "item/permissions/requestApproval":
                    reply["result"] = new JsonObject { ["permissions"] = new JsonObject(), ["scope"] = "turn" }; break;
                default:
                    reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"velo does not handle '{method}'" }; break;
            }
            log($"declined request {method}: {Truncate(Str(p, "command") ?? Str(p, "reason") ?? Str(p, "message") ?? "", 150)}");
            await Send(reply);
            return;
        }

        switch (method)
        {
            case "thread/tokenUsage/updated":
                if (p?["tokenUsage"]?["total"]?["totalTokens"] is JsonValue t && t.TryGetValue(out long total)) _tokens = total;
                break;
            case "error":
                string msg = Str(p, "error", "message") ?? "unknown error";
                bool retry = p?["willRetry"] is JsonValue r && r.TryGetValue(out bool b) && b;
                if (!retry) _lastError = msg;
                log($"error: {Truncate(msg, 300)}{(retry ? " (will retry)" : "")}");
                break;
            case "warning":
                log("warning: " + Truncate(Str(p, "message") ?? "", 300));
                break;
            case "item/completed":
                DescribeItem(p?["item"]);
                break;
            case "turn/completed":
                string status = Str(p, "turn", "status") ?? "completed";
                _turnDone = true;
                if (status != "completed")
                    _turnFailure = $"turn {status}: {Str(p, "turn", "error", "message") ?? _lastError ?? "no error reported"}";
                break;
        }
    }

    private void DescribeItem(JsonNode? item)
    {
        switch (Str(item, "type"))
        {
            case "agentMessage":
                _finalMessage = Str(item, "text") ?? _finalMessage;
                log("agent: " + Truncate(Str(item, "text") ?? "", 400));
                break;
            case "commandExecution":
                log($"$ {Truncate(Str(item, "command") ?? "", 200)} -> exit {item?["exitCode"]?.ToJsonString() ?? "?"}");
                break;
            case "fileChange":
                var files = (item?["changes"] as JsonArray ?? [])
                    .Select(c => $"{Str(c, "kind", "type") ?? Str(c, "kind") ?? "change"} {Str(c, "path")}");
                log("files: " + Truncate(string.Join(", ", files), 400));
                break;
            case "reasoning":
                var parts = (item?["summary"] as JsonArray ?? []).Select(s => s?.GetValueKind() == JsonValueKind.String ? s.GetValue<string>() : null);
                string summary = string.Join(" ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (summary.Length > 0) log("reasoning: " + Truncate(summary, 300));
                break;
            case "mcpToolCall" or "dynamicToolCall":
                log($"tool {Str(item, "server")}/{Str(item, "tool")} {Str(item, "status")}");
                break;
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static string? Str(JsonNode? node, params string[] path)
    {
        foreach (string key in path) node = node is JsonObject o ? o[key] : null;
        return node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    }

    private static string Truncate(string text, int max)
    {
        text = text.Replace("\r", "").Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + $"... [{text.Length - max} more chars]";
    }
}
