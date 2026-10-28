using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Velo.Tests;

public class ServerLockTests
{
    [Fact]
    public async Task SameHomeOnAnotherPort_IsRejected_AndKilledProcessReleasesLock()
    {
        using var temp = new TempDir();
        string home = temp.Combine("home");
        int port = ServerFixture.FreePort();
        ServerFixture.WriteConfig(home, port);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(2),
        };
        var output = new StringBuilder();
        using Process first = ServerFixture.Start(home, ["serve"], output);
        Process? restarted = null;
        try
        {
            async Task WaitForServer(Process process) => await Wait.UntilAsync(async () =>
            {
                Assert.False(process.HasExited, output.ToString());
                try { return (await http.GetAsync("/api/issues")).IsSuccessStatusCode; }
                catch (HttpRequestException) { return false; }
                catch (TaskCanceledException) { return false; }
            }, "server to listen");

            await WaitForServer(first);
            ServerFixture.WriteConfig(home, ServerFixture.FreePort());
            (int code, _, string error) = await ServerFixture.Cli(home, "serve");
            Assert.Equal(1, code);
            Assert.Contains("cannot lock data directory", error);
            Assert.False(first.HasExited);

            var other = new ServerFixture();
            try
            {
                await other.InitializeAsync();
            }
            finally
            {
                await other.DisposeAsync();
            }

            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync();
            Assert.True(File.Exists(Path.Combine(home, "server.lock")));
            ServerFixture.WriteConfig(home, port);
            restarted = ServerFixture.Start(home, ["serve"], output);
            await WaitForServer(restarted);
        }
        finally
        {
            if (!first.HasExited) first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync();
            if (restarted is not null)
            {
                if (!restarted.HasExited) restarted.Kill(entireProcessTree: true);
                await restarted.WaitForExitAsync();
                restarted.Dispose();
            }
        }
    }
}

public class HttpTests(ServerFixture s) : IClassFixture<ServerFixture>
{
    private static string Error(string body) => JsonDocument.Parse(body).RootElement.GetProperty("error").GetString()!;

    [Fact]
    public async Task Host_MustBeLoopback()
    {
        (HttpStatusCode status, string body, _) = await s.Send(HttpMethod.Get, "/api/issues", host: "evil.example");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Host", Error(body));

        Assert.Equal(HttpStatusCode.OK, (await s.Send(HttpMethod.Get, "/api/issues", host: "localhost:" + s.Port)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Send(HttpMethod.Get, "/", host: "evil.example")).Status); // static files too
    }

