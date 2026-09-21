using System.Diagnostics;
using System.Text;
using Velo.Configuration;
using Velo.Orchestration;

namespace Velo.Cli;

public static class CliApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args == null || args.Length <= 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        VeloPaths.EnsureInitialized();

        return args[0] switch
        {
            "start" => await HandleStartAsync(args[1..]).ConfigureAwait(false),
            "proj" or "p" => await ProjCommand.RunAsync(args[1..]).ConfigureAwait(false),
            "work" or "w" => await WorkCommand.RunAsync(args[1..]).ConfigureAwait(false),
            "stop" => HandleStop(),
            "status" => HandleStatus(),
            _ => UnknownCommand(args[0])
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: velo <command> [options]");
        Console.WriteLine("Commands:");
        Console.WriteLine("  proj - Manage projects");
        Console.WriteLine("  work - Manage work items");
        Console.WriteLine("  help - Show this help message");
        Console.WriteLine("  start - Start the Velo daemon");
        Console.WriteLine("  stop - Stop the Velo daemon");
        Console.WriteLine("  status - Show the status of the Velo daemon");
    }

    private static async Task<int> HandleStartAsync(string[] args)
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
            pidFile = new FileStream(VeloPaths.PidFile, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
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
                if (File.Exists(VeloPaths.StopFlag)) { cts.Cancel(); break; }
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
            File.Delete(VeloPaths.PidFile);
            File.Delete(VeloPaths.StopFlag);
        }

        return 0;
    }

    private static int HandleStop()
    {
        try
        {
            var pid = GetRunningPid();
            if (pid <= 0)
            {
                Console.Error.WriteLine("Error: velo is not running.");
                return 0;
            }
            File.WriteAllText(VeloPaths.StopFlag, string.Empty);
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
                File.Delete(VeloPaths.PidFile);
                File.Delete(VeloPaths.StopFlag);
            }
            Console.WriteLine("velo stopped.");
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("Error: failed to stop velo.");
        }
        return 0;
    }

    private static int HandleStatus()
    {
        try
        {
            var pid = GetRunningPid();
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
        return 0;
    }

    private static int GetRunningPid()
    {
        if (!File.Exists(VeloPaths.PidFile)) return 0;
        try
        {
            using var fs = new FileStream(VeloPaths.PidFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var pid = sr.ReadToEnd().Trim();
            if (!string.IsNullOrWhiteSpace(pid) && int.TryParse(pid, out var pidInt) && pidInt > 0)
            {
                using var process = Process.GetProcessById(pidInt);
                return process != null && !process.HasExited ? pidInt : 0;
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command {command}.");
        return 1;
    }
}