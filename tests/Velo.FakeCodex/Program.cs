using System.Text;

// Velo sends prompts and reads output as UTF-8. Use UTF-8 here too, not the console code page.
var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

// The real codex rejects these two flags together, so the fake does too.
if (args.Contains("--sandbox") && args.Contains("--approve-for-me"))
{
    Console.Error.WriteLine("error: the argument '--sandbox <SANDBOX_MODE>' cannot be used with '--approve-for-me'");
    return 2;
}

var behavior = Environment.GetEnvironmentVariable("VELO_FAKE_CODEX");
if (behavior is not null
    && behavior.StartsWith("block-stdin:", StringComparison.Ordinal))
{
    var marker = behavior["block-stdin:".Length..];
    // Publish the PID atomically: tests poll for the file as soon as it exists.
    await File.WriteAllTextAsync(marker + ".tmp", Environment.ProcessId.ToString());
    File.Move(marker + ".tmp", marker, overwrite: true);
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (behavior == "exit-early") return 7;

var prompt = await input.ReadToEndAsync();

if (prompt.StartsWith("warn:", StringComparison.Ordinal))
{
    Console.Error.WriteLine("2026-10-13T00:00:00.000Z  WARN codex: noisy diagnostic");
    Console.Error.WriteLine(prompt[5..]);
    return 23;
}

if (prompt.StartsWith("fail:", StringComparison.Ordinal))
{
    Console.Error.WriteLine(prompt[5..]);
    return 23;
}

if (prompt.StartsWith("wait:", StringComparison.Ordinal))
{
    var path = prompt[5..];
    await File.WriteAllTextAsync(path + ".started", Environment.ProcessId.ToString());
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (!File.Exists(path) && DateTime.UtcNow < deadline)
        await Task.Delay(25);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Timed out waiting for {path}.");
        return 24;
    }
    await File.WriteAllTextAsync(path + ".completed", Environment.ProcessId.ToString());
    return 0;
}

await File.WriteAllTextAsync(
    Path.Combine(Environment.CurrentDirectory, "codex-ran.txt"),
    prompt);
Console.WriteLine($"fake codex received: {prompt}");
return 0;
