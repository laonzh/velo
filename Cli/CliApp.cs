using Velo.Configuration;

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

        VeloConfig.EnsureInitialized();

        return args[0] switch
        {
            "start" => await StartCommand.RunAsync(args[1..]).ConfigureAwait(false),
            "stop" => await StopCommand.RunAsync(args[1..]).ConfigureAwait(false),
            "status" => await StatusCommand.RunAsync(args[1..]).ConfigureAwait(false),
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

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command {command}.");
        return 1;
    }
}