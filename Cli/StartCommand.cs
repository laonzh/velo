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

        var maxConcurrency = 3;
        var pollingInterval = TimeSpan.FromSeconds(3);
        var taskTimeout = TimeSpan.FromMinutes(30);

        for (int i = 0; i < args.Length; i++)
        {
            var key = args[i];
            string? value = null;
            var idx = key.IndexOf('=');
            if (idx > 0)
            {
                key = key[..idx];
                value = key[(idx + 1)..];
            }
            else
            {
                if (i + 1 < args.Length)
                {
                    value = args[++i];
                }
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                Console.Error.WriteLine($"Missing value for option: {key}");
                return 1;
            }

            switch (key)
            {
                case "-c":
                case "--concurrency":
                    if (!int.TryParse(value, out var concurrency))
                    {
                        Console.Error.WriteLine($"Invalid value for option: {key}");
                        return 1;
                    }
                    maxConcurrency = concurrency;
                    break;
                case "-i":
                case "--interval":
                    if (!TimeSpan.TryParse(value, out var interval))
                    {
                        Console.Error.WriteLine($"Invalid value for option: {key}");
                        return 1;
                    }
                    pollingInterval = interval;
                    break;
                case "-t":
                case "--timeout":
                    if (!TimeSpan.TryParse(value, out var timeout))
                    {
                        Console.Error.WriteLine($"Invalid value for option: {key}");
                        return 1;
                    }
                    break;
                default:
                    break;
            }
        }

        var config = new VeloConfig()
        {
            DefaultAgent = "codex",
            MaxConcurrency = maxConcurrency,
            PollingInterval = pollingInterval,
            TaskTimeout = taskTimeout,
        };

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
            var engine = new VeloEngine(config);
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