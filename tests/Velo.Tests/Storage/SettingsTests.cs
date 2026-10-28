using System.Text.Json;

namespace Velo.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly TempDir _temp = new();
    private VeloPaths Paths => new(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private Settings Load(string? json)
    {
        if (json is not null) File.WriteAllText(Paths.ConfigFile, json);
        return Store.LoadSettings(Paths);
    }

    [Fact]
    public void NoFile_UsesDefaults()
    {
        Settings s = Load(null);
        Assert.Equal((4870, 2), (s.Port, s.MaxConcurrency));
        Assert.Null(s.CodexPath);
        Assert.Null(s.Model);
        Assert.Null(s.Effort);
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreAccepted_MissingKeysKeepDefaults()
    {
        Settings s = Load("""
            {
              // my port
              "port": 5000,
              "codexPath": "C:\\bin\\codex.exe", /* inline */
              "effort": "high",
            }
            """);
        Assert.Equal(5000, s.Port);
        Assert.Equal(2, s.MaxConcurrency);
        Assert.Equal(@"C:\bin\codex.exe", s.CodexPath);
        Assert.Equal("high", s.Effort);
    }

    [Theory]
    [InlineData("""{"codexPath":"C:\\nowhere\\legacy-codex.exe"}""", "legacy-codex.exe")] // legacy top-level codexPath still works
    [InlineData("""{"codexPath":"C:\\nowhere\\legacy-codex.exe","agents":{"codex":{"path":"C:\\nowhere\\new-codex.exe"}}}""", "new-codex.exe")]
    public async Task CodexAgentPath_IsAgentsCodexPath_ElseLegacyCodexPath(string json, string used)
    {
        Settings s = Load(json);
        Assert.Equal("codex", s.Agent);
        IAgent agent = new AgentRegistry(s).Get(s.Agent);

        AgentResult r = await agent.RunAsync(new AgentRequest { Prompt = "p", Directory = _temp.Path }, _ => { }, CancellationToken.None);
        Assert.Equal(AgentStatus.Failed, r.Status);
        Assert.Contains($"{used}' does not exist", r.Error);
        Assert.Equal("codex resume th", agent.TakeOver("th"));
        Assert.Equal(400, Assert.Throws<VeloException>(() => new AgentRegistry(s).Get("nope")).Status);
    }

    [Theory]
    [InlineData("""{"port":0}""", "port")]
    [InlineData("""{"port":65536}""", "port")]
    [InlineData("""{"maxConcurrency":0}""", "maxConcurrency")]
    [InlineData("""{"port":"abc"}""", "config.json")]
    [InlineData("{ nope", "config.json")]
    public void InvalidValues_AreRejected(string json, string mention)
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => Load(json));
        Assert.Contains(mention, ex.Message);
    }
}
