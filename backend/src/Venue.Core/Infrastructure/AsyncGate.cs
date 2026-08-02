namespace Venue.Infrastructure;

/// <summary>
/// Coarse correctness gate: every mutation of the venue core (ledger + engine +
/// markets + RFM mirror) runs under this single semaphore. The concurrency model is
/// deliberately "correct, not fancy" (PLAN_BACKEND): one gate serializes the indexer,
/// the order API, the settlement-confirm path and the RFM crank, so no torn read or
/// double-apply is possible. Chain I/O happens OUTSIDE the gate (the batcher submits,
/// then re-enters under the gate to apply the outcome).
/// </summary>
public sealed class AsyncGate
{
    private readonly SemaphoreSlim _sem = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        await _sem.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _sem.Release();
        }
    }

    public async Task RunAsync(Func<Task> action)
    {
        await _sem.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _sem.Release();
        }
    }

    public T Run<T>(Func<T> action)
    {
        _sem.Wait();
        try
        {
            return action();
        }
        finally
        {
            _sem.Release();
        }
    }

    public void Run(Action action)
    {
        _sem.Wait();
        try
        {
            action();
        }
        finally
        {
            _sem.Release();
        }
    }
}
