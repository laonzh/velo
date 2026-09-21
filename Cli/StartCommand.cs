using System.Diagnostics;
using System.Text;
using Velo.Configuration;
using Velo.Orchestration;

namespace Velo.Cli;

public static class StartCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("-d") || args.Contains("--daemon"))
        {
            var executable = Environment.ProcessPath;
            var cleanArgs = "start " + string.Join(" ", args.Where(x => x is not ("-d" or "--daemon")));
            var p = Process.Start(new ProcessStartInfo()
            {
                FileName = executable,
                Arguments = cleanArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            Console.WriteLine(p != null ? $"velo started pid: {p.Id}" : "velo start failed");
            return 0;
        }

        FileStream? pidFile = null;
        try
        {
            pidFile = new FileStream(VeloConfig.PidFile, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            using var sw = new StreamWriter(pidFile, Encoding.ASCII, 32, true);
            sw.Write(Environment.ProcessId);
            sw.Flush();
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Error: velo is already running.");
            return 1;
        }

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (File.Exists(VeloConfig.StopFlag)) { cts.Cancel(); break; }
                await Task.Delay(500).ConfigureAwait(false);
            }
        });

        try
        {
            var engine = new VeloEngine();
            await engine.StartAsync(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            pidFile?.Dispose();
            File.Delete(VeloConfig.PidFile);
            File.Delete(VeloConfig.StopFlag);
        }

        return 0;
    }
}