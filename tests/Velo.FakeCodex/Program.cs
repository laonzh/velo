var behavior = Environment.GetEnvironmentVariable("VELO_FAKE_CODEX");
if (behavior is not null
    && behavior.StartsWith("block-stdin:", StringComparison.Ordinal))
{
    await File.WriteAllTextAsync(
        behavior["block-stdin:".Length..],
        Environment.ProcessId.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

var prompt = await Console.In.ReadToEndAsync();

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
