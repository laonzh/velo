using System.Diagnostics;
using System.Text.Json;

var home = Environment.GetEnvironmentVariable("VELO_HOME")
    ?? throw new InvalidOperationException("VELO_HOME is required.");

if (args is ["--b10-unrelated-process"])
{
    await File.WriteAllTextAsync(
        Path.Combine(home, "b10-unrelated-process.ready"),
        Environment.ProcessId.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
if (args is ["--b8-timeout-child"])
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

var prompt = Console.In.ReadToEnd();

if (prompt.Equals("b12-worktree-execution", StringComparison.Ordinal))
{
    await File.WriteAllTextAsync(
        Path.Combine(Environment.CurrentDirectory, "b12-codex-output.txt"),
        "created by fake Codex");
    return 0;
}

if (prompt.Equals("b13-native-aot", StringComparison.Ordinal))
{
    await File.WriteAllTextAsync(
        Path.Combine(home, "b13-native-aot.completed"),
        prompt);
    return 0;
}

if (prompt.StartsWith("b11-cmd-", StringComparison.Ordinal))
{
    var captureName = prompt.StartsWith("b11-cmd-safe", StringComparison.Ordinal)
        ? "b11-cmd-safe"
        : "b11-cmd-unsafe";
    var capture = new
    {
        arguments = args,
        standardInput = prompt
    };
    await File.WriteAllTextAsync(
        Path.Combine(home, $"{captureName}.invocation.json"),
        JsonSerializer.Serialize(capture));
    return 0;
}
if (prompt.Equals("b8-failure", StringComparison.Ordinal))
{
    Console.Error.WriteLine("b8 failure first diagnostic");
    Console.Error.WriteLine("b8 failure final error");
    return 17;
}

if (prompt.Equals("b8-timeout", StringComparison.Ordinal))
{
    using var child = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate fake Codex executable."),
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "--b8-timeout-child" }
    }) ?? throw new InvalidOperationException("Failed to start timeout child process.");

    await File.WriteAllTextAsync(
        Path.Combine(home, "b8-timeout-parent.pid"),
        Environment.ProcessId.ToString());
    await File.WriteAllTextAsync(
        Path.Combine(home, "b8-timeout-child.pid"),
        child.Id.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (prompt.Equals("b8-retry", StringComparison.Ordinal))
{
    var marker = Path.Combine(home, "b8-retry.marker");
    if (!File.Exists(marker))
    {
        await File.WriteAllTextAsync(marker, "failed-once");
        Console.Error.WriteLine("b8 retry transient error");
        return 23;
    }

    await File.WriteAllTextAsync(Path.Combine(home, "b8-retry.succeeded"), "success");
    return 0;
}

if (prompt.StartsWith("b7-serial-", StringComparison.Ordinal))
    return await RunSerialProbeAsync(prompt);

if (prompt.StartsWith("b7-parallel-", StringComparison.Ordinal))
    return await RunParallelProbeAsync(prompt);

if (prompt.StartsWith("b6-", StringComparison.Ordinal))
{
    var capture = new
    {
        arguments = args,
        standardInput = prompt
    };
    var capturePath = Path.Combine(home, $"{prompt}.invocation.json");
    await File.WriteAllTextAsync(capturePath, JsonSerializer.Serialize(capture));
    return 0;
}

if (prompt.Contains("b4-requeue", StringComparison.Ordinal))
{
    var marker = Path.Combine(home, "requeue.marker");
    if (File.Exists(marker))
    {
        File.WriteAllText(Path.Combine(home, "requeue.second"), "success");
        return 0;
    }

    File.WriteAllText(marker, "first");
    File.WriteAllText(
        Path.Combine(home, "requeue.pid"),
        Environment.ProcessId.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (prompt.Contains("b4-cancel", StringComparison.Ordinal))
{
    File.WriteAllText(
        Path.Combine(home, "cancel.pid"),
        Environment.ProcessId.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

Console.Error.WriteLine($"Unexpected prompt: {prompt}");
return 2;

async Task<int> RunSerialProbeAsync(string name)
{
    var startedAtUtc = DateTimeOffset.UtcNow;
    await Task.Delay(TimeSpan.FromSeconds(1.5));
    await WriteIntervalAsync(name, startedAtUtc, DateTimeOffset.UtcNow);
    return 0;
}

async Task<int> RunParallelProbeAsync(string name)
{
    var startedAtUtc = DateTimeOffset.UtcNow;
    await File.WriteAllTextAsync(
        Path.Combine(home, $"{name}.started"),
        Environment.ProcessId.ToString());

    var deadline = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < deadline)
    {
        if (File.Exists(Path.Combine(home, "b7-parallel-main.started"))
            && File.Exists(Path.Combine(home, "b7-parallel-linked.started")))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            await WriteIntervalAsync(name, startedAtUtc, DateTimeOffset.UtcNow);
            return 0;
        }
        await Task.Delay(50);
    }

    Console.Error.WriteLine("Timed out waiting for both linked-worktree probes to start.");
    return 3;
}

Task WriteIntervalAsync(
    string name,
    DateTimeOffset startedAtUtc,
    DateTimeOffset endedAtUtc) =>
    File.WriteAllTextAsync(
        Path.Combine(home, $"{name}.interval.json"),
        JsonSerializer.Serialize(new
        {
            startedAtUtc,
            endedAtUtc,
            processId = Environment.ProcessId
        }));
