namespace Velo.Tests;

public sealed class WorkerOwnershipTests
{
    [Fact]
    public async Task StalePidFile_DoesNotClaimOrTerminateUnrelatedProcess()
    {
        var harness = new VeloCliHarness();
        try
        {
            using var unrelated = harness.StartFakeCodexProcess(
                "--b10-unrelated-process");
            await harness.WaitForFileAsync(
                harness.HomePath("b10-unrelated-process.ready"));

            var pidFile = harness.HomePath("velo.pid");
            await File.WriteAllTextAsync(pidFile, unrelated.Id.ToString());

            var status = await harness.RunAsync("status");
            Assert.Contains("Velo is not running.", status.StandardOutput);
            Assert.False(File.Exists(pidFile));
            Assert.False(unrelated.HasExited);

            await File.WriteAllTextAsync(pidFile, unrelated.Id.ToString());
            var stop = await harness.RunAsync("stop");
            Assert.Contains("Velo is not running.", stop.StandardOutput);
            Assert.False(File.Exists(pidFile));
            Assert.False(unrelated.HasExited);

            await File.WriteAllTextAsync(pidFile, unrelated.Id.ToString());
            await harness.StartWorkerAsync();

            var workerPid = await harness.WaitForPidAsync("velo.pid");
            Assert.NotEqual(unrelated.Id, workerPid);
            Assert.False(unrelated.HasExited);

            var running = await harness.RunAsync("status");
            Assert.Contains($"Velo is running with pid {workerPid}.", running.StandardOutput);

            await harness.StopWorkerAsync();
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.False(Directory.Exists(harness.RootPath));
    }
}