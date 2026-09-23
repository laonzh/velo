using System.Text.Json;

namespace Velo.Tests;

public sealed class CommandShimTests
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
    public async Task Worker_ExecutesWindowsCmdShimWithoutChangingArgumentsOrPrompt()
    {
        var harness = new VeloCliHarness();
        try
        {
            harness.UseCmdCodexShim();
            const string safePrompt =
                "b11-cmd-safe spaces & | < > ^ % ! \"quoted\" 中文";
            const string unsafePrompt =
                "b11-cmd-unsafe spaces & | < > ^ % ! \"quoted\" 中文";

            var safeId = (await harness.RunAsync("add", safePrompt))
                .StandardOutput.Trim();
            var unsafeId = (await harness.RunAsync("add", "--unsafe", unsafePrompt))
                .StandardOutput.Trim();

            await harness.StartWorkerAsync();
            await harness.WaitForFileAsync(harness.TaskPath("done", safeId));
            await harness.WaitForFileAsync(harness.TaskPath("done", unsafeId));
            await harness.StopWorkerAsync();

            await AssertInvocationAsync(
                harness,
                "b11-cmd-safe",
                safePrompt,
                SafeArguments);
            await AssertInvocationAsync(
                harness,
                "b11-cmd-unsafe",
                unsafePrompt,
                UnsafeArguments);
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }

    private static async Task AssertInvocationAsync(
        VeloCliHarness harness,
        string captureName,
        string expectedPrompt,
        IReadOnlyList<string> expectedArguments)
    {
        var capturePath = harness.HomePath($"{captureName}.invocation.json");
        await harness.WaitForFileAsync(capturePath);

        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(capturePath));
        var actualArguments = capture.RootElement
            .GetProperty("arguments")
            .EnumerateArray()
            .Select(element => element.GetString()
                ?? throw new InvalidDataException("Invocation argument was null."))
            .ToArray();

        Assert.Equal(expectedArguments, actualArguments);
        Assert.Equal(
            expectedPrompt,
            capture.RootElement.GetProperty("standardInput").GetString());
    }
}