using System.Text.Json;

namespace Velo.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly TempDir _temp = new();
    private VeloPaths Paths => new(_temp.Combine("home"));

    public void Dispose() => _temp.Dispose();

    private static Issue New(int n, string title = "t") => new() { Number = n, Title = title, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch };

    [Fact]
    public void HomeId_IsPersistedBeforeFirstIssue_AndSurvivesEverySaveAndRestart()
    {
        var store = new Store(Paths);
        string homeId = store.HomeId;
        Assert.True(Guid.TryParseExact(homeId, "N", out Guid parsed));
        Assert.NotEqual(Guid.Empty, parsed);
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Paths.StateFile)))
            Assert.Equal(homeId, doc.RootElement.GetProperty("homeId").GetString());
        Assert.Equal(homeId, new Store(Paths).HomeId);

        store.Add(n => New(n));
        store.Update(1, i => i with { Title = "updated" });
        store.Remove(1);
        Assert.Equal(homeId, new Store(Paths).HomeId);
        Assert.Equal(2, new Store(Paths).Add(n => New(n)).Number);
    }

    [Fact]
    public void SeparateHomes_HaveDifferentIdentities_AndMovingStatePreservesIdentity()
    {
        var first = new Store(Paths);
        var otherPaths = new VeloPaths(_temp.Combine("other-home"));
        var other = new Store(otherPaths);
        Assert.NotEqual(first.HomeId, other.HomeId);

        var movedPaths = new VeloPaths(_temp.Combine("moved-home"));
        Directory.Move(Paths.Root, movedPaths.Root);
        Assert.Equal(first.HomeId, new Store(movedPaths).HomeId);
    }

    [Fact]
    public void RoundTrip_PersistsIssuesRunsAndNextNumber()
    {
        var store = new Store(Paths);
        Issue a = store.Add(n => New(n, "first"));
        store.Add(n => New(n, "second"));
        store.Update(a.Number, i => i with
        {
            Status = IssueStatus.Review,
            Message = "more",
            Runs = [new Run { Number = 1, Status = RunStatus.Failed, Summary = "boom", Tokens = 12, SessionId = "th", StartedAt = DateTimeOffset.UnixEpoch, EndedAt = DateTimeOffset.UnixEpoch }],
        });

        var reopened = new Store(Paths);
        Assert.Equal([1, 2], reopened.List().Select(i => i.Number));
        Issue got = reopened.Get(1)!;
        Assert.Equal("first", got.Title);
        Assert.Equal(IssueStatus.Review, got.Status);
        Assert.Equal("more", got.Message);
        Run run = Assert.Single(got.Runs);
        Assert.Equal((RunStatus.Failed, "boom", 12L, "th"), (run.Status, run.Summary, run.Tokens, run.SessionId));
        Assert.Equal(3, reopened.Add(n => New(n)).Number);
    }

    [Fact]
    public void File_IsCamelCaseWithLowercaseEnumsAndNoComputedKey()
    {
        var store = new Store(Paths);
        store.Add(n => New(n) with { Status = IssueStatus.Review, Runs = [new Run { Number = 1, Status = RunStatus.Interrupted }] });

        string json = File.ReadAllText(Paths.StateFile);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("nextNumber").GetInt32());
        JsonElement issue = doc.RootElement.GetProperty("issues")[0];
        Assert.Equal("review", issue.GetProperty("status").GetString());
        Assert.Equal("interrupted", issue.GetProperty("runs")[0].GetProperty("status").GetString());
        Assert.False(issue.TryGetProperty("key", out _));
    }

    [Fact]
    public void Save_IsAtomic_NoTempFileLeftAndFileAlwaysValid()
    {
        var store = new Store(Paths);
        for (int i = 0; i < 20; i++)
        {
            store.Add(n => New(n));
            Assert.False(File.Exists(Paths.StateFile + ".tmp"));
            Assert.Equal(i + 1, JsonDocument.Parse(File.ReadAllText(Paths.StateFile)).RootElement.GetProperty("issues").GetArrayLength());
        }
    }

    [Theory]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("remove")]
    public void SaveFailure_LeavesMemoryDiskAndNextNumberUnchanged(string operation)
    {
        var store = new Store(Paths);
        Issue original = store.Add(n => New(n, "keep"));
        string saved = File.ReadAllText(Paths.StateFile);
        Directory.CreateDirectory(Paths.StateFile + ".tmp"); // fail the temporary-file write on every platform

        Exception? error = Record.Exception(() =>
        {
            switch (operation)
            {
                case "add": store.Add(n => New(n, "lost")); break;
                case "update": store.Update(1, i => i with { Status = IssueStatus.Running }); break;
                case "remove": store.Remove(1); break;
            }
        });

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Same(original, Assert.Single(store.List()));
        Assert.Equal(saved, File.ReadAllText(Paths.StateFile));
        Assert.Equal(IssueStatus.Todo, new Store(Paths).Get(1)!.Status);

        Directory.Delete(Paths.StateFile + ".tmp");
        Assert.Equal(2, store.Add(n => New(n, "next")).Number);
        Assert.Equal([1, 2], new Store(Paths).List().Select(i => i.Number));
    }

    [Fact]
    public void NextNumber_IsNeverReused_AfterRemove()
    {
        var store = new Store(Paths);
        store.Add(n => New(n));
        store.Add(n => New(n));
        Issue last = store.Add(n => New(n));
        store.Remove(last.Number);

        Assert.Equal(4, store.Add(n => New(n)).Number);
        store.Remove(4);
        Assert.Equal(5, new Store(Paths).Add(n => New(n)).Number); // also across a restart
        Assert.Null(store.Get(3));
    }

    [Fact]
    public void Update_UnknownIs404_AndAThrowingChangeLeavesTheIssueAlone()
    {
        var store = new Store(Paths);
        store.Add(n => New(n, "keep"));
        Assert.Equal(404, Assert.Throws<VeloException>(() => store.Update(9, i => i)).Status);

        Assert.Throws<VeloException>(() => store.Update(1, _ => throw VeloException.Conflict("no")));
        Assert.Equal("keep", new Store(Paths).Get(1)!.Title);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"nextNumber":"x"}""")]
    [InlineData("""{"issues":[{"number":1,"title":"t","status":"bogus"}]}""")]
    public void InvalidStateFile_IsReportedWithItsPath(string content)
    {
        Directory.CreateDirectory(Paths.Root);
        File.WriteAllText(Paths.StateFile, content);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => new Store(Paths));
        Assert.Contains("state.json", ex.Message);
    }

    [Fact]
    public void InvalidHomeId_IsRejectedWithoutRewritingState()
    {
        Directory.CreateDirectory(Paths.Root);
        foreach (string content in new[]
        {
            "null",
            """{"nextNumber":2,"issues":[{"number":1,"title":"old task"}]}""",
            """{"homeId":null}""",
            """{"homeId":""}""",
            """{"homeId":"not-a-guid"}""",
            """{"homeId":"00000000000000000000000000000000"}""",
        })
        {
            File.WriteAllText(Paths.StateFile, content);
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => new Store(Paths));
            Assert.Contains(Paths.StateFile, error.Message);
            Assert.Equal(content, File.ReadAllText(Paths.StateFile));
        }
    }

    [Fact]
    public void LegacyThreadId_IsReadAsSessionId_AndRewrittenAsSessionId()
    {
        Directory.CreateDirectory(Paths.Root);
        File.WriteAllText(Paths.StateFile, """{"homeId":"73dd625c013b45a1977f109531a9f640","nextNumber":2,"issues":[{"number":1,"title":"t","status":"review","runs":[{"number":1,"status":"completed","threadId":"th-old"}]}]}""");

        var store = new Store(Paths);
        Assert.Equal("th-old", Assert.Single(store.Get(1)!.Runs).SessionId);

        store.Update(1, i => i with { Message = "save" });
        JsonElement run = JsonDocument.Parse(File.ReadAllText(Paths.StateFile)).RootElement.GetProperty("issues")[0].GetProperty("runs")[0];
        Assert.Equal("th-old", run.GetProperty("sessionId").GetString());
        Assert.False(run.TryGetProperty("threadId", out _));
    }

    [Fact]
    public void MissingStateFile_StartsEmptyAtOne()
    {
        var store = new Store(Paths);
        Assert.Empty(store.List());
        Assert.Equal(1, store.Add(n => New(n)).Number);
    }

    [Fact]
    public void Resolve_DefaultsToDotVeloInUserProfile()
    {
        string? old = Environment.GetEnvironmentVariable("VELO_HOME");
        string? oldXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("VELO_HOME", null);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", _temp.Path);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".velo"), VeloPaths.Resolve().Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VELO_HOME", old);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldXdg);
        }
    }

    [Fact]
    public void Resolve_UsesVeloHome()
    {
        string? old = Environment.GetEnvironmentVariable("VELO_HOME");
        try
        {
            Environment.SetEnvironmentVariable("VELO_HOME", _temp.Path);
            Assert.Equal(Path.GetFullPath(_temp.Path), VeloPaths.Resolve().Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VELO_HOME", old);
        }
    }
}
