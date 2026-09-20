using Velo.Cli;

try
{
    return await CliApp.RunAsync(args).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}