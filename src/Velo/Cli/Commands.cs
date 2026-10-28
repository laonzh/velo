using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Velo;

public static partial class Cli
{
    /// <summary>
    /// Prints the log from the start; with <paramref name="follow"/> polls every second until the run has ended.
    /// </summary>
    private static async Task<int> LogsAsync(Api api, string id, string? runText, bool follow, bool json)
    {
        int? run = runText is null ? null : int.TryParse(runText, out int r) && r > 0 ? r : throw Usage($"--run needs a run number, not '{runText}'");
        long offset = 0;
        while (true)
        {
            string raw = await api.SendAsync(HttpMethod.Get, $"/api/issues/{id}/logs?offset={offset}" + (run is null ? "" : $"&run={run}"));
            LogChunk chunk = Parse(raw, VeloJson.Default.LogChunk);
            if (json) Console.WriteLine(raw);
            else Console.Write(chunk.Text);
            offset = chunk.Offset;
            if (chunk.Run > 0) run ??= chunk.Run; // keep following the run we started with
            if (chunk.Text.Length > 0) continue;
            if (!follow || chunk.Done) return 0;
            await Task.Delay(1000);
        }
    }

    /// <summary>
    /// Polls until the issue is in review or done; exit code 0 when its last run completed (or it is done),
    /// 1 when the run failed or was stopped.
    /// </summary>
    private static async Task<int> WaitAsync(Api api, string id, bool json)
    {
        while (true)
        {
            string raw = await api.SendAsync(HttpMethod.Get, $"/api/issues/{id}");
            Issue issue = Parse(raw, VeloJson.Default.Issue);
            if (issue.Status is IssueStatus.Review or IssueStatus.Done)
            {
                Print(json ? raw : $"{issue.Key} {Status(issue)}");
                return issue.Status == IssueStatus.Done || issue.Runs.Count == 0 || issue.Runs[^1].Status == RunStatus.Completed ? 0 : 1;
            }
            await Task.Delay(1000);
        }
    }

    private static T Parse<T>(string raw, JsonTypeInfo<T> type) =>
        JsonSerializer.Deserialize(raw, type) ?? throw new CliException(1, "unexpected empty response from the server");

    private static void OpenBrowser(string url)
    {
        ProcessStartInfo psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(url) { UseShellExecute = true }
            : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url);
        try
        {
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new CliException(1, $"cannot open a browser ({ex.Message}); open {url} yourself");
        }
    }

    /// <summary>HTTP client of the local server; every request carries <c>X-Velo: 1</c>.</summary>
    private sealed class Api(int port) : IDisposable
    {
        // A loopback connect succeeds in milliseconds; without a short limit Windows retries the SYN of a refused
        // connection for ~2 s before reporting "server not running".
        private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromMilliseconds(500) })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = Timeout.InfiniteTimeSpan,
        };

        /// <summary>
        /// Returns the response body; an error status or an unreachable server throws a <see cref="CliException"/>.
        /// </summary>
        public async Task<string> SendAsync(HttpMethod method, string path, string? json = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Velo", "1");
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { InnerException: TimeoutException })
            {
                throw new CliException(1, $"the velo server is not running on 127.0.0.1:{port}; start it with: velo serve");
            }
            using (response)
            {
                string body = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode) return body;
                string message = $"HTTP {(int)response.StatusCode}";
                try { message = JsonSerializer.Deserialize(body, VeloJson.Default.ErrorBody)?.Error ?? message; }
                catch (JsonException) { }
                throw new CliException(1, message) { HttpStatus = (int)response.StatusCode };
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
