using System.Diagnostics;
using Velo.Common;
using Velo.Configuration;

namespace Velo.Cli;

public static class StopCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var pid = Utils.GetRunningPid();
            if (pid <= 0)
            {
                Console.Error.WriteLine("Error: velo is not running.");
                return 0;
            }
            File.WriteAllText(VeloConfig.StopFlag, string.Empty);
            using var process = Process.GetProcessById(pid);
            if (process == null || process.HasExited)
            {
                Console.Error.WriteLine("Error: velo is not running.");
                return 0;
            }
            Console.WriteLine("stopping velo...");
            if (!process.WaitForExit(10000))
            {
                process.Kill(true);
                File.Delete(VeloConfig.PidFile);
                File.Delete(VeloConfig.StopFlag);
            }
            Console.WriteLine("velo stopped.");
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("Error: failed to stop velo.");
        }
        return await Task.FromResult(0).ConfigureAwait(false);
    }
}