    [Fact]
    public async Task NonGetRequests_NeedTheVeloHeader()
    {
        (HttpStatusCode post, string body, _) = await s.Send(HttpMethod.Post, "/api/issues", """{"title":"x"}""", velo: false);
        Assert.Equal(HttpStatusCode.Forbidden, post);
        Assert.Contains("X-Velo", Error(body));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Send(HttpMethod.Delete, "/api/issues/1", velo: false)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Send(HttpMethod.Post, "/api/issues/1/approve", velo: false)).Status);
        Assert.Equal(HttpStatusCode.OK, (await s.Send(HttpMethod.Get, "/api/issues", velo: false)).Status);
    }

    [Fact]
    public async Task Errors_AreJsonWithTheRightStatus()
    {
        (HttpStatusCode s404, string b404, HttpResponseMessage r404) = await s.Send(HttpMethod.Get, "/api/issues/9999");
        Assert.Equal(HttpStatusCode.NotFound, s404);
        Assert.Contains("9999", Error(b404));
        Assert.Equal("application/json", r404.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Send(HttpMethod.Get, "/api/nothing-here")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Send(HttpMethod.Get, "/api/issues/abc")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Send(HttpMethod.Post, "/api/issues", """{"title":"  "}""")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Send(HttpMethod.Post, "/api/issues", "{ not json")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Send(HttpMethod.Post, "/api/issues", "")).Status);

        using var plain = new TempDir();
        (HttpStatusCode notGit, string bGit, _) = await s.Send(HttpMethod.Post, "/api/issues", JsonSerializer.Serialize(new { title = "x", directory = plain.Path }));
        Assert.Equal(HttpStatusCode.BadRequest, notGit);
        Assert.Contains("not a git repository", Error(bGit));

        int n = await s.Create("approve me twice");
        await s.WaitReview(n);
        Assert.Equal(HttpStatusCode.OK, (await s.Send(HttpMethod.Post, $"/api/issues/{n}/approve")).Status);
        (HttpStatusCode again, string bAgain, _) = await s.Send(HttpMethod.Post, $"/api/issues/{n}/approve");
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Contains("only review issues", Error(bAgain));
    }

    [Fact]
    public async Task CreateListGetDelete()
    {
        int n = await s.Create("crud");
        (_, string created, _) = await s.Send(HttpMethod.Get, $"/api/issues/VL-{n}");
        JsonElement issue = JsonDocument.Parse(created).RootElement;
        Assert.Equal($"VL-{n}", issue.GetProperty("key").GetString());
        Assert.Equal("crud", issue.GetProperty("title").GetString());
        await s.WaitReview(n);

        Assert.Equal(HttpStatusCode.OK, (await s.Send(HttpMethod.Post, $"/api/issues/{n}/approve")).Status);
        JsonElement open = JsonDocument.Parse((await s.Send(HttpMethod.Get, "/api/issues")).Body).RootElement;
        JsonElement all = JsonDocument.Parse((await s.Send(HttpMethod.Get, "/api/issues?all=true")).Body).RootElement;
        Assert.DoesNotContain(open.EnumerateArray(), i => i.GetProperty("number").GetInt32() == n); // done issues are hidden
        Assert.Contains(all.EnumerateArray(), i => i.GetProperty("number").GetInt32() == n && i.GetProperty("status").GetString() == "done");

        Assert.Equal(HttpStatusCode.NoContent, (await s.Send(HttpMethod.Delete, $"/api/issues/{n}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Send(HttpMethod.Get, $"/api/issues/{n}")).Status);
    }

    [Fact]
    public async Task IssueList_IsRevalidatedByETag_304UntilAnIssueChanges()
    {
        // other tests' runs may still change issues in the background: wait for one unchanged revalidation
        string? etag = null;
        await Wait.UntilAsync(async () =>
        {
            (HttpStatusCode status, _, HttpResponseMessage r) = await s.Send(HttpMethod.Get, "/api/issues?all=true", ifNoneMatch: etag);
            if (status == HttpStatusCode.NotModified) return true;
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(r.Headers.CacheControl?.NoCache);
            etag = r.Headers.ETag!.Tag;
            return false;
        }, "a 304 for an unchanged issue list");

        await s.Create("changes the list");

        (HttpStatusCode changed, string body, HttpResponseMessage response) = await s.Send(HttpMethod.Get, "/api/issues?all=true", ifNoneMatch: etag);
        Assert.Equal(HttpStatusCode.OK, changed);
        Assert.NotEqual(etag, response.Headers.ETag!.Tag);
        Assert.Contains(JsonDocument.Parse(body).RootElement.EnumerateArray(), i => i.GetProperty("title").GetString() == "changes the list");
    }

    [Fact]
    public async Task Run_WithAndWithoutBody_ReturnsTheIssue()
    {
        int n = await s.Create("rerun");
        await s.WaitReview(n);

        (HttpStatusCode status, string body, _) = await s.Send(HttpMethod.Post, $"/api/issues/{n}/run", """{"message":"again"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(JsonDocument.Parse(body).RootElement.GetProperty("status").GetString(), new[] { "todo", "running" });
        JsonElement second = await s.WaitReview(n, runs: 2);
        Assert.Equal("again", second.GetProperty("runs")[1].GetProperty("message").GetString());

        Assert.Equal(HttpStatusCode.OK, (await s.Send(HttpMethod.Post, $"/api/issues/{n}/run")).Status); // no body at all
        await s.WaitReview(n, runs: 3);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Send(HttpMethod.Post, $"/api/issues/{n}/stop")).Status);
    }

    [Fact]
    public async Task Logs_UseByteOffsets()
    {
        int n = await s.Create("logs");
        await s.WaitReview(n);

        JsonElement first = JsonDocument.Parse((await s.Send(HttpMethod.Get, $"/api/issues/{n}/logs")).Body).RootElement;
        Assert.Equal(1, first.GetProperty("run").GetInt32());
        Assert.True(first.GetProperty("done").GetBoolean());
        Assert.Contains($"velo: VL-{n} run 1", first.GetProperty("text").GetString());
        long offset = first.GetProperty("offset").GetInt64();
        Assert.True(offset > 0);

        JsonElement next = JsonDocument.Parse((await s.Send(HttpMethod.Get, $"/api/issues/{n}/logs?run=1&offset={offset}")).Body).RootElement;
        Assert.Equal("", next.GetProperty("text").GetString());
        Assert.Equal(offset, next.GetProperty("offset").GetInt64());
        Assert.True(next.GetProperty("done").GetBoolean());

        JsonElement tail = JsonDocument.Parse((await s.Send(HttpMethod.Get, $"/api/issues/{n}/logs?offset=5")).Body).RootElement;
        Assert.Equal(first.GetProperty("text").GetString()![5..], tail.GetProperty("text").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await s.Send(HttpMethod.Get, $"/api/issues/{n}/logs?run=9")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Send(HttpMethod.Get, $"/api/issues/{n}/logs?offset=x")).Status);
    }

    [Fact]
    public async Task Diff_IsPlainText_AndNotFoundWithoutDirectory()
    {
        int scratch = await s.Create("no dir");
        Assert.Equal(HttpStatusCode.NotFound, (await s.Send(HttpMethod.Get, $"/api/issues/{scratch}/diff")).Status);

        s.Fake.Scenario = "write_file";
        try
        {
            int git = await s.Create("with dir", s.Repo);
            await s.WaitReview(git);
            (HttpStatusCode status, string diff, HttpResponseMessage response) = await s.Send(HttpMethod.Get, $"/api/issues/{git}/diff");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("+++ b/written-1.txt", diff);
            Assert.Equal(HttpStatusCode.NoContent, (await s.Send(HttpMethod.Delete, $"/api/issues/{git}")).Status);
        }
        finally
        {
            s.Fake.Scenario = "complete";
        }
    }

    [Theory]
    [InlineData("/", "text/html")]
    [InlineData("/app.css", "text/css")]
    [InlineData("/app.js", "text/javascript")]
    public async Task StaticFiles_AreServedWithoutAuth(string path, string contentType)
    {
        (HttpStatusCode status, string body, HttpResponseMessage response) = await s.Send(HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.True(body.Length > 100);
        if (path == "/") Assert.Contains("app.js", body);
    }

    [Fact]
    public async Task UnknownStaticFile_Is404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await s.Send(HttpMethod.Get, "/nope.txt")).Status);
    }
}
