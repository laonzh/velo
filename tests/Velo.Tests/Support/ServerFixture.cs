using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Velo.Tests;

/// <summary>
/// The real <c>velo serve</c> process (temp VELO_HOME, free port, FakeCodex as the agent)
/// plus the real <c>velo</c> CLI against it.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    private readonly TempDir _temp = new();
    private readonly StringBuilder _output = new();
    private Process? _server;

    public string Home => _temp.Combine("home");
    public int Port { get; private set; }
    public FakeCodexCopy Fake { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public string Repo => _temp.Combine("repo");

    public static string VeloExe => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "velo.exe" : "velo");

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static void WriteConfig(string home, int port, string? codexPath = null)
    {
        Directory.CreateDirectory(home);
        string codex = codexPath is null ? "null" : "\"" + codexPath.Replace("\\", "\\\\") + "\"";
        File.WriteAllText(Path.Combine(home, "config.json"), $$"""{ "port": {{port}}, "codexPath": {{codex}}, "maxConcurrency": 2 }""");
    }

    public async Task InitializeAsync()
    {
        Fake = new FakeCodexCopy(_temp.Combine("fake")) { Scenario = "complete" };
        TestGit.InitRepo(Repo);
        Port = FreePort();
        WriteConfig(Home, Port, Fake.Exe);
        Http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{Port}") };

        _server = Start(Home, ["serve"], _output);
        await Wait.UntilAsync(async () =>
        {
            Assert.False(_server.HasExited, "velo serve exited: " + _output);
            try { return (await Http.GetAsync("/api/issues")).IsSuccessStatusCode; }
            catch (HttpRequestException) { return false; }
        }, "velo serve to listen");
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
        if (_server is not null) await _server.WaitForExitAsync();
        _server?.Dispose();
        _temp.Dispose();
    }

    internal static Process Start(string home, string[] args, StringBuilder? captureTo = null, string? stdin = null)
    {
        var psi = new ProcessStartInfo(VeloExe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["VELO_HOME"] = home;
        var p = Process.Start(psi)!;
        if (stdin is not null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        if (captureTo is not null)
        {
            p.OutputDataReceived += (_, e) => { lock (captureTo) captureTo.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { lock (captureTo) captureTo.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        return p;
    }

    /// <summary>Runs the CLI (args as given) and returns its exit code, stdout and stderr.</summary>
    public static async Task<(int Code, string Out, string Err)> Cli(string home, params string[] args) => await CliWithInput(home, null, args);

    /// <summary>Like Cli, with <paramref name="stdin"/> piped in (null = closed stdin).</summary>
    public static async Task<(int Code, string Out, string Err)> CliWithInput(string home, string? stdin, params string[] args)
    {
        using Process p = Start(home, args, null, stdin);
        Task<string> stdout = p.StandardOutput.ReadToEndAsync(), stderr = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException($"velo {string.Join(' ', args)} did not finish");
        }
        return (p.ExitCode, await stdout, await stderr);
    }

    public Task<(int Code, string Out, string Err)> Cli(params string[] args) => Cli(Home, args);

    public Task<(int Code, string Out, string Err)> CliWithInput(string stdin, params string[] args) => CliWithInput(Home, stdin, args);

    public async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> Send(HttpMethod method, string path, string? json = null, bool velo = true, string? host = null, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (velo) request.Headers.Add("X-Velo", "1");
        if (host is not null) request.Headers.Host = host;
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    /// <summary>Creates an issue over HTTP and returns its number.</summary>
    public async Task<int> Create(string title, string? directory = null)
    {
        string body = JsonSerializer.Serialize(new Dictionary<string, string?> { ["title"] = title, ["directory"] = directory });
        (HttpStatusCode status, string response, _) = await Send(HttpMethod.Post, "/api/issues", body);
        Assert.True(status == HttpStatusCode.Created, response);
        return JsonDocument.Parse(response).RootElement.GetProperty("number").GetInt32();
    }

    public async Task<JsonElement> Get(int number)
    {
        (_, string body, _) = await Send(HttpMethod.Get, "/api/issues/" + number);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public async Task<JsonElement> WaitReview(int number, int runs = 1)
    {
        await Wait.UntilAsync(async () =>
        {
            JsonElement i = await Get(number);
            return i.GetProperty("status").GetString() == "review" && i.GetProperty("runs").GetArrayLength() == runs;
        }, $"VL-{number} in review after {runs} run(s)");
        return await Get(number);
    }
}
