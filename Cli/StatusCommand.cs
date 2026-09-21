using Velo.Common;

namespace Velo.Cli;

public static class StatusCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var pid = Utils.GetRunningPid();
            if (pid > 0)
            {
                Console.WriteLine($"velo is running with pid {pid}.");
            }
            else
            {
                Console.WriteLine("velo is not running.");
            }
        }
        catch
        {
            Console.Error.WriteLine("Error: failed to get status of velo.");
        }
        return await Task.FromResult(0).ConfigureAwait(false);
    }
}