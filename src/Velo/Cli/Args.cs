namespace Velo;

public static partial class Cli
{
    private static readonly string[] s_valueOptions = ["-d", "-m", "--model", "--effort", "--run"];
    private static readonly string[] s_flagOptions = ["-a", "--all", "-f", "-y", "--json", "-h", "--help", "-v", "--version"];

    private static CliException Usage(string message) => new(2, message);
}
