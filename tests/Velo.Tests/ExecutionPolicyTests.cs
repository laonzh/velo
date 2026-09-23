using System.Text.Json;

namespace Velo.Tests;

public sealed class ExecutionPolicyTests
{
    private static readonly string[] SafeArguments =
    [
        "exec",
        "--sandbox", "workspace-write",
        "--approve-for-me",
        "--skip-git-repo-check",
        "--color", "never",
        "-"
    ];

    private static readonly string[] UnsafeArguments =
    [
        "exec",
        "--dangerously-bypass-approvals-and-sandbox",
        "--skip-git-repo-check",
        "--color", "never",
        "-"
    ];

    [Fact]
    public async Task Worker_PreservesExecutionPolicyPromptAndLegacySafeDefault()
    {
        var harness = new VeloCliHarness();
        try
        {
            const string safePrompt = "b6-safe-prompt";
            const string unsafePrompt = "b6-unsafe-prompt";
            const string legacyPrompt = "b6-legacy-prompt";
            const string legacyId = "20260922000000-legacy";

            var safeId = (await harness.RunAsync("add", safePrompt)).StandardOutput.Trim();
            var unsafeId = (await harness.RunAsync("add", "--unsafe", unsafePrompt))
                .StandardOutput.Trim();

            var now = DateTimeOffset.UtcNow;
            var legacyTask = new
            {
                title = legacyPrompt,
                workspacePath = harness.RootPath,
                createdAtUtc = now,
                updatedAtUtc = now,
                lastError = (string?)null
            };
            await File.WriteAllTextAsync(
                harness.TaskPath("todo", legacyId),
                JsonSerializer.Serialize(legacyTask, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));

            Assert.Contains(
                "Execution: safe (workspace-write sandbox with automatic approval review)",
                (await harness.RunAsync("show", safeId)).StandardOutput);
            Assert.Contains(
                "Execution: unsafe (approvals and sandbox bypassed)",
                (await harness.RunAsync("show", unsafeId)).StandardOutput);
            Assert.Contains(
                "Execution: safe (workspace-write sandbox with automatic approval review)",
                (await harness.RunAsync("show", legacyId)).StandardOutput);

            await harness.StartWorkerAsync();
            await harness.WaitForFileAsync(harness.TaskPath("done", safeId));
            await harness.WaitForFileAsync(harness.TaskPath("done", unsafeId));
            await harness.WaitForFileAsync(harness.TaskPath("done", legacyId));
            await harness.StopWorkerAsync();

            await AssertInvocationAsync(harness, safePrompt, SafeArguments);
            await AssertInvocationAsync(harness, unsafePrompt, UnsafeArguments);
            await AssertInvocationAsync(harness, legacyPrompt, SafeArguments);

            Assert.Contains("State: done", (await harness.RunAsync("show", safeId)).StandardOutput);
            Assert.Contains("State: done", (await harness.RunAsync("show", unsafeId)).StandardOutput);
            Assert.Contains("State: done", (await harness.RunAsync("show", legacyId)).StandardOutput);
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }

    private static async Task AssertInvocationAsync(
        VeloCliHarness harness,
        string prompt,
        IReadOnlyList<string> expectedArguments)
    {
        var capturePath = harness.HomePath($"{prompt}.invocation.json");
        await harness.WaitForFileAsync(capturePath);

        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(capturePath));
        var actualArguments = capture.RootElement
            .GetProperty("arguments")
            .EnumerateArray()
            .Select(element => element.GetString() ?? throw new InvalidDataException("Invocation argument was null."))
            .ToArray();

        Assert.Equal(expectedArguments, actualArguments);
        Assert.Equal(
            prompt,
            capture.RootElement.GetProperty("standardInput").GetString());
    }
}

