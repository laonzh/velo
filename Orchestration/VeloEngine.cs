namespace Velo.Orchestration;

public sealed class VeloEngine
{
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            timer.Dispose();
        }
    }
}