using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace Velo.Tests;

[Collection("Sequential")]
public sealed class HttpApiTests
{
    [Fact]
    public async Task EveryApiRequestNeedsTheTokenAndALoopbackHost()
    {
        await using var harness = new CliFixture();
        using var api = await ServeAsync(harness, harness.Workspace());

        var anonymous = await api.SendAsync(HttpMethod.Get, "/api/v1/state", withToken: false);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
        Assert.Equal("unauthorized", anonymous.ErrorCode);

        var wrong = await api.SendAsync(HttpMethod.Get, "/api/v1/state", token: "not-the-token");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);

        var foreignHost = await api.SendAsync(HttpMethod.Get, "/api/v1/state", host: "example.com");
        Assert.Equal(HttpStatusCode.Forbidden, foreignHost.Status);
        Assert.Equal("forbidden_host", foreignHost.ErrorCode);

        var ok = await api.SendAsync(HttpMethod.Get, "/api/v1/state");
        Assert.Equal(HttpStatusCode.OK, ok.Status);
    }

    [Fact]
    public async Task StartWithoutPortWritesNoToken()
    {
        await using var harness = new CliFixture();

        var run = await harness.RunAsync(harness.Workspace(), "start", "--once");

        Assert.Equal(0, run.ExitCode);
        Assert.False(File.Exists(Path.Combine(harness.VeloHome, "start.token")));
    }

    [Fact]
    public async Task AddedWorkIsReportedThroughStateListAndDetail()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        using var api = await ServeAsync(harness, workspace);

        var add = await api.SendAsync(HttpMethod.Post, "/api/v1/work", new { prompt = "over http", workspace });
        Assert.Equal(HttpStatusCode.Created, add.Status);
        var id = add.Body!["work_id"]!.GetValue<string>();
        Assert.Equal("pending", add.Body["status"]!.GetValue<string>());

        await WaitForStatusAsync(api, id, "succeeded");
        var detail = await api.SendAsync(HttpMethod.Get, $"/api/v1/{id}");
        Assert.Equal("over http", detail.Body!["prompt"]!.GetValue<string>());
        Assert.Equal(workspace, detail.Body["workspace"]!["path"]!.GetValue<string>());
        Assert.Equal(1, detail.Body["attempts"]!.GetValue<int>());
        Assert.Equal(0, detail.Body["exit_code"]!.GetValue<int>());
        Assert.True(detail.Body["logs"]!["exists"]!.GetValue<bool>());

        var state = await api.SendAsync(HttpMethod.Get, "/api/v1/state");
        Assert.Equal(HttpStatusCode.OK, state.Status);
        Assert.Equal(1, state.Body!["counts"]!["succeeded"]!.GetValue<int>());
        Assert.Empty(state.Body["pending"]!.AsArray());

        var succeeded = await api.SendAsync(HttpMethod.Get, "/api/v1/work?state=succeeded");
        Assert.Equal(id, succeeded.Body!["work"]![0]!["work_id"]!.GetValue<string>());
        var failed = await api.SendAsync(HttpMethod.Get, "/api/v1/work?state=failed");
        Assert.Empty(failed.Body!["work"]!.AsArray());
        var invalid = await api.SendAsync(HttpMethod.Get, "/api/v1/work?state=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
        Assert.Equal("invalid_request", invalid.ErrorCode);
    }

    [Fact]
    public async Task InvalidRequestsGetJsonErrors()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        using var api = await ServeAsync(harness, workspace);

        var relative = await api.SendAsync(HttpMethod.Post, "/api/v1/work", new { prompt = "x", workspace = "relative" });
        Assert.Equal(HttpStatusCode.BadRequest, relative.Status);
        Assert.Equal("invalid_request", relative.ErrorCode);

        var noPrompt = await api.SendAsync(HttpMethod.Post, "/api/v1/work", new { workspace });
        Assert.Equal(HttpStatusCode.BadRequest, noPrompt.Status);
        Assert.Equal("invalid_request", noPrompt.ErrorCode);

        var missing = await api.SendAsync(HttpMethod.Get, "/api/v1/20990101000000-missing");
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Equal("work_not_found", missing.ErrorCode);
    }

    [Fact]
    public async Task PendingWorkCanBeEditedAndRunningWorkCanBeCancelled()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        var gate = Path.Combine(harness.VeloHome, "http-gate");
        using var api = await ServeAsync(harness, workspace, concurrency: 1);

        var running = await AddAsync(api, workspace, $"wait:{gate}");
        await harness.WaitForFileAsync(gate + ".started");
        var pending = await AddAsync(api, workspace, "queued");

        var edit = await api.SendAsync(HttpMethod.Patch, $"/api/v1/{pending}", new { prompt = "edited" });
        Assert.Equal(HttpStatusCode.OK, edit.Status);
        Assert.Equal("edited", edit.Body!["prompt"]!.GetValue<string>());

        var editRunning = await api.SendAsync(HttpMethod.Patch, $"/api/v1/{running}", new { prompt = "too late" });
        Assert.Equal(HttpStatusCode.Conflict, editRunning.Status);
        Assert.Equal("work_not_pending", editRunning.ErrorCode);

        var deleteRunning = await api.SendAsync(HttpMethod.Delete, $"/api/v1/{running}");
        Assert.Equal(HttpStatusCode.Conflict, deleteRunning.Status);
        Assert.Equal("work_running", deleteRunning.ErrorCode);

        var cancel = await api.SendAsync(HttpMethod.Post, $"/api/v1/{running}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancel.Status);
        Assert.Equal("cancel_requested", cancel.Body!["result"]!.GetValue<string>());
        await WaitForStatusAsync(api, running, "failed");
        var detail = await api.SendAsync(HttpMethod.Get, $"/api/v1/{running}");
        Assert.Equal("Cancelled by user.", detail.Body!["last_error"]!.GetValue<string>());

        var cancelAgain = await api.SendAsync(HttpMethod.Post, $"/api/v1/{running}/cancel");
        Assert.Equal(HttpStatusCode.Conflict, cancelAgain.Status);
        Assert.Equal("work_not_cancellable", cancelAgain.ErrorCode);

        var deleteFinished = await api.SendAsync(HttpMethod.Delete, $"/api/v1/{running}");
        Assert.Equal(HttpStatusCode.NoContent, deleteFinished.Status);
        var gone = await api.SendAsync(HttpMethod.Get, $"/api/v1/{running}");
        Assert.Equal(HttpStatusCode.NotFound, gone.Status);

        await WaitForStatusAsync(api, pending, "succeeded");
    }

    [Fact]
    public async Task FailedWorkCanBeRetriedOverHttp()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        using var api = await ServeAsync(harness, workspace);
        var id = await AddAsync(api, workspace, "fail:http failure");
        await WaitForStatusAsync(api, id, "failed");
        var failed = await api.SendAsync(HttpMethod.Get, "/api/v1/work?state=failed");
        Assert.Contains("http failure", failed.Body!["work"]![0]!["last_error"]!.GetValue<string>());

        var retry = await api.SendAsync(HttpMethod.Post, $"/api/v1/{id}/retry");
        Assert.Equal(HttpStatusCode.OK, retry.Status);
        Assert.Equal("pending", retry.Body!["status"]!.GetValue<string>());

        var missing = await api.SendAsync(HttpMethod.Post, "/api/v1/20990101000000-missing/retry");
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
    }

    [Fact]
    public async Task ReviewAndDiscardAreServedOverHttp()
    {
        await using var harness = new CliFixture();
        var repository = harness.Workspace("repository");
        await TestGit.InitRepositoryAsync(repository);
        var plain = harness.Workspace("plain");
        using var api = await ServeAsync(harness, repository);

        var add = await api.SendAsync(
            HttpMethod.Post,
            "/api/v1/work",
            new { prompt = "review over http", workspace = repository, worktree = true });
        Assert.Equal(HttpStatusCode.Created, add.Status);
        var id = add.Body!["work_id"]!.GetValue<string>();
        await WaitForStatusAsync(api, id, "succeeded");

        var detail = await api.SendAsync(HttpMethod.Get, $"/api/v1/{id}");
        Assert.True(detail.Body!["reviewable"]!.GetValue<bool>());
        Assert.False(detail.Body["discarded"]!.GetValue<bool>());

        var diff = await api.SendAsync(HttpMethod.Get, $"/api/v1/{id}/diff");
        Assert.Equal(HttpStatusCode.OK, diff.Status);
        Assert.Contains("codex-ran.txt", diff.Body!["stat"]!.GetValue<string>());
        Assert.Contains("+review over http", diff.Body["patch"]!.GetValue<string>());
        Assert.False(diff.Body["truncated"]!.GetValue<bool>());

        var discard = await api.SendAsync(HttpMethod.Post, $"/api/v1/{id}/discard");
        Assert.Equal(HttpStatusCode.OK, discard.Status);
        Assert.True(discard.Body!["discarded"]!.GetValue<bool>());
        var after = await api.SendAsync(HttpMethod.Get, $"/api/v1/{id}");
        Assert.True(after.Body!["discarded"]!.GetValue<bool>());
        Assert.False(after.Body["reviewable"]!.GetValue<bool>());

        var discardAgain = await api.SendAsync(HttpMethod.Post, $"/api/v1/{id}/discard");
        Assert.Equal(HttpStatusCode.Conflict, discardAgain.Status);
        Assert.Equal("work_not_reviewable", discardAgain.ErrorCode);
        var diffAfter = await api.SendAsync(HttpMethod.Get, $"/api/v1/{id}/diff");
        Assert.Equal(HttpStatusCode.Conflict, diffAfter.Status);
        Assert.Equal("work_not_reviewable", diffAfter.ErrorCode);

        var plainId = await AddAsync(api, plain, "plain over http");
        await WaitForStatusAsync(api, plainId, "succeeded");
        var plainDiff = await api.SendAsync(HttpMethod.Get, $"/api/v1/{plainId}/diff");
        Assert.Equal(HttpStatusCode.Conflict, plainDiff.Status);
        Assert.Equal("work_not_reviewable", plainDiff.ErrorCode);

        var unknown = await api.SendAsync(HttpMethod.Post, "/api/v1/20990101000000-missing/discard");
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal("work_not_found", unknown.ErrorCode);
        var wrongMethod = await api.SendAsync(HttpMethod.Get, $"/api/v1/{id}/discard");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.Status);
    }

    [Fact]
    public async Task AcceptAppliesTheChangesOverHttpAndRefusesARepeat()
    {
        await using var harness = new CliFixture();
        var repository = harness.Workspace("repository");
        await TestGit.InitRepositoryAsync(repository);
        var plain = harness.Workspace("plain");
        using var api = await ServeAsync(harness, repository);

        var add = await api.SendAsync(
            HttpMethod.Post,
            "/api/v1/work",
            new { prompt = "accept over http", workspace = repository, worktree = true });
        Assert.Equal(HttpStatusCode.Created, add.Status);
        var id = add.Body!["work_id"]!.GetValue<string>();
        await WaitForStatusAsync(api, id, "succeeded");

        var accept = await api.SendAsync(HttpMethod.Post, $"/api/v1/{id}/accept");
        Assert.Equal(HttpStatusCode.OK, accept.Status);
        Assert.True(accept.Body!["accepted"]!.GetValue<bool>());
        Assert.Equal(
            "accept over http",
            await File.ReadAllTextAsync(Path.Combine(repository, "codex-ran.txt")));

        var detail = await api.SendAsync(HttpMethod.Get, $"/api/v1/{id}");
        Assert.True(detail.Body!["accepted"]!.GetValue<bool>());
        Assert.True(detail.Body["reviewable"]!.GetValue<bool>());

        var again = await api.SendAsync(HttpMethod.Post, $"/api/v1/{id}/accept");
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Equal("work_already_accepted", again.ErrorCode);

        var discard = await api.SendAsync(HttpMethod.Post, $"/api/v1/{id}/discard");
        Assert.Equal(HttpStatusCode.OK, discard.Status);

        var plainId = await AddAsync(api, plain, "plain accept over http");
        await WaitForStatusAsync(api, plainId, "succeeded");
        var plainAccept = await api.SendAsync(HttpMethod.Post, $"/api/v1/{plainId}/accept");
        Assert.Equal(HttpStatusCode.Conflict, plainAccept.Status);
        Assert.Equal("work_not_reviewable", plainAccept.ErrorCode);

        var unknown = await api.SendAsync(HttpMethod.Post, "/api/v1/20990101000000-missing/accept");
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        var wrongMethod = await api.SendAsync(HttpMethod.Get, $"/api/v1/{id}/accept");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.Status);
    }

    [Fact]
    public async Task AddWithWorktreeReportsAMissingGitAsAServerError()
    {
        await using var harness = new CliFixture();
        var repository = harness.Workspace("repository");
        await TestGit.InitRepositoryAsync(repository);
        // A PATH with no executables in it: the server cannot start git, but it still runs.
        var emptyPath = harness.Workspace("empty-path");
        using var api = await ServeAsync(harness, repository, path: emptyPath);

        var add = await api.SendAsync(
            HttpMethod.Post,
            "/api/v1/work",
            new { prompt = "needs git", workspace = repository, worktree = true });
        Assert.Equal(HttpStatusCode.InternalServerError, add.Status);
        Assert.Equal("git_failed", add.ErrorCode);

        var state = await api.SendAsync(HttpMethod.Get, "/api/v1/state");
        Assert.Equal(HttpStatusCode.OK, state.Status);
    }

    [Fact]
    public async Task LogPagesReturnCompleteOutputFromAnOffset()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        using var api = await ServeAsync(harness, workspace);
        var id = await AddAsync(api, workspace, "log paging");
        await WaitForStatusAsync(api, id, "succeeded");
        var size = new FileInfo(Path.Combine(harness.VeloHome, "logs", id + ".log")).Length;

        var first = await api.SendAsync(HttpMethod.Get, $"/api/v1/{id}/log");
        Assert.Equal(0, first.Body!["offset"]!.GetValue<long>());
        Assert.Equal(size, first.Body["next_offset"]!.GetValue<long>());
        Assert.Contains("Starting: codex", first.Body["text"]!.GetValue<string>());

        var rest = await api.SendAsync(HttpMethod.Get, $"/api/v1/{id}/log?offset={size}");
        Assert.Equal(size, rest.Body!["offset"]!.GetValue<long>());
        Assert.Equal(size, rest.Body["next_offset"]!.GetValue<long>());
        Assert.Equal(string.Empty, rest.Body["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task RefreshAcceptsARequestAndReportsItsOperations()
    {
        await using var harness = new CliFixture();
        using var api = await ServeAsync(harness, harness.Workspace());

        var refresh = await api.SendAsync(HttpMethod.Post, "/api/v1/refresh");

        Assert.Equal(HttpStatusCode.Accepted, refresh.Status);
        Assert.True(refresh.Body!["queued"]!.GetValue<bool>());
        Assert.Equal("poll", refresh.Body["operations"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task UnsupportedMethodsOnKnownRoutesGetAJsonMethodNotAllowed()
    {
        await using var harness = new CliFixture();
        using var api = await ServeAsync(harness, harness.Workspace());

        (HttpMethod Method, string Path)[] cases =
        [
            (HttpMethod.Delete, "/api/v1/state"),
            (HttpMethod.Get, "/api/v1/refresh"),
            (HttpMethod.Put, "/api/v1/work"),
            (HttpMethod.Post, "/api/v1/some-id"),
            (HttpMethod.Get, "/api/v1/some-id/cancel")
        ];
        foreach (var (method, path) in cases)
        {
            var response = await api.SendAsync(method, path);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.Status);
            Assert.Equal("method_not_allowed", response.ErrorCode);
        }

        var unknown = await api.SendAsync(HttpMethod.Get, "/api/v1/some-id/unknown");
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal("not_found", unknown.ErrorCode);

        var root = await api.SendAsync(HttpMethod.Get, "/nope", withToken: false);
        Assert.Equal(HttpStatusCode.NotFound, root.Status);
        Assert.Equal("not_found", root.ErrorCode);
    }

    [Fact]
    public async Task DashboardIsServedWithTheTokenAndAStrictPolicy()
    {
        await using var harness = new CliFixture();
        using var api = await ServeAsync(harness, harness.Workspace());
        var token = await harness.ReadAllTextAsync(Path.Combine(harness.VeloHome, "start.token"));

        using var response = await api.SendRawAsync(HttpMethod.Get, "/", withToken: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"<meta name=\"velo-token\" content=\"{token}\">", html);
        Assert.DoesNotContain("__VELO_TOKEN__", html);
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task DashboardScriptAndStylesheetAreServed()
    {
        await using var harness = new CliFixture();
        using var api = await ServeAsync(harness, harness.Workspace());

        using var script = await api.SendRawAsync(HttpMethod.Get, "/dashboard.js", withToken: false);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Contains("X-Velo-Token", await script.Content.ReadAsStringAsync());

        using var style = await api.SendRawAsync(HttpMethod.Get, "/dashboard.css", withToken: false);
        Assert.Equal(HttpStatusCode.OK, style.StatusCode);
        Assert.Equal("text/css", style.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task DashboardRefusesAForeignHost()
    {
        await using var harness = new CliFixture();
        using var api = await ServeAsync(harness, harness.Workspace());

        using var response = await api.SendRawAsync(HttpMethod.Get, "/", withToken: false, host: "evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PortZeroPicksAFreePortAndPrintsTheAddress()
    {
        await using var harness = new CliFixture();
        using var process = harness.StartServing(harness.Workspace(), port: 0);
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var match = Regex.Match(line ?? string.Empty, @"http://127\.0\.0\.1:(\d+)");
        Assert.True(match.Success, $"Unexpected first line: {line}");
        var port = int.Parse(match.Groups[1].Value);
        Assert.NotEqual(0, port);

        await harness.WaitForFileAsync(Path.Combine(harness.VeloHome, "start.pid"));
        var token = await harness.ReadAllTextAsync(Path.Combine(harness.VeloHome, "start.token"));
        using var api = new Api(process, port, token);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/api/v1/state")).Status);
    }

    [Fact]
    public async Task DetachedStartServesTheApiInTheBackground()
    {
        await using var harness = new CliFixture();
        var port = FreePort();

        var start = await harness.RunAsync(harness.Workspace(), "start", "-d", "--port", port.ToString());
        Assert.Equal(0, start.ExitCode);
        await harness.WaitForFileAsync(Path.Combine(harness.VeloHome, "start.pid"));
        var processId = int.Parse(await harness.ReadAllTextAsync(Path.Combine(harness.VeloHome, "start.pid")));
        var token = await harness.ReadAllTextAsync(Path.Combine(harness.VeloHome, "start.token"));
        using var api = new Api(Process.GetProcessById(processId), port, token);

        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/api/v1/state")).Status);
        var log = await harness.ReadAllTextAsync(Path.Combine(harness.VeloHome, "start.log"));
        Assert.Contains($"HTTP API listening on http://127.0.0.1:{port}.", log);
    }

    [Fact]
    public async Task PortInUseIsReportedAsAnError()
    {
        await using var harness = new CliFixture();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var run = await harness.RunAsync(harness.Workspace(), "start", "--port", port.ToString());

            Assert.Equal(1, run.ExitCode);
            Assert.Contains($"Cannot listen on 127.0.0.1:{port}", run.StandardError);
            Assert.False(File.Exists(Path.Combine(harness.VeloHome, "start.token")));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ASecondStartPreservesTheRunningServerToken()
    {
        await using var harness = new CliFixture();
        var workspace = harness.Workspace();
        using var api = await ServeAsync(harness, workspace);
        var tokenFile = Path.Combine(harness.VeloHome, "start.token");
        var token = await harness.ReadAllTextAsync(tokenFile);

        var duplicate = await harness.RunAsync(workspace, "start", "--port", FreePort().ToString());

        Assert.Equal(1, duplicate.ExitCode);
        Assert.Equal(token, await harness.ReadAllTextAsync(tokenFile));
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/api/v1/state")).Status);
    }

    [Fact]
    public async Task RunLockIsCheckedBeforeTryingToStartTheApi()
    {
        await using var harness = new CliFixture();
        var config = new Config(harness.VeloHome);
        config.Initialize();
        using var held = FileLock.OpenExclusive(config.RunLock);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new Cli(config).RunQueueAsync(exitWhenIdle: false, background: false, port));

            Assert.Contains("Another velo run", error.Message);
            Assert.False(File.Exists(config.TokenFile));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("--port 70000")]
    [InlineData("--port x")]
    [InlineData("--port")]
    [InlineData("--once --port 1")]
    [InlineData("--once -d")]
    public async Task InvalidStartOptionsReturnUsage(string options)
    {
        await using var harness = new CliFixture();
        var arguments = new[] { "start" }.Concat(options.Split(' ')).ToArray();

        var result = await harness.RunAsync(harness.RootPath, arguments);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Usage: velo start [--once] [-d] [--port N]", result.StandardError);
    }

    private static async Task<Api> ServeAsync(CliFixture harness, string workspace, int concurrency = 2, string? path = null)
    {
        var port = FreePort();
        var process = harness.StartServing(workspace, port, concurrency, path);
        // start.pid is written after the HTTP listener is up, so it marks the server as ready.
        await harness.WaitForFileAsync(Path.Combine(harness.VeloHome, "start.pid"));
        var token = await harness.ReadAllTextAsync(Path.Combine(harness.VeloHome, "start.token"));
        return new Api(process, port, token);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> AddAsync(Api api, string workspace, string prompt)
    {
        var add = await api.SendAsync(HttpMethod.Post, "/api/v1/work", new { prompt, workspace });
        Assert.Equal(HttpStatusCode.Created, add.Status);
        return add.Body!["work_id"]!.GetValue<string>();
    }

    private static async Task WaitForStatusAsync(Api api, string id, string status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var detail = await api.SendAsync(HttpMethod.Get, $"/api/v1/{id}");
            if (detail.Body?["status"]?.GetValue<string>() == status) return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Timed out waiting for {id} to become {status}.");
    }

    private sealed class Api(Process process, int port, string validToken) : IDisposable
    {
        private readonly HttpClient _client = new(new SocketsHttpHandler { UseProxy = false });

        public async Task<HttpResponseMessage> SendRawAsync(
            HttpMethod method,
            string path,
            bool withToken = true,
            string? host = null)
        {
            using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}");
            if (withToken) request.Headers.Add("X-Velo-Token", validToken);
            if (host is not null) request.Headers.Host = host;
            return await _client.SendAsync(request);
        }

        public async Task<ApiResponse> SendAsync(
            HttpMethod method,
            string path,
            object? body = null,
            bool withToken = true,
            string? token = null,
            string? host = null)
        {
            using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}");
            if (withToken) request.Headers.Add("X-Velo-Token", token ?? validToken);
            if (host is not null) request.Headers.Host = host;
            if (body is not null) request.Content = JsonContent.Create(body);

            using var response = await _client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return new ApiResponse(response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
        }

        public void Dispose()
        {
            _client.Dispose();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
        }
    }

    private sealed record ApiResponse(HttpStatusCode Status, JsonNode? Body)
    {
        public string? ErrorCode => Body?["error"]?["code"]?.GetValue<string>();
    }
}
