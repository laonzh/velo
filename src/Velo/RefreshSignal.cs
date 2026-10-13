namespace Velo;

// A refresh request makes the idle watch loop poll now instead of waiting out its interval.
internal sealed class RefreshSignal
{
    private int _pending;

    // False when a request was already waiting to be consumed.
    public bool Request() => Interlocked.Exchange(ref _pending, 1) == 0;

    public bool TryConsume() => Interlocked.Exchange(ref _pending, 0) == 1;
}